using System.Diagnostics;
using System.Net;
using CcDirector.Core;
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
    /// Issue #469: mints and verifies the short-lived 4-digit pairing code that authorizes a new
    /// device to enroll. The GatewayApp host window drives this in-process (it mints the code,
    /// shows it locally, and polls the device registry for the join); the /devices/register
    /// endpoint verifies and consumes it. In-memory by design - a Gateway restart cancels any
    /// pending pairing.
    /// </summary>
    public Pairing.PairingCodeService Pairing { get; } = new();

    /// <summary>
    /// Issue #469: the registry of enrolled devices and their unique per-device keys - the single
    /// issuer and record of credentials in the per-device-key trust model. Persisted under the
    /// config root so issued keys survive a Gateway restart.
    /// </summary>
    public Pairing.DeviceRegistry Devices { get; }

    /// <summary>
    /// Issue #288: which Director last owned each session, so the per-session WS proxy can answer
    /// 503 (owner offline) instead of 404 (unknown session). Populated by the /sessions aggregator
    /// and the WS proxy; read by the WS proxy.
    /// </summary>
    public SessionOwnerCache SessionOwners { get; } = new();

    /// <summary>
    /// Issue #330: the per-director ring of received doorbell events (session-created /
    /// session-exited / prompt-detected) - the minimal Phase-1 observable sink, served at
    /// GET /directors/{id}/events. In-memory by design (resets on Gateway restart).
    /// </summary>
    public Events.DirectorEventLog DirectorEvents { get; } = new();

    /// <summary>
    /// Issue #376: the async voice-turn job cache (10-minute TTL). The submit endpoint creates
    /// jobs here, a background task mirrors the owning Director's SSE stage events into them,
    /// and the poll endpoint reads them - in-memory by design (a Gateway restart drops in-flight
    /// turns and the phone re-submits).
    /// </summary>
    public Voice.GatewayTurnJobStore TurnJobs { get; } = new();

    /// <summary>When this host was constructed - the Cockpit Settings page reads it for uptime.</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// Host-process-owned settings the Cockpit Settings page needs (run mode + autostart). The
    /// GatewayApp tray process sets this before <see cref="StartAsync"/>; null on hosts that have
    /// no tray (the dev console host), where the settings endpoint degrades gracefully.
    /// </summary>
    public Api.GatewaySettingsHooks? SettingsHooks { get; set; }

    /// <summary>
    /// Invoked when POST /shutdown is received (the self-update helper asking the running Gateway
    /// to exit so its exe unlocks). The hosting process decides how to exit: the tray app stops the
    /// host and shuts the Avalonia app down; the dev console host stops the generic host. When no
    /// handler is set the endpoint answers 501 - it never half-stops the host on its own.
    /// </summary>
    public Action? OnShutdownRequested { get; set; }

    /// <summary>
    /// Issue #331: registered cc-launcher processes. The relay endpoints use this to
    /// forward lifecycle verbs to the correct machine's launcher loopback REST API.
    /// </summary>
    public LauncherRegistry Launchers { get; } = new();

    private readonly DirectorEndpointClient _client;
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private readonly GatewayTurnBriefStore _turnBriefStore;
    private readonly KeyVault _keyVault;
    private readonly WorkListStore _workLists;
    private readonly CronJobStore _cronJobs;
    private readonly CronRunHistoryStore _cronRuns;
    private readonly Running.CronEngine _cronEngine;
    // The cron firing sweep (epic #479, #483): wakes ~every minute and fires due jobs. Created in
    // StartAsync, disposed in StopAsync.
    private System.Threading.Timer? _cronTimer;
    private static readonly TimeSpan CronSweepInterval = TimeSpan.FromMinutes(1);
    private readonly Running.WorkListRunnerManager _runnerManager = new();
    // Issue #218: Gateway-owned clock for when each session entered the red / NEEDS-YOU state.
    private readonly NeedsYouClock _needsYouClock = new();
    // Issue #549: the always-on turn-brief stamping pipeline (GatewayTurnBriefAgent) is retired.
    // TurnEndWatcher stays and runs unconditionally - its only job now is firing voice
    // auto-refresh on turn-end for voice sessions, and clearing the stale voice/text cache on
    // the Working transition. The wingman brain (BrainSupervisor) is kept; voice mode uses it.
    private TurnEndWatcher? _turnEndWatcher;
    private Wingman.WingmanVoiceService? _voiceService;
    // Editable/versioned wingman instructions (issue #537); the voice translator reads the active set.
    private readonly Wingman.WingmanInstructionsStore _instructionsStore = new();
    // Shared training-data store: the voice service WRITES captures, the instructions A/B test READS them.
    private readonly Wingman.WingmanTrainingStore _trainingStore = new();
    private System.Threading.Timer? _voiceSweepTimer;
    private AdvertisedEndpointMonitor? _endpointMonitor;
    // Issue #629: the durable, bounded, restart-surviving retry queue behind the login-telemetry
    // relay. Constructed here (loads any events a previous run left on disk), wired into the relay
    // endpoint, started flushing in StartAsync, and disposed in StopAsync.
    private readonly Api.TelemetryRetryQueue _telemetryQueue;
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
    /// <param name="workListsPath">
    /// Override the named work-list store file (issue #301). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\worklists.json</c>
    /// (the keyvault.json precedent).
    /// </param>
    /// <param name="cronJobsPath">
    /// Override the cron-job store file (epic #479, #482). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\cronjobs.json</c>.
    /// </param>
    /// <param name="cronRunsPath">
    /// Override the cron run-history store file (epic #479, #483). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\cronruns.json</c>.
    /// </param>
    /// <param name="telemetryQueuePath">
    /// Override the durable telemetry retry-queue store file (issue #629). Tests pass an isolated temp
    /// path; production omits it for the shared default at
    /// <c>%LOCALAPPDATA%\cc-director\config\director\telemetry-queue.json</c>.
    /// </param>
    /// <param name="telemetryQueueMaxSize">
    /// Override the telemetry retry-queue bound (issue #629). Tests pass a small value to exercise
    /// eviction; production omits it for <see cref="Api.TelemetryRetryQueue.DefaultMaxSize"/>.
    /// </param>
    /// <param name="telemetryRetryInterval">
    /// Override how often the telemetry retry-queue flusher re-attempts delivery (issue #629). Tests
    /// pass a short interval; production omits it for a sensible default.
    /// </param>
    public GatewayHost(int port = DefaultPort, string? token = null, bool authEnabled = false, string? instancesDirectory = null, int? cockpitProxyPort = null, string? turnBriefDirectory = null, string? keyVaultPath = null, string? workListsPath = null, string? cronJobsPath = null, string? cronRunsPath = null, string? devicesPath = null, string? telemetryQueuePath = null, int? telemetryQueueMaxSize = null, TimeSpan? telemetryRetryInterval = null)
    {
        Port = port;
        Token = token ?? GatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry(instancesDirectory);
        Devices = new Pairing.DeviceRegistry(devicesPath);
        AuthEnabled = authEnabled;
        _client = new DirectorEndpointClient(Token);
        _cockpitProxyPort = cockpitProxyPort ?? Cockpit.CockpitSupervisor.ResolvePort();
        _serveProvisioner = new TailscaleServeProvisioner(Registry, Port, Cockpit.CockpitSupervisor.ResolvePort());

        // The Gateway's in-process warm brain (issue #184): supervisor only - the chosen
        // tool spawns on first use (the brief agent's first ask, or Settings' Restart Brain).
        // The tool and model are an EXPLICIT Gateway-level choice (issue #393, building on the
        // pinned-model #204): the wingman is the product's one always-on intelligence point,
        // so it runs the configured tool + model deliberately instead of a hardcoded claude.exe
        // and the account-default model. Both default to claude + opus when unset, so existing
        // fleets are unchanged. A config change applies on the next Gateway restart.
        BrainTool = BrainToolConfig.Get();
        BrainModel = BrainModelConfig.Get();
        var brainDriver = AgentDrivers.For(BrainTool);
        FileLog.Write($"[GatewayHost] brain tool: {BrainTool}, model: {BrainModel}");
        Brain = new BrainSupervisor(
            new HostedAgentOptions
            {
                WorkingDirectory = Path.Combine(CcStorage.Root(), "brain"),
                AgentArgs = $"{ClaudeDriver.DefaultArgs} --model {BrainModel}",
                Log = FileLog.Write,
            },
            // Host the chosen agent through its own driver. As of issue #510 the wingman agent is
            // chosen from the machine's registered agents (any AgentKind), since the driver-level
            // hostability work landed in issue #509; BrainToolConfig.Get validates the configured
            // name is a recognised AgentKind (default ClaudeCode).
            agentFactory: o => new CcDirector.HostedAgent.HostedAgent(o, brainDriver));
        _turnBriefStore = new GatewayTurnBriefStore(turnBriefDirectory);
        // Production omits keyVaultPath for the shared default; tests pass an isolated path so
        // they never touch the real %LOCALAPPDATA% key store.
        _keyVault = new KeyVault(keyVaultPath);
        // Named work lists persist across a Gateway restart (issue #301): one JSON file in the
        // Gateway data dir, loaded here (stale claims released) and written through on every
        // mutation. Tests MUST pass an isolated path so they never touch the real store.
        _workLists = new WorkListStore(workListsPath ?? Path.Combine(CcStorage.Root(), "worklists.json"));
        // Cron-job definitions persist across a Gateway restart (epic #479, #482): one JSON file in
        // the Gateway data dir, loaded here (next-run times recomputed) and written through on every
        // mutation - the WorkListStore precedent. Tests MUST pass an isolated path so they never
        // touch the real store.
        _cronJobs = new CronJobStore(cronJobsPath ?? Path.Combine(CcStorage.Root(), "cronjobs.json"));
        // Cron run history + the firing engine (epic #479, #483). The engine resolves each due job's
        // target Director from the registry and starts a session over the shared client (the same
        // path the work-list runner uses). The background sweep timer is started in StartAsync.
        _cronRuns = new CronRunHistoryStore(cronRunsPath ?? Path.Combine(CcStorage.Root(), "cronruns.json"));
        // Durable telemetry retry queue (issue #629): one JSON file under the Gateway config directory
        // (the DeviceRegistry / gateway-token precedent), loaded here so events a previous run left
        // undelivered survive a restart. A short-timeout forwarder client keeps a slow/unreachable
        // backend from holding a flush pass open. Tests pass an isolated path + a small bound + a short
        // retry interval so they never touch the real store and can exercise eviction quickly.
        var telemetryForwardClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _telemetryQueue = new Api.TelemetryRetryQueue(
            telemetryQueuePath ?? Path.Combine(CcStorage.Config(), "director", "telemetry-queue.json"),
            telemetryForwardClient,
            telemetryRetryInterval ?? TimeSpan.FromSeconds(30),
            telemetryQueueMaxSize ?? Api.TelemetryRetryQueue.DefaultMaxSize);
        // A cron job targets a MACHINE (#503): resolve it to a Director at fire time, launching one
        // via the launcher (the shipped /machines/{m}/director/start relay, #331) if none is running.
        var cronTargetResolver = new Running.RegistryDirectorTargetResolver(
            () => Registry.ListDirectors(),
            new Running.RelayDirectorLauncher(Port, Token));
        // A work-list cron job (#484) drains a named list via the shipped #274 runner on the resolved
        // Director, launching the drain in the background on the shared runner manager.
        var cronWorkListRunner = new Running.DirectorCronWorkListRunner(
            _workLists,
            cronTargetResolver,
            _runnerManager,
            new Running.DirectorWorkListDrainLauncher(_workLists, _client));
        // Run-complete notifications (issue #622, the deferred "notify on completion" piece of #479).
        // The notifier rides the EXISTING fleet channel - the per-Director doorbell event ring
        // (DirectorEvents, #330) observed at GET /directors/{id}/events - and optionally POSTs the same
        // payload to a per-job webhook. The deep link is built from the resolved Director's tailnet
        // endpoint (the same source the /sessions aggregation uses for ViewUrl); the gw query roots on
        // this Gateway's loopback base. The webhook client is short-timeout, best-effort.
        var cronNotifier = new Running.GatewayCronNotifier(
            DirectorEvents,
            directorId =>
            {
                var d = Registry.Get(directorId);
                return d is null ? null : (d.TailnetEndpoint ?? d.ControlEndpoint);
            },
            $"http://127.0.0.1:{Port}",
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        _cronEngine = new Running.CronEngine(
            _cronJobs, _cronRuns, new Running.DirectorCronSessionStarter(_client, cronTargetResolver),
            cronWorkListRunner, cronNotifier, new Running.SystemClock());
    }

    /// <summary>
    /// One-time bootstrap of the central vault from the user environment (option A). If the vault
    /// does not yet carry OPENAI_API_KEY, seed it from the environment (process, then User scope on
    /// Windows). Never clobbers an existing vault value (the Cockpit is the live rotation surface).
    /// Key name matches <see cref="Core.Configuration.OpenAiKeyResolver.KeyName"/>.
    /// </summary>
    private void SeedKeyVaultFromEnvironment()
    {
        const string keyName = "OPENAI_API_KEY";
        var fromEnv = Environment.GetEnvironmentVariable(keyName);
        if (string.IsNullOrWhiteSpace(fromEnv) && OperatingSystem.IsWindows())
            fromEnv = Environment.GetEnvironmentVariable(keyName, EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(fromEnv))
        {
            FileLog.Write($"[GatewayHost] no {keyName} in the environment to seed the vault from");
            return;
        }

        var seeded = _keyVault.SetIfAbsent(keyName, fromEnv.Trim());
        FileLog.Write(seeded
            ? $"[GatewayHost] seeded vault {keyName} from the user environment (one-time bootstrap)"
            : $"[GatewayHost] vault already has {keyName}; left as-is (vault is the source of truth)");
    }

    /// <summary>
    /// Pre-build voice for voice sessions that are idle and missing it, so the session list shows
    /// them "voice ready" BEFORE the person enters - including after a gateway restart (the voice-
    /// session set is persisted). Gentle: at most a few per cycle, idle sessions only (a working
    /// session regenerates on its turn-end). Best-effort; never throws into the timer.
    /// </summary>
    private async Task SweepVoiceSessionsAsync()
    {
        var vs = _voiceService;
        if (vs is null) return;
        try
        {
            var directors = Registry.ListDirectors();
            if (directors.Count == 0) return;
            var generated = 0;
            foreach (var sid in vs.VoiceSessionIds())
            {
                if (generated >= 3) break;          // gentle on the serialized brain
                if (vs.HasVoice(sid)) continue;     // already cached, nothing to do
                foreach (var d in directors)
                {
                    var ep = (d.ControlEndpoint ?? d.TailnetEndpoint ?? "").TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(ep)) continue;
                    var s = await _client.GetSessionAsync(ep, sid);
                    if (s is null) continue;        // not owned by this Director
                    var st = s.ActivityState ?? "";
                    if (st is "Idle" or "WaitingForInput" or "WaitingForPerm")
                    {
                        FileLog.Write($"[GatewayHost] voice sweep: pre-building voice for idle session {sid}");
                        await vs.GenerateAsync(sid, ep, CancellationToken.None);
                        generated++;
                    }
                    break;  // found the owning Director
                }
            }
        }
        catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep error: {ex.Message}"); }
    }

    /// <summary>
    /// The Gateway's warm brain (issue #184): a claude.exe this process hosts itself - no
    /// Director dependency. Dormant until first use; RestartAsync is the recovery verb.
    /// </summary>
    public BrainSupervisor Brain { get; }

    /// <summary>The agent tool the brain runs as (issue #393), resolved at construction from
    /// config.json "brain_tool" (default: <see cref="BrainToolConfig.Default"/>, Claude Code).
    /// A config change applies on the next Gateway restart.</summary>
    public Core.Agents.AgentKind BrainTool { get; }

    /// <summary>The model the brain is pinned to (issue #204), resolved at construction
    /// from config.json "brain_model" (default: <see cref="BrainModelConfig.Default"/>).
    /// Recorded on every brief; a config change applies on the next Gateway restart.</summary>
    public string BrainModel { get; }

    /// <summary>Gateway-side turn-brief storage (issue #185): append-only, fleet-wide.</summary>
    public GatewayTurnBriefStore TurnBriefs => _turnBriefStore;

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={Port}");

        // Option A bootstrap (docs/install/INSTALLATION.md section 4): a Gateway install guarantees
        // OPENAI_API_KEY is in the user environment. Seed the central vault from it ONCE here so the
        // Cockpit shows the key as set and Directors can pull it immediately. The vault is the live
        // source of truth thereafter - rotating the key in the Cockpit overwrites this seed, and we
        // never clobber an existing vault value (SetIfAbsent).
        SeedKeyVaultFromEnvironment();

        // Subscribe the Tailscale provisioner BEFORE Registry.Start() so the initial
        // file-discovery load fires OnDirectorAdded into it and every Director port
        // gets an HTTPS mapping without anyone re-running a script.
        _serveProvisioner.Start();
        Registry.Start();

        // Issue #331: start the stale-launcher sweep timer so launchers that crash
        // without unregistering are evicted after 90 s.
        Launchers.StartSweep();

        // Registry is now loaded with the current Director set: run the first self-healing
        // reconcile - re-assert the front door, drop serve mappings for Directors that died
        // while the Gateway was down (orphans -> 502 from a phone), and sweep any leaked
        // ephemeral-port mappings (issue #179). The provisioner repeats this on a timer.
        _serveProvisioner.Reconcile();

        // Issue #325: re-verify each HTTP-registered Director's advertised endpoint every
        // heartbeat cycle (15 s) - an advertised name that goes bad AFTER the registration-time
        // handshake (#223/#224) is flagged unreachable-by-name on the registration within two
        // cycles, and auto-clears when the name answers again.
        _endpointMonitor = new AdvertisedEndpointMonitor(Registry, _client);
        _endpointMonitor.Start();

        // Issue #549: the always-on turn-brief stamping pipeline is retired. TurnEndWatcher stays
        // and runs unconditionally - a small always-running watcher whose only job is firing voice
        // auto-refresh for voice sessions on turn-end, and clearing the stale voice/text cache on
        // the Working transition. It no longer depends on a brief agent existing. PUSH-fed since
        // #186 by Director doorbell pings and heartbeat snapshots (wired into the endpoints below);
        // the only pull left is the one-time startup catch-up sweep.
        FileLog.Write("[GatewayHost] StartAsync: starting the turn-end watcher (voice auto-refresh only; turn-brief pipeline retired in #549)");
        _voiceService ??= new Wingman.WingmanVoiceService(ct => Brain.GetAsync(ct), _keyVault, _client, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent);
        _turnEndWatcher = new TurnEndWatcher(
            Registry, _client,
            onTurnEnd: signal =>
            {
                // Voice sessions (issue #531): the turn just finished on its own, so re-make the
                // spoken summary + audio in the background. It is then "voice ready" in the session
                // list with no wait. Non-voice sessions do nothing here - the watcher is voice-only.
                if (_voiceService is { } vs && vs.IsVoiceSession(signal.SessionId))
                {
                    FileLog.Write($"[GatewayHost] turn-end -> voice auto-refresh: sid={signal.SessionId}");
                    _ = vs.GenerateAsync(signal.SessionId, signal.DirectorEndpoint, CancellationToken.None);
                }
            },
            onSessionWorking: sid =>
            {
                // Working again: the cached voice/text summary is now stale - clear it so the list
                // stops showing it ready and nothing stale plays (issue #531). It regenerates on the
                // next turn-end.
                _voiceService?.OnSessionWorking(sid);
            });
        // First tick = the startup catch-up sweep; then the 15s reconcile poll for
        // Directors that never push (file-discovered locals, old builds).
        _turnEndWatcher.Start();

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
            // Issue #469: a per-device key issued at enrollment is a valid Bearer credential
            // alongside the shared machine token, so an enrolled Director authenticates with its
            // own unique key. The shared token still authenticates the host's own browser/cookie
            // surface, but it is no longer the path a NEW device uses to get in (that is pairing).
            var requireToken = new AuthMiddleware.RequireToken { Token = Token, Devices = Devices };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        // Browser-aware front door (the Cockpit sitemap): a PERSON navigating to /sessions,
        // /directors, or /cockpit (Accept: text/html) gets the Cockpit page; programs keep
        // getting JSON from the explicit endpoints below. After auth, before routing.
        var cockpitForwarder = new Cockpit.CockpitProxy.CockpitForwarder(_app.Services, _cockpitProxyPort);
        Cockpit.CockpitProxy.UseBrowserPageRoutes(_app, cockpitForwarder);

        // Enable ASP.NET WebSocket support so the per-session proxy can recognize an inbound WS
        // upgrade (ctx.WebSockets.IsWebSocketRequest) and accept it (AcceptWebSocketAsync) for the
        // hand-rolled terminal/dictation stream proxy. The old YARP forwarder used the raw upgrade
        // feature and needed no middleware; the manual proxy (SessionWsForwarder) does. Pass-through
        // for upgrades it does not accept, so the YARP-forwarded Cockpit/Blazor circuit is unaffected.
        _app.UseWebSockets();

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
                // Any observed Working state means a new turn is in progress, so the cached voice/text
                // summary is stale - clear it (broad net for turns started outside the voice app, e.g.
                // the desktop cockpit). The voice-turn endpoint also clears deterministically on send.
                if (string.Equals(newState, "Working", StringComparison.OrdinalIgnoreCase))
                    _voiceService?.OnSessionWorking(sessionId);
                if (_turnEndWatcher is null) return;
                var endpoint = Registry.Get(directorId)?.ControlEndpoint;
                if (string.IsNullOrEmpty(endpoint)) return;
                _turnEndWatcher.Observe(sessionId, newState, endpoint);
            },
            // Issue #549: the assessed-state refutation (issue #186) is dropped with the pipeline
            // (Option A) - "needs you" reverts to the Director's raw mechanical signal. The
            // turn-brief stamping (issue #187 briefStampFor) is gone too; the brief agent that
            // wrote those fields no longer exists.
            // Voice mode (issue #531): while the gateway's wingman is producing a session's spoken
            // summary, paint it yellow ("not ready yet") and back to red. Independent of any brief
            // agent and never via the Director's --print explain.
            voiceGeneratingFor: sid => _voiceService?.IsGenerating(sid) == true,
            // Issue #553: whether the gateway has fetchable, playable cached audio for this session -
            // the single truthful "voice you can play right now" signal. Holds a voice-mode waiting
            // session yellow until this is true, then lets it go red (SessionOrdering.IsVoicePreparing).
            voiceAudioReadyFor: sid => _voiceService?.HasVoice(sid) == true,
            // Issue #218: stamp the Gateway-owned NeedsYouSince entry clock onto each session.
            needsYouStampFor: (sid, isRed) => _needsYouClock.Stamp(sid, isRed),
            // Issue #212 W3: enrich the Interrupted sessions list from the durable brief store. Always
            // available (read-only is safe even with briefing disabled), and the brief survives
            // the Director that died - which is exactly when we need it.
            interruptedBriefFor: sid =>
            {
                var b = _turnBriefStore.Latest(sid);
                return (b?.NeedsYou?.RailLine, b?.Headline);
            },
            // Issue #212 W4: the restore endpoint builds its continuation context from the
            // full brief history; the store outlives the dead Director, so this serves
            // sessions whose owner is gone.
            briefHistoryFor: sid => _turnBriefStore.List(sid),
            // Issue #288: record session->Director ownership as the fleet is aggregated, so the WS
            // proxy can return 503 (owner offline) rather than 404 for a session whose Director went dark.
            owners: SessionOwners,
            // Issue #330: doorbell event-vocabulary pings land in the per-director event ring.
            directorEvents: DirectorEvents,
            // Issue #376: async voice-turn submit/poll rides the host-owned job store.
            turnJobs: TurnJobs);

        // Issue #268: the two raw per-session WebSocket legs (live Terminal stream + dictation)
        // proxied through the Gateway so a remote Cockpit talks same-origin to the Gateway and
        // never needs a Director's own (possibly loopback) address. Mapped endpoints win over the
        // fallback Cockpit proxy below.
        // Pass the fleet token (issue #457): the proxy injects it as the Bearer on every forward
        // so an auth-enabled Director (LAN mode) accepts the call. Harmless for auth-off Directors.
        SessionWsProxyEndpoints.Map(_app, Registry, _client, SessionOwners, Token);

        // Issue #469: device enrollment via local pairing code (the ONLY way a new device gets in).
        // POST /devices/register verifies+consumes the 4-digit code and issues a unique per-device
        // key; GET /devices is the host-readable registry listing. Mapped after the WS proxy so its
        // literal routes win over the catch-all session forwarder, same as the other literal routes.
        Api.DeviceEnrollmentEndpoint.Map(_app, Pairing, Devices);

        // Wingman-voice surface for the Cockpit's Voice tab (issue #531): drive one turn of a
        // session and have the persistent wingman brain translate the reply into speakable form,
        // plus the direct-to-wingman path. Backed by the same warm Brain the brief agent uses.
        _voiceService ??= new Wingman.WingmanVoiceService(ct => Brain.GetAsync(ct), _keyVault, _client, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent);
        GatewayWingmanVoiceEndpoint.Map(_app, Registry, _client, ct => Brain.GetAsync(ct), _keyVault, _voiceService, instructionsProvider: () => _instructionsStore.ActiveContent);
        // Editable/versioned wingman instructions settings surface (issue #537), incl. A/B test
        // over saved training sessions (reads the shared training store; uses the warm brain).
        WingmanInstructionsEndpoint.Map(_app, _instructionsStore, _trainingStore, ct => Brain.GetAsync(ct));
        // The gateway OWNS keeping voice sessions' summaries pre-built (issue #531): a gentle
        // background sweep regenerates voice for any idle voice session that is missing it, so the
        // list shows it ready BEFORE you enter - including after a gateway restart (the voice-session
        // set is persisted). Turn-end regeneration + the deterministic voice-turn path also feed it.
        _voiceSweepTimer = new System.Threading.Timer(_ => { _ = SweepVoiceSessionsAsync(); }, null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));

        // Central key vault (docs/architecture/gateway/GATEWAY_KEY_VAULT.md): set keys once
        // here (via the Cockpit Keys page); Directors pull them on demand. Inherits the
        // host-wide token middleware above.
        VaultEndpoints.Map(_app, _keyVault);

        // Gateway Centralization Phase 1 (issue #628): the inbound login-telemetry RELAY. The Director
        // POSTs its login-telemetry event here (instead of the cloud) and the Gateway forwards it on,
        // so the Gateway becomes the single egress. Best-effort: a backend failure is logged and the
        // caller still gets a non-5xx; the inbound access token is forwarded unchanged but NEVER logged.
        // Inherits the host-wide token middleware above (the existing gateway.token convention).
        // Issue #629: the relay enqueues every accepted event into the durable retry queue (which owns
        // delivery, retry-with-backoff, FIFO flush, the bound, and restart survival) instead of
        // forwarding inline. The flush loop is started just below in StartAsync.
        TelemetryRelayEndpoint.Map(_app, _telemetryQueue);

        // Transcription routing (issue #506): the Gateway serves the WHOLE routing target
        // (mode + base URL + model + key) for its configured transcription mode, so a connected
        // Director stops hardcoding the URL/mode. Composes URL+key server-side from the one pure
        // resolver, so the bring-your-own OpenAI key is never paired with the devthrottle.com URL.
        TranscriptionRoutingEndpoint.Map(_app, _keyVault);

        // Transcription smoke test: the Cockpit Settings page records a short clip and posts it here;
        // the Gateway transcribes it with the SAME configured mode + key the pipeline uses and returns
        // the text. Proves the stored key actually works (the status dot only proves one is stored).
        TranscriptionTestEndpoint.Map(_app, _keyVault);

        // Named work lists (issue #273, child of #270): an ordered list of structured item refs
        // { source, id, area? } + a single-consumer claim, the object the product skill writes to,
        // the Cockpit views, and the queue runner drains. Persisted to worklists.json across
        // Gateway restarts since issue #301 (write-through + reload-on-start with stale-claim
        // release). Inherits the host-wide token middleware above and is reachable cross-machine
        // like the rest of the Gateway surface.
        WorkListEndpoints.Map(_app, _workLists);

        // Cron jobs (epic #479, part 1 = #482): the REST CRUD surface over the cron-job definition
        // store. Manages definitions only - the background firing engine is part 2 (#483).
        // Persisted to cronjobs.json across restarts (write-through + reload-on-start with
        // next-run recompute). Inherits the host-wide token middleware above.
        CronJobEndpoints.Map(_app, _cronJobs);

        // Cron firing surface (epic #479, part 2 = #483): run-now and run-history over the engine.
        // Scheduled firing runs on the background sweep timer started below in StartAsync.
        CronRunEndpoints.Map(_app, _cronEngine, _cronRuns);

        // The queue runner (issue #274, child 3 of #270): the thin orchestration that turns a named
        // work list into unattended, ordered runs - one implementation session per github item,
        // watched to its IMPL-LOOP-TERMINAL sentinel (child 1, #272) before advancing. All runner
        // logic lives HERE at the Gateway; the Director host gains nothing (criterion 7). The
        // same-machine single-drain guard (criterion 8) lives on the shared runner manager.
        WorkListRunnerEndpoints.Map(_app, _workLists, Registry, _client, _runnerManager);

        // Issue #331: launcher registration + cross-machine Director lifecycle relay.
        // Launchers POST /launchers/register on startup; relay callers POST
        // /machines/{machine}/director/restart|start|stop to reach that machine's Director.
        MachineEndpoints.Map(_app, Launchers);

        // The Cockpit Settings page surface (docs/architecture/gateway/SETTINGS_OWNERSHIP.md):
        // one snapshot GET plus brain-restart and autostart actions. Reads this host directly
        // for status/brain; run mode + autostart come from SettingsHooks (GatewayApp-owned).
        SettingsEndpoints.Map(_app, this);

        // The fleet-level wingman pipeline view (issue #239): GET /wingman/queue. Issue #549
        // retired the always-on stamping machine that fed it, so there is no live pipeline to
        // snapshot - pass null and the endpoint answers an honest idle "Disabled" snapshot.
        WingmanQueueEndpoints.Map(_app, snapshot: null);

        // Gateway-served turn briefs (issue #185): the Cockpit and the interrupted/restore paths
        // read briefs from the store HERE. Issue #549 removed the only WRITER (GatewayTurnBriefAgent),
        // so the store is read-only-serving (effectively empty going forward); the read endpoints
        // stay so existing callers degrade cleanly. The explain trigger (#217) rode the brief agent,
        // which is gone - pass null and the explain endpoint answers 503.
        TurnBriefGatewayEndpoints.Map(_app, _turnBriefStore,
            sid => _turnBriefStore.Latest(sid) is not null ? "Briefed" : "None",
            requestExplainAsync: null);

        // One URL: everything no explicit endpoint above claimed falls through to the
        // loopback Cockpit (docs/plans/one-url-cockpit.md). Mapped LAST by design.
        Cockpit.CockpitProxy.Map(_app, cockpitForwarder);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://127.0.0.1:{Port} (version {version})");

        // Cron firing sweep (epic #479, #483): wake ~every minute and fire due jobs. The first tick
        // also catches up a fire that came due while the Gateway was down (at most once per job).
        _cronTimer = new System.Threading.Timer(_ => SweepCron(), null, CronSweepInterval, CronSweepInterval);
        FileLog.Write($"[GatewayHost] cron sweep started: every {CronSweepInterval.TotalSeconds:0}s");

        // Issue #629: start the durable telemetry retry-queue flusher. It drains any events restored
        // from disk on construction (so a backend outage that spanned the previous run's lifetime now
        // delivers) and every event the relay enqueues going forward, in FIFO order, retrying with
        // backoff while the backend is unreachable.
        _telemetryQueue.StartFlushing();
    }

    /// <summary>
    /// The cron sweep timer callback (a boundary - it owns the try/catch so a sweep failure never
    /// crashes the timer thread). Fires due jobs; per-job failures are isolated inside the engine.
    /// </summary>
    private void SweepCron()
    {
        try
        {
            _ = _cronEngine.EvaluateDueAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] cron sweep FAILED: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        try { _cronTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] cron timer dispose error: {ex.Message}"); }
        _cronTimer = null;

        try { _endpointMonitor?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] endpoint monitor dispose error: {ex.Message}"); }
        _endpointMonitor = null;

        // Issue #629: stop the telemetry retry-queue flusher. The queue file is written through on
        // every mutation, so any undelivered events are already on disk and reload on the next start -
        // stopping never loses them.
        try { await _telemetryQueue.DisposeAsync(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] telemetry queue dispose error: {ex.Message}"); }

        // Turn-end watcher + voice sweep first (they drive the brain), then the brain itself - the
        // supervisor's dispose gracefully stops the hosted claude.exe (never leaked).
        try { _voiceSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep dispose error: {ex.Message}"); }
        _voiceSweepTimer = null;
        try { _turnEndWatcher?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] watcher dispose error: {ex.Message}"); }
        _turnEndWatcher = null;
        try { Brain.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] brain dispose error: {ex.Message}"); }

        // Unsubscribe from registry events. We deliberately do NOT tear down the serve
        // mappings: the Directors are still alive and reachable, and a Gateway restart
        // re-asserts every mapping on Start().
        _serveProvisioner.Dispose();
        Registry.Dispose();
        Launchers.Dispose();
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
