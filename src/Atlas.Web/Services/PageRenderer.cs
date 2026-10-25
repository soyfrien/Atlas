using Microsoft.Extensions.Options;

namespace Atlas.Web.Services;

/// <summary>Renders PDF pages to JPEG on demand (cached on disk) so any browser, including iPhone Safari, can read a PDF page by page.</summary>
public sealed class PageRenderer
{
    private readonly DataPaths _paths;
    private readonly AtlasOptions _o;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public PageRenderer(DataPaths paths, IOptions<AtlasOptions> o) { _paths = paths; _o = o.Value; }

    /// <summary>Returns the cached JPEG for a page, rendering it if needed. Null when the page or file doesn't exist.</summary>
    public async Task<string?> RenderAsync(Doc doc, bool publicCopy, int page, CancellationToken ct)
    {
        if (doc.Kind != Kinds.Pdf || page < 1 || page > (doc.PageCount ?? 0)) return null;
        var pdf = publicCopy ? _paths.Public(doc) : _paths.Original(doc);
        if (!File.Exists(pdf)) return null;
        var dir = _paths.PagesDir(doc.Id, publicCopy);
        var jpg = Path.Combine(dir, page + ".jpg");
        if (File.Exists(jpg)) return jpg;
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(jpg)) return jpg;
            Directory.CreateDirectory(dir);
            var r = await ProcessRunner.RunAsync("pdftoppm",
                ["-jpeg", "-jpegopt", "quality=82", "-f", page.ToString(), "-l", page.ToString(), "-scale-to-x", "1100", "-scale-to-y", "-1", "-singlefile", pdf, Path.Combine(dir, page.ToString())],
                TimeSpan.FromSeconds(Math.Max(5, _o.ToolTimeoutSeconds)), ct);
            return r is { Ok: true } && File.Exists(jpg) ? jpg : null;
        }
        finally { _gate.Release(); }
    }
}
