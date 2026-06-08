using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
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
    private TailscaleServeSelfProvisioner? _serveProvisioner;
    private readonly SemaphoreSlim _gatewayReapplyLock = new(1, 1);

    /// <summary>
    /// The one home of this Director's Gateway-connection truth (issues #223/#224).
    /// Host-owned so it survives GatewayClient replacement on settings changes; the
    /// desktop indicator subscribes to its Changed event, the /verify/{nonce} endpoint
    /// records callback receipts in it.
    /// </summary>
    public GatewayConnectionMonitor GatewayMonitor { get; } = new();

    /// <summary>This Director's own serve provisioner (issue #197). Exposed for the
    /// troubleshooting dialog: rung 2's "Fix it now" runs EnsureMappingAsync, and the
    /// provisioner's LastError explains WHY a mapping is missing. Null on ephemeral-port
    /// hosts (tests, hosted agents), which never self-provision.</summary>
    public TailscaleServeSelfProvisioner? ServeProvisioner => _serveProvisioner;

    /// <summary>
    /// On-demand two-way handshake (issues #223/#224) for the troubleshooter's Re-test
    /// button. Null result when no Gateway is configured or a handshake is already in
    /// flight; the verdict also lands in <see cref="GatewayMonitor"/> either way.
    /// </summary>
    public Task<Gateway.Contracts.DirectorVerifyResultDto?> VerifyGatewayNowAsync(CancellationToken ct = default)
        => _gatewayClient?.VerifyAsync(ct) ?? Task.FromResult<Gateway.Contracts.DirectorVerifyResultDto?>(null);
    private TurnSummaryCache? _turnSummaryCache;
    private SessionStatusWingman? _statusWingman;
    private ProactiveExplainService? _proactiveExplain;
    private TerminalStateDetector? _terminalStateDetector;
    private TerminalSessionRecorder? _sessionRecorder;
    private Core.Storage.TurnReviewLogger? _turnReviewLogger;
    private Core.Storage.SessionLogManager? _sessionLogManager;
    // Resolved lazily at request time: the scheduler is created AFTER the Control API host
    // (StartControlApi runs before StartScheduler), so we capture an accessor, not the instance.
    private readonly Func<Core.Scheduler.SchedulerService?>? _schedulerAccessor;
    private readonly string? _instancesDirectory;
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
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, bool useEphemeralPort = false, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, string? directorId = null, Func<Core.Scheduler.SchedulerService?>? schedulerAccessor = null, string? instancesDirectory = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _useEphemeralPort = useEphemeralPort;
        _authEnabled = authEnabled;
        _repositoryRegistry = repositoryRegistry;
        _schedulerAccessor = schedulerAccessor;
        // Tests pass an isolated instances directory so test Directors never appear in a real
        // Gateway's discovery (and a real Director never appears in a test Gateway's).
        _instancesDirectory = instancesDirectory;

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

        // Global exception envelope + access log (issue #212 L2). The Director Control API
        // previously logged only what each endpoint happened to mention, so many requests -
        // including state-changing ones - left no trace; the 2026-06-06 post-mortem had to
        // reconstruct who-called-what from indirect evidence. We now log every MUTATING
        // request (POST/PUT/PATCH/DELETE) and any request that errors (>=400), with method,
        // path, status, elapsed, and client. Successful GET/HEAD are skipped because the
        // Director is polled hard (GET /sessions every 2s per viewer) and would flood the log.
        _app.Use(async (ctx, next) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
            finally
            {
                sw.Stop();
                var method = ctx.Request.Method;
                var isMutation = method is "POST" or "PUT" or "PATCH" or "DELETE";
                if (isMutation || ctx.Response.StatusCode >= 400)
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[ControlApiHost] {method} {ctx.Request.Path}{ctx.Request.QueryString} " +
                        $"-> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client}");
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
        // /sessions/{sid}/turn-summaries returns whatever is already cached. Summaries are
        // generated on demand (the voice/mobile views call GenerateForLatestTurnAsync).
        _turnSummaryCache = new TurnSummaryCache(_sessionManager, _sessionManager.Options);
        _turnSummaryCache.Start();

        // Proactive explain: for Wingman-enabled sessions, regenerate + cache the Opus briefing
        // at each decision-point turn-end so the phone reads it instantly on open. TEXT ONLY --
        // no auto-narration. The phone's voice mode invokes /tts on demand against the cached
        // briefing's spoken-version field.
        _proactiveExplain = new ProactiveExplainService(_sessionManager, _sessionManager.Options.ClaudePath, _turnSummaryCache);
        _proactiveExplain.Start();

        // Terminal-driven state: the detector's only rule is byte -> working, plus the idle
        // clock (time since the last ConPTY character). No footer/grid/LLM guesswork, and no
        // Claude Code hooks - the detector is the single authority for session state.
        _terminalStateDetector = new TerminalStateDetector(_sessionManager, driveState: true);
        _terminalStateDetector.Start();
        FileLog.Write("[ControlApiHost] Session state source: terminal (byte->working)");

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

        // Per-turn review log: one record each time a session flips Working -> needs-you
        // (our detector's transition, no hooks). Terminal + what the Wingman said/did, 7-day
        // retention. See CcStorage.TurnReviewLogs().
        _turnReviewLogger = new Core.Storage.TurnReviewLogger(_sessionManager);
        _turnReviewLogger.Start();

        // Turn briefing left the Director (issue #187, the Gateway Wingman end state):
        // the GATEWAY's warm-brain agent observes turn ends (doorbell/heartbeat, #186),
        // generates briefs, stores them, and stamps BriefingState/RailLine onto the
        // aggregated session view. The Director is dumb metal here.

        // Load the gateway config up front so the served HTML can render a "Gateway"
        // nav button pointing at it. Reused below for the GatewayClient registration.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        var gatewayUrl = gatewayConfig.IsEnabled ? gatewayConfig.Url : null;

        ControlEndpoints.Map(_app, _sessionManager, DirectorId, _version, _requestShutdownAsync, _authEnabled, _repositoryRegistry, _turnSummaryCache, gatewayUrl, _proactiveExplain, GatewayMonitor);
        // Dictation key resolution: the Gateway vault when attached to a Gateway, the local
        // Settings > Voice key when standalone (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
        var openAiKeyResolver = new Core.Configuration.OpenAiKeyResolver(_sessionManager.Options, gatewayConfig);
        DictationEndpoint.Map(_app, _sessionManager.Options, openAiKeyResolver);
        TerminalStreamEndpoint.Map(_app, _sessionManager);
        SessionUsageEndpoint.Map(_app, _sessionManager);
        ClaudeTranscriptsEndpoint.Map(_app);
        SettingsEndpoint.Map(_app, ReapplyGatewayAsync, () => Port);
        ToolsEndpoint.Map(_app);
        WorkspacesEndpoint.Map(_app);
        SchedulerEndpoint.Map(_app, _schedulerAccessor);

        await _app.StartAsync();

        if (_useEphemeralPort)
        {
            Port = ReadAssignedPort(_app)
                ?? throw new InvalidOperationException("Kestrel started but did not expose a bound address.");
        }
        FileLog.Write($"[ControlApiHost] Kestrel listening on http://127.0.0.1:{Port} (loopback only; remote access via Tailscale Serve)");

        // Let the SessionManager stamp CC_DIRECTOR_API / CC_DIRECTOR_ID into every session
        // it spawns from now on, so agents inside a session can call this Control API
        // (e.g. GET $CC_DIRECTOR_API/sessions/$CC_SESSION_ID to find themselves).
        _sessionManager.ControlApiBaseUrl = $"http://127.0.0.1:{Port}";
        _sessionManager.DirectorId = DirectorId;

        _registration = new InstanceRegistration(DirectorId, Port, _version, _instancesDirectory);
        _registration.Register();

        // Issue #197: this Director owns its own Tailscale Serve front door. Only real
        // fixed-range Directors self-provision; ephemeral-port hosts (tests, hosted
        // agents) are reached through the Gateway and must not churn the serve table
        // (the #179 lesson).
        if (!_useEphemeralPort)
        {
            _serveProvisioner = new TailscaleServeSelfProvisioner(Port);
            _serveProvisioner.Start();
        }

        // Phase 1: if gateway.url is configured, register with the Gateway over HTTP and
        // start the heartbeat. Disabled (no-op) when local-only. Reuses the config
        // loaded above for the HTML nav button.
        _gatewayClient = BuildGatewayClient(gatewayConfig);
        _gatewayClient.Start();
        WireDoorbellPush();

        return Port;
    }

    /// <summary>
    /// Construct the Gateway client and, when this Director self-provisions its serve
    /// mapping, wire verify-before-advertise (issue #197): every register attempt first
    /// asserts the own-port mapping, then probes the advertised URL from the outside-in.
    /// The PROBE is the gate - a registration only happens for an endpoint that
    /// demonstrably answers; the mapping step is best-effort-but-loud, so an exotic
    /// setup (hand-run reverse proxy via gateway.tailnetEndpoint) that answers without
    /// our mapping still registers truthfully.
    /// </summary>
    private GatewayClient BuildGatewayClient(GatewayConfig gatewayConfig)
    {
        var client = new GatewayClient(gatewayConfig, DirectorId, Port, _version, SnapshotSessionStates, GatewayMonitor);
        if (_serveProvisioner is { } provisioner)
        {
            client.EndpointVerifier = async (endpoint, ct) =>
            {
                var (mapped, mapError) = await provisioner.EnsureMappingAsync(ct);
                var probeError = await GatewayClient.ProbeAdvertisedEndpointAsync(endpoint, ct);
                if (probeError is null) return null;
                return mapped ? probeError : $"{probeError} (serve mapping: {mapError})";
            };
        }
        return client;
    }

    /// <summary>Per-session mechanical-state snapshot for the heartbeat body (issue #186).</summary>
    private List<SessionStateSnapshot> SnapshotSessionStates()
        => _sessionManager.ListSessions()
            .Select(s => new SessionStateSnapshot
            {
                SessionId = s.Id.ToString(),
                ActivityState = s.ActivityState.ToString(),
            })
            .ToList();

    /// <summary>
    /// Subscribe every session's activity-state change to the Gateway doorbell (issue #186).
    /// Subscribed ONCE per host; the handler reads the _gatewayClient FIELD so a settings
    /// change that replaces the client (ReapplyGatewayAsync) is picked up without
    /// re-subscribing. NotifySessionState is a no-op while disabled/unregistered.
    /// </summary>
    private void WireDoorbellPush()
    {
        void Attach(Core.Sessions.Session session)
            => session.OnActivityStateChanged += (_, newState)
                => _gatewayClient?.NotifySessionState(session.Id.ToString(), newState.ToString());

        _sessionManager.OnSessionCreated += Attach;
        foreach (var s in _sessionManager.ListSessions())
            Attach(s);
    }

    /// <summary>
    /// Re-read the gateway config from config.json and re-register the Director with the
    /// gateway, replacing the running <see cref="GatewayClient"/>. Called when PUT /settings
    /// (or the Settings UI) changes the gateway block, so a new gateway URL / advertised
    /// endpoint / token takes effect without restarting the app. Serialized so two concurrent
    /// settings writes can't leave two heartbeat timers running.
    /// </summary>
    public async Task ReapplyGatewayAsync()
    {
        await _gatewayReapplyLock.WaitAsync();
        try
        {
            FileLog.Write("[ControlApiHost] ReapplyGatewayAsync: reloading gateway config");
            if (_gatewayClient is not null)
            {
                // Stop the old heartbeat + unregister BEFORE building the new client, so we
                // never have two clients heartbeating for the same directorId.
                try { await _gatewayClient.StopAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] ReapplyGateway stop error: {ex.Message}"); }
                _gatewayClient.Dispose();
                _gatewayClient = null;
            }

            var gatewayConfig = GatewayConfig.Load();
            _gatewayClient = BuildGatewayClient(gatewayConfig);
            _gatewayClient.Start();
        }
        finally
        {
            _gatewayReapplyLock.Release();
        }
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
        _turnReviewLogger?.Dispose();
        _turnReviewLogger = null;
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

        // Issue #197: graceful shutdown removes this Director's own serve mapping. A crash
        // skips this; the next startup re-asserts the same stable port, so the leftover
        // self-heals.
        if (_serveProvisioner is not null)
        {
            _serveProvisioner.RemoveOwnMapping();
            _serveProvisioner.Dispose();
            _serveProvisioner = null;
        }

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
