using System.Diagnostics;
using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tailscale;
using CcDirector.Gateway.Util;
using CcDirector.HostedAgent;
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

    /// <summary>
    /// Invoked when POST /shutdown is received (the self-update helper asking the running Gateway
    /// to exit so its exe unlocks). The hosting process decides how to exit: the tray app stops the
    /// host and shuts the Avalonia app down; the dev console host stops the generic host. When no
    /// handler is set the endpoint answers 501 - it never half-stops the host on its own.
    /// </summary>
    public Action? OnShutdownRequested { get; set; }

    private readonly DirectorEndpointClient _client;
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private readonly GatewayTurnBriefStore _turnBriefStore;
    private readonly SessionAssessments _assessments = new();
    private GatewayTurnBriefAgent? _briefAgent;
    private TurnEndWatcher? _turnEndWatcher;
    private WebApplication? _app;
    private bool _stopped;

    private readonly int _cockpitProxyPort;

    /// <param name="instancesDirectory">
    /// Override the Director-discovery instances directory (see <see cref="DirectorRegistry"/>).
    /// Tests pass an isolated temp directory; production omits it for the shared default.
    /// </param>
    /// <param name="cockpitProxyPort">
    /// Override the loopback port the fallback proxy forwards to (one-URL plan). Tests pass
    /// a dead port so they never reach a real Cockpit running on the dev machine; production
    /// omits it for <see cref="Cockpit.CockpitSupervisor.ResolvePort"/>.
    /// </param>
    /// <param name="turnBriefDirectory">
    /// Override the gateway turn-brief store directory (issue #185). Tests pass an isolated
    /// temp directory; production omits it for the shared default.
    /// </param>
    public GatewayHost(int port = DefaultPort, string? token = null, bool authEnabled = false, string? instancesDirectory = null, int? cockpitProxyPort = null, string? turnBriefDirectory = null)
    {
        Port = port;
        Token = token ?? GatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry(instancesDirectory);
        AuthEnabled = authEnabled;
        _client = new DirectorEndpointClient(Token);
        _cockpitProxyPort = cockpitProxyPort ?? Cockpit.CockpitSupervisor.ResolvePort();
        _serveProvisioner = new TailscaleServeProvisioner(Registry, Port, Cockpit.CockpitSupervisor.ResolvePort());

        // The Gateway's in-process warm brain (issue #184): supervisor only - claude.exe
        // spawns on first use (the brief agent's first ask, or Settings' Restart Brain).
        // The model is PINNED (issue #204): the wingman is the product's one always-on
        // intelligence point, so it runs the configured (default: smartest) model
        // deliberately instead of inheriting whatever the account default happens to be.
        BrainModel = BrainModelConfig.Get();
        FileLog.Write($"[GatewayHost] brain model: {BrainModel}");
        Brain = new BrainSupervisor(new HostedAgentOptions
        {
            WorkingDirectory = Path.Combine(CcStorage.Root(), "brain"),
            AgentArgs = $"{ClaudeDriver.DefaultArgs} --model {BrainModel}",
            Log = FileLog.Write,
        });
        _turnBriefStore = new GatewayTurnBriefStore(turnBriefDirectory);
    }

    /// <summary>
    /// The Gateway's warm brain (issue #184): a claude.exe this process hosts itself - no
    /// Director dependency. Dormant until first use; RestartAsync is the recovery verb.
    /// </summary>
    public BrainSupervisor Brain { get; }

    /// <summary>The model the brain is pinned to (issue #204), resolved at construction
    /// from config.json "brain_model" (default: <see cref="BrainModelConfig.Default"/>).
    /// Recorded on every brief; a config change applies on the next Gateway restart.</summary>
    public string BrainModel { get; }

    /// <summary>Gateway-side turn-brief storage (issue #185): append-only, fleet-wide.</summary>
    public GatewayTurnBriefStore TurnBriefs => _turnBriefStore;

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={Port}");

        // Subscribe the Tailscale provisioner BEFORE Registry.Start() so the initial
        // file-discovery load fires OnDirectorAdded into it and every Director port
        // gets an HTTPS mapping without anyone re-running a script.
        _serveProvisioner.Start();
        Registry.Start();

        // Registry is now loaded with the current Director set: run the first self-healing
        // reconcile - re-assert the front door, drop serve mappings for Directors that died
        // while the Gateway was down (orphans -> 502 from a phone), and sweep any leaked
        // ephemeral-port mappings (issue #179). The provisioner repeats this on a timer.
        _serveProvisioner.Reconcile();

        // The turn-brief stamping machine (issues #185/#186): PUSH-fed since #186 - the
        // tracker is driven by Director doorbell pings and heartbeat snapshots (wired into
        // the endpoints below); the only pull left is the one-time startup catch-up sweep.
        // A stored brief derives the Gateway-owned assessedState and pushes it down to the
        // owning Director as a display annotation. Kill switch: CC_TURNBRIEFS=0.
        if (!GatewayTurnBriefAgent.Disabled)
        {
            _briefAgent = new GatewayTurnBriefAgent(Brain, _turnBriefStore, _client,
                generatorId: $"{GatewayTurnBriefAgent.GeneratorId}/{BrainModel}");
            _turnEndWatcher = new TurnEndWatcher(
                Registry, _client,
                _briefAgent.OnTurnEnd,
                sid =>
                {
                    // Working again: the brief is moot AND the standing assessment is stale.
                    _briefAgent.OnSessionWorking(sid);
                    _assessments.Invalidate(sid);
                });
            _briefAgent.OnBriefStored = (sid, endpoint, brief) =>
            {
                var assessed = _assessments.RecordBrief(sid, brief);
                if (assessed is not null)
                    _ = _client.PostAssessmentAsync(endpoint, sid, assessed);
            };
            // First tick = the startup catch-up sweep; then the 15s reconcile poll for
            // Directors that never push (file-discovered locals, old builds).
            _turnEndWatcher.Start();
        }
        else
        {
            FileLog.Write("[GatewayHost] turn-brief pipeline disabled (CC_TURNBRIEFS=0)");
        }

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
        // Direct forwarding for the one-URL front door (CockpitProxy fallback route).
        builder.Services.AddHttpForwarder();

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
                    && !path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                    // Proxied Blazor plumbing (circuit + static assets) would flood the log.
                    && !path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase))
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

        // Browser-aware front door (the Cockpit sitemap): a PERSON navigating to /sessions,
        // /directors, or /cockpit (Accept: text/html) gets the Cockpit page; programs keep
        // getting JSON from the explicit endpoints below. After auth, before routing.
        var cockpitForwarder = new Cockpit.CockpitProxy.CockpitForwarder(_app.Services, _cockpitProxyPort);
        Cockpit.CockpitProxy.UseBrowserPageRoutes(_app, cockpitForwarder);

        _app.UseRouting();

        // Product version stamped by Directory.Build.props; full form carries the commit SHA.
        var version = AppVersion.Full;
        GatewayEndpoints.Map(_app, Registry, _client, version, Token, AuthEnabled,
            requestShutdown: () =>
            {
                var handler = OnShutdownRequested;
                if (handler is null) return false;
                handler();
                return true;
            },
            // Issue #186: doorbell pings and heartbeat snapshots feed the turn tracker;
            // the aggregated /sessions view carries the Gateway-owned assessedState.
            onSessionState: (directorId, sessionId, newState) =>
            {
                if (_turnEndWatcher is null) return;
                var endpoint = Registry.Get(directorId)?.ControlEndpoint;
                if (string.IsNullOrEmpty(endpoint)) return;
                _turnEndWatcher.Observe(sessionId, newState, endpoint);
            },
            assessedStateFor: sid => _briefAgent is null ? null : _assessments.For(sid),
            // Null when briefing is disabled so old Directors' own values pass through.
            briefStampFor: _briefAgent is { } stampAgent
                ? sid => (stampAgent.BriefingStateFor(sid), _turnBriefStore.Latest(sid)?.NeedsYou?.RailLine)
                : null,
            // Issue #212 W3: enrich the interrupted bucket from the durable brief store. Always
            // available (read-only is safe even with briefing disabled), and the brief survives
            // the Director that died - which is exactly when we need it.
            interruptedBriefFor: sid =>
            {
                var b = _turnBriefStore.Latest(sid);
                return (b?.NeedsYou?.RailLine, b?.Headline);
            });

        // Gateway-served turn briefs (issue #185): the Cockpit reads briefs from HERE; the
        // store serves even when the pipeline is disabled (read-only is always safe).
        // The explain trigger (#217) locates the owning Director across the fleet, then
        // queues the deep dive on the ONE brief agent (null when the pipeline is disabled).
        TurnBriefGatewayEndpoints.Map(_app, _turnBriefStore,
            sid => _briefAgent?.BriefingStateFor(sid) ?? (_turnBriefStore.Latest(sid) is not null ? "Briefed" : "None"),
            requestExplainAsync: _briefAgent is { } explainAgent
                ? async sid =>
                {
                    var lookups = Registry.ListDirectors().Select(async d =>
                    {
                        var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
                        var s = await _client.GetSessionAsync(ep, sid);
                        return (endpoint: ep, session: s);
                    }).ToList();
                    foreach (var (endpoint, session) in await Task.WhenAll(lookups))
                    {
                        if (session is null) continue;
                        return explainAgent.RequestExplain(sid, endpoint)
                            ? (true, "")
                            : (false, "brief agent is shutting down");
                    }
                    return (false, "session not found across any director");
                }
                : null);

        // One URL: everything no explicit endpoint above claimed falls through to the
        // loopback Cockpit (docs/plans/one-url-cockpit.md). Mapped LAST by design.
        Cockpit.CockpitProxy.Map(_app, cockpitForwarder);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://127.0.0.1:{Port} (version {version})");
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        // Brief pipeline first (it drives the brain), then the brain itself - the
        // supervisor's dispose gracefully stops the hosted claude.exe (never leaked).
        try { _turnEndWatcher?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] watcher dispose error: {ex.Message}"); }
        _turnEndWatcher = null;
        try { _briefAgent?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] brief agent dispose error: {ex.Message}"); }
        _briefAgent = null;
        try { Brain.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] brain dispose error: {ex.Message}"); }

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
