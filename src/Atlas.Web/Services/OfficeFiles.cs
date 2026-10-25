using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Web.Services;

/// <summary>Text extraction and metadata scrubbing for OOXML (docx/xlsx/pptx) and OpenDocument (odt/ods/odp) files.</summary>
public static class OfficeFiles
{
    private const long MaxEntryBytes = 40L << 20;

    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC");
    private static readonly byte[] TinyJpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD0CiiigD//2Q==");

    private static bool IsTextPart(string name) =>
        name is "word/document.xml" or "xl/sharedStrings.xml" or "content.xml"
        || Regex.IsMatch(name, @"^ppt/slides/slide\d+\.xml$") || Regex.IsMatch(name, @"^word/(header|footer)\d*\.xml$");

    public static string ExtractText(string path, int maxChars)
    {
        var sb = new StringBuilder();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries.Where(e => IsTextPart(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                if (e.Length > MaxEntryBytes) continue;
                using var r = new StreamReader(e.Open(), Encoding.UTF8);
                var xml = r.ReadToEnd();
                xml = Regex.Replace(xml, @"</(w:p|a:p|text:p|text:h|si|row|table:table-row)>", "\n");
                xml = Regex.Replace(xml, @"<[^>]+>", " ");
                sb.AppendLine(WebUtility.HtmlDecode(xml));
                if (sb.Length >= maxChars) break;
            }
        }
        catch (InvalidDataException) { throw new PipelineException("That file isn't a valid Office/OpenDocument file."); }
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }

    /// <summary>
    /// Copies the file, blanking document properties (author, last-modified-by, company, dates), the embedded thumbnail,
    /// custom properties and tracked-change/comment author names. Best effort: see the README for what is not covered.
    /// </summary>
    public static void Sanitize(string source, string dest)
    {
        try
        {
            using var inZip = ZipFile.OpenRead(source);
            using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write);
            using var outZip = new ZipArchive(outFs, ZipArchiveMode.Create);
            foreach (var e in inZip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var name = e.FullName;
                byte[]? replacement = name switch
                {
                    "docProps/core.xml" => Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"></cp:coreProperties>"""),
                    "docProps/app.xml" => Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"></Properties>"""),
                    "docProps/custom.xml" => Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties"></Properties>"""),
                    "word/people.xml" => Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w15:people xmlns:w15="http://schemas.microsoft.com/office/word/2012/wordml"></w15:people>"""),
                    "meta.xml" => Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?><office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" office:version="1.2"><office:meta/></office:document-meta>"""),
                    _ when Regex.IsMatch(name, @"(^|/)thumbnail\.(jpe?g)$", RegexOptions.IgnoreCase) => TinyJpeg,
                    _ when Regex.IsMatch(name, @"(^|/)thumbnail\.png$", RegexOptions.IgnoreCase) => TinyPng,
                    _ => null,
                };
                var entry = outZip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var os = entry.Open();
                if (replacement is not null) { os.Write(replacement); continue; }
                using var inS = e.Open();
                if (Regex.IsMatch(name, @"^word/[^/]+\.xml$") && e.Length <= MaxEntryBytes)
                {
                    using var r = new StreamReader(inS, Encoding.UTF8);
                    var xml = r.ReadToEnd();
                    xml = Regex.Replace(xml, @"(w:author|w:initials)=""[^""]*""", "$1=\"\"");
                    os.Write(Encoding.UTF8.GetBytes(xml));
                }
                else inS.CopyTo(os);
            }
        }
        catch (InvalidDataException) { throw new PipelineException("That file isn't a valid Office/OpenDocument file."); }
    }
}
