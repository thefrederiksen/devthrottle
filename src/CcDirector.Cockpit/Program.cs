using CcDirector.Cockpit.Components;
using CcDirector.Cockpit.Logging;
using CcDirector.Cockpit.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Persisted logging (issue #199): the Cockpit was the only product component with no file sink.
// Start the background writer FIRST, then route ILogger through it, so every component's
// Log.* call (action logging + stream/circuit lifecycle) lands in
// %LOCALAPPDATA%\cc-director\logs\cockpit\cockpit-YYYY-MM-DD-<PID>.log. Keep the framework's
// own providers (console) for the dev `dotnet run` case; the file provider is additive.
CockpitFileLog.Start();
builder.Logging.AddProvider(new CockpitFileLoggerProvider());
// Effective floor for the file sink: Debug for our own components (so DEBUG diagnostics persist
// during active development per the issue's INFO-heavy direction), Information elsewhere to keep
// the framework's own chatter out of the file.
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("CcDirector.Cockpit", LogLevel.Debug);
CockpitFileLog.Write($"INFO [Program] Cockpit starting; log file = {CockpitFileLog.CurrentLogPath}");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Persist Blazor circuit reconnect events to the Cockpit log (issue #199, scope C).
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CcDirector.Cockpit.Logging.CockpitCircuitHandler>();

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

// Direct-to-Director write/act client. No fixed base address (each call targets the owning
// Director's TailnetEndpoint). The Tailscale Serve front door uses a valid public cert, so
// the default handler trusts it - no cert bypass needed.
builder.Services.AddHttpClient<DirectorClient>(c =>
{
    // Generous: most Director calls return in milliseconds, but recap generation is an
    // opus call that can take ~90s. A high ceiling lets that one call through without
    // affecting the fast ones (it only bounds how long a hung call waits).
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
app.MapGet("/exes", () => ServePage("exes.html"));
app.MapGet("/transcripts", () => ServePage("transcripts.html"));
app.MapGet("/dictionary", () => ServePage("dictionary.html"));
app.MapGet("/keys", () => ServePage("keys.html"));
app.MapGet("/settings", () => ServePage("settings.html"));
app.MapGet("/voice", () => Results.Redirect("/transcripts"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Flush and stop the file-log writer on a clean shutdown so the tail of the log is not lost.
app.Lifetime.ApplicationStopped.Register(() =>
{
    CockpitFileLog.Write("INFO [Program] Cockpit stopped");
    CockpitFileLog.Stop();
});

app.Run();
