namespace Atlas.Web.Services;

public sealed class AtlasOptions
{
    public const string Section = "Atlas";

    /// <summary>Admin passcode. Comes from the ATLAS_PASSCODE environment variable; never put it in a file in the repo.</summary>
    public string? Passcode { get; set; }

    /// <summary>Root folder for the database, uploads, originals and public copies.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Largest single upload accepted (default 6 GB).</summary>
    public long MaxUploadBytes { get; set; } = 6L << 30;

    /// <summary>Uploads are refused when less than this much disk would remain (default 2 GB).</summary>
    public long MinFreeBytes { get; set; } = 2L << 30;

    /// <summary>Largest single upload chunk the server accepts (default 64 MB).</summary>
    public long MaxChunkBytes { get; set; } = 64L << 20;

    public int LoginAttemptsPerMinute { get; set; } = 5;
    public int RequestsPerMinute { get; set; } = 600;
    public int ReportsPerHour { get; set; } = 5;

    /// <summary>OCR at most this many pages of a scanned PDF (OCR is slow on small VMs).</summary>
    public int OcrMaxPages { get; set; } = 60;
    public string OcrLanguage { get; set; } = "eng";

    /// <summary>Largest amount of extracted text kept per item, in characters.</summary>
    public int MaxTextChars { get; set; } = 4_000_000;

    /// <summary>
    /// Optional transcription hook for audio/video. Executable plus arguments; "{input}" is replaced by the media file
    /// and the command must print the transcript to stdout. Empty = transcription disabled.
    /// </summary>
    public string? TranscriptionCommand { get; set; }
    public string[] TranscriptionArgs { get; set; } = [];
    public int TranscriptionTimeoutMinutes { get; set; } = 120;

    /// <summary>Seconds a tool (ffmpeg, qpdf, ...) may run before it is killed, per call.</summary>
    public int ToolTimeoutSeconds { get; set; } = 3600;

    public string ResolvedDataDir => Path.GetFullPath(DataDir);
}
