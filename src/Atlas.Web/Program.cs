using System.Security.Claims;
using System.Threading.RateLimiting;
using Atlas.Web.Endpoints;
using Atlas.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AtlasOptions>()
    .Bind(builder.Configuration.GetSection(AtlasOptions.Section))
    // ATLAS_PASSCODE comes from the environment (systemd EnvironmentFile on the server), never from a file in the repo.
    .PostConfigure<IConfiguration>((o, c) => o.Passcode = c["ATLAS_PASSCODE"] ?? o.Passcode);

builder.Services.AddSingleton(sp => new PasscodeService(sp.GetRequiredService<IOptions<AtlasOptions>>().Value.Passcode));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<DocStore>();
builder.Services.AddSingleton<SiteSettings>();
builder.Services.AddSingleton<WorkQueue>();
builder.Services.AddSingleton<IMediaPipeline, ToolPipeline>();
builder.Services.AddSingleton<PageRenderer>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<UploadCleanup>();

// Persist data-protection keys so the login cookie survives restarts.
builder.Services.AddDataProtection().SetApplicationName("Atlas");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<DataPaths>((km, paths) =>
        km.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
            new DirectoryInfo(paths.Keys), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "atlas.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
    // Only the local Caddy reverse proxy (loopback, the default trusted proxy) may set these headers.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<AtlasOptions>>((rl, atlas) =>
{
    var a = atlas.Value;
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    rl.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Math.Max(1, a.LoginAttemptsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    rl.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Math.Max(1, a.RequestsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    rl.AddPolicy("report", ctx => RateLimitPartition.GetFixedWindowLimiter(Key(ctx), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Math.Max(1, a.ReportsPerHour), Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
});

var app = builder.Build();

if (string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<AtlasOptions>>().Value.Passcode))
    app.Logger.LogWarning("ATLAS_PASSCODE is not set: nobody will be able to log in.");

app.UseForwardedHeaders();

var site = app.Services.GetRequiredService<SiteSettings>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; media-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    var path = ctx.Request.Path;
    // Search engines stay out until the admin flips the switch; the admin area is never indexable.
    if (site.Current.NoIndex || (path.Value ?? "").StartsWith("/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/api/admin"))
        h["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    await next();
});

// CSRF defence in depth on top of SameSite=Strict: state-changing requests must come from this site.
app.Use(async (ctx, next) =>
{
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method)
        && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin
        && (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || !string.Equals(u.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Cross-site request refused." });
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = new FileExtensionContentTypeProvider { Mappings = { [".webmanifest"] = "application/manifest+json" } },
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        ctx.Context.Response.Headers.CacheControl = name is "sw.js" or "manifest.webmanifest" || name.EndsWith(".html") || name.EndsWith(".js") || name.EndsWith(".css")
            ? "no-cache" : "public, max-age=86400";
    },
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapPost("/api/login", async (LoginRequest req, HttpContext ctx, PasscodeService passcode) =>
{
    if (!passcode.IsConfigured) return Results.Json(new { error = "The server has no passcode configured." }, statusCode: 503);
    if (!passcode.Verify(req.Passcode)) return Results.Json(new { error = "Wrong passcode." }, statusCode: 401);
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("login");

app.MapPost("/api/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(new { ok = true }); });

app.MapGet("/api/session", (HttpContext ctx) => Results.Ok(new { authenticated = ctx.User.Identity?.IsAuthenticated == true }));

PublicEndpoints.Map(app);
AdminEndpoints.Map(app);

app.Run();

public sealed record LoginRequest(string? Passcode);

public partial class Program;
