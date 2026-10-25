using System.Text.RegularExpressions;

namespace Atlas.Web.Services;

public static class FtsQuery
{
    private static readonly Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// Turns free text into a safe FTS5 expression: every word becomes a quoted prefix term and all must match.
    /// Returns null when the text has no searchable words. FTS5 operators typed by the user are neutralised.
    /// </summary>
    public static string? Build(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = Token.Matches(text).Select(m => m.Value).Take(12).ToList();
        return tokens.Count == 0 ? null : string.Join(" ", tokens.Select(t => "\"" + t + "\"*"));
    }
}
