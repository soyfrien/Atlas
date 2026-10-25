namespace Atlas.Web.Services;

public static class Kinds
{
    public const string Pdf = "pdf", Image = "image", Video = "video", Audio = "audio", Text = "text", Office = "office";
}

public static class FileKinds
{
    private static readonly Dictionary<string, (string Kind, string Mime)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = (Kinds.Pdf, "application/pdf"),
        ["jpg"] = (Kinds.Image, "image/jpeg"), ["jpeg"] = (Kinds.Image, "image/jpeg"),
        ["png"] = (Kinds.Image, "image/png"), ["gif"] = (Kinds.Image, "image/gif"), ["webp"] = (Kinds.Image, "image/webp"),
        ["bmp"] = (Kinds.Image, "image/bmp"), ["tif"] = (Kinds.Image, "image/tiff"), ["tiff"] = (Kinds.Image, "image/tiff"),
        ["mp4"] = (Kinds.Video, "video/mp4"), ["m4v"] = (Kinds.Video, "video/mp4"), ["mov"] = (Kinds.Video, "video/quicktime"),
        ["webm"] = (Kinds.Video, "video/webm"), ["mkv"] = (Kinds.Video, "video/x-matroska"),
        ["mp3"] = (Kinds.Audio, "audio/mpeg"), ["m4a"] = (Kinds.Audio, "audio/mp4"), ["wav"] = (Kinds.Audio, "audio/wav"),
        ["ogg"] = (Kinds.Audio, "audio/ogg"), ["oga"] = (Kinds.Audio, "audio/ogg"), ["opus"] = (Kinds.Audio, "audio/ogg"),
        ["flac"] = (Kinds.Audio, "audio/flac"), ["aac"] = (Kinds.Audio, "audio/aac"),
        ["txt"] = (Kinds.Text, "text/plain; charset=utf-8"), ["md"] = (Kinds.Text, "text/plain; charset=utf-8"),
        ["csv"] = (Kinds.Text, "text/plain; charset=utf-8"), ["json"] = (Kinds.Text, "text/plain; charset=utf-8"),
        ["log"] = (Kinds.Text, "text/plain; charset=utf-8"),
        ["docx"] = (Kinds.Office, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        ["xlsx"] = (Kinds.Office, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ["pptx"] = (Kinds.Office, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        ["odt"] = (Kinds.Office, "application/vnd.oasis.opendocument.text"),
        ["ods"] = (Kinds.Office, "application/vnd.oasis.opendocument.spreadsheet"),
        ["odp"] = (Kinds.Office, "application/vnd.oasis.opendocument.presentation"),
    };

    public static string SupportedList => string.Join(", ", Map.Keys.Order());

    public static string ExtOf(string fileName) => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

    public static bool TryGet(string ext, out string kind, out string mime)
    {
        if (Map.TryGetValue(ext, out var v)) { kind = v.Kind; mime = v.Mime; return true; }
        kind = mime = "";
        return false;
    }

    /// <summary>The extension of the public copy: formats browsers can't show are converted to PNG when published.</summary>
    public static string PublicExt(string ext) => ext is "bmp" or "tif" or "tiff" ? "png" : ext;

    public static string MimeFor(string ext) => Map.TryGetValue(ext, out var v) ? v.Mime : "application/octet-stream";
}
