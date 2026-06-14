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
// static this round. The /voice -> /transcripts redirect is preserved.
app.MapGet("/keys", () => ServePage("keys.html"));
app.MapGet("/settings", () => ServePage("settings.html"));
app.MapGet("/voice", () => Results.Redirect("/transcripts"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
