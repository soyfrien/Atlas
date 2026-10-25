using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Web.Services;

public static class Status
{
    public const string Processing = "processing", Pending = "pending", Publishing = "publishing",
        Published = "published", Rejected = "rejected", Failed = "failed";
}

public static class Verification
{
    public static readonly string[] All = ["unverified", "partially verified", "verified"];
    public static bool IsValid(string? v) => v is not null && All.Contains(v);
}

/// <summary>Full database row. Includes private fields; never serialize this to a public response.</summary>
public class Doc
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? DocDate { get; set; }
    public string Kind { get; set; } = "";
    public string Ext { get; set; } = "";
    public string Mime { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string Status { get; set; } = Services.Status.Processing;
    public string Verification { get; set; } = "unverified";
    public string SourceNotes { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? PublishedAt { get; set; }
    public string UpdatedAt { get; set; } = "";
    public long? PublicSize { get; set; }
    public string? PublicSha256 { get; set; }
    public string? PublicExt { get; set; }
    public int? PageCount { get; set; }
    public double? DurationSec { get; set; }
    public string Redactions { get; set; } = "[]";
    public string? Error { get; set; }
    public bool HasThumbOriginal { get; set; }
    public bool HasThumbPublic { get; set; }
}

public sealed record Redaction(int Page, double X, double Y, double W, double H)
{
    public static List<Redaction> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Redaction>>(json, Json.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string? Validate(IReadOnlyList<Redaction>? list)
    {
        if (list is null) return null;
        if (list.Count > 500) return "Too many redaction boxes (max 500).";
        foreach (var r in list)
        {
            if (r.Page < 1 || r.Page > 100_000) return "Redaction page out of range.";
            if (!(r.X >= 0 && r.Y >= 0 && r.W > 0 && r.H > 0 && r.X + r.W <= 1.0001 && r.Y + r.H <= 1.0001))
                return "Redaction boxes must lie inside the page (values between 0 and 1).";
        }
        return null;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public static class Tags
{
    private static readonly Regex Bad = new(@"[^\p{L}\p{N}\-_. ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (var t in tags ?? [])
        {
            var n = Spaces.Replace(Bad.Replace(t ?? "", " "), " ").Trim().ToLowerInvariant();
            if (n.Length == 0) continue;
            if (n.Length > 40) n = n[..40].Trim();
            if (!result.Contains(n)) result.Add(n);
            if (result.Count >= 20) break;
        }
        return result;
    }
}

/// <summary>What the public may see. There is deliberately no source notes, original file name, upload time or status here.</summary>
public sealed record PublicDoc(
    string Id, string Title, string Description, List<string> Tags, string? Date, string Kind, string Verification,
    long? SizeBytes, string? Sha256, string? PublishedAt, int? PageCount, double? DurationSec, string Mime, string Ext,
    bool HasThumb, string? Snippet);

/// <summary>Everything the admin sees, including private fields.</summary>
public sealed record AdminDoc(
    string Id, string Title, string Description, List<string> Tags, string? Date, string Kind, string Ext, string Mime,
    string OriginalName, long SizeBytes, string? Sha256, string Status, string Verification, string SourceNotes,
    string CreatedAt, string? PublishedAt, string UpdatedAt, long? PublicSize, string? PublicSha256, string? PublicExt,
    int? PageCount, double? DurationSec, List<Redaction> Redactions, string? Error, bool HasThumbOriginal, bool HasThumbPublic,
    bool CanRedact, string? Snippet);

public sealed record Report(long Id, string DocId, string DocTitle, string Reason, string? Contact, string Message, string CreatedAt, string Status);

public sealed record PagedResult<T>(List<T> Items, int Total, int Offset, int Limit);
