using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Atlas.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Atlas.Tests;

public class TestApp : WebApplicationFactory<Program>
{
    public const string Passcode = "test-passcode-123";
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "atlas-test-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _extra = [];

    public TestApp() { }
    protected TestApp(Dictionary<string, string?> extra) => _extra = extra;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ATLAS_PASSCODE", Passcode);
        builder.UseSetting("Atlas:DataDir", Dir);
        builder.UseSetting("Atlas:LoginAttemptsPerMinute", "1000");
        builder.UseSetting("Atlas:RequestsPerMinute", "100000");
        builder.UseSetting("Atlas:MinFreeBytes", "0");
        foreach (var (k, v) in _extra) builder.UseSetting(k, v);
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public async Task<HttpClient> AdminAsync()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var r = await c.PostAsJsonAsync("/api/login", new { passcode = Passcode });
        r.EnsureSuccessStatusCode();
        return c;
    }

    public static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Uploads bytes through the chunked API (in pieces of <paramref name="chunk"/> bytes) and returns the new item's id.</summary>
    public static async Task<string> UploadAsync(HttpClient admin, string name, byte[] data, int chunk = 1 << 20)
    {
        var init = await admin.PostAsJsonAsync("/api/admin/uploads", new { name, size = data.Length });
        init.EnsureSuccessStatusCode();
        var uid = (await J(init)).GetProperty("id").GetString()!;
        for (var off = 0; off < data.Length; off += chunk)
        {
            var len = Math.Min(chunk, data.Length - off);
            var put = await admin.PutAsync($"/api/admin/uploads/{uid}?offset={off}", new ByteArrayContent(data, off, len));
            put.EnsureSuccessStatusCode();
        }
        var done = await admin.PostAsync($"/api/admin/uploads/{uid}/complete", null);
        done.EnsureSuccessStatusCode();
        return (await J(done)).GetProperty("id").GetString()!;
    }

    public static async Task<JsonElement> WaitForStatusAsync(HttpClient admin, string id, string status, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonElement doc = default;
        while (DateTime.UtcNow < deadline)
        {
            doc = await J(await admin.GetAsync($"/api/admin/docs/{id}"));
            if (doc.GetProperty("status").GetString() == status) return doc;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Item {id} never reached '{status}'. Last: {doc}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, true); } catch { /* ignore */ }
    }
}
