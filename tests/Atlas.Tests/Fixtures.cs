using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Atlas.Web.Services;

namespace Atlas.Tests;

/// <summary>A fact that is skipped (not failed) when a needed command-line tool isn't installed.</summary>
public sealed class ToolFactAttribute : FactAttribute
{
    public ToolFactAttribute(params string[] tools)
    {
        var missing = tools.Where(t => !ProcessRunner.Has(t)).ToList();
        if (missing.Count > 0) Skip = "Missing tools: " + string.Join(", ", missing);
    }
}

/// <summary>Generates harmless synthetic test files; no real content is ever used.</summary>
public static class Fixtures
{
    public static byte[] Pdf(string[] lines, string? title = null, string? author = null)
    {
        var content = new StringBuilder("BT /F1 24 Tf 72 700 Td 30 TL\n");
        foreach (var l in lines) content.Append($"({l}) Tj T*\n");
        content.Append("ET");
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        if (title is not null || author is not null) objs.Add($"<< /Title ({title}) /Author ({author}) /Creator (TestCreator) >>");
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R{(objs.Count > 5 ? " /Info 6 0 R" : "")} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static async Task<byte[]> FfmpegAsync(string outName, params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "atlas-fx-" + Guid.NewGuid().ToString("N") + "-" + outName);
        try
        {
            var r = await ProcessRunner.RunAsync("ffmpeg", ["-nostdin", "-y", "-v", "error", .. args, path], TimeSpan.FromSeconds(60), default);
            if (r is not { Ok: true }) throw new InvalidOperationException("ffmpeg failed: " + r?.Stderr);
            return await File.ReadAllBytesAsync(path);
        }
        finally { try { File.Delete(path); } catch { /* temp */ } }
    }

    public static Task<byte[]> SolidPng(string color, int w = 200, int h = 100) =>
        FfmpegAsync("x.png", "-f", "lavfi", "-i", $"color=c={color}:s={w}x{h}", "-frames:v", "1");

    public static async Task<byte[]> JpegWithExif(string secret)
    {
        var jpg = await FfmpegAsync("x.jpg", "-f", "lavfi", "-i", "testsrc=s=160x120", "-frames:v", "1");
        // TIFF header + one ASCII entry (Make) carrying the secret string, wrapped in an APP1/Exif segment.
        var text = Encoding.ASCII.GetBytes(secret + "\0");
        var tiff = new List<byte> { 0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0 };
        tiff.AddRange(BitConverter.GetBytes((uint)text.Length));
        tiff.AddRange(BitConverter.GetBytes((uint)(8 + 2 + 12 + 4)));
        tiff.AddRange([0, 0, 0, 0]);
        tiff.AddRange(text);
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var seg = new List<byte> { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF) };
        seg.AddRange(payload);
        return [jpg[0], jpg[1], .. seg, .. jpg.Skip(2)];
    }

    public static Task<byte[]> Mp4(string secretTitle) => FfmpegAsync("v.mp4", "-f", "lavfi", "-i", "testsrc=d=2:s=160x120:r=10",
        "-f", "lavfi", "-i", "sine=d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
        "-metadata", "title=" + secretTitle, "-metadata", "location=+40.7128-074.0060/", "-metadata", "comment=" + secretTitle);

    public static byte[] Docx(string body, string author)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>""");
            Add("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:ins w:id="1" w:author="{author}"><w:r><w:t>{body}</w:t></w:r></w:ins></w:p></w:body></w:document>""");
            Add("docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>{author}</dc:creator><cp:lastModifiedBy>{author}</cp:lastModifiedBy></cp:coreProperties>""");
        }
        return ms.ToArray();
    }

    public static async Task<string> RunAsync(string exe, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(60), default);
        return r is null ? "" : r.Stdout + r.Stderr;
    }

    public static async Task<string> WriteTempAsync(byte[] data, string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), "atlas-fx-" + Guid.NewGuid().ToString("N") + "." + ext);
        await File.WriteAllBytesAsync(p, data);
        return p;
    }
}
