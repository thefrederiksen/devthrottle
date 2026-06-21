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
    // Not readonly: LAN addressing mode (issue #457) auto-enables auth at StartAsync, because
    // binding the Control API to the LAN without auth would expose it to the whole network.
    private bool _authEnabled;

    public string DirectorId { get; }
    public int Port { get; private set; }
    public bool AuthEnabled => _authEnabled;

    /// <summary>
    /// True once Kestrel has bound and <see cref="StartAsync"/> has completed successfully.
    /// False while starting AND after a start failure (e.g. all ports in [7879..7898] busy).
    /// The session-state services (badge tracking) run regardless -- see
    /// <see cref="StartSessionStateServices"/> -- so this specifically means "the REST/Control
    /// API and remote (Gateway/Cockpit/phone) access are up".
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Null while healthy; set to the failure reason when <see cref="StartAsync"/> could not
    /// bind the Control API (reported by the boundary that catches the exception via
    /// <see cref="ReportStartupFailure"/>). The desktop surfaces this as a loud sidebar
    /// indicator so a port-exhausted Director is never silently degraded.
    /// </summary>
    public string? StartupError { get; private set; }

    /// <summary>
    /// Fires whenever <see cref="IsListening"/> / <see cref="StartupError"/> change, so the UI
    /// can repaint its Control-API indicator. May fire on a background thread.
    /// </summary>
    public event Action? StartupStatusChanged;

    /// <summary>
    /// Record that the Control API failed to start. Called by the boundary that catches the
    /// <see cref="StartAsync"/> exception (App startup) -- StartAsync re-throws, so the host
    /// itself cannot set this from a success-returning path. Raises
    /// <see cref="StartupStatusChanged"/> so the UI surfaces the degraded state.
    /// </summary>
    public void ReportStartupFailure(string error)
    {
        FileLog.Write($"[ControlApiHost] ReportStartupFailure: {error}");
        IsListening = false;
        StartupError = error;
        StartupStatusChanged?.Invoke();
    }

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
    /// Issue #335 test seam: pin the tailnet identity resolution for the session DTO
    /// mapper so unit tests can assert identity fields without requiring a live Tailscale
    /// daemon. Must be set before <see cref="StartAsync"/> if used; the resolver is
    /// captured at start time. Null (default) uses the real detection ladder.
    /// </summary>
    internal Func<CcDirector.Core.Network.TailnetEndpointResolution>? TailnetEndpointResolverOverride { get; set; }

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

    /// <summary>
    /// Fetch the latest Gateway turn brief for a session - the desktop Wingman tab's source.
    /// Null when no Gateway is configured/connected or none stamped yet; the caller then shows
    /// the local explain instead.
    /// </summary>
    public Task<Gateway.Contracts.TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
        => _gatewayClient?.GetLatestTurnBriefAsync(sessionId, ct) ?? Task.FromResult<Gateway.Contracts.TurnBriefDto?>(null);

    private TurnSummaryCache? _turnSummaryCache;
    private SessionStatusWingman? _statusWingman;
    private ProactiveExplainService? _proactiveExplain;
    private TerminalStateDetector? _terminalStateDetector;
    private TransientErrorAutoResume? _transientErrorAutoResume;
    private TerminalSessionRecorder? _sessionRecorder;
    private Core.Storage.TurnReviewLogger? _turnReviewLogger;
    private Core.Storage.SessionLogManager? _sessionLogManager;
    // Resolved lazily at request time: the scheduler is created AFTER the Control API host
    // (StartControlApi runs before StartScheduler), so we capture an accessor, not the instance.
    private readonly Func<Core.Scheduler.SchedulerService?>? _schedulerAccessor;
    // Resolved lazily too (issue #329): the Engine starts after this host AND its dispatcher
    // only exists once the deferred email-tool discovery completes.
    private readonly Func<Engine.Dispatcher.CommunicationDispatcher?>? _commDispatcherAccessor;
    private readonly string? _instancesDirectory;
    private bool _stopped;
    private bool _stateServicesStarted;

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
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, bool useEphemeralPort = false, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, string? directorId = null, Func<Core.Scheduler.SchedulerService?>? schedulerAccessor = null, string? instancesDirectory = null, Func<Engine.Dispatcher.CommunicationDispatcher?>? commDispatcherAccessor = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _useEphemeralPort = useEphemeralPort;
        _authEnabled = authEnabled;
        _repositoryRegistry = repositoryRegistry;
        _schedulerAccessor = schedulerAccessor;
        _commDispatcherAccessor = commDispatcherAccessor;
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

        // Start the session-state services FIRST, before any port allocation or Kestrel work.
        // They observe the SessionManager + terminal buffers only -- never the bound port -- so
        // they must come up even when the Control API fails to bind (e.g. every port in
        // [7879..7898] is taken by other Directors). Before this was hoisted out, PortAllocator
        // throwing aborted StartAsync before these started, freezing every session on its last
        // badge colour: a silent session could never flip to the red "needs you" state.
        StartSessionStateServices();

        // Load the gateway config FIRST: the addressing mode (issue #457) decides the bind
        // interface below, and it is reused for the GatewayClient + session DTO mapper.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        var addressingMode = gatewayConfig.AddressingMode;

        // LAN mode puts the Control API on a routable interface, so it MUST be authenticated -
        // auto-enable auth (the tailnet trust boundary is gone). This is why LAN "just works"
        // without a separate toggle: choosing LAN turns on auth.
        if (addressingMode == Core.Configuration.AddressingMode.Lan && !_authEnabled)
        {
            _authEnabled = true;
            FileLog.Write("[ControlApiHost] LAN addressing mode: auth auto-enabled (Control API will require the fleet token)");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.ControlApi",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        if (_useEphemeralPort)
        {
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        }
        else if (addressingMode == Core.Configuration.AddressingMode.Lan)
        {
            // LAN addressing mode (issue #457): the Director must be reachable on its real LAN
            // IP, so the Control API binds to ALL interfaces - there is no Tailscale Serve
            // fronting it in this mode. Auth was auto-enabled above so the raw port is not open;
            // the fleet token (gateway.token) is required on every call.
            Port = PortAllocator.Allocate(DirectorId);
            FileLog.Write($"[ControlApiHost] LAN addressing mode: binding Control API to 0.0.0.0:{Port} (auth enabled)");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, Port));
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
            // Accept the shared fleet token (gateway.token) when attached to a Gateway, so the
            // Gateway authenticates across machines in LAN mode (issue #457); else the local token.
            var token = DirectorAuth.ResolveAcceptedToken(gatewayConfig.Token);
            _app.Use((ctx, next) => DirectorAuth.Run(ctx, token, next));
        }

        // Enable WebSocket support for /dictate and any future streaming endpoints.
        _app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        _app.UseRouting();

        // NOTE: the per-session state-tracking services (status wingman, terminal-state
        // detector, recorders, loggers) are NOT started here. They are started up front by
        // StartSessionStateServices(), before any port allocation, so they survive a
        // Control-API bind failure. See that method for the rationale.

        // Turn briefing left the Director (issue #187, the Gateway Wingman end state):
        // the GATEWAY's warm-brain agent observes turn ends (doorbell/heartbeat, #186),
        // generates briefs, stores them, and stamps BriefingState/RailLine onto the
        // aggregated session view. The Director is dumb metal here.

        // gatewayConfig was loaded up front (the addressing mode set the bind interface);
        // reuse it for the served HTML's "Gateway" nav button and the GatewayClient.
        var gatewayUrl = gatewayConfig.IsEnabled ? gatewayConfig.Url : null;

        // Issue #335: tailnet identity resolver for session DTO population. The resolver is
        // captured once at start time and shared with the per-session Map helper (runs on
        // every /sessions request). Production uses the real detection ladder; tests can pin
        // a fixed endpoint via TailnetEndpointResolverOverride before calling StartAsync.
        Func<CcDirector.Core.Network.TailnetEndpointResolution> resolveTailnetEndpoint;
        if (TailnetEndpointResolverOverride is not null)
        {
            resolveTailnetEndpoint = TailnetEndpointResolverOverride;
        }
        else if (addressingMode == Core.Configuration.AddressingMode.Lan)
        {
            // LAN mode (issue #457): the session DTO's routable endpoint is this machine's LAN IP.
            var lanResolver = new CcDirector.Core.Network.LanIdentityResolver();
            resolveTailnetEndpoint = () => lanResolver.ResolveEndpoint(Port, gatewayConfig.TailnetEndpoint);
        }
        else
        {
            var identityResolver = new CcDirector.Core.Network.TailnetIdentityResolver();
            resolveTailnetEndpoint = () => identityResolver.ResolveEndpoint(Port, gatewayConfig.TailnetEndpoint);
        }

        ControlEndpoints.Map(_app, _sessionManager, DirectorId, _version, _requestShutdownAsync, _authEnabled, _repositoryRegistry, _turnSummaryCache, gatewayUrl, _proactiveExplain, GatewayMonitor, resolveTailnetEndpoint);
        // Dictation key resolution: the Gateway vault when attached to a Gateway, the local
        // Settings > Voice key when standalone (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
        // Pass GatewayConfig.Load (not the snapshot above) so the resolver re-reads config.json
        // on every dictation: a Director that booted standalone and later had a gateway.url
        // added self-heals into Gateway mode without a restart.
        var openAiKeyResolver = new Core.Configuration.OpenAiKeyResolver(
            _sessionManager.Options, Core.Configuration.GatewayConfig.Load);
        // Dictation glossary resolution mirrors the key resolver (#253): the Gateway's shared
        // dictionary when attached, the local cache when standalone. GatewayConfig.Load (not the
        // snapshot) is passed so the resolver re-reads config.json each dictation and self-heals
        // into Gateway mode without a restart.
        var dictionaryResolver = new Core.Dictation.DictionaryResolver(
            _sessionManager.Options, Core.Configuration.GatewayConfig.Load);
        DictationEndpoint.Map(_app, _sessionManager.Options, openAiKeyResolver, dictionaryResolver);
        TerminalStreamEndpoint.Map(_app, _sessionManager);
        SessionUsageEndpoint.Map(_app, _sessionManager);
        ClaudeTranscriptsEndpoint.Map(_app);
        SettingsEndpoint.Map(_app, ReapplyGatewayAsync, () => Port);
        // /settings/agents (issue #584): full Settings-dialog Agents-tab parity over REST -
        // library CRUD/reorder/enable plus Detect, Quick check, resolved command line, and the
        // catalog, reusing the same Core services the Agents tab uses (one implementation).
        AgentsEndpoint.Map(_app, _sessionManager.Options);
        ToolsEndpoint.Map(_app);
        WorkspacesEndpoint.Map(_app);
        SchedulerEndpoint.Map(_app, _schedulerAccessor);
        // POST /dispatch (issue #329): null accessor means "no Engine hosted here" (tests
        // that don't care, embedded hosts) - the endpoint then answers 503, never throws.
        DispatchEndpoint.Map(_app, _commDispatcherAccessor ?? (() => null));
        // GET /facts (issue #330): the tool inventory + launcher facts the Gateway pulls.
        FactsEndpoint.Map(_app, DirectorId, _version);
        // POST /sessions/{id}/voice-turn (issue #351): server-side walkie-talkie turn
        // (transcribe -> wait -> send -> poll -> summarize -> TTS -> SSE reply).
        VoiceTurnEndpoint.Map(_app, _sessionManager);

        await _app.StartAsync();

        if (_useEphemeralPort)
        {
            Port = ReadAssignedPort(_app)
                ?? throw new InvalidOperationException("Kestrel started but did not expose a bound address.");
        }
        FileLog.Write(addressingMode == Core.Configuration.AddressingMode.Lan
            ? $"[ControlApiHost] Kestrel listening on http://0.0.0.0:{Port} (LAN addressing mode; reachable on this machine's LAN IP)"
            : $"[ControlApiHost] Kestrel listening on http://127.0.0.1:{Port} (loopback only; remote access via Tailscale Serve)");

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
        // (the #179 lesson). Issue #457: LAN addressing mode has no Serve front door at all -
        // the Director is reached directly on its LAN IP - so it never provisions a mapping.
        if (!_useEphemeralPort && addressingMode != Core.Configuration.AddressingMode.Lan)
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

        IsListening = true;
        StartupError = null;
        StartupStatusChanged?.Invoke();
        return Port;
    }

    /// <summary>
    /// Start the per-session state-tracking services. Every service here observes the
    /// <see cref="SessionManager"/> and its sessions' terminal buffers only -- none of them
    /// touch Kestrel or the bound port -- so they run independently of whether the Control API
    /// binds.
    ///
    /// This is deliberately decoupled from the port bind. The desktop "needs you" badge is
    /// <see cref="Session.StatusColor"/>, written by <see cref="SessionStatusWingman"/> from the
    /// <see cref="ActivityState"/> that <see cref="TerminalStateDetector"/> drives (byte -> Working;
    /// <see cref="TerminalStateDetector.QuietThreshold"/> of silence -> WaitingForInput = red).
    /// Before these were hoisted out of the post-bind section of <see cref="StartAsync"/>, a
    /// port-allocation failure (e.g. every port in [7879..7898] busy from other Directors)
    /// aborted StartAsync before they started -- leaving every session frozen on its last colour,
    /// so a silent session could sit forever and never flip to red. Idempotent: safe to call
    /// again (StartAsync calls it once up front).
    /// </summary>
    internal void StartSessionStateServices()
    {
        if (_stateServicesStarted) return;
        _stateServicesStarted = true;

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

        // Transient-error auto-resume (issue #476): content-aware detection of a TRANSIENT
        // Anthropic API server error in a Claude Code session, with an opt-in auto-continue loop.
        // Gated behind config.json "auto_resume.enabled" which DEFAULTS OFF, so this is inert
        // unless the user has explicitly turned it on (human decision on assumption A-3). Always
        // wired so the toggle takes effect without a Director restart; the scheduler re-reads the
        // live config each cycle.
        _transientErrorAutoResume = new TransientErrorAutoResume(_sessionManager);
        _transientErrorAutoResume.Start();

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
    /// Sessions whose session-exited event has already been announced (issue #330): a
    /// session can hit the exit moment twice - the process dying (ActivityState -> Exited)
    /// and the roster removal (OnSessionRemoved, e.g. a user closing an active session) -
    /// and the Gateway must hear session-exited exactly ONCE per session.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _exitAnnounced = new();

    /// <summary>
    /// Subscribe every session's activity-state change to the Gateway doorbell (issue #186).
    /// Issue #330 widens the same channel with the event vocabulary: session-created on
    /// roster add, session-exited once per session (state -> Exited or roster removal,
    /// whichever happens first), and prompt-detected on the detector's transition into a
    /// detected input-prompt state (WaitingForInput / WaitingForPerm - the flagged
    /// assumption on the issue: the existing detector signal, no prompt understanding).
    /// Subscribed ONCE per host; the handlers read the _gatewayClient FIELD so a settings
    /// change that replaces the client (ReapplyGatewayAsync) is picked up without
    /// re-subscribing. NotifySessionState is a no-op while disabled/unregistered.
    /// </summary>
    private void WireDoorbellPush()
    {
        void Attach(Core.Sessions.Session session)
            => session.OnActivityStateChanged += (_, newState) =>
            {
                var eventName = newState switch
                {
                    Core.Sessions.ActivityState.Exited when _exitAnnounced.TryAdd(session.Id, 0)
                        => DoorbellEvents.SessionExited,
                    Core.Sessions.ActivityState.WaitingForInput or Core.Sessions.ActivityState.WaitingForPerm
                        => DoorbellEvents.PromptDetected,
                    _ => null,
                };
                _gatewayClient?.NotifySessionState(session.Id.ToString(), newState.ToString(), eventName);
            };

        _sessionManager.OnSessionCreated += session =>
        {
            Attach(session);
            _gatewayClient?.NotifySessionState(session.Id.ToString(), session.ActivityState.ToString(),
                DoorbellEvents.SessionCreated);
        };
        _sessionManager.OnSessionRemoved += session =>
        {
            if (_exitAnnounced.TryAdd(session.Id, 0))
                _gatewayClient?.NotifySessionState(session.Id.ToString(),
                    Core.Sessions.ActivityState.Exited.ToString(), DoorbellEvents.SessionExited);
            // The session is gone from the roster - drop the announce guard so the map
            // never grows past the live roster.
            _exitAnnounced.TryRemove(session.Id, out _);
        };
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
        _transientErrorAutoResume?.Dispose();
        _transientErrorAutoResume = null;
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
