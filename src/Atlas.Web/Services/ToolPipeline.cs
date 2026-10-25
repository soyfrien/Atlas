using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Atlas.Web.Services;

/// <summary>
/// The real pipeline, built on well-known command-line tools: poppler (pdfinfo/pdftotext/pdftoppm), qpdf, ffmpeg/ffprobe and
/// tesseract. Publishing is fail-closed: if a sanitized copy can't be produced, the item stays unpublished and the admin sees why.
/// </summary>
public sealed class ToolPipeline : IMediaPipeline
{
    private const double RedactDpi = 150;
    private const int MaxRedactPages = 300;

    private readonly AtlasOptions _o;
    private readonly DataPaths _paths;
    private readonly DocStore _store;
    private readonly ILogger<ToolPipeline> _log;

    public ToolPipeline(IOptions<AtlasOptions> o, DataPaths paths, DocStore store, ILogger<ToolPipeline> log)
    {
        _o = o.Value; _paths = paths; _store = store; _log = log;
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(5, _o.ToolTimeoutSeconds));
    private static readonly Dictionary<string, string> OcrEnv = new() { ["OMP_THREAD_LIMIT"] = "1" };

    public static bool HasOcr => ProcessRunner.Has("tesseract");

    private async Task<ProcResult> RunRequiredAsync(string exe, IEnumerable<string> args, CancellationToken ct, int maxStdout = 16_000_000)
        => await ProcessRunner.RunAsync(exe, args, Timeout, ct, maxStdout)
           ?? throw new PipelineException($"The server is missing a required tool: {exe}.");

    // ------------------------------------------------------------------ ingest

    public async Task<DocStore.IngestData> IngestAsync(Doc doc, CancellationToken ct)
    {
        var orig = _paths.Original(doc);
        var sha = await Hashing.Sha256FileAsync(orig, ct);
        string text = "";
        int? pages = null;
        double? duration = null;
        var thumbPath = _paths.ThumbOriginal(doc.Id);
        bool thumb = false;
        switch (doc.Kind)
        {
            case Kinds.Pdf:
                pages = await PdfPageCountAsync(orig, ct);
                text = await PdfTextAsync(orig, pages ?? 0, ct);
                thumb = await PdfThumbAsync(orig, thumbPath, ct);
                break;
            case Kinds.Image:
                thumb = await ImageThumbAsync(orig, thumbPath, 0, ct);
                text = await OcrFileAsync(orig, ct);
                break;
            case Kinds.Video:
                duration = await DurationAsync(orig, ct);
                thumb = await ImageThumbAsync(orig, thumbPath, 1, ct) || await ImageThumbAsync(orig, thumbPath, 0, ct);
                text = await TranscribeAsync(orig, ct);
                break;
            case Kinds.Audio:
                duration = await DurationAsync(orig, ct);
                text = await TranscribeAsync(orig, ct);
                break;
            case Kinds.Text:
                text = await ReadTextFileAsync(orig, ct);
                break;
            case Kinds.Office:
                text = OfficeFiles.ExtractText(orig, _o.MaxTextChars);
                break;
        }
        return new DocStore.IngestData(sha, CleanText(text), thumb, pages, duration);
    }

    private async Task<int> PdfPageCountAsync(string pdf, CancellationToken ct)
    {
        var r = await RunRequiredAsync("pdfinfo", [pdf], ct);
        var m = Regex.Match(r.Stdout, @"^Pages:\s+(\d+)", RegexOptions.Multiline);
        if (!r.Ok || !m.Success) throw new PipelineException("This PDF couldn't be read. It may be damaged or password-protected.");
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private async Task<string> PdfTextAsync(string pdf, int pages, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("pdftotext", ["-layout", "-enc", "UTF-8", "-q", pdf, "-"], Timeout, ct, _o.MaxTextChars);
        var text = r is { Ok: true } ? r.Stdout : "";
        var perPage = pages == 0 ? 0 : text.Count(c => !char.IsWhiteSpace(c)) / (double)pages;
        // Little or no embedded text: it's probably a scan, so run OCR on the first pages.
        if (perPage < 30 && HasOcr) text = (text + "\n" + await OcrPdfAsync(pdf, ct)).Trim();
        return text;
    }

    private async Task<string> OcrPdfAsync(string pdf, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlas-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var r = await ProcessRunner.RunAsync("pdftoppm",
                ["-jpeg", "-gray", "-r", "200", "-f", "1", "-l", Math.Max(1, _o.OcrMaxPages).ToString(), pdf, Path.Combine(dir, "p")], Timeout, ct);
            if (r is not { Ok: true }) return "";
            return await OcrFilesAsync(Directory.GetFiles(dir, "p-*.jpg"), ct);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
    }

    private async Task<string> OcrFilesAsync(IEnumerable<string> files, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(PageNumberOf)) sb.AppendLine(await OcrFileAsync(f, ct));
        return sb.ToString();
    }

    private async Task<string> OcrFileAsync(string image, CancellationToken ct)
    {
        if (!HasOcr) return "";
        var r = await ProcessRunner.RunAsync("tesseract", [image, "stdout", "-l", _o.OcrLanguage], Timeout, ct, _o.MaxTextChars, OcrEnv);
        return r is { Ok: true } ? r.Stdout : "";
    }

    private async Task<bool> PdfThumbAsync(string pdf, string dest, CancellationToken ct)
    {
        var prefix = dest[..^4];
        var r = await ProcessRunner.RunAsync("pdftoppm", ["-jpeg", "-singlefile", "-f", "1", "-l", "1", "-scale-to", "480", pdf, prefix], Timeout, ct);
        return r is { Ok: true } && File.Exists(dest);
    }

    private async Task<bool> ImageThumbAsync(string src, string dest, double seekSeconds, CancellationToken ct)
    {
        if (File.Exists(dest)) File.Delete(dest);
        var args = new List<string> { "-nostdin", "-y", "-v", "error" };
        if (seekSeconds > 0) { args.Add("-ss"); args.Add(seekSeconds.ToString(CultureInfo.InvariantCulture)); }
        args.AddRange(["-i", src, "-frames:v", "1", "-vf", "scale='min(480,iw)':-2", "-q:v", "4", dest]);
        var r = await ProcessRunner.RunAsync("ffmpeg", args, Timeout, ct);
        return r is { Ok: true } && File.Exists(dest) && new FileInfo(dest).Length > 0;
    }

    private async Task<double?> DurationAsync(string src, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", src], Timeout, ct);
        return r is { Ok: true } && double.TryParse(r.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Optional hook: runs the configured command (e.g. whisper.cpp) and uses its stdout as the transcript.</summary>
    private async Task<string> TranscribeAsync(string media, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_o.TranscriptionCommand)) return "";
        try
        {
            var args = _o.TranscriptionArgs.Select(a => a.Replace("{input}", media)).ToList();
            var r = await ProcessRunner.RunAsync(_o.TranscriptionCommand, args, TimeSpan.FromMinutes(Math.Max(1, _o.TranscriptionTimeoutMinutes)), ct, _o.MaxTextChars);
            if (r is { Ok: true }) return r.Stdout;
            _log.LogWarning("Transcription command failed ({Exit}).", r?.ExitCode);
        }
        catch (PipelineException ex) { _log.LogWarning("Transcription skipped: {Message}", ex.Message); }
        return "";
    }

    private async Task<string> ReadTextFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(fs.Length, 16L << 20)];
        var n = await fs.ReadAtLeastAsync(buf, buf.Length, false, ct);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    // ------------------------------------------------------------------ publish

    public async Task<DocStore.PublishData> PublishAsync(Doc doc, CancellationToken ct)
    {
        var orig = _paths.Original(doc);
        var ext = FileKinds.PublicExt(doc.Ext);
        var redactions = Redaction.Parse(doc.Redactions);
        var tmp = Path.Combine(_paths.ItemDir(doc.Id), "publish-tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var outFile = Path.Combine(tmp, "out." + ext);
        try
        {
            // With no redactions the visible content is unchanged, so the text index can reuse the original's text.
            // With redactions the index is rebuilt from the redacted pixels only, so covered text can never be searched.
            var text = _store.GetOriginalText(doc.Id);
            int? pages = doc.PageCount;
            switch (doc.Kind)
            {
                case Kinds.Pdf when redactions.Count > 0:
                    (pages, text) = await RedactPdfAsync(orig, tmp, outFile, redactions, ct);
                    break;
                case Kinds.Pdf:
                    await CleanPdfAsync(orig, outFile, ct);
                    pages = await PdfPageCountAsync(outFile, ct);
                    break;
                case Kinds.Image:
                    await SanitizeImageAsync(orig, outFile, ext, redactions, ct);
                    if (redactions.Count > 0) text = await OcrFileAsync(outFile, ct);
                    break;
                case Kinds.Video:
                case Kinds.Audio:
                    if (redactions.Count > 0) throw new PipelineException("Redaction isn't available for audio or video.");
                    await StripMediaAsync(orig, outFile, ext, doc.Kind == Kinds.Audio, ct);
                    break;
                case Kinds.Text:
                    if (redactions.Count > 0) throw new PipelineException("Redaction isn't available for text files.");
                    File.Copy(orig, outFile, true);
                    break;
                case Kinds.Office:
                    if (redactions.Count > 0) throw new PipelineException("Redaction isn't available for Office files. Convert the document to PDF first.");
                    OfficeFiles.Sanitize(orig, outFile);
                    break;
                default:
                    throw new PipelineException("Unsupported file type.");
            }

            var info = new FileInfo(outFile);
            if (!info.Exists || info.Length == 0) throw new PipelineException("Producing the public copy failed.");

            var dest = _paths.PublicFor(doc.Id, ext);
            foreach (var old in Directory.EnumerateFiles(_paths.ItemDir(doc.Id), "public.*")) File.Delete(old);
            File.Move(outFile, dest, true);
            var pagesDir = _paths.PagesDir(doc.Id, true);
            if (Directory.Exists(pagesDir)) Directory.Delete(pagesDir, true);

            var thumbPath = _paths.ThumbPublic(doc.Id);
            if (File.Exists(thumbPath)) File.Delete(thumbPath);
            var thumb = doc.Kind switch
            {
                Kinds.Pdf => await PdfThumbAsync(dest, thumbPath, ct),
                Kinds.Image => await ImageThumbAsync(dest, thumbPath, 0, ct),
                Kinds.Video => await ImageThumbAsync(dest, thumbPath, 1, ct) || await ImageThumbAsync(dest, thumbPath, 0, ct),
                _ => false,
            };
            return new DocStore.PublishData(info.Length, await Hashing.Sha256FileAsync(dest, ct), ext, FileKinds.MimeFor(ext), CleanText(text), thumb, pages);
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best effort */ } }
    }

    /// <summary>Copies the pages into a brand-new PDF, which leaves the document info, XMP packet and other file-level metadata behind.</summary>
    private async Task CleanPdfAsync(string src, string dest, CancellationToken ct)
    {
        var r = await RunRequiredAsync("qpdf", ["--empty", "--pages", src, "--", dest], ct);
        // qpdf exits 3 for "succeeded with warnings".
        if (r.ExitCode is not (0 or 3) || !File.Exists(dest)) throw new PipelineException("The PDF couldn't be cleaned for publishing, so it was not published. It may be damaged or encrypted.");
    }

    private async Task<(int Pages, string Text)> RedactPdfAsync(string src, string tmp, string dest, List<Redaction> redactions, CancellationToken ct)
    {
        var pages = await PdfPageCountAsync(src, ct);
        if (pages > MaxRedactPages) throw new PipelineException($"Redaction supports PDFs up to {MaxRedactPages} pages.");
        if (redactions.Any(r => r.Page > pages)) throw new PipelineException("A redaction box is on a page that doesn't exist. Remove it and try again.");

        var render = await RunRequiredAsync("pdftoppm", ["-jpeg", "-jpegopt", "quality=88", "-r", RedactDpi.ToString(CultureInfo.InvariantCulture), src, Path.Combine(tmp, "pg")], ct);
        if (!render.Ok) throw new PipelineException("The PDF couldn't be rendered for redaction.");
        var rendered = Directory.GetFiles(tmp, "pg-*.jpg").OrderBy(PageNumberOf).ToList();
        if (rendered.Count != pages) throw new PipelineException("The PDF couldn't be rendered completely, so it was not published.");

        var final = new List<string>();
        for (var i = 0; i < rendered.Count; i++)
        {
            var boxes = redactions.Where(r => r.Page == i + 1).ToList();
            if (boxes.Count == 0) { final.Add(rendered[i]); continue; }
            var o = Path.Combine(tmp, $"red-{i + 1}.jpg");
            var r = await RunRequiredAsync("ffmpeg", ["-nostdin", "-y", "-v", "error", "-i", rendered[i], "-vf", DrawBoxes(boxes), "-q:v", "2", o], ct);
            if (!r.Ok || !File.Exists(o)) throw new PipelineException("Applying a redaction failed, so nothing was published.");
            final.Add(o);
        }
        PdfWriter.WriteJpegPdf(final, RedactDpi, dest);

        // Text for search comes only from what is still visible.
        var text = HasOcr ? await OcrFilesAsync(final.Take(Math.Max(1, _o.OcrMaxPages)), ct) : "";
        return (pages, text);
    }

    /// <summary>ffmpeg drawbox chain: each box is filled opaque black and padded by a hair so rounding can't leave a sliver visible.</summary>
    public static string DrawBoxes(IEnumerable<Redaction> boxes)
    {
        string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        return string.Join(",", boxes.Select(b =>
        {
            const double pad = 0.0015;
            var x = Math.Max(0, b.X - pad); var y = Math.Max(0, b.Y - pad);
            var w = Math.Min(1 - x, b.W + 2 * pad); var h = Math.Min(1 - y, b.H + 2 * pad);
            return $"drawbox=x=iw*{F(x)}:y=ih*{F(y)}:w=iw*{F(w)}:h=ih*{F(h)}:color=black@1:t=fill";
        }));
    }

    private async Task SanitizeImageAsync(string src, string dest, string ext, List<Redaction> redactions, CancellationToken ct)
    {
        if (redactions.Any(r => r.Page != 1)) throw new PipelineException("Images have a single page; remove boxes on other pages.");
        var args = new List<string> { "-nostdin", "-y", "-v", "error", "-i", src };
        if (redactions.Count > 0) { args.Add("-vf"); args.Add(DrawBoxes(redactions)); }
        args.AddRange(["-map_metadata", "-1"]);
        if (ext != "gif" || redactions.Count > 0) args.AddRange(["-frames:v", "1"]);
        if (ext is "jpg" or "jpeg") args.AddRange(["-q:v", "2"]);
        args.Add(dest);
        var r = await RunRequiredAsync("ffmpeg", args, ct);
        if (!r.Ok || !File.Exists(dest)) throw new PipelineException("The image couldn't be processed for publishing. It may be damaged or in an unusual format.");
    }

    private async Task StripMediaAsync(string src, string dest, string ext, bool audioOnly, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-y", "-v", "error", "-i", src };
        if (audioOnly) args.AddRange(["-map", "0:a", "-vn"]);
        else args.AddRange(["-map", "0:v:0?", "-map", "0:a?"]);
        args.AddRange(["-c", "copy", "-dn", "-sn", "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1",
            "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact"]);
        if (ext is "mp4" or "m4v" or "mov" or "m4a") args.AddRange(["-movflags", "+faststart"]);
        args.Add(dest);
        var r = await RunRequiredAsync("ffmpeg", args, ct);
        if (!r.Ok || !File.Exists(dest)) throw new PipelineException("The recording couldn't be processed for publishing. It may be damaged or use an unusual codec.");
    }

    // ------------------------------------------------------------------ helpers

    private static int PageNumberOf(string path)
    {
        var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\0", "").Replace('\f', '\n');
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length > _o.MaxTextChars ? text[.._o.MaxTextChars] : text;
    }
}
