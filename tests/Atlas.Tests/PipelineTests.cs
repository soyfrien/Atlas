using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using static Atlas.Tests.TestApp;

namespace Atlas.Tests;

public class PipelineTests : IClassFixture<PipelineTests.App>
{
    public sealed class App : TestApp
    {
        public App() : base(new Dictionary<string, string?>
        {
            ["Atlas:TranscriptionCommand"] = "/bin/echo",
            ["Atlas:TranscriptionArgs:0"] = "transcript-xylophone",
        }) { }
    }

    private readonly App _app;
    public PipelineTests(App app) => _app = app;

    private async Task<string> PublishAsync(HttpClient admin, string id)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/admin/docs/{id}/approve", null)).StatusCode);
        await WaitForStatusAsync(admin, id, "published");
        return id;
    }

    [ToolFact("pdfinfo", "pdftotext", "pdftoppm", "qpdf")]
    public async Task Pdf_is_indexed_previewed_and_published_without_file_metadata()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var pdf = Fixtures.Pdf(["Harmless platypus memo", "second line"], title: "SensitiveTitleQ", author: "SensitiveAuthorQ");
        var id = await UploadAsync(admin, "memo.pdf", pdf);
        var pending = await WaitForStatusAsync(admin, id, "pending");
        Assert.Equal(1, pending.GetProperty("pageCount").GetInt32());
        Assert.True(pending.GetProperty("hasThumbOriginal").GetBoolean());
        Assert.Equal(1, (await J(await admin.GetAsync("/api/admin/docs?q=platypus"))).GetProperty("total").GetInt32());

        // admin preview of the original page
        var adminPage = await admin.GetAsync($"/api/admin/docs/{id}/pages/1.jpg");
        Assert.Equal("image/jpeg", adminPage.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/pages/{id}/1.jpg")).StatusCode);   // not public yet
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/admin/docs/{id}/pages/1.jpg")).StatusCode);

        await PublishAsync(admin, id);
        Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?q=platypus"))).GetProperty("total").GetInt32());
        Assert.Equal("image/jpeg", (await anon.GetAsync($"/pages/{id}/1.jpg")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/jpeg", (await anon.GetAsync($"/thumbs/{id}.jpg")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/pages/{id}/2.jpg")).StatusCode);

        var publicBytes = await anon.GetByteArrayAsync($"/files/{id}");
        var latin = Encoding.Latin1.GetString(publicBytes);
        Assert.DoesNotContain("SensitiveTitleQ", latin);
        Assert.DoesNotContain("SensitiveAuthorQ", latin);
        Assert.DoesNotContain("TestCreator", latin);
        var tmp = await Fixtures.WriteTempAsync(publicBytes, "pdf");
        var info = await Fixtures.RunAsync("pdfinfo", tmp);
        Assert.DoesNotContain("SensitiveTitleQ", info);
        Assert.Contains("platypus", await Fixtures.RunAsync("pdftotext", tmp, "-"));
        File.Delete(tmp);

        // the original is still intact for the admin
        Assert.Contains("SensitiveTitleQ", Encoding.Latin1.GetString(await admin.GetByteArrayAsync($"/api/admin/docs/{id}/original")));
    }

    [ToolFact("pdfinfo", "pdftotext", "pdftoppm", "ffmpeg")]
    public async Task Pdf_redaction_is_burned_in_and_removes_the_text_from_the_public_copy_and_search()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var pdf = Fixtures.Pdf(["CLASSIFIEDTOKEN is here", "ordinary narwhal sentence"]);
        var id = await UploadAsync(admin, "redact.pdf", pdf);
        await WaitForStatusAsync(admin, id, "pending");
        Assert.Equal(1, (await J(await admin.GetAsync("/api/admin/docs?q=classifiedtoken"))).GetProperty("total").GetInt32());

        // the first text line spans roughly 9.5%-12.4% from the top of the page; the second line starts at about 13.3%
        var patch = await admin.PatchAsJsonAsync($"/api/admin/docs/{id}", new { redactions = new[] { new { page = 1, x = 0.05, y = 0.088, w = 0.9, h = 0.042 } } });
        patch.EnsureSuccessStatusCode();
        await PublishAsync(admin, id);

        var bytes = await anon.GetByteArrayAsync($"/files/{id}");
        var tmp = await Fixtures.WriteTempAsync(bytes, "pdf");
        var text = await Fixtures.RunAsync("pdftotext", tmp, "-");
        Assert.DoesNotContain("CLASSIFIEDTOKEN", text);               // there is no text layer left at all
        Assert.DoesNotContain("CLASSIFIEDTOKEN", Encoding.Latin1.GetString(bytes));
        Assert.Equal("", text.Trim());                                  // whole document is flattened to images
        File.Delete(tmp);

        // public search can't find the covered word; the admin index still can
        Assert.Equal(0, (await J(await anon.GetAsync("/api/docs?q=classifiedtoken"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await J(await admin.GetAsync("/api/admin/docs?q=classifiedtoken"))).GetProperty("total").GetInt32());
        if (Atlas.Web.Services.ToolPipeline.HasOcr)
            Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?q=narwhal"))).GetProperty("total").GetInt32());

        // the rendered public page really has a black band where the line was
        var page = await anon.GetByteArrayAsync($"/pages/{id}/1.jpg");
        Assert.True(await DarkFractionAsync(page, 0.2, 0.095, 0.6, 0.025) > 0.95, "the redaction band should be solid black");
        Assert.True(await DarkFractionAsync(page, 0.2, 0.30, 0.6, 0.02) < 0.5, "the rest of the page should not be black");

        // unpublishing a redacted item and changing its boxes requires going back through review
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/docs/{id}", new { redactions = Array.Empty<object>() })).StatusCode);
    }

    /// <summary>Fraction of pixels in the given (fractional) region of a JPEG that are nearly black.</summary>
    private static async Task<double> DarkFractionAsync(byte[] jpeg, double fx, double fy, double fw, double fh)
    {
        var path = await Fixtures.WriteTempAsync(jpeg, "jpg");
        try
        {
            var probe = await Fixtures.RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path);
            var wh = probe.Trim().Split(',');
            int w = int.Parse(wh[0]), h = int.Parse(wh[1]);
            var crop = $"crop={(int)(w * fw)}:{(int)(h * fh)}:{(int)(w * fx)}:{(int)(h * fy)}";
            var raw = Path.ChangeExtension(path, "gray");
            await Fixtures.RunAsync("ffmpeg", "-nostdin", "-y", "-v", "error", "-i", path, "-vf", crop, "-f", "rawvideo", "-pix_fmt", "gray", raw);
            var px = await File.ReadAllBytesAsync(raw);
            File.Delete(raw);
            return px.Count(p => p < 40) / (double)px.Length;
        }
        finally { File.Delete(path); }
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task Image_metadata_is_stripped_and_redaction_boxes_are_burned_in()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var jpg = await Fixtures.JpegWithExif("SECRET-CAMERA-MODEL");
        Assert.Contains("SECRET-CAMERA-MODEL", Encoding.Latin1.GetString(jpg));
        var id = await UploadAsync(admin, "photo.jpg", jpg);
        await WaitForStatusAsync(admin, id, "pending");
        await PublishAsync(admin, id);
        var pub = await anon.GetByteArrayAsync($"/files/{id}");
        Assert.DoesNotContain("SECRET-CAMERA-MODEL", Encoding.Latin1.GetString(pub));
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(pub));
        Assert.Equal("image/jpeg", (await anon.GetAsync($"/thumbs/{id}.jpg")).Content.Headers.ContentType?.MediaType);

        // redaction on a lossless image: left half becomes black, right half stays red
        var png = await Fixtures.SolidPng("red");
        var id2 = await UploadAsync(admin, "solid.png", png);
        await WaitForStatusAsync(admin, id2, "pending");
        (await admin.PatchAsJsonAsync($"/api/admin/docs/{id2}", new { redactions = new[] { new { page = 1, x = 0.0, y = 0.0, w = 0.5, h = 1.0 } } })).EnsureSuccessStatusCode();
        await PublishAsync(admin, id2);
        var outPng = await anon.GetByteArrayAsync($"/files/{id2}");
        var path = await Fixtures.WriteTempAsync(outPng, "png");
        var raw = Path.ChangeExtension(path, "rgb");
        await Fixtures.RunAsync("ffmpeg", "-nostdin", "-y", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
        var px = await File.ReadAllBytesAsync(raw);
        File.Delete(path); File.Delete(raw);
        byte[] At(int x, int y) => px[((y * 200 + x) * 3)..((y * 200 + x) * 3 + 3)];
        Assert.Equal(new byte[] { 0, 0, 0 }, At(20, 50));
        Assert.True(At(180, 50)[0] > 235 && At(180, 50)[1] < 20 && At(180, 50)[2] < 20, "unredacted area stays red");
    }

    [ToolFact("ffmpeg", "ffprobe")]
    public async Task Video_is_probed_posterized_stripped_range_served_and_transcript_hook_is_indexed()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var mp4 = await Fixtures.Mp4("SECRETVIDEOTITLE");
        Assert.Contains("SECRETVIDEOTITLE", Encoding.Latin1.GetString(mp4));
        var id = await UploadAsync(admin, "clip.mp4", mp4);
        var pending = await WaitForStatusAsync(admin, id, "pending");
        Assert.InRange(pending.GetProperty("durationSec").GetDouble(), 1.5, 2.5);
        Assert.True(pending.GetProperty("hasThumbOriginal").GetBoolean());
        // transcription hook output is searchable by the admin before publishing, and by everyone after
        Assert.Equal(1, (await J(await admin.GetAsync("/api/admin/docs?q=xylophone"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await J(await anon.GetAsync("/api/docs?q=xylophone"))).GetProperty("total").GetInt32());

        // redaction is not offered for video
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/admin/docs/{id}", new { redactions = new[] { new { page = 1, x = 0.1, y = 0.1, w = 0.2, h = 0.2 } } })).StatusCode);

        await PublishAsync(admin, id);
        Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?q=xylophone"))).GetProperty("total").GetInt32());
        var pub = await anon.GetByteArrayAsync($"/files/{id}");
        Assert.DoesNotContain("SECRETVIDEOTITLE", Encoding.Latin1.GetString(pub));
        var path = await Fixtures.WriteTempAsync(pub, "mp4");
        var tags = await Fixtures.RunAsync("ffprobe", "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "default=nw=1", path);
        Assert.DoesNotContain("SECRETVIDEOTITLE", tags);
        Assert.DoesNotContain("location", tags);
        File.Delete(path);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/files/{id}");
        req.Headers.Range = new RangeHeaderValue(100, 599);
        var part = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(500, (await part.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("video/mp4", part.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/jpeg", (await anon.GetAsync($"/thumbs/{id}.jpg")).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Office_documents_are_text_indexed_and_scrubbed_of_author_metadata()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var id = await UploadAsync(admin, "letter.docx", Fixtures.Docx("The ocelot letter body", "Hidden Author Name"));
        await WaitForStatusAsync(admin, id, "pending");
        Assert.Equal(1, (await J(await admin.GetAsync("/api/admin/docs?q=ocelot"))).GetProperty("total").GetInt32());
        await PublishAsync(admin, id);
        Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?q=ocelot"))).GetProperty("total").GetInt32());

        var res = await anon.GetAsync($"/files/{id}");
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);   // office files never open inline
        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            Assert.DoesNotContain("Hidden Author Name", await r.ReadToEndAsync());
        }
        using var doc = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        Assert.Contains("ocelot", await doc.ReadToEndAsync());
    }

    [ToolFact("pdfinfo")]
    public async Task A_broken_file_fails_with_a_clear_message_instead_of_being_published()
    {
        var admin = await _app.AdminAsync();
        var id = await UploadAsync(admin, "broken.pdf", Encoding.ASCII.GetBytes("this is not a pdf at all"));
        var failed = await WaitForStatusAsync(admin, id, "failed");
        Assert.Contains("couldn't be read", failed.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/docs/{id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Text_files_are_served_as_plain_text_never_as_html()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var id = await UploadAsync(admin, "note.txt", Encoding.UTF8.GetBytes("<script>alert(1)</script> hello"));
        await WaitForStatusAsync(admin, id, "pending");
        await PublishAsync(admin, id);
        var res = await anon.GetAsync($"/files/{id}");
        Assert.StartsWith("text/plain", res.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public void DrawBoxes_builds_padded_opaque_boxes()
    {
        var f = Atlas.Web.Services.ToolPipeline.DrawBoxes([new Atlas.Web.Services.Redaction(1, 0.1, 0.2, 0.3, 0.4), new Atlas.Web.Services.Redaction(1, 0, 0, 1, 1)]);
        Assert.Contains("drawbox=x=iw*0.0985:y=ih*0.1985:w=iw*0.303:h=ih*0.403:color=black@1:t=fill", f);
        Assert.Equal(2, f.Split(',').Length);
        Assert.Contains("x=iw*0:y=ih*0:w=iw*1:h=ih*1", f);
    }
}
