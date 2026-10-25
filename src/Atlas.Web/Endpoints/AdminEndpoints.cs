using Atlas.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Atlas.Web.Endpoints;

public sealed record PatchDoc(string? Title, string? Description, List<string>? Tags, string? Date, string? Verification,
    string? SourceNotes, List<Redaction>? Redactions);
public sealed record InitUpload(string? Name, long Size);
public sealed record SettingsRequest(bool NoIndex, string? Title, string? Intro, string? ContactNote);
public sealed record ReportStatusRequest(string? Status);

public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization();
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            ctx.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(ctx);
        });

        // ---------- review queue ----------
        admin.MapGet("/docs", (HttpContext ctx, DocStore store) =>
        {
            var q = ctx.Request.Query;
            return Results.Ok(store.SearchAdmin(new AdminQuery
            {
                Q = q["q"], Status = q["status"],
                Limit = int.TryParse(q["limit"], out var l) ? l : 50, Offset = int.TryParse(q["offset"], out var o) ? o : 0,
            }));
        });

        admin.MapGet("/docs/{id}", (string id, DocStore store) =>
            DataPaths.IsSafeId(id) && store.GetAdmin(id) is { } d ? Results.Ok(d) : Results.NotFound(new { error = "Not found." }));

        admin.MapPatch("/docs/{id}", (string id, PatchDoc req, DocStore store) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d) return Results.NotFound(new { error = "Not found." });
            var title = req.Title is null ? d.Title : req.Title.Trim();
            var description = req.Description is null ? d.Description : req.Description.Trim();
            var notes = req.SourceNotes ?? d.SourceNotes;
            var verification = req.Verification ?? d.Verification;
            string? date = req.Date is null ? d.DocDate : (req.Date.Trim().Length == 0 ? null : req.Date.Trim());
            if (title.Length is < 1 or > 300) return Results.BadRequest(new { error = "The title must be 1 to 300 characters." });
            if (description.Length > 10_000) return Results.BadRequest(new { error = "The description is too long (10,000 characters max)." });
            if (notes.Length > 20_000) return Results.BadRequest(new { error = "The source notes are too long (20,000 characters max)." });
            if (!Verification.IsValid(verification)) return Results.BadRequest(new { error = "Verification must be one of: " + string.Join(", ", Verification.All) + "." });
            if (date is not null && !DocStore.IsDate(date)) return Results.BadRequest(new { error = "The date must look like 2024-05-31." });
            var tags = req.Tags is null ? store.TagsOf(id) : Tags.Normalize(req.Tags);
            var redactionsJson = d.Redactions;
            if (req.Redactions is not null)
            {
                if (d.Kind is not (Kinds.Pdf or Kinds.Image)) return Results.BadRequest(new { error = "Redaction is only available for PDFs and images. Convert other documents to PDF first." });
                if (d.Status is Status.Published or Status.Publishing) return Results.Conflict(new { error = "Unpublish this item before changing its redactions." });
                if (Redaction.Validate(req.Redactions) is { } bad) return Results.BadRequest(new { error = bad });
                redactionsJson = System.Text.Json.JsonSerializer.Serialize(req.Redactions, Json.Options);
            }
            store.UpdateMetadata(id, title, description, date, verification, notes, tags, redactionsJson);
            return Results.Ok(store.GetAdmin(id));
        });

        admin.MapPost("/docs/{id}/approve", (string id, DocStore store, WorkQueue queue) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d) return Results.NotFound(new { error = "Not found." });
            if (d.Status is not (Status.Pending or Status.Rejected))
                return Results.Conflict(new { error = $"An item that is {d.Status} can't be approved." });
            store.SetStatus(id, Status.Publishing);
            queue.Enqueue(JobKind.Publish, id);
            return Results.Accepted($"/api/admin/docs/{id}", store.GetAdmin(id));
        });

        admin.MapPost("/docs/{id}/reject", (string id, DocStore store, DataPaths paths) => Withdraw(id, Status.Rejected, store, paths));
        admin.MapPost("/docs/{id}/unpublish", (string id, DocStore store, DataPaths paths) => Withdraw(id, Status.Pending, store, paths));

        admin.MapPost("/docs/{id}/reprocess", (string id, DocStore store, WorkQueue queue) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d) return Results.NotFound(new { error = "Not found." });
            if (d.Status is not (Status.Failed or Status.Pending)) return Results.Conflict(new { error = "Only pending or failed items can be reprocessed." });
            store.SetStatus(id, Status.Processing);
            queue.Enqueue(JobKind.Ingest, id);
            return Results.Accepted($"/api/admin/docs/{id}", store.GetAdmin(id));
        });

        admin.MapDelete("/docs/{id}", (string id, DocStore store, DataPaths paths) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is null) return Results.NotFound(new { error = "Not found." });
            store.Delete(id);
            paths.DeleteItem(id);
            return Results.Ok(new { ok = true });
        });

        // ---------- private views of the original ----------
        admin.MapGet("/docs/{id}/original", (string id, DocStore store, DataPaths paths) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d || !File.Exists(paths.Original(d))) return Results.NotFound();
            var attachment = d.Kind == Kinds.Office;
            return Results.File(paths.Original(d), FileKinds.MimeFor(d.Ext), fileDownloadName: attachment ? d.OriginalName : null, enableRangeProcessing: true);
        });

        admin.MapGet("/docs/{id}/thumb", (string id, DocStore store, DataPaths paths) =>
            DataPaths.IsSafeId(id) && store.Get(id) is { HasThumbOriginal: true } && File.Exists(paths.ThumbOriginal(id))
                ? Results.File(paths.ThumbOriginal(id), "image/jpeg") : Results.NotFound());

        // Page images of a PDF: src=original (default) or public (the published copy).
        admin.MapGet("/docs/{id}/pages/{n:int}.jpg", async (string id, int n, string? src, HttpContext ctx, DocStore store, PageRenderer pages) =>
        {
            if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d) return Results.NotFound();
            var path = await pages.RenderAsync(d, src == "public", n, ctx.RequestAborted);
            return path is null ? Results.NotFound() : Results.File(path, "image/jpeg");
        });

        // ---------- uploads ----------
        admin.MapPost("/uploads", (InitUpload req, UploadService up) => Upload(() => Results.Ok(up.Init(req.Name, req.Size))));
        admin.MapGet("/uploads/{id}", (string id, UploadService up) => Upload(() => Results.Ok(up.GetStatus(id))));
        admin.MapDelete("/uploads/{id}", (string id, UploadService up) => Upload(() => { up.Abort(id); return Results.Ok(new { ok = true }); }));
        admin.MapPost("/uploads/{id}/complete", (string id, UploadService up, DocStore store) => Upload(() =>
        {
            var doc = up.Complete(id);
            return Results.Ok(store.GetAdmin(doc.Id));
        }));
        admin.MapPut("/uploads/{id}", async (string id, long offset, HttpContext ctx, UploadService up, IOptions<AtlasOptions> o) =>
        {
            // Chunks are streamed to disk; lift Kestrel's body-size cap and slow-client timeout for this endpoint only.
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } lim) lim.MaxRequestBodySize = o.Value.MaxChunkBytes + 1;
            if (ctx.Features.Get<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
            try { return Results.Ok(await up.AppendAsync(id, offset, ctx.Request.Body, ctx.RequestAborted)); }
            catch (UploadException ex) { return Fail(ex); }
            catch (BadHttpRequestException) { return Results.Json(new { error = "That chunk is too large or was cut off." }, statusCode: 413); }
        });

        // ---------- reports, settings, stats ----------
        admin.MapGet("/reports", (HttpContext ctx, DocStore store) => Results.Ok(store.Reports(ctx.Request.Query["status"].ToString() is { Length: > 0 } s ? s : null)));
        admin.MapPost("/reports/{id:long}/status", (long id, ReportStatusRequest req, DocStore store) =>
            req.Status is "open" or "resolved" && store.SetReportStatus(id, req.Status) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Unknown report or status." }));
        admin.MapDelete("/reports/{id:long}", (long id, DocStore store) => store.DeleteReport(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

        admin.MapGet("/settings", (SiteSettings s) => Results.Ok(s.Current));
        admin.MapPut("/settings", (SettingsRequest req, SiteSettings s) =>
        {
            var title = (req.Title ?? "").Trim();
            var intro = (req.Intro ?? "").Trim();
            var note = (req.ContactNote ?? "").Trim();
            if (title.Length is < 1 or > 80) return Results.BadRequest(new { error = "The site name must be 1 to 80 characters." });
            if (intro.Length > 1000 || note.Length > 1000) return Results.BadRequest(new { error = "Intro and contact note are limited to 1000 characters." });
            return Results.Ok(s.Save(req.NoIndex, title, intro, note));
        });

        admin.MapGet("/stats", (DocStore store, DataPaths paths, IOptions<AtlasOptions> o) =>
            Results.Ok(new
            {
                counts = store.CountsByStatus(),
                openReports = store.OpenReportCount(),
                freeBytes = paths.FreeBytes(),
                maxUploadBytes = o.Value.MaxUploadBytes,
                supported = FileKinds.SupportedList,
            }));
    }

    private static IResult Withdraw(string id, string newStatus, DocStore store, DataPaths paths)
    {
        if (!DataPaths.IsSafeId(id) || store.Get(id) is not { } d) return Results.NotFound(new { error = "Not found." });
        if (newStatus == Status.Pending && d.Status != Status.Published) return Results.Conflict(new { error = "Only published items can be unpublished." });
        if (newStatus == Status.Rejected && d.Status is Status.Processing or Status.Failed) return Results.Conflict(new { error = "That item isn't ready for review yet." });
        paths.DeletePublicArtifacts(d);
        store.ClearPublic(id, newStatus);
        return Results.Ok(store.GetAdmin(id));
    }

    private static IResult Upload(Func<IResult> action)
    {
        try { return action(); }
        catch (UploadException ex) { return Fail(ex); }
    }

    private static IResult Fail(UploadException ex) => Results.Json(new { error = ex.Message, received = ex.Received }, statusCode: ex.Status);
}
