using System.Net;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.Gateway;

/// <summary>
/// The Gateway's Kestrel host. One process per machine. Binds to 127.0.0.1:7878.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public const int DefaultPort = 7878;

    public int Port { get; }
    public string Token { get; }
    public DirectorRegistry Registry { get; }
    public bool AuthEnabled { get; }

    private readonly DirectorEndpointClient _client;
    private WebApplication? _app;
    private bool _stopped;

    public GatewayHost(int port = DefaultPort, string? token = null, bool authEnabled = false)
    {
        Port = port;
        Token = token ?? GatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry();
        AuthEnabled = authEnabled;
        _client = new DirectorEndpointClient(Token);
    }

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={Port}");

        Registry.Start();

        // PreventHostingStartup avoids ASP.NET Core trying to load a (nonexistent) hosting startup
        // assembly with our application name, which otherwise emits a noisy crit log line on boot.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.Gateway",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        builder.WebHost.ConfigureKestrel(o =>
        {
            // Bind to all interfaces so Tailscale clients can reach the dashboard.
            // Auth is required for every route except /healthz, /login, /logout.
            o.Listen(IPAddress.Any, Port);
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        // Honor X-Forwarded-Proto/Host/For from a Tailscale Serve front-end so
        // ctx.Request.Scheme reflects the public scheme the user actually used.
        // Without this, every request appears as plain "http" to the Gateway
        // (Tailscale terminates TLS at :443 and forwards plaintext to loopback),
        // and ViewUrl ends up with the wrong scheme on the phone.
        //
        // Trust only loopback as a forwarding proxy: anything else must not be
        // allowed to claim "I'm HTTPS" by spoofing the header.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                               | ForwardedHeaders.XForwardedProto
                               | ForwardedHeaders.XForwardedHost;
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
            o.KnownIPNetworks.Clear();
        });

        _app = builder.Build();

        _app.UseForwardedHeaders();

        _app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[GatewayHost] pipeline exception: {ex}");
                FileLog.Write($"[GatewayHost] unhandled exception in pipeline: {ex}");
                ctx.Response.StatusCode = 500;
                if (!ctx.Response.HasStarted)
                    await ctx.Response.WriteAsync($"internal error: {ex.GetType().Name}: {ex.Message}");
            }
        });

        if (AuthEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken { Token = Token };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }
        _app.UseRouting();

        var version = typeof(GatewayHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        GatewayEndpoints.Map(_app, Registry, _client, version, Token, AuthEnabled);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://127.0.0.1:{Port}");
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        Registry.Dispose();
        _client.Dispose();

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[GatewayHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
