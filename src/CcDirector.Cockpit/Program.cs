using CcDirector.Cockpit.Components;
using CcDirector.Cockpit.Services;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
