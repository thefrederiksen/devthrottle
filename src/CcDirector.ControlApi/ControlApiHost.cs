using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.ControlApi;

/// <summary>
/// Hosts the Director's HTTP Control API on a stable, predictable port so the
/// URL is bookmarkable across restarts and reachable from Tailscale clients.
///
/// Binding:
///   - Listens on loopback (127.0.0.1) ONLY. The raw port is never on the LAN or the
///     Tailscale interface; remote access is exclusively via Tailscale Serve (HTTPS),
///     auto-provisioned per Director by the Gateway's TailscaleServeProvisioner.
///
/// Lifecycle:
///   - StartAsync() -> picks port via PortAllocator, starts Kestrel, writes instances/{guid}.json
///   - StopAsync()  -> deletes registration file, releases port state, stops Kestrel
/// </summary>
public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly RepositoryRegistry? _repositoryRegistry;
    private readonly string _version;
    private readonly Func<Task> _requestShutdownAsync;
    private readonly bool _useEphemeralPort;
    private readonly bool _authEnabled;

    public string DirectorId { get; }
    public int Port { get; private set; }
    public bool AuthEnabled => _authEnabled;

    /// <summary>
    /// Per-session persistent JSONL log. Exposed so the Avalonia UI can persist
    /// rendered agent-view widgets to <c>agent-view.jsonl</c> alongside the raw
    /// stream and turn summaries we already write. Null until <see cref="StartAsync"/>.
    /// </summary>
    public Core.Storage.SessionLogManager? SessionLogManager => _sessionLogManager;

    private WebApplication? _app;
    private InstanceRegistration? _registration;
    private GatewayClient? _gatewayClient;
    private TurnSummaryCache? _turnSummaryCache;
    private SessionStatusWingman? _statusWingman;
    private ProactiveExplainService? _proactiveExplain;
    private TerminalStateDetector? _terminalStateDetector;
    private TerminalSessionRecorder? _sessionRecorder;
    private Core.Storage.SessionLogManager? _sessionLogManager;
    private bool _stopped;

    /// <summary>
    /// Construct a Director Control API host.
    /// </summary>
    /// <param name="useEphemeralPort">
    /// If true, Kestrel picks a free port and we bind only to loopback (intended for tests).
    /// If false (production), PortAllocator picks a stable port in [7879..7898] and we bind to loopback (Tailscale Serve fronts it).
    /// </param>
    /// <param name="authEnabled">
    /// If true, bearer-token or cookie auth is required for all routes except /healthz/login/logout.
    /// If false (default), the Director is completely open. The Tailscale tailnet is the trust boundary.
    /// </param>
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, bool useEphemeralPort = false, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, string? directorId = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _useEphemeralPort = useEphemeralPort;
        _authEnabled = authEnabled;
        _repositoryRegistry = repositoryRegistry;

        // Production: persisted id (same across restarts so the Gateway recognizes us).
        // Tests: inject a fresh id per fixture so parallel runs don't collide on the
        // single instances/{id}.json file.
        DirectorId = directorId ?? (useEphemeralPort
            ? Guid.NewGuid().ToString()
            : DirectorIdStore.LoadOrCreate());
    }

    /// <summary>Start Kestrel and write the instance registration file. Returns the chosen port.</summary>
    public async Task<int> StartAsync()
    {
        FileLog.Write($"[ControlApiHost] StartAsync: directorId={DirectorId}, ephemeral={_useEphemeralPort}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.ControlApi",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        if (_useEphemeralPort)
        {
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            Port = PortAllocator.Allocate(DirectorId);
            // Loopback ONLY. The raw port is never exposed on the LAN or the Tailscale
            // interface; the sole remote path is Tailscale Serve (HTTPS), which the
            // Gateway's TailscaleServeProvisioner maps as https://<host>:<port> ->
            // http://localhost:<port>. This kills the plain-HTTP-on-raw-port surface.
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, Port));
        }

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Global exception envelope so the browser sees a readable 500 instead of a hung connection.
        _app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlApiHost] pipeline exception: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        if (_authEnabled)
        {
            var token = DirectorAuth.LoadOrCreateToken();
            _app.Use((ctx, next) => DirectorAuth.Run(ctx, token, next));
        }

        // Enable WebSocket support for /dictate and any future streaming endpoints.
        _app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        _app.UseRouting();

        // Phase 5: persistent JSONL log per session. Must start FIRST so brand-new
        // sessions have a writer attached before any events fire.
        _sessionLogManager = new Core.Storage.SessionLogManager(_sessionManager);
        _sessionLogManager.Start();

        // Phase 3: the SessionStatusWingman is the sole writer of each Session's
        // StatusColor. Must start BEFORE TurnSummaryCache so brand-new sessions are
        // already "green/session created" by the time anything else observes them.
        _statusWingman = new SessionStatusWingman(_sessionManager);
        _statusWingman.Start();

        // Start the Wingman's per-turn summary cache before mapping endpoints so
        // /sessions/{sid}/turn-summaries returns whatever is already cached.  Hooks
        // OnSessionCreated + per-session OnTurnCompleted.  See Phase 2 of
        // docs/goals/GOAL_CC_DIRECTOR_SUPERVISOR.md.
        _turnSummaryCache = new TurnSummaryCache(_sessionManager, _sessionManager.Options, _sessionLogManager);
        _turnSummaryCache.Start();

        // Proactive explain: for mobile-mode sessions, regenerate + cache the Opus briefing
        // at each decision-point turn-end so the phone reads it instantly on open.
        _proactiveExplain = new ProactiveExplainService(_sessionManager, _sessionManager.Options.ClaudePath, _turnSummaryCache);
        _proactiveExplain.Start();

        // Terminal-driven state (CLEAN SLATE): the detector's only rule is byte -> working,
        // plus the idle clock (time since the last ConPTY character). No footer/grid/LLM
        // determination -- that guesswork was removed. On by default; set
        // CC_DIRECTOR_TERMINAL_STATE=0 to run the detector in observe-only mode (logs, writes
        // nothing) and fall back to the Claude-Code hook path.
        var terminalDriven = Environment.GetEnvironmentVariable("CC_DIRECTOR_TERMINAL_STATE") != "0";
        Core.Sessions.Session.TerminalDrivenState = terminalDriven;
        _terminalStateDetector = new TerminalStateDetector(_sessionManager, driveState: terminalDriven);
        _terminalStateDetector.Start();
        FileLog.Write($"[ControlApiHost] Session state source: {(terminalDriven ? "terminal (byte->working)" : "hooks")}");

        // Always-on terminal recorder: logs every session's resolved grid (on change, with the
        // activity state) to build the ground-truth corpus for offline analysis/learning.
        // Turn detection itself is the trigger + LLM judge in TerminalStateDetector above - no
        // regex screen parsing. Observe-only, capped per session. On by default; set
        // CC_DIRECTOR_RECORD_SESSIONS=0 to disable. See docs/wingman/WINGMAN.md.
        if (Environment.GetEnvironmentVariable("CC_DIRECTOR_RECORD_SESSIONS") != "0")
        {
            _sessionRecorder = new TerminalSessionRecorder(_sessionManager);
            _sessionRecorder.Start();
        }

        // Load the gateway config up front so the served HTML can render a "Gateway"
        // nav button pointing at it. Reused below for the GatewayClient registration.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        var gatewayUrl = gatewayConfig.IsEnabled ? gatewayConfig.Url : null;

        ControlEndpoints.Map(_app, _sessionManager, DirectorId, _version, _requestShutdownAsync, _authEnabled, _repositoryRegistry, _turnSummaryCache, gatewayUrl, _proactiveExplain);
        DictationEndpoint.Map(_app, _sessionManager.Options);
        TerminalStreamEndpoint.Map(_app, _sessionManager);

        await _app.StartAsync();

        if (_useEphemeralPort)
        {
            Port = ReadAssignedPort(_app)
                ?? throw new InvalidOperationException("Kestrel started but did not expose a bound address.");
        }
        FileLog.Write($"[ControlApiHost] Kestrel listening on http://127.0.0.1:{Port} (loopback only; remote access via Tailscale Serve)");

        _registration = new InstanceRegistration(DirectorId, Port, _version);
        _registration.Register();

        // Phase 1: if gateway.url is configured, register with the Gateway over HTTP and
        // start the heartbeat. Disabled (no-op) when local-only. Reuses the config
        // loaded above for the HTML nav button.
        _gatewayClient = new GatewayClient(gatewayConfig, DirectorId, Port, _version);
        _gatewayClient.Start();

        return Port;
    }

    private static int? ReadAssignedPort(WebApplication app)
    {
        var server = app.Services.GetService<IServer>();
        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return null;
        foreach (var addr in addresses)
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
                return uri.Port;
        return null;
    }

    /// <summary>Stop Kestrel and delete the registration file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[ControlApiHost] StopAsync");

        if (_gatewayClient is not null)
        {
            try { await _gatewayClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayClient.StopAsync error: {ex.Message}"); }
            _gatewayClient.Dispose();
            _gatewayClient = null;
        }

        _terminalStateDetector?.Dispose();
        _terminalStateDetector = null;
        _sessionRecorder?.Dispose();
        _sessionRecorder = null;
        _proactiveExplain?.Dispose();
        _proactiveExplain = null;
        _turnSummaryCache?.Dispose();
        _turnSummaryCache = null;
        _statusWingman?.Dispose();
        _statusWingman = null;
        _sessionLogManager?.Dispose();
        _sessionLogManager = null;

        _registration?.Unregister();

        // Release the persisted port file only if we used a real allocated port
        if (!_useEphemeralPort && Port > 0)
        {
            try { PortAllocator.Release(DirectorId); } catch { }
        }

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
