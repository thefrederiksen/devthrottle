using System.Diagnostics;
using System.Net;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tailscale;
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
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private WebApplication? _app;
    private bool _stopped;

    /// <param name="instancesDirectory">
    /// Override the Director-discovery instances directory (see <see cref="DirectorRegistry"/>).
    /// Tests pass an isolated temp directory; production omits it for the shared default.
    /// </param>
    public GatewayHost(int port = DefaultPort, string? token = null, bool authEnabled = false, string? instancesDirectory = null)
    {
        Port = port;
        Token = token ?? GatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry(instancesDirectory);
        AuthEnabled = authEnabled;
        _client = new DirectorEndpointClient(Token);
        _serveProvisioner = new TailscaleServeProvisioner(Registry, Port, Cockpit.CockpitSupervisor.ResolvePort());
    }

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={Port}");

        // Subscribe the Tailscale provisioner BEFORE Registry.Start() so the initial
        // file-discovery load fires OnDirectorAdded into it and every Director port
        // gets an HTTPS mapping without anyone re-running a script.
        _serveProvisioner.Start();
        Registry.Start();

        // Registry is now loaded with the current Director set: drop any serve mappings
        // for Directors that died while the Gateway was down (orphans -> 502 from a phone).
        _serveProvisioner.ReconcileOrphans();

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

        // Access log + single top-level exception boundary. Every request leaves one
        // line (method, path, status, elapsed, client, host) so a phone-side problem is
        // traceable after the fact. Health polls and favicon are skipped to keep the log
        // focused on real traffic. RemoteIpAddress reflects X-Forwarded-For because
        // UseForwardedHeaders ran first, so a phone shows its tailnet IP.
        _app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                // Log full detail server-side; return a generic body so we never leak
                // an exception type or message to a remote client.
                Console.Error.WriteLine($"[GatewayHost] pipeline exception: {ex}");
                FileLog.Write($"[GatewayHost] unhandled exception: {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"error\":\"internal error\"}");
                }
            }
            finally
            {
                sw.Stop();
                var path = ctx.Request.Path.Value ?? "";
                if (!path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    && !path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[GatewayHost] {ctx.Request.Method} {path}{ctx.Request.QueryString} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client} host={ctx.Request.Host}");
                }
            }
        });

        if (AuthEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken { Token = Token };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }
        _app.UseRouting();

        // Product version stamped by Directory.Build.props; full form carries the commit SHA.
        var version = AppVersion.Full;
        GatewayEndpoints.Map(_app, Registry, _client, version, Token, AuthEnabled);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://127.0.0.1:{Port} (version {version})");
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        // Unsubscribe from registry events. We deliberately do NOT tear down the serve
        // mappings: the Directors are still alive and reachable, and a Gateway restart
        // re-asserts every mapping on Start().
        _serveProvisioner.Dispose();
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
