using CcDirector.Cockpit.Components;
using CcDirector.Cockpit.Logging;
using CcDirector.Cockpit.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Persisted file sink (issue #199): route every Cockpit log line to
// %LOCALAPPDATA%\cc-director\logs\cockpit\cockpit-YYYY-MM-DD-<PID>.log, matching the
// Director/Gateway FileLog format and rotation. The configured category levels in
// appsettings.json (CcDirector.Cockpit: Debug) still apply - the factory filters before the
// sink is called. Registered first so even early startup lines are captured.
var cockpitFileLog = new CockpitFileLoggerProvider();
builder.Logging.AddProvider(cockpitFileLog);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The Gateway base URL. Defaults to the loopback Gateway on this box; override in
// appsettings.json (Cockpit:GatewayUrl) or via env var Cockpit__GatewayUrl.
var gatewayUrl = builder.Configuration["Cockpit:GatewayUrl"] ?? "http://127.0.0.1:7878";
if (!gatewayUrl.EndsWith('/')) gatewayUrl += "/";

builder.Services.AddHttpClient<GatewayClient>(c =>
{
    c.BaseAddress = new Uri(gatewayUrl);
    // Generous: the Gateway's own fan-out can take a few seconds when a Director is slow.
    c.Timeout = TimeSpan.FromSeconds(30);
});

// Per-item status/title resolver for the Lists view (issue #275): reads each github item's
// title + flow:* label straight from GitHub, so the badge always follows the label (the work-list
// object carries no status). Points at the GitHub REST API; the bearer token is read per call.
builder.Services.AddHttpClient<GitHubItemStatusClient>(c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
});

// Per-session write/act/read client. Issue #372: every per-session verb now goes to the local
// GATEWAY (same base as GatewayClient), which resolves the owning Director by session id and
// reverse-proxies to it - so the Cockpit never dials a Director address directly. The few
// Director-scoped calls (screenshots folder, settings, fanout) still pass an absolute Director
// base per call, which overrides this BaseAddress.
builder.Services.AddHttpClient<DirectorClient>(c =>
{
    c.BaseAddress = new Uri(gatewayUrl);
    // Generous: most calls return in milliseconds, but recap generation is an opus call that can
    // take ~90s. A high ceiling lets that one call through without affecting the fast ones (it
    // only bounds how long a hung call waits).
    c.Timeout = TimeSpan.FromSeconds(150);
});

// ONE URL (docs/plans/one-url-cockpit.md): the Cockpit sits behind the Gateway's loopback
// fallback proxy. Honor the X-Forwarded headers it sends so generated URLs carry the
// public Tailscale scheme/host, trusting only loopback as the proxy.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                       | ForwardedHeaders.XForwardedProto
                       | ForwardedHeaders.XForwardedHost;
    o.KnownProxies.Clear();
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    o.KnownIPNetworks.Clear();
});

var app = builder.Build();

// First persisted line: proves the file sink is live and records where it writes (issue #199).
app.Logger.LogInformation(
    "Cockpit starting (pid={Pid}) on {Urls}; log file: {LogPath}",
    Environment.ProcessId,
    string.Join(",", app.Urls.Count == 0 ? new[] { "(pending)" } : app.Urls.ToArray()),
    cockpitFileLog.CurrentLogPath);

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

// Static tool pages that moved over from the Gateway (one-URL plan). They are plain
// HTML+JS apps fetching the Gateway's REST with same-origin paths, which works because
// the browser's origin is the Gateway front door for both the pages and the API.
// Served through WebRootFileProvider (NOT a physical path): the static-web-assets
// manifest resolves wwwroot correctly in dev, bin-run, and publish alike.
IResult ServePage(string name)
{
    var file = app.Environment.WebRootFileProvider.GetFileInfo($"pages/{name}");
    if (!file.Exists)
        throw new InvalidOperationException($"Tool page missing from wwwroot: pages/{name}");
    return Results.File(file.CreateReadStream(), "text/html; charset=utf-8");
}
// /exes, /transcripts, /dictionary are now Blazor pages (issue #183): Components/Pages/
// Exes.razor, Transcripts.razor, Dictionary.razor. Their static html + MapGet routes were
// removed so the Blazor route is the only one serving each path. /keys and /settings stay
// static this round.
app.MapGet("/keys", () => ServePage("keys.html"));
app.MapGet("/settings", () => ServePage("settings.html"));
// /voice is the mobile-first Voice Mode (offline-capable static PWA): a plain HTML+JS app
// under wwwroot/pages/voice/ that records audio and drives the Gateway's voice-turn endpoints.
// Static, NOT Blazor, because offline-first cannot depend on a live SignalR circuit.
//
// The voice-turn endpoints are token-gated even when the Gateway runs with global auth off
// (issue #369), and the Gateway's cc-gateway-token cookie is only set via /login - which a
// browser on an auth-off Gateway never hits. So we inject the per-machine Gateway token
// (read from disk on this same box) into the page; voice.js sends it as a Bearer header. The
// page is reachable only via the Gateway front door (Tailscale-authenticated tailnet, TLS),
// the same trust boundary the phone app's bearer token already relies on.
app.MapGet("/voice", () =>
{
    var file = app.Environment.WebRootFileProvider.GetFileInfo("pages/voice/index.html");
    if (!file.Exists)
        throw new InvalidOperationException("Voice page missing from wwwroot: pages/voice/index.html");
    using var reader = new StreamReader(file.CreateReadStream());
    var html = reader.ReadToEnd().Replace("__GATEWAY_TOKEN__", ReadGatewayToken());
    return Results.Content(html, "text/html; charset=utf-8");
});

// /voice/sw.js is the Voice Mode Service Worker (issue #426): it drains the offline upload
// outbox automatically when connectivity returns. A Service Worker may only control URLs at
// or below its own script path UNLESS the script is served with the Service-Worker-Allowed
// header naming a wider scope. The worker is registered with scope "/voice", but the page
// itself is at "/voice" (no trailing slash) which is ABOVE the script's own "/voice/"
// directory - so we send "Service-Worker-Allowed: /" to authorize the wider scope. Served
// via a route (not the static handler) so we can set that header and the correct JS MIME.
app.MapGet("/voice/sw.js", (HttpContext ctx) =>
{
    var file = app.Environment.WebRootFileProvider.GetFileInfo("pages/voice/sw.js");
    if (!file.Exists)
        throw new InvalidOperationException("Voice Service Worker missing from wwwroot: pages/voice/sw.js");
    ctx.Response.Headers["Service-Worker-Allowed"] = "/";
    return Results.File(file.CreateReadStream(), "text/javascript; charset=utf-8");
});

// Read the per-machine Gateway token written by the Gateway at
// {root}\config\director\gateway-token.txt, where {root} is CC_DIRECTOR_ROOT or
// %LOCALAPPDATA%\cc-director (the same resolution CcStorage uses; the Cockpit does not
// reference CcDirector.Core, so the path is computed directly). Empty when absent: the page
// still loads and shows a clear "not connected" state instead of failing to render.
static string ReadGatewayToken()
{
    var root = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    if (string.IsNullOrEmpty(root))
        root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cc-director");
    var path = System.IO.Path.Combine(root, "config", "director", "gateway-token.txt");
    return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : "";
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
