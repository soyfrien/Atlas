using System.Net;
using System.Text;
using Atlas.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Atlas.Web.Endpoints;

public sealed record ReportRequest(string? DocId, string? Reason, string? Message, string? Contact, string? Website);

public static class PublicEndpoints
{
    public static readonly string[] ReportReasons = ["copyright", "privacy", "inaccurate", "safety", "other"];

    public static void Map(WebApplication app)
    {
        var env = app.Environment;
        var pub = app.MapGroup("").RequireRateLimiting("public");

        pub.MapGet("/api/config", (SiteSettings s) =>
        {
            var c = s.Current;
            return Results.Ok(new { title = c.Title, intro = c.Intro, contactNote = c.ContactNote, reportReasons = ReportReasons, verification = Verification.All });
        });

        pub.MapGet("/api/docs", (HttpContext ctx, DocStore store) =>
        {
            var q = ctx.Request.Query;
            var query = new PublicQuery
            {
                Q = q["q"], Tag = q["tag"], Verification = q["verification"], Kind = q["kind"], From = q["from"], To = q["to"],
                Limit = int.TryParse(q["limit"], out var l) ? l : 24, Offset = int.TryParse(q["offset"], out var o) ? o : 0,
            };
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(store.SearchPublic(query));
        });

        pub.MapGet("/api/docs/{id}", (string id, HttpContext ctx, DocStore store) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return DataPaths.IsSafeId(id) && store.GetPublic(id) is { } d ? Results.Ok(d) : Results.NotFound(new { error = "Not found." });
        });

        pub.MapGet("/api/tags", (DocStore store) =>
            Results.Ok(store.PublicTags().Select(t => new { tag = t.Tag, count = t.Count })));

        pub.MapPost("/api/reports", (ReportRequest req, DocStore store) =>
        {
            // A hidden "website" field that humans never fill in: bots that do get a fake success.
            if (!string.IsNullOrEmpty(req.Website)) return Results.Ok(new { ok = true });
            var reason = (req.Reason ?? "").Trim().ToLowerInvariant();
            var message = (req.Message ?? "").Trim();
            var contact = (req.Contact ?? "").Trim();
            if (!DataPaths.IsSafeId(req.DocId) || store.GetPublic(req.DocId!) is null) return Results.NotFound(new { error = "That item isn't available." });
            if (!ReportReasons.Contains(reason)) return Results.BadRequest(new { error = "Choose a reason." });
            if (message.Length is < 5 or > 2000) return Results.BadRequest(new { error = "Please describe the problem (5 to 2000 characters)." });
            if (contact.Length > 200) return Results.BadRequest(new { error = "Contact details are too long." });
            store.AddReport(req.DocId!, reason, contact.Length == 0 ? null : contact, message);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("report");

        // ---- files: only ever the published (sanitized, redacted) copy ----
        pub.MapGet("/files/{id}", (string id, HttpContext ctx, DocStore store, DataPaths paths) => ServeFile(id, false, ctx, store, paths));
        pub.MapGet("/files/{id}/download", (string id, HttpContext ctx, DocStore store, DataPaths paths) => ServeFile(id, true, ctx, store, paths));

        pub.MapGet("/thumbs/{file}", (string file, HttpContext ctx, DocStore store, DataPaths paths) =>
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!DataPaths.IsSafeId(id) || !file.EndsWith(".jpg") || store.GetPublic(id) is not { HasThumb: true }) return Results.NotFound();
            var path = paths.ThumbPublic(id);
            if (!File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(path, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(path));
        });

        // ---- pages ----
        string Template(string name) => File.ReadAllText(Path.Combine(env.WebRootPath ?? "wwwroot", name));

        pub.MapGet("/d/{id}", (string id, HttpContext ctx, DocStore store, SiteSettings site) =>
        {
            var doc = DataPaths.IsSafeId(id) ? store.GetPublic(id) : null;
            var html = Template("doc.html");
            var title = doc is null ? "Not found" : doc.Title;
            var desc = doc is null ? "" : Trunc(doc.Description, 200);
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            html = html.Replace("{{title}}", WebUtility.HtmlEncode(title + " · " + site.Current.Title))
                       .Replace("{{description}}", WebUtility.HtmlEncode(desc))
                       .Replace("{{url}}", WebUtility.HtmlEncode($"{origin}/d/{id}"))
                       .Replace("{{image}}", doc is { HasThumb: true } ? WebUtility.HtmlEncode($"{origin}/thumbs/{id}.jpg") : "");
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8, doc is null ? 404 : 200);
        });

        app.MapGet("/admin", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(Path.Combine(env.WebRootPath ?? "wwwroot", "admin.html"), "text/html");
        });

        // ---- crawlers ----
        app.MapGet("/robots.txt", (SiteSettings site, HttpContext ctx) =>
        {
            var body = site.Current.NoIndex
                ? "User-agent: *\nDisallow: /\n"
                : $"User-agent: *\nDisallow: /admin\nDisallow: /api/\nSitemap: {ctx.Request.Scheme}://{ctx.Request.Host}/sitemap.xml\n";
            return Results.Text(body, "text/plain");
        });

        app.MapGet("/sitemap.xml", (SiteSettings site, DocStore store, HttpContext ctx) =>
        {
            if (site.Current.NoIndex) return Results.NotFound();
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
            sb.Append($"<url><loc>{origin}/</loc></url>\n");
            foreach (var (id, published) in store.PublishedIds())
                sb.Append($"<url><loc>{origin}/d/{id}</loc>{(published is null ? "" : $"<lastmod>{published[..10]}</lastmod>")}</url>\n");
            sb.Append("</urlset>");
            return Results.Text(sb.ToString(), "application/xml");
        });
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    private static IResult ServeFile(string id, bool download, HttpContext ctx, DocStore store, DataPaths paths)
    {
        if (!DataPaths.IsSafeId(id)) return Results.NotFound();
        var doc = store.Get(id);
        if (doc is not { Status: Status.Published } || doc.PublicExt is null) return Results.NotFound();
        var path = paths.Public(doc);
        if (!File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "no-cache";
        var mime = FileKinds.MimeFor(doc.PublicExt);
        // Office files never open inline; everything else can be shown in the browser unless a download was asked for.
        var attachment = download || doc.Kind == Kinds.Office;
        return Results.File(path, mime, fileDownloadName: attachment ? DownloadName(doc) : null, enableRangeProcessing: true,
            lastModified: File.GetLastWriteTimeUtc(path), entityTag: new EntityTagHeaderValue($"\"{doc.PublicSha256}\""));
    }

    /// <summary>The public file name comes from the title, never from the original upload name.</summary>
    public static string DownloadName(Doc d)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(d.Title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        if (slug.Length == 0) slug = "item";
        if (slug.Length > 80) slug = slug[..80].Trim('-');
        return slug + "." + (d.PublicExt ?? d.Ext);
    }
}
