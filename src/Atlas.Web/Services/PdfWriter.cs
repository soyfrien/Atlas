using System.Globalization;
using System.Text;

namespace Atlas.Web.Services;

/// <summary>
/// Writes a PDF whose pages are nothing but JPEG images. Used for redaction: the output contains only pixels, so nothing
/// that was covered by a black box (text, vectors, hidden layers, metadata) can exist in it.
/// </summary>
public static class PdfWriter
{
    public sealed record JpegInfo(int Width, int Height, int Components);

    public static JpegInfo ReadJpegInfo(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8) throw new PipelineException("A rendered page was not a JPEG.");
        while (true)
        {
            int b;
            do { b = fs.ReadByte(); if (b < 0) throw new PipelineException("A rendered page was truncated."); } while (b != 0xFF);
            int marker;
            do { marker = fs.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) throw new PipelineException("A rendered page was truncated.");
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            var len = (fs.ReadByte() << 8) | fs.ReadByte();
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                fs.ReadByte(); // precision
                var h = (fs.ReadByte() << 8) | fs.ReadByte();
                var w = (fs.ReadByte() << 8) | fs.ReadByte();
                var comps = fs.ReadByte();
                return new JpegInfo(w, h, comps);
            }
            fs.Seek(len - 2, SeekOrigin.Current);
        }
    }

    /// <summary>Builds a PDF from page images that were rendered at <paramref name="dpi"/> so pages keep their physical size.</summary>
    public static void WriteJpegPdf(IReadOnlyList<string> jpegPaths, double dpi, string outPath)
    {
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var offsets = new List<long>();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void BeginObj(int n) { offsets.Add(fs.Position); Write($"{n} 0 obj\n"); }
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        Write("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        // object layout: 1 catalog, 2 pages, then per page: page, image, content
        var pageObjs = Enumerable.Range(0, jpegPaths.Count).Select(i => 3 + i * 3).ToList();
        BeginObj(1); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2);
        Write($"<< /Type /Pages /Count {jpegPaths.Count} /Kids [{string.Join(" ", pageObjs.Select(o => $"{o} 0 R"))}] >>\nendobj\n");
        for (var i = 0; i < jpegPaths.Count; i++)
        {
            var info = ReadJpegInfo(jpegPaths[i]);
            var wpt = info.Width * 72.0 / dpi;
            var hpt = info.Height * 72.0 / dpi;
            var o = pageObjs[i];
            BeginObj(o);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(wpt)} {F(hpt)}] /Resources << /XObject << /Im0 {o + 1} 0 R >> >> /Contents {o + 2} 0 R >>\nendobj\n");
            BeginObj(o + 1);
            var cs = info.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            var len = new FileInfo(jpegPaths[i]).Length;
            Write($"<< /Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} /ColorSpace {cs} /BitsPerComponent 8 /Filter /DCTDecode /Length {len} >>\nstream\n");
            using (var src = File.OpenRead(jpegPaths[i])) src.CopyTo(fs);
            Write("\nendstream\nendobj\n");
            BeginObj(o + 2);
            var content = $"q {F(wpt)} 0 0 {F(hpt)} 0 0 cm /Im0 Do Q";
            Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }
        var xref = fs.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
