using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using static Atlas.Tests.TestApp;

namespace Atlas.Tests;

public class WorkflowTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    public WorkflowTests(TestApp app) => _app = app;

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Admin_endpoints_require_login()
    {
        var anon = _app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/admin/uploads", new { name = "a.txt", size = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/api/admin/uploads/abc?offset=0", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/admin/docs/abcdefgh/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_wrong_passcode_and_accepts_right_one()
    {
        var c = _app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/login", new { passcode = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/login", new { passcode = Passcode })).StatusCode);
    }

    [Fact]
    public async Task Cross_site_writes_are_refused()
    {
        var admin = await _app.AdminAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/admin/uploads") { Content = JsonContent.Create(new { name = "a.txt", size = 3 }) };
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Pending_items_are_invisible_until_approved_then_searchable_and_never_leak_private_fields()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        const string secretNote = "private-note-zq9x";
        const string secretName = "secret-upload-name-4471";
        var body = "The quarterly zebracrossing report mentions quokkas and hedgehogs.";
        var id = await UploadAsync(admin, secretName + ".txt", Text(body));
        var pending = await WaitForStatusAsync(admin, id, "pending");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Text(body))).ToLowerInvariant(), pending.GetProperty("sha256").GetString());

        // invisible while pending
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/docs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/files/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/files/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/d/{id}")).StatusCode);
        Assert.DoesNotContain(id, await anon.GetStringAsync("/api/docs"));
        Assert.DoesNotContain(id, await anon.GetStringAsync("/api/docs?q=zebracrossing"));

        // admin can find it by its private text
        var adminSearch = await J(await admin.GetAsync("/api/admin/docs?q=quokkas"));
        Assert.Equal(1, adminSearch.GetProperty("total").GetInt32());

        var patch = await admin.PatchAsJsonAsync($"/api/admin/docs/{id}", new
        {
            title = "Quarterly Report", description = "A harmless test document", date = "2024-03-05", tags = new[] { "Finance ", "test-tag", "FINANCE" },
            verification = "partially verified", sourceNotes = secretNote,
        });
        patch.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/admin/docs/{id}/approve", null)).StatusCode);
        var pub = await WaitForStatusAsync(admin, id, "published");
        Assert.Equal(2, pub.GetProperty("tags").GetArrayLength());

        // visible and searchable now
        var list = await J(await anon.GetAsync("/api/docs?q=zebracross"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Contains("zebracrossing", list.GetProperty("items")[0].GetProperty("snippet").GetString());
        Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?tag=finance&verification=partially%20verified&from=2024-01-01&to=2024-12-31"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await J(await anon.GetAsync("/api/docs?verification=verified"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await J(await anon.GetAsync("/api/docs?to=2023-12-31"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await J(await anon.GetAsync("/api/docs?kind=text&q=zebracross"))).GetProperty("total").GetInt32());

        // nothing private anywhere in the public representation
        var json = await anon.GetStringAsync($"/api/docs/{id}");
        var listJson = await anon.GetStringAsync("/api/docs?q=quokkas");
        var page = await anon.GetStringAsync($"/d/{id}");
        foreach (var s in new[] { json, listJson, page })
        {
            Assert.DoesNotContain(secretNote, s);
            Assert.DoesNotContain(secretName, s);
            Assert.DoesNotContain("sourceNotes", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("originalName", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("createdAt", s, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Quarterly Report", page);

        // files: inline with range support, download as attachment named after the title
        var range = new HttpRequestMessage(HttpMethod.Get, $"/files/{id}");
        range.Headers.Range = new RangeHeaderValue(0, 9);
        var partial = await anon.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(10, (await partial.Content.ReadAsByteArrayAsync()).Length);
        var dl = await anon.GetAsync($"/files/{id}/download");
        Assert.Equal("attachment", dl.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("quarterly-report.txt", dl.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(Text(body), await dl.Content.ReadAsByteArrayAsync());
        Assert.Equal(pub.GetProperty("publicSha256").GetString(), (await J(await anon.GetAsync($"/api/docs/{id}"))).GetProperty("sha256").GetString());

        // unpublish -> gone again, search index cleaned
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/docs/{id}/unpublish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/docs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/files/{id}")).StatusCode);
        Assert.Equal(0, (await J(await anon.GetAsync("/api/docs?q=zebracross"))).GetProperty("total").GetInt32());

        // reject, then delete
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/docs/{id}/reject", null)).StatusCode);
        Assert.Equal("rejected", (await J(await admin.GetAsync($"/api/admin/docs/{id}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/docs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/docs/{id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.Dir, "items", id)));
    }

    [Fact]
    public async Task Upload_validates_type_size_and_offset()
    {
        var admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await admin.PostAsJsonAsync("/api/admin/uploads", new { name = "x.exe", size = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await admin.PostAsJsonAsync("/api/admin/uploads", new { name = "x.html", size = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/uploads", new { name = "x.txt", size = 0 })).StatusCode);

        var init = await J(await admin.PostAsJsonAsync("/api/admin/uploads", new { name = "x.txt", size = 10 }));
        var uid = init.GetProperty("id").GetString();
        var wrong = await admin.PutAsync($"/api/admin/uploads/{uid}?offset=5", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(0, (await J(wrong)).GetProperty("received").GetInt64());
        var tooBig = await admin.PutAsync($"/api/admin/uploads/{uid}?offset=0", new ByteArrayContent(new byte[11]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/uploads/{uid}/complete", null)).StatusCode);
        // resume: a partial upload reports how much the server has
        await admin.PutAsync($"/api/admin/uploads/{uid}?offset=0", new ByteArrayContent([1, 2, 3, 4]));
        Assert.Equal(4, (await J(await admin.GetAsync($"/api/admin/uploads/{uid}"))).GetProperty("received").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/uploads/{uid}")).StatusCode);
    }

    [Fact]
    public async Task Large_multi_chunk_upload_is_reassembled_exactly()
    {
        var admin = await _app.AdminAsync();
        var data = new byte[5_500_000];
        new Random(7).NextBytes(data);
        for (var i = 0; i < data.Length; i++) data[i] = (byte)('a' + data[i] % 26); // keep it valid text
        var id = await UploadAsync(admin, "big.txt", data, chunk: 1_000_003);
        var doc = await WaitForStatusAsync(admin, id, "pending");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), doc.GetProperty("sha256").GetString());
        Assert.Equal(data.Length, doc.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Reports_are_stored_for_the_admin_and_validated()
    {
        var admin = await _app.AdminAsync();
        var anon = _app.Anonymous();
        var id = await UploadAsync(admin, "report-me.txt", Text("some words about walruses"));
        await WaitForStatusAsync(admin, id, "pending");

        // can't report what isn't public
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync("/api/reports", new { docId = id, reason = "privacy", message = "please remove this" })).StatusCode);

        await admin.PostAsync($"/api/admin/docs/{id}/approve", null);
        await WaitForStatusAsync(admin, id, "published");
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/reports", new { docId = id, reason = "nonsense", message = "please remove this" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/reports", new { docId = id, reason = "privacy", message = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/reports", new { docId = id, reason = "privacy", message = "please remove this item", contact = "me@example.org" })).StatusCode);
        // honeypot: looks successful but stores nothing
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/reports", new { docId = id, reason = "privacy", message = "spam spam spam", website = "http://spam" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/reports")).StatusCode);
        var reports = await J(await admin.GetAsync("/api/admin/reports"));
        var mine = reports.EnumerateArray().Where(r => r.GetProperty("docId").GetString() == id).ToList();
        Assert.Single(mine);
        Assert.Equal("please remove this item", mine[0].GetProperty("message").GetString());
        var rid = mine[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/reports/{rid}/status", new { status = "resolved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/reports/{rid}")).StatusCode);
    }

    [Fact]
    public async Task Noindex_is_on_by_default_and_can_be_switched_off()
    {
        using var app = new TestApp();
        var anon = app.Anonymous();
        var admin = await app.AdminAsync();
        var home = await anon.GetAsync("/");
        Assert.Contains("noindex", string.Join(",", home.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("Disallow: /", await anon.GetStringAsync("/robots.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/sitemap.xml")).StatusCode);

        var put = await admin.PutAsJsonAsync("/api/admin/settings", new { noIndex = false, title = "My Archive", intro = "Hello", contactNote = "" });
        put.EnsureSuccessStatusCode();
        var home2 = await anon.GetAsync("/");
        Assert.False(home2.Headers.Contains("X-Robots-Tag"));
        var robots = await anon.GetStringAsync("/robots.txt");
        Assert.DoesNotContain("Disallow: /\n", robots);
        Assert.Contains("Sitemap:", robots);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/sitemap.xml")).StatusCode);
        Assert.Equal("My Archive", (await J(await anon.GetAsync("/api/config"))).GetProperty("title").GetString());
        // the admin area is never indexable
        Assert.Contains("noindex", string.Join(",", (await anon.GetAsync("/admin")).Headers.GetValues("X-Robots-Tag")));
    }

    [Fact]
    public async Task Security_headers_and_pages_are_served()
    {
        var anon = _app.Anonymous();
        var home = await anon.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", home.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/manifest.webmanifest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);
    }
}
