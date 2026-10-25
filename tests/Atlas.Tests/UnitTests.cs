using Atlas.Web.Services;

namespace Atlas.Tests;

public class UnitTests
{
    [Theory]
    [InlineData("hello world", "\"hello\"* \"world\"*")]
    [InlineData("  ", null)]
    [InlineData("\"; DROP TABLE x; --", "\"DROP\"* \"TABLE\"* \"x\"*")]
    [InlineData("a OR b NEAR(c)", "\"a\"* \"OR\"* \"b\"* \"NEAR\"* \"c\"*")]
    [InlineData("***", null)]
    public void FtsQuery_is_safe(string input, string? expected) => Assert.Equal(expected, FtsQuery.Build(input));

    [Fact]
    public void Tags_are_normalized_deduplicated_and_capped()
    {
        Assert.Equal(["finance", "a b"], Tags.Normalize([" Finance ", "FINANCE", "a   b", ""]));
        Assert.Equal(["script"], Tags.Normalize(["<script>"]));
        Assert.Equal(20, Tags.Normalize(Enumerable.Range(0, 50).Select(i => "t" + i)).Count);
    }

    [Theory]
    [InlineData("report.PDF", "pdf")]
    [InlineData("a.b.jpeg", "jpeg")]
    public void Extensions_are_lowercased(string name, string ext) => Assert.Equal(ext, FileKinds.ExtOf(name));

    [Fact]
    public void Unsupported_types_are_rejected_including_active_content()
    {
        foreach (var e in new[] { "exe", "html", "htm", "svg", "js", "php", "sh", "doc", "zip" })
            Assert.False(FileKinds.TryGet(e, out _, out _), e);
        Assert.True(FileKinds.TryGet("mp4", out var kind, out _));
        Assert.Equal("video", kind);
    }

    [Fact]
    public void Redaction_boxes_are_validated()
    {
        Assert.Null(Redaction.Validate([new Redaction(1, 0.1, 0.1, 0.2, 0.2)]));
        Assert.NotNull(Redaction.Validate([new Redaction(1, 0.9, 0.1, 0.2, 0.2)]));
        Assert.NotNull(Redaction.Validate([new Redaction(0, 0.1, 0.1, 0.2, 0.2)]));
        Assert.NotNull(Redaction.Validate([new Redaction(1, 0.1, 0.1, 0, 0.2)]));
    }

    [Theory]
    [InlineData("passcode", "passcode", true)]
    [InlineData("passcode", "Passcode", false)]
    [InlineData("passcode", "", false)]
    public void Passcode_comparison(string expected, string given, bool ok) => Assert.Equal(ok, new PasscodeService(expected).Verify(given));

    [Fact]
    public void Passcode_not_configured_means_nobody_gets_in() => Assert.False(new PasscodeService(null).Verify("anything"));
}
