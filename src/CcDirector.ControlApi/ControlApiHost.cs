using CcDirector.Core.Configuration;
using CcDirector.Core.Fleet;
using CcDirector.Core.Instances;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Hosts the Director's long-running services: the per-session state tracking, the maintained
/// session-hook files, the Gateway HTTP client and the outbound tunnel, and the same-machine
/// instance registration.
///
/// THERE IS NO LISTENER HERE, AND THERE MUST NOT BE. The Remove-the-network-port mission deleted
/// the Director's HTTP Control API - the Kestrel host, the loopback routes, and the port allocator
/// that picked from [7879..7898]. Everything an agent or another machine does to this Director
/// arrives over the Gateway, down the tunnel this host dials OUT (<see cref="GatewayStreamClient"/>).
/// Process lifecycle (stop, update check) is a named signal, not a route. If a change here needs a
/// caller to reach this process directly, the answer is a tunnel verb or a file, never a socket.
///
/// The class name is kept from the listener era because it is referenced throughout the desktop,
/// the tests and the documentation; what it hosts today is the Director's service plane.
///
/// Lifecycle:
///   StartAsync() -> starts the state services, writes instances/{guid}.json, dials the Gateway
///   StopAsync()  -> farewells the Gateway, stops the services, deletes the registration file
/// </summary>
public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly RepositoryRegistry? _repositoryRegistry;
    private readonly string _version;
    private readonly Func<Task> _requestShutdownAsync;

    public string DirectorId { get; }

    /// <summary>
    /// Per-session persistent JSONL log. Exposed so the Avalonia UI can persist
    /// rendered agent-view widgets to <c>agent-view.jsonl</c> alongside the raw
    /// stream and turn summaries we already write. Null until <see cref="StartAsync"/>.
    /// </summary>
    public Core.Storage.SessionLogManager? SessionLogManager => _sessionLogManager;

    private InstanceRegistration? _registration;
    private GatewayClient? _gatewayClient;
    // Issue #1176 (Phase 1a): the outbound push-stream client, running alongside _gatewayClient when
    // gateway.streamMode is on. Null when stream mode is off, so the Director behaves exactly as today.
    private GatewayStreamClient? _streamClient;
    /// <summary>The turn-push mission's Director side: keeps the Gateway's stored conversation current for
    /// every session by pushing what it has not seen. Null when there is no Gateway.</summary>
    private TurnPusher? _turnPusher;

    /// <summary>
    /// Remove-the-network-port mission, phase 1b: the hashes of the Gateway session keys this Director's
    /// live sessions hold, so it can re-register them on a tunnel reseed. Owned by the host - it outlives
    /// the stream client, which is rebuilt whenever the Gateway settings change, and the keys stamped into
    /// already-running sessions' environments cannot be re-issued when that happens.
    /// </summary>
    private readonly SessionGatewayKeys _sessionGatewayKeys = new();
    private readonly SemaphoreSlim _gatewayReapplyLock = new(1, 1);

    /// <summary>
    /// The one home of this Director's Gateway-connection truth. Host-owned so it survives
    /// GatewayClient replacement on settings changes; the desktop indicator subscribes to its
    /// Changed event and the Gateway tunnel marks itself connected/reconnecting in it.
    /// </summary>
    public GatewayConnectionMonitor GatewayMonitor { get; } = new();

    // Cancels the injected-text refresh poll (started in StartAsync) when the host stops.
    private readonly CancellationTokenSource _injectedTextRefreshCts = new();

    // How often a connected Director re-downloads the Gateway-owned injected text, so a Cockpit save
    // reaches its next session launch without a reconnect or restart. The connect-triggered refresh gives
    // immediacy on reconnect; this bounds staleness while already connected.
    private static readonly TimeSpan InjectedTextRefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Snooze Length mission (Phase 3): the seam the desktop drives to record/clear a Gateway-owned
    /// snooze THROUGH the Gateway (so a desktop snooze gets the same timer the phone/cockpit get),
    /// instead of setting <c>Session.OnHold</c> in-process. Backed by the live <see cref="GatewayClient"/>
    /// (which already holds the resolved Gateway address + fleet token), so it reuses the Director's
    /// existing Gateway connection. Null while the Gateway is not configured (no client) - the desktop
    /// gates the Snooze button on <see cref="GatewayMonitor"/> being Connected anyway.
    /// </summary>
    public IGatewayHold? GatewayHold => _gatewayClient;

    /// <summary>
    /// The desktop's last-known copy of the user's snooze lengths and default, read FROM the Gateway. The
    /// session Snooze menu reads this because it must build without blocking, and because the setting
    /// is Gateway-owned - this machine's own config.json is not the user's answer unless this machine
    /// happens to be the Gateway's. Reads the GatewayHold property lazily, so it follows a GatewayClient
    /// replaced on a settings change rather than pinning the one that existed at construction.
    /// </summary>
    public SnoozeOptionsCache SnoozeOptions { get; }

    /// <summary>
    /// Fetch the latest Gateway turn brief for a session - the desktop Wingman tab's source.
    /// Null when no Gateway is configured/connected or none stamped yet; the caller then shows
    /// the local explain instead.
    /// </summary>
    public Task<Gateway.Contracts.TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
        => _gatewayClient?.GetLatestTurnBriefAsync(sessionId, ct) ?? Task.FromResult<Gateway.Contracts.TurnBriefDto?>(null);

    /// <summary>
    /// Issue #1627: the FLEET-WIDE session roster - every session on every machine - as the desktop fleet
    /// map's source. Backed by the live <see cref="GatewayClient"/>, which already holds the resolved
    /// Gateway address and fleet token, so it reuses the Director's existing outbound Gateway connection.
    ///
    /// This is an outbound HTTP GET, NOT a tunnel call, and that is deliberate: the tunnel is push-only
    /// (the Director pushes its own sessions up; there is no verb on DirectorHub that returns a roster).
    /// "Tunnel-only" means the Gateway never DIALS the Director - it does not mean the Director stopped
    /// calling out. GatewayClient survives for exactly these on-demand outbound operations.
    ///
    /// The returned sessions arrive with the Gateway's own answers already stamped - SessionRole,
    /// EffectiveColor, StateLabel - because the Gateway folds them across the whole fleet before
    /// responding. The desktop READS those; it must not recompute them (only the Gateway can see a
    /// controller that lives on another machine).
    ///
    /// Null when no Gateway is configured - the caller shows "not connected" rather than an empty fleet,
    /// which would be a lie.
    /// </summary>
    public Task<List<Gateway.Contracts.SessionDto>>? ListFleetSessionsAsync(CancellationToken ct = default)
        => _gatewayClient?.ListFleetSessionsAsync(ct);

    /// <summary>The fleet roster WITH per-Director reachability, so a caller that must not act on a
    /// partial roster (the destructive reaper) can fail closed when the fleet view is incomplete.</summary>
    public Task<(List<Gateway.Contracts.SessionDto> Sessions, List<Gateway.Contracts.DirectorReachabilityDto> Reachability)>? ListFleetSessionsWithReachabilityAsync(CancellationToken ct = default)
        => _gatewayClient?.ListFleetSessionsWithReachabilityAsync(ct);

    private TurnSummaryCache? _turnSummaryCache;
    private SessionStatusWingman? _statusWingman;
    private ProactiveExplainService? _proactiveExplain;
    private TerminalStateDetector? _terminalStateDetector;
    private Core.Activity.ActivityEventOutbox? _activityOutbox;
    private Core.Activity.ActivityEventProducer? _activityProducer;
    private ActivityEventUploader? _activityUploader;
    private RepoStatePusher? _repoStatePusher;
    private Core.Git.SessionGitStatusMonitor? _sessionGitStatusMonitor;
    private TransientErrorAutoResume? _transientErrorAutoResume;
    private TerminalSessionRecorder? _sessionRecorder;
    private Core.Storage.TurnReviewLogger? _turnReviewLogger;
    private Core.Sessions.SessionRecordsWatcher? _recordsWatcher;
    private Core.Pi.PiSessionRebinder? _piSessionRebinder;
    // Remove-the-network-port mission, phase 3: the two halves of the session-hook channel that
    // replaced the three Control API routes. Both started by StartSessionStateServices, because
    // neither touches the bound port and both must run even when it fails to bind - a Director whose
    // Control API cannot start must still give its agents their preamble and still track their
    // transcripts.
    private Core.Sessions.SessionPreambleMaintainer? _preambleMaintainer;
    private Core.Sessions.SessionPointerWatcher? _pointerWatcher;
    private Core.Storage.ConversationIngestor? _conversationIngestor;
    private Core.Storage.SessionLogManager? _sessionLogManager;
    private readonly string? _instancesDirectory;
    private bool _stopped;
    private bool _stateServicesStarted;

    /// <summary>
    /// Construct a Director host. There is nothing to configure about a listener because there is
    /// no listener; see the class comment.
    /// </summary>
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, RepositoryRegistry? repositoryRegistry = null, string? directorId = null, string? instancesDirectory = null, Core.Git.RepositoryMonitor? repositoryMonitor = null)
    {
        _repositoryMonitor = repositoryMonitor;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));

        // Warms itself the moment the Gateway connection goes green, so a menu opened later
        // reads a list that is already there.
        SnoozeOptions = new SnoozeOptionsCache(() => GatewayHold);
        SnoozeOptions.AttachTo(GatewayMonitor);

        // The injected text is Gateway-owned too (Cockpit -> Injected text): download it when the
        // Gateway connection goes green so a session launched later injects the user's current choice
        // without waiting on the network at launch. Fire-and-forget - a failed refresh keeps the
        // last-known cache, which the launch path reads synchronously.
        GatewayMonitor.Changed += () =>
        {
            if (GatewayMonitor.Status == GatewayConnectionStatus.Connected)
                _ = RefreshInjectedTextAsync();
        };
        _repositoryRegistry = repositoryRegistry;
        // Tests pass an isolated instances directory so test Directors never appear in a real
        // Gateway's discovery (and a real Director never appears in a test Gateway's).
        _instancesDirectory = instancesDirectory;

        // Production: persisted id (same across restarts so the Gateway recognizes us).
        // Tests: inject a fresh id per fixture so parallel runs don't collide on the
        // single instances/{id}.json file.
        DirectorId = directorId ?? DirectorIdStore.LoadOrCreate();
    }

    /// <summary>
    /// Download the Gateway-owned injected text - and the workflow catalog index that rides it
    /// (Workflows mission, phase 5) - into the Director's on-disk caches. Swallows failure on
    /// purpose - an unreachable Gateway must not crash the host, and a failed refresh leaves the
    /// last-known caches in place for the launch path to read.
    /// </summary>
    // An instance method, not static: the skill-placement report at the end of this cycle needs this
    // Director's own identity and Gateway client to say WHICH machine it is reporting about.
    private async Task RefreshInjectedTextAsync()
    {
        try
        {
            await new InjectedTextStore().RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] injected-text refresh FAILED (keeping the last-known cache): {ex.Message}");
        }
        try
        {
            await new WorkflowIndexStore().RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] workflow-index refresh FAILED (keeping the last-known cache): {ex.Message}");
        }
        // A SEPARATE try, deliberately: sharing one with the workflow refresh above would mean a
        // Gateway that fails the workflow read silently skips the skill read entirely, and the machine
        // would lose its skill index for a reason that has nothing to do with skills. Each index
        // fails on its own and keeps its own last-known cache.
        try
        {
            await new SkillIndexStore().RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] skill-index refresh FAILED (keeping the last-known cache): {ex.Message}");
        }
        // And the skills THEMSELVES, materialized to disk so the next session launch can put them
        // where its agent looks. Its own try for the same reason as above: the index and the bodies
        // fail independently, and losing one must not silently cost the other.
        try
        {
            await new CcDirector.Core.Skills.SkillStoreRefresh().RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] skill store refresh FAILED (keeping the current store): {ex.Message}");
        }
        // And REPORT whether the skills we serve could actually be read here. The Gateway can see that it
        // served a skill and still be wrong about whether any agent can read it - only this machine
        // observes that, and while nobody reported it a machine where nothing landed looked exactly like a
        // machine where everything was fine. Its own try, for the same reason as the two above.
        try
        {
            await PushSkillPlacementAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] skill placement report FAILED (will retry next cycle): {ex.Message}");
        }
        // Remove-the-network-port mission, phase 3: THE SECOND JOB OF THIS REFRESH. All three stores a
        // session's fleet preamble renders from have just been re-downloaded, so every live session's
        // maintained hook-output file is rewritten now.
        //
        // This is what makes the file as fresh as the routes it replaced. Those routes rendered on
        // demand - but from these same caches, so the moment the caches move is the only moment there is
        // anything new to render. Rewriting HERE, rather than on a timer of its own, means the file and
        // the deleted route would always have produced the same bytes. Its own try, like the four above:
        // a store that failed to refresh must not also cost every session its preamble.
        try
        {
            _preambleMaintainer?.RewriteAll();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] session preamble rewrite FAILED (will retry next cycle): {ex.Message}");
        }
    }

    /// <summary>
    /// Ship what the last session launches observed about skill placement. Reads the Director's own local
    /// record - written on the launch path, which never touches the network - and pushes the current
    /// outcome per agent family. Nothing to report is not an error and sends nothing.
    /// </summary>
    private async Task PushSkillPlacementAsync()
    {
        var records = CcDirector.Core.Skills.SkillPlacementLog.ReadAll();
        if (records.Count == 0)
            return;

        var client = _gatewayClient;
        if (client is null)
            return;

        var request = new CcDirector.Gateway.Contracts.SkillPlacementPushRequest
        {
            DirectorId = DirectorId,
            MachineName = Environment.MachineName,
            Reports = records.Select(r => new CcDirector.Gateway.Contracts.SkillPlacementReportDto
            {
                AgentKind = r.AgentKind,
                Held = r.Held,
                Reachable = r.Reachable,
                StoreMissing = r.StoreMissing,
                ObservedAtUtc = r.ObservedAtUtc,
                Problems = r.Problems.Select(p => new CcDirector.Gateway.Contracts.SkillPlacementProblemDto
                {
                    SkillId = p.SkillId,
                    Target = p.Target,
                    Fault = p.Fault,
                }).ToList(),
            }).ToList(),
        };

        var response = await client.PushSkillPlacementAsync(request).ConfigureAwait(false);
        if (response is not null)
            FileLog.Write($"[ControlApiHost] skill placement reported: {response.Stored} agent(s)");
    }

    /// <summary>
    /// Re-download the Gateway-owned injected text on a slow timer for as long as the host runs. The
    /// connect-triggered refresh only fires when the connection state changes, so an already-connected
    /// Director would otherwise never see a Cockpit edit until it reconnected; this closes that gap so the
    /// setting has the same "no restart" guarantee the snooze lengths have. Best-effort - each tick keeps
    /// the last-known cache on failure - and stops cleanly when the host cancels the token.
    /// </summary>
    private async Task PollInjectedTextAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(InjectedTextRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await RefreshInjectedTextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping - a clean end, not a failure.
        }
    }

    /// <summary>
    /// Start the Director's services, write the instance registration file, and dial the Gateway.
    ///
    /// This method used to start Kestrel on a loopback port and return the port it bound. There is
    /// no port any more - nothing in this process listens - so what remains is the service plane:
    /// the session-state services, the session-environment wiring, the same-machine registration
    /// file, and the outbound Gateway connections.
    /// </summary>
    public Task StartAsync()
    {
        FileLog.Write($"[ControlApiHost] StartAsync: directorId={DirectorId}");

        // Start the session-state services FIRST, before anything that can fail. They observe the
        // SessionManager and the terminal buffers only, so a Director whose Gateway is missing or
        // whose registration directory is unwritable still tracks its sessions' badge colours.
        // (When a listener existed, this ordering was what kept a port-bind failure from freezing
        // every session on its last colour; the port is gone, the property is kept.)
        StartSessionStateServices();

        // Issue #846: one-time session-number backfill at Director startup. By this point every
        // session the manager already tracks - sessions restored from persistence or carried over
        // from a pre-#820 build - is in place, so a single pass numbers any that still lack a
        // three-digit number (sessions created from now on are numbered at creation by
        // RaiseSessionCreated -> AssignSessionNumber). The method itself logs per-session; the
        // count is logged here.
        var backfilledAtStartup = _sessionManager.BackfillNumbers();
        FileLog.Write($"[ControlApiHost] Startup session-number backfill assigned {backfilledAtStartup} number(s)");

        var gatewayConfig = Core.Configuration.GatewayConfig.Load();

        // Issue #1181, Task 3b: the desktop-side enforced dictation lock. Project the durable PENDING
        // delivery marker (issue #1188) from the shared dictation-uploads store into Session's send
        // path, so a human typing on the desktop is refused while a dictation is inbound to that
        // session. Same rule the Gateway front door enforces. Idempotent to set on each start.
        Core.Sessions.Session.DictationLockCheck = id => Core.Sessions.DictationLockReader.IsSessionLocked(id);

        // Issue #1111: the same projection, read in ONE pass, for the roster's per-tick refresh of every
        // session. Wired from the same reader as the line above so the two can never disagree about a
        // marker; the single-session hook stays for the single-session question.
        Core.Sessions.Session.DictationLockedIdsCheck = () => Core.Sessions.DictationLockReader.LockedSessionIds();

        // Issue #1357: the signed-in DevThrottle user for a session's preamble. The account credential
        // lives on the Gateway (issue #651), so this reads GET /account/status (email + provider +
        // nickname) through a short-lived cache. GatewayConfig.Load is re-read each resolve so a settings
        // change is picked up without a restart. Standalone/no-Gateway resolves to null (line omitted).
        var signedInUserProvider = new Core.Account.SignedInUserProvider(Core.Configuration.GatewayConfig.Load);

        // The session environment. CC_DIRECTOR_ID names which Director a session belongs to - identity,
        // not an address - and the machine-local verbs (the automation browsers) are scoped by it.
        _sessionManager.DirectorId = DirectorId;

        // Remove-the-network-port mission, phase 5: CC_DIRECTOR_API and CC_DIRECTOR_TOKEN are GONE.
        // Nothing answers on this machine, so handing every agent a live-looking loopback address -
        // and a credential for it - would send a straggling caller to a dead door and hide the
        // dependency for as long as the variable existed. A session's ONLY door to the fleet is the
        // Gateway pair below.

        // Remove-the-network-port mission, phase 1b: every session gets a credential for the GATEWAY,
        // bound to its own id and limited to the agent routes, so an agent's command line can reach the
        // fleet through the Gateway without being handed this Director's own account-wide key.
        //
        // The registration is sent the moment the key is minted, which is inside the environment build - so
        // it goes up the tunnel BEFORE the agent process is even launched, and is long since accepted by the
        // time that process boots and runs its first command. It is not awaited here because session
        // creation must never block on the network; a registration that does not land is re-sent on the next
        // tunnel reseed, and until then the session's calls are REFUSED rather than falling back to anything.
        _sessionManager.GatewayUrl = gatewayConfig.IsEnabled ? gatewayConfig.Url : null;
        _sessionManager.GatewaySessionCredentialSource = sessionId =>
        {
            if (_streamClient is null) return null;
            var key = _sessionGatewayKeys.Mint(sessionId);
            var registration = _sessionGatewayKeys.RegistrationFor(sessionId);
            if (registration is not null)
                ObserveSessionKeyRegistration(_streamClient, registration, sessionId);
            return key;
        };

        // A session whose launch throws after its key was minted never reaches the roster, so the reaper
        // never revokes it. Wired to the same pair the reap uses: forget the hash here, and owe the
        // Gateway a revocation that the reseed will deliver.
        _sessionManager.GatewaySessionCredentialRevoker = sessionId =>
        {
            if (_sessionGatewayKeys.Forget(sessionId))
                _streamClient?.RevokeSessionKey(sessionId.ToString());
        };
        _sessionManager.SignedInUserAccessor = () => signedInUserProvider.CurrentSnapshot;
        _ = Task.Run(async () =>
        {
            try { await signedInUserProvider.ResolveAsync(CancellationToken.None); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] signed-in user warm-up failed (best-effort): {ex.Message}"); }
        });

        // Keep the Director's cache of the Gateway-owned injected text current while it runs, so a Cockpit
        // edit reaches the next session launch without a restart. Only when a Gateway is configured - with
        // none, RefreshAsync is a no-op and polling would just log every tick.
        if (gatewayConfig.IsEnabled)
        {
            FileLog.Write($"[ControlApiHost] starting injected-text refresh poll every {InjectedTextRefreshInterval.TotalSeconds:0}s");
            _ = Task.Run(() => PollInjectedTextAsync(_injectedTextRefreshCts.Token));
        }

        // Issue #1292: the Gateway is the authority for the fleet-unique session number. Wire the
        // SessionManager to ask the CURRENT GatewayClient (read the FIELD lazily so a settings change
        // that replaces the client via ReapplyGatewayAsync is picked up without re-wiring). A null /
        // failed answer (Gateway disabled or unreachable) makes the Director fall back to a local
        // offline number. Release rides the same client when a session ends.
        _sessionManager.FleetNumberSource = (sessionId, ct) =>
            _gatewayClient?.AllocateSessionNumberAsync(sessionId.ToString(), ct) ?? Task.FromResult<int?>(null);
        _sessionManager.FleetNumberRelease = sessionId =>
            _gatewayClient?.ReleaseSessionNumber(sessionId.ToString());
        // Only ask the Gateway (asynchronously) when one is configured; otherwise number locally and
        // synchronously. Kept in step with the config in ReapplyGatewayAsync.
        _sessionManager.FleetNumberingActive = gatewayConfig.IsEnabled;

        _registration = new InstanceRegistration(DirectorId, _version, _instancesDirectory,
            // devthrottle_internal#1176: stamp the instance's display name into the same-machine discovery
            // file too, so a file-discovered Director carries the same name a tunnel Hello would.
            displayName: InstanceContext.DisplayName);
        _registration.Register();

        // Remove-the-network-port mission, phase 5: clean up what the listener era left on this
        // machine. Best-effort, off the startup path.
        _ = Task.Run(CleanUpListenerLeftovers);

        // If gateway.url is configured, register with the Gateway over outbound HTTP and start the
        // heartbeat, and dial the tunnel. Disabled (no-op) when local-only.
        _gatewayClient = BuildGatewayClient(gatewayConfig);
        _gatewayClient.Start();
        _turnPusher = BuildTurnPusher(gatewayConfig);
        _streamClient = BuildStreamClient(gatewayConfig);
        _streamClient?.Start();
        _turnPusher?.Start();
        WireDoorbellPush();
        WireRepositoryPush();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove what this Director's OWN listener era left behind, exactly once per artifact:
    ///
    ///   - The port reservation file (config\director\ports\{directorId}.port), written by the
    ///     deleted PortAllocator so a restart could re-claim the same port. Nothing reads it now,
    ///     and the schedule command line used to read the whole directory as evidence of which
    ///     Directors a data root owns - stale files would keep answering a question whose premise
    ///     is gone.
    ///   - The Tailscale Serve mapping for that port, if one is still published. Builds older than
    ///     the tunnel-only cut published an inbound HTTPS front door per Director port; the cut
    ///     tore it down on every start, and this is the LAST teardown - after the reservation file
    ///     is gone, the port this Director used to bind is unknowable, so the mapping must go now.
    ///
    /// Reading the reservation BEFORE deleting it is what makes the serve teardown possible at all:
    /// the file is the only remaining record of which port this Director ever bound.
    /// </summary>
    private void CleanUpListenerLeftovers()
    {
        try
        {
            var reservation = Path.Combine(Core.Storage.CcStorage.Config(), "director", "ports", $"{DirectorId}.port");
            if (!File.Exists(reservation))
                return;

            var raw = File.ReadAllText(reservation).Trim();
            if (int.TryParse(raw, out var oldPort) && oldPort > 0)
            {
                try
                {
                    using var provisioner = new TailscaleServeSelfProvisioner(oldPort);
                    provisioner.RemoveOwnMapping();
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlApiHost] stale Serve teardown for old port {oldPort} failed (best-effort): {ex.Message}");
                }
            }

            File.Delete(reservation);
            FileLog.Write($"[ControlApiHost] removed the listener-era port reservation {reservation}" +
                (raw.Length > 0 ? $" (old port {raw})" : ""));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] listener-leftover cleanup failed (best-effort, retried next start): {ex.Message}");
        }
    }

    /// <summary>
    /// Send a session key registration WITHOUT waiting for it, but observe how it turns out.
    ///
    /// The send is still not awaited by the caller, and must not be: session creation cannot block on
    /// the network (see the note at the call site). What changed is that the result is no longer
    /// thrown away. The call site used to read `_ = _streamClient.RegisterSessionKeyAsync(...)`, and
    /// that discard cost two things.
    ///
    /// First, this is the only place that knows WHICH session the key belongs to and that it was
    /// minted for a real launch rather than for the health probe. The stream client logs its own
    /// failure, but it logs it as one more transport-level line; the fact that a session was just
    /// handed a credential the Gateway will refuse belongs here, in the launch path, said in those
    /// terms.
    ///
    /// Second, a discarded task is an UNOBSERVED task. Everything RegisterSessionKeyAsync does today
    /// is inside its own try, so nothing escapes - but that is a property of the callee that the call
    /// site was silently depending on, and the day it stops being true the exception disappears into
    /// the runtime instead of the log.
    ///
    /// Issues #2457 and #2459: on 2026-08-05 every registration in the fleet was refused at once
    /// because the hosted Gateway predated the RegisterSessionKey hub method. The sessions that came
    /// up in that window went looking for a fault in their own credential, which was fine.
    /// </summary>
    private static void ObserveSessionKeyRegistration(
        GatewayStreamClient stream, SessionKeyRegistration registration, Guid sessionId)
    {
        // STARTED HERE, SYNCHRONOUSLY, AND ONLY THEN OBSERVED. The call runs on this thread to its
        // first await, which is what puts the registration on the tunnel before the agent process is
        // launched - let alone booted - exactly as the call site promises.
        //
        // Task.Run would look equivalent and is NOT. It defers the whole call until the thread pool
        // gets to it, so a session could present its key before the registration had even begun and be
        // answered 401, and a short-lived session could be revoked before the queued work started and
        // then have its dead credential re-registered behind it. Observing a result must not change
        // WHEN the work happens.
        var pending = stream.RegisterSessionKeyAsync(registration);
        _ = ObserveAsync(pending, sessionId);

        // Parameters named apart from the locals above on purpose: a local function parameter that
        // shadows an enclosing local is a compile error in some scopes, and this is not the place to
        // find that out.
        static async Task ObserveAsync(Task<bool> registrationTask, Guid id)
        {
            // A task root is an entry point, so the catch belongs here: there is no caller left to
            // throw to, and an escape would be an unobserved exception rather than a reported one.
            try
            {
                var registered = await registrationTask.ConfigureAwait(false);
                if (!registered)
                    FileLog.Write(
                        $"[ControlApiHost] session {id} was launched with a Gateway key the Gateway did NOT "
                        + "accept, so its fleet commands will answer 401 until a reseed registers it. The reason is "
                        + "NOT known here: RegisterSessionKeyAsync returns the same false for a hub-side refusal, a "
                        + "tunnel that was not connected, and a transport failure. If this repeats for EVERY session, "
                        + "check whether the Gateway is older than this Director.");
            }
            catch (Exception ex)
            {
                FileLog.Write(
                    $"[ControlApiHost] session key registration for {id} FAULTED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Mint, register, and hand out a short-lived Gateway session key for the desktop's fleet-tool
    /// health probe - the same kind of credential a real session launch stamps, taken through the
    /// same mint-and-register path, so the probe proves exactly what a spawned session would
    /// experience. Registration is AWAITED here (unlike a session launch) because the probe is
    /// about to present the key immediately and a race would report a healthy machine as broken.
    ///
    /// Returns null for ONE state and one only: there is no Gateway to reach (none configured, or
    /// no tunnel). That is a fact about this machine's connection and the caller renders it as the
    /// mission's accepted no-Gateway trade.
    ///
    /// A Gateway that is connected and REFUSES the registration throws
    /// <see cref="GatewayRefusedSessionKeyException"/> instead, and the difference matters more than
    /// it looks. Returning null there would report "no Gateway" - a benign, expected state - for a
    /// Gateway that is right there and rejecting us, which is a real fault wearing a harmless label,
    /// and precisely the kind of plausible-but-wrong state this mission keeps finding.
    ///
    /// The caller turns that specific exception into a RED Sessions row naming an out-of-date
    /// Gateway. It used to turn it into no verdict, which renders as no row at all - so the one
    /// screen built to surface this failure stayed blank through a fleet-wide outage (#2457, #2459).
    /// Any OTHER exception is still no verdict, and that is still right: an unexplained failure must
    /// never be handed a confident label.
    ///
    /// This is not hypothetical. The registration is keyed by session id and the hub does not check
    /// that the id belongs to a live session of the calling Director - the mission's own inspection
    /// filed that as a defect. If it is hardened, THIS probe's synthetic id is exactly what stops
    /// being accepted, and the failure must be audible on the day it happens.
    ///
    /// The caller MUST invoke the returned Revoke when the probe is done; the key is bound to a
    /// probe-only id that never joins the session roster, so nothing else will ever end it.
    /// </summary>
    public async Task<FleetToolProbeCredential?> MintFleetToolProbeCredentialAsync()
    {
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        if (!gatewayConfig.IsEnabled || _streamClient is not { } stream)
            return null;

        var probeId = Guid.NewGuid();
        var key = _sessionGatewayKeys.Mint(probeId);
        var registration = _sessionGatewayKeys.RegistrationFor(probeId);
        if (registration is null)
        {
            // The key was just minted, so its registration must exist; if it does not, the store
            // itself is wrong and saying so beats reporting a connection state that is not the fault.
            _sessionGatewayKeys.Forget(probeId);
            throw new InvalidOperationException(
                "the fleet-tool probe key was minted but no registration could be built for it");
        }

        var registered = await stream.RegisterSessionKeyAsync(registration).ConfigureAwait(false);
        if (!registered)
        {
            if (_sessionGatewayKeys.Forget(probeId))
                _streamClient?.RevokeSessionKey(probeId.ToString());
            throw new GatewayRefusedSessionKeyException(
                "the Gateway is connected but refused to register the fleet-tool probe key, so the "
                + "tools cannot be checked - this is a Gateway-side refusal, not a missing Gateway");
        }

        return new FleetToolProbeCredential(gatewayConfig.Url, key, () =>
        {
            if (_sessionGatewayKeys.Forget(probeId))
                _streamClient?.RevokeSessionKey(probeId.ToString());
        });
    }

    /// <summary>
    /// Start the per-session state-tracking services. Every service here observes the
    /// <see cref="SessionManager"/> and its sessions' terminal buffers only -- none of them
    /// touch Kestrel or the bound port -- so they run independently of whether the Control API
    /// binds.
    ///
    /// This is deliberately the FIRST thing StartAsync does. The desktop "needs you" badge is
    /// <see cref="Session.StatusColor"/>, written by <see cref="SessionStatusWingman"/> from the
    /// <see cref="ActivityState"/> that <see cref="TerminalStateDetector"/> drives (byte -> Working;
    /// <see cref="TerminalStateDetector.QuietThreshold"/> of silence -> WaitingForInput = red).
    /// In the listener era these once ran AFTER the port bind, and a port-allocation failure
    /// aborted StartAsync before they started -- leaving every session frozen on its last colour,
    /// so a silent session could sit forever and never flip to red. The port is gone; the lesson
    /// (state services before anything that can fail) is kept. Idempotent: safe to call
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

        // Remove-the-network-port mission, phase 3: the session-hook channel. The maintainer keeps each
        // live session's SessionStart hook output CURRENT (the routes it replaced rendered on demand, so
        // a file written once at launch would have served a user their old injected text and hidden
        // newly published skills). The watcher reads the drop box a Claude hook reports its rotated
        // transcript pointer into.
        //
        // The signed-in user is read through the SessionManager's accessor rather than captured: this
        // runs before StartAsync wires the account provider, and the account itself resolves from the
        // Gateway after boot, so anything captured here would name nobody for the life of the process.
        _preambleMaintainer = new Core.Sessions.SessionPreambleMaintainer(
            _sessionManager, () => _sessionManager.SignedInUserAccessor?.Invoke());
        _preambleMaintainer.Start();

        _pointerWatcher = new Core.Sessions.SessionPointerWatcher(_sessionManager);
        _pointerWatcher.Start();
        // A removed session's drop file goes with it, so nothing is left that a later sweep could
        // re-apply to an id that has been reused.
        _sessionManager.OnSessionRemoved += s => _pointerWatcher?.Forget(s.Id);

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
        // The activity-evidence producer (docs/PLAN-trustworthy-working-start-2026-07-24.md, increment 2):
        // records submissions, state transitions with shadow causes, unexplained terminal wakes, and
        // transcript-proven turns into a durable outbox the uploader below drains to the Gateway ledger.
        // OBSERVE-ONLY - it changes no session state and gates nothing.
        _activityOutbox = new Core.Activity.ActivityEventOutbox();
        _activityProducer = new Core.Activity.ActivityEventProducer(_sessionManager, _activityOutbox);
        _activityProducer.Start();

        _terminalStateDetector = new TerminalStateDetector(_sessionManager, driveState: true, _activityProducer);
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

        // The terminal recorder: logs every session's resolved grid (on change, with the activity
        // state) to build the ground-truth corpus for offline analysis/learning. Observe-only and
        // capped per session. See docs/wingman/WINGMAN.md.
        //
        // Capture is OFF by default, and switched on by a visible setting - session_recording.enabled
        // in config.json, or the CC_DIRECTOR_RECORD_SESSIONS override. It used to run on every install
        // unless someone found an environment variable mentioned only in a source comment, which made
        // an internal engineering corpus - every screen every agent has drawn, secrets included, with
        // no age limit - an invisible product default. What we collect for our own benefit has to be
        // something the user can see they turned on.
        //
        // The recorder itself is constructed and started UNCONDITIONALLY, because purge-on-removal
        // must not follow the capture setting: when this host was only created with capture on, an
        // install that upgraded from the default-on release kept every pre-existing recording forever -
        // the sessions that owned them were removed while no purge handler existed to notice.
        var captureEnabled = Core.Configuration.SessionRecordingConfig.IsEnabled();
        _sessionRecorder = new TerminalSessionRecorder(_sessionManager, captureEnabled: captureEnabled);
        _sessionRecorder.Start();
        if (captureEnabled)
            FileLog.Write("[ControlApiHost] Terminal session recording is ON (session_recording.enabled)");

        // Per-turn review log: one record each time a session flips Working -> needs-you
        // (our detector's transition, no hooks). Terminal + what the Wingman said/did, 7-day
        // retention. See CcStorage.TurnReviewLogs().
        _turnReviewLogger = new Core.Storage.TurnReviewLogger(_sessionManager);
        _turnReviewLogger.Start();

        // The model producer (issue #1637): on the same turn-end trigger, ask each session's driver
        // what model the agent is currently using and stamp Session.CurrentModel, which rides the
        // snapshot/delta path to the Gateway (SessionDto.CurrentModel) for the model-usage
        // statistics. Turn-end-driven so the records read never runs per roster poll.
        _recordsWatcher = new Core.Sessions.SessionRecordsWatcher(_sessionManager);
        _recordsWatcher.Start();

        // A Pi session's transcript is named by the id the Director launched it with - until pi's /new
        // starts a new file under an id of its own. On the same turn-end trigger, find that file and
        // relink the session to it, so the turn push, the gauge and the model report follow (#2670).
        _piSessionRebinder = new Core.Pi.PiSessionRebinder(_sessionManager);
        _piSessionRebinder.Start();

        // The prompt record (issue #1551): on the same turn-end trigger, read each session's
        // conversation out of the agent's own transcript, join on where each prompt came from, and PUSH
        // it to the Gateway's log. The Director captures because it is the only thing that sees a prompt
        // or knows its origin; the Gateway stores because it is what the whole fleet reports to and what
        // moves to the server. The Director keeps no copy.
        _conversationIngestor = new Core.Storage.ConversationIngestor(
            _sessionManager, new GatewayPromptSink(() => _gatewayClient), _activityProducer);
        _conversationIngestor.Start();

        // Drain the activity outbox to the Gateway ledger. Late-binds the Gateway client per tick (the
        // prompt sink's pattern) and deletes an outbox record only on the Gateway's acknowledgement.
        _activityUploader = new ActivityEventUploader(() => _gatewayClient, _activityOutbox);
        _activityUploader.Start();
        // The repo-state feed (issue #2118): every six hours, ship this machine's branches and worktrees
        // to the Gateway so the morning report can make its stale-worktree and unmerged-branch
        // recommendations. Only when a repository registry exists - with none there is nothing to report.
        // Late-binds the Gateway client per cycle, exactly like the uploader above.
        if (_repositoryRegistry is not null)
        {
            _repoStatePusher = new RepoStatePusher(
                // Late-bound per cycle: the Gateway client is created after this and can be replaced when the
                // configuration changes. A null client is an install with no Gateway - nowhere to report to.
                (req, ct) => _gatewayClient is { } c ? c.PushRepoStateAsync(req, ct) : Task.FromResult<RepoStatePushResponse?>(null),
                _repositoryRegistry, DirectorId, Environment.MachineName,
                liveSessions: LiveSessionRefs);
            _repoStatePusher.Start();
        }

        // Every live session's uncommitted file count, refreshed every fifteen seconds. This poll used to
        // live in the desktop window, which meant the number existed only where it was rendered and never
        // reached the Gateway - so the Cockpit roster had nothing to show. Here it lands on the Session,
        // which ControlEndpoints.Map puts on the wire, and the desktop rail reads the same number rather
        // than probing git a second time.
        _sessionGitStatusMonitor = new Core.Git.SessionGitStatusMonitor(_sessionManager);
        _sessionGitStatusMonitor.Start();
    }

    /// <summary>
    /// Construct the Gateway client. Tunnel-only: the Director dials OUT and is reached only down that
    /// stream, so there is no inbound endpoint to verify-before-advertise anymore. The old issue #197
    /// own-port probe was already dead once the Director stopped self-provisioning a serve front door
    /// (the wiring here was gated on a serve provisioner this Director never creates), so it is removed.
    /// </summary>
    private GatewayClient BuildGatewayClient(GatewayConfig gatewayConfig)
        => new(gatewayConfig, DirectorId, _version, SnapshotSessionStates, GatewayMonitor);

    /// <summary>
    /// Issue #1176 (Phase 1a): build the push-stream client, or null when stream mode is off / no Gateway
    /// is configured. Its snapshot source is <see cref="SnapshotFullSessions"/>, which uses the SAME
    /// mapper as the local /sessions endpoint (review #6) so a pushed row equals a pulled row.
    /// </summary>
    private GatewayStreamClient? BuildStreamClient(GatewayConfig gatewayConfig)
    {
        // Gateway Cleanup mission (tunnel-only): the streamMode gate is GONE. If a Gateway is configured,
        // the Director dials the tunnel - it is the ONLY connection. Null only when local-only (no gateway.url).
        if (!gatewayConfig.IsEnabled) return null;
        // Issue #1177 (Phase 1): the stream client's down-channel dispatcher reuses the SAME in-process
        // SessionCommandExecutor the Control API endpoints call, so a command executed over the stream is
        // byte-for-byte identical to the same command over HTTP.
        return new GatewayStreamClient(gatewayConfig, DirectorId, _version, SnapshotFullSessions,
            // The services are read per-command (inside the lambda) so they reflect the fields once
            // StartAsync has initialized them - BuildStreamClient runs before _proactiveExplain /
            // _turnSummaryCache are set. Additive: verbs that need no service ignore it (issue #1177 inc 6).
            // Gateway Cleanup mission, Phase 0 (wave 3): also carry the Director version and the repository
            // registry so the director-level reads that stamp/read them (facts, handover, repos-list) serve the
            // same value over the tunnel that their REST route served.
            cmd => DispatchTunnelCommandAsync(cmd),
            // Turn push: every Hello hands back what the Gateway already holds of this Director's sessions'
            // conversations; the pusher seeds from it and backfills the rest.
            // Only when the Gateway actually READ its store. A Gateway that could not read answers a
            // silence, and reconciling against a silence would re-push every conversation from the start.
            onHello: capabilities =>
            {
                if (capabilities.TurnWatermarksKnown) _turnPusher?.SeedWatermarks(capabilities.TurnWatermarks);
            },
            // Gateway Cleanup mission, Phase 0 (up-stream): pass the SessionManager so the four connection-bound
            // stream verbs work - their terminal/file producers read session and file state from it and stream
            // frames up this same connection.
            sessionManager: _sessionManager,
            // Gateway Cleanup mission (tunnel-only): the tunnel drives the desktop connectivity light directly -
            // connected = green (a live stream IS the proven two-way link), reconnecting = yellow.
            monitor: GatewayMonitor,
            // Repositories mission (#510 phase C): the repository/worktree snapshot rides the same
            // tunnel; null when this host was built without a monitor (tests, older callers).
            repoSnapshot: _repositoryMonitor is null ? null : SnapshotRepositories,
            // devthrottle_internal#1176: read the display name from the named-instance registry on EVERY
            // reseed (not InstanceContext.DisplayName, a start-once static) so a rename lands fleet-wide
            // on the next ~10s Hello without a Director restart.
            displayName: () => NamedInstanceRegistry.Get(InstanceContext.Slug)?.DisplayName,
            // Remove-the-network-port phase 1b: every live session's Gateway key rides the reseed, so a
            // registration lost to a tunnel drop or a Gateway restart heals on the next connection instead
            // of leaving that session's agent permanently refused.
            sessionKeys: () => _sessionGatewayKeys.LiveRegistrations(),
            pendingRevocations: () => _sessionGatewayKeys.PendingRevocations(),
            onRevocationConfirmed: id =>
            {
                if (Guid.TryParse(id, out var confirmed))
                    _sessionGatewayKeys.RevocationConfirmed(confirmed);
            });
    }

    /// <summary>
    /// The turn-push mission's Director side. Reads each session's conversation through the one resolver
    /// (<see cref="TurnPushBuilder"/>) and pushes what the Gateway has not seen, over the stream client that
    /// is current at the time of the push (the field is read per call, so a Gateway settings change that
    /// rebuilds the stream does not strand the pusher on a dead one). Null when there is no Gateway.
    /// </summary>
    private TurnPusher? BuildTurnPusher(GatewayConfig gatewayConfig)
    {
        if (!gatewayConfig.IsEnabled) return null;
        return new TurnPusher(
            sessionIds: () => _sessionManager.ListSessions().Select(s => s.Id).ToList(),
            snapshot: sid =>
            {
                var session = _sessionManager.GetSession(sid);
                return session is null ? null : TurnPushBuilder.Snapshot(session);
            },
            push: (batch, ct) =>
            {
                var client = _streamClient;
                if (client is null) throw new InvalidOperationException("there is no Gateway stream to push turns over");
                return client.PushTurnsAsync(batch, ct);
            },
            canPush: () => _streamClient?.GatewaySupports("PushTurns") == true);
    }

    /// <summary>
    /// The Director-side dispatcher for a command that arrived over the tunnel. Almost every verb runs through
    /// the shared <see cref="SessionCommandExecutor"/> so it is byte-identical to its HTTP route. The one
    /// exception is <c>shutdown</c> (Gateway Cleanup mission): stopping the whole Director is a HOST concern,
    /// not a session-command concern - it has no place in a session executor and needs the host's shutdown hook -
    /// so it is handled here. <c>POST /shutdown</c> stays on the loopback floor for the local launcher; this is
    /// the Gateway-initiated REMOTE stop (<c>DELETE /directors/{id}</c>) taking the same in-process self-shutdown
    /// path, fired-and-forgotten (like the REST route) so the Ok result flushes before Kestrel and the stream tear down.
    /// </summary>
    private Task<DirectorCommandResult> DispatchTunnelCommandAsync(DirectorCommand cmd)
    {
        // Issue #2725 (restart epic, Phase 6): the two host-level verbs of the restart cycle. Host-level
        // for the same reason shutdown is - they are about THIS PROCESS, not a session - and answered here
        // rather than in the session executor.
        if (string.Equals(cmd.Verb, Gateway.Contracts.DirectorRestartVerbs.Eligibility, StringComparison.Ordinal))
            return Task.FromResult(AnswerRestartEligibility(cmd));
        if (string.Equals(cmd.Verb, Gateway.Contracts.DirectorRestartVerbs.Cycle, StringComparison.Ordinal))
            return Task.FromResult(StartRestartCycle(cmd));

        if (string.Equals(cmd.Verb, "shutdown", StringComparison.Ordinal))
        {
            FileLog.Write("[ControlApiHost] tunnel 'shutdown' command received; self-shutting-down");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await _requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] tunnel shutdown FAILED: {ex.Message}"); }
            });
            return Task.FromResult(DirectorCommandResult.Success());
        }

        return SessionCommandExecutor.DispatchAsync(_sessionManager, DirectorId, cmd,
            new SessionCommandServices { ProactiveExplain = _proactiveExplain, TurnSummaryCache = _turnSummaryCache, DirectorVersion = _version, Repositories = _repositoryRegistry, ReapplyGatewayAsync = ReapplyGatewayAsync });
    }

    /// <summary>
    /// The executable the launcher on this machine supervises - the ONE definition of that path, from the
    /// install layout, computed from the shared root this instance was started under.
    /// </summary>
    private static string SupervisedDirectorPath()
        => new CcDirector.Setup.Engine.InstallLayout(InstanceContext.SharedRoot)
            .PathFor(CcDirector.Setup.Engine.ComponentRegistry.Director);

    /// <summary>
    /// THE DRAIN SEAM. On a tree carrying the drain inside the Director (issue #2723) this is the real step
    /// over CreateDrain; on this tree it is the step that says the drain is not here - and says so BEFORE
    /// the owner is asked, through the eligibility answer, not only when the cycle runs.
    /// </summary>
    private static Restart.IRestartCycleDrain RestartDrainStep() => new Restart.NoDrainOnThisBuild();

    /// <summary>This Director's own answer to "am I the Director my launcher would restart, and can I drain?",
    /// read fresh.</summary>
    private static DirectorRestartEligibilityDto JudgeRestartEligibility()
    {
        var answer = Restart.RestartEligibility.Judge(InstanceContext.Slug, InstanceContext.IsDefault,
            Environment.ProcessPath, SupervisedDirectorPath());
        var drain = RestartDrainStep().Availability;
        answer.DrainAvailable = drain.Available;
        answer.DrainReason = drain.Reason;
        return answer;
    }

    /// <summary>The Gateway asks, before the owner is shown a request, whether a launcher restart would
    /// restart THIS Director. A read: it changes nothing.</summary>
    private DirectorCommandResult AnswerRestartEligibility(DirectorCommand cmd)
    {
        var answer = JudgeRestartEligibility();
        FileLog.Write($"[ControlApiHost] tunnel '{cmd.Verb}': eligible={(answer.Eligible.HasValue ? answer.Eligible.Value.ToString() : "unknown")} - {answer.Reason}");
        var result = DirectorCommandResult.Success(System.Text.Json.JsonSerializer.Serialize(answer,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        result.CommandId = cmd.CommandId;
        return result;
    }

    /// <summary>
    /// The owner accepted: run the cycle, alone, to the end. Answered as soon as the cycle has been TAKEN
    /// - the drain runs for up to ninety minutes and a command result cannot wait for it - and everything
    /// after this reaches the owner through the request's own report route.
    ///
    /// REFUSED, NEVER DEGRADED: without a Gateway client there is nowhere to drain to and nowhere to
    /// report; with a cycle already running a second would be two drains racing.
    /// </summary>
    private DirectorCommandResult StartRestartCycle(DirectorCommand cmd)
    {
        var client = _gatewayClient;
        if (client is null)
        {
            var fail = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict,
                "this Director is not connected to a Gateway, so it has nowhere to drain to and nowhere to report; the cycle is refused before touching anything.");
            fail.CommandId = cmd.CommandId;
            return fail;
        }

        DirectorRestartCycleOrder? order = null;
        try
        {
            order = System.Text.Json.JsonSerializer.Deserialize<DirectorRestartCycleOrder>(cmd.PayloadJson ?? "",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException) { /* refused below as an unreadable order */ }
        if (order is null || string.IsNullOrWhiteSpace(order.RequestId) || string.IsNullOrWhiteSpace(order.Machine))
        {
            var fail = DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "the restart order could not be read, or names no request or no machine; nothing was started.");
            fail.CommandId = cmd.CommandId;
            return fail;
        }

        var cycle = new Restart.DirectorRestartCycle(order,
            RestartDrainStep(),
            new Restart.GatewayClientRestartCycleGateway(client),
            JudgeRestartEligibility,
            Environment.ProcessPath);

        // CLAIMED HERE, SYNCHRONOUSLY, BEFORE "TAKEN" IS ANSWERED. Checking Running and then scheduling left a
        // window in which two commands were both told "taken" and only one cycle ran; the other's request
        // was never reported. The claim is the answer.
        if (!cycle.TryClaim())
        {
            var fail = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict,
                $"a restart cycle is already running on this Director for request {Restart.DirectorRestartCycle.Running?.Order.RequestId}; a second is refused before it touches anything.");
            fail.CommandId = cmd.CommandId;
            return fail;
        }

        FileLog.Write($"[ControlApiHost] tunnel '{cmd.Verb}': taking the restart cycle for request {order.RequestId} (asked by {order.RequestedBySessionName}: {order.Reason})");
        _ = Task.Run(async () =>
        {
            try { await cycle.RunAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] restart cycle {order.RequestId} FAILED: {ex}"); }
        });

        var ok = DirectorCommandResult.Success(System.Text.Json.JsonSerializer.Serialize(new { taken = true, requestId = order.RequestId }));
        ok.CommandId = cmd.CommandId;
        return ok;
    }

    /// <summary>
    /// Full per-session snapshot for the stream, built through the SAME <see cref="ControlEndpoints.Map"/>
    /// the local /sessions endpoint uses (issue #1176, review #6), so a pushed snapshot row is identical
    /// to what a pull would have returned for the same session.
    /// </summary>
    private List<SessionDto> SnapshotFullSessions()
        => _sessionManager.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList();

    /// <summary>
    /// This machine's live sessions as the worktree classifier wants them (issue #2118): a worktree a
    /// session is genuinely working in must be reported as occupied, never as abandoned work someone
    /// could delete. Read fresh on every collection rather than captured once, so a session that started
    /// since the last cycle still protects its worktree.
    /// </summary>
    private IReadOnlyList<Core.Git.LiveSessionRef> LiveSessionRefs()
        => _sessionManager.ListSessions()
            .Where(s => !string.IsNullOrWhiteSpace(s.RepoPath))
            .Select(s => new Core.Git.LiveSessionRef
            {
                RepoPath = s.RepoPath,
                Label = string.IsNullOrWhiteSpace(s.CustomName) ? s.Id.ToString() : s.CustomName!,
            })
            .ToList();

    private readonly Core.Git.RepositoryMonitor? _repositoryMonitor;
    private Timer? _repoPushDebounce;

    /// <summary>The repository snapshot for the stream and the local relay (#510 phase C).</summary>
    private List<RepoStatusDto> SnapshotRepositories()
        => (_repositoryMonitor?.Snapshot() ?? (IReadOnlyList<Core.Git.RepositoryStatus>)Array.Empty<Core.Git.RepositoryStatus>())
            .Select(s => RepositoryDtoMapper.Map(s, DirectorId, Environment.MachineName))
            .ToList();

    /// <summary>
    /// Push the repository snapshot on model changes, debounced: a scan streaming thirty upserts
    /// coalesces into one push a few seconds after it settles (plus the periodic reseed).
    /// </summary>
    private void WireRepositoryPush()
    {
        if (_repositoryMonitor is null)
            return;
        void Schedule()
        {
            _repoPushDebounce?.Dispose();
            _repoPushDebounce = new Timer(_ => _streamClient?.NotifyRepoSnapshot(), null,
                TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        _repositoryMonitor.Upserted += _ => Schedule();
        _repositoryMonitor.Removed += _ => Schedule();
        _repositoryMonitor.ScanCompleted += Schedule;
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
        {
            session.OnActivityStateChanged += (oldState, newState) =>
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
                // Turn push: the Director's OWN turn-end edge - FROM working TO waiting for input or a
                // permission, or idle - which is the moment the conversation has something new; and the
                // session ending from any state, for the final push. Deterministic, not sampled: this is the
                // edge itself, and a transition into a waiting state from anywhere else is not a turn ending.
                var turnEnded = oldState is Core.Sessions.ActivityState.Working
                                && newState is Core.Sessions.ActivityState.WaitingForInput
                                    or Core.Sessions.ActivityState.WaitingForPerm
                                    or Core.Sessions.ActivityState.Idle;
                if (turnEnded || newState is Core.Sessions.ActivityState.Exited)
                    _turnPusher?.Trigger(session.Id);
                // Issue #1176 (Phase 1a): push the changed session up the stream as a delta.
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            };
            // A hold change is a state change like any other, so it pushes like any other. Without this the
            // Director set the flag, answered the caller, and told the Gateway NOTHING: a hold toggle that
            // rode no activity change stayed invisible to every OTHER screen until the next 10-second
            // re-push - and because the Gateway derives each session's color and triage bucket from this
            // flag when it serves the roster, the session also sorted into the wrong bucket for that whole
            // window. Fires on every HoldState transition, including None <-> DeferredHold, which leaves
            // OnHold untouched but does change the label clients render.
            session.HoldStateChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));

            // Defect 14: the three colour inputs that were invisible to the Gateway until something ELSE
            // happened. The Gateway folds orange from IsTranscribing, orange from IsAutoExplaining, and
            // purple from IsBackgroundRunning - reading them off the pushed SessionDto - but nothing pushed
            // when they changed. Each raised its Director event to an empty room: OnIsBackgroundRunningChanged
            // and OnIsExplainingChanged had ZERO subscribers anywhere in the codebase, and
            // OnIsTranscribingChanged had exactly one (a desktop UI handler, which pushes nothing). So the
            // fact sat on the Session until some unrelated activity change happened to push a delta, or the
            // ten-second re-push came around - and those three colours lagged by up to that long.
            //
            // A colour input is a state change like any other, so it pushes like any other. This is the same
            // one-line shape as the hold push above, for the same reason.
            session.OnIsTranscribingChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            session.OnIsBackgroundRunningChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            session.OnIsExplainingChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));

            // THE GATE ON TWO OF THOSE THREE, and it was missing from the list above - which is the whole
            // trap in miniature. The comment right there says "the three colour inputs", and the fold reads
            // a FOURTH: yellow needs WingmanEnabled AND IsAutoExplaining, purple needs WingmanEnabled AND
            // IsBackgroundRunning (SessionOrdering.ResolveActivity). So a wingman-enabled=false command on a
            // session parked on its background task changes the right answer from purple "Background" to red
            // "Needs you" while NONE of the three above fire - nothing pushes, and the phone and Cockpit
            // keep the stale fold until the ten-second re-push.
            //
            // It hid because a gate is not the thing being rendered. Defect 14 went looking for "colour
            // inputs", found the flags, and did not count the condition guarding them. The rule that
            // actually holds: if the fold READS it, it pushes - no judgement about whether it feels like a
            // colour. Found by review of pull request 1598, after three earlier passes hunting exactly this.
            session.OnWingmanEnabledChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));

            // The uncommitted file count. Same one-line shape and same reason as the pushes above: without
            // it the Cockpit's "N chg" badge would only move on the ten-second full re-push, or on whatever
            // unrelated event happened to fire first. The monitor raises this ONLY when the number actually
            // changes, so an idle session pushes nothing.
            session.OnUncommittedCountChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
        }

        _sessionManager.OnSessionCreated += session =>
        {
            Attach(session);
            _gatewayClient?.NotifySessionState(session.Id.ToString(), session.ActivityState.ToString(),
                DoorbellEvents.SessionCreated);
            _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            _turnPusher?.Trigger(session.Id);   // a resumed session already has a conversation to push
        };
        _sessionManager.OnSessionRemoved += session =>
        {
            if (_exitAnnounced.TryAdd(session.Id, 0))
                _gatewayClient?.NotifySessionState(session.Id.ToString(),
                    Core.Sessions.ActivityState.Exited.ToString(), DoorbellEvents.SessionExited);
            // Issue #1176 (Phase 1a): tombstone the removed session so the Gateway prunes it immediately.
            _streamClient?.NotifyRemove(session.Id.ToString());
            // Remove-the-network-port phase 1b: END the session's Gateway credential. Both halves are
            // needed and neither substitutes for the other - Forget stops THIS Director re-registering the
            // key on the next reseed, and the revocation is what makes the GATEWAY refuse it. Forgetting
            // alone would leave a working credential behind for a session that no longer exists; revoking
            // alone would have the next reseed try to bring it back.
            // Forget RECORDS the revocation as owed before dropping the hash, so a revocation that
            // cannot be delivered right now survives to be replayed on the next reseed. It used to be
            // dropped: the hash went first, the send returned silently on a disconnected tunnel and was
            // swallowed on failure, and the reseed replayed registrations from a set the entry had
            // already been removed from - so a reaped session's key stayed usable until its expiry.
            if (_sessionGatewayKeys.Forget(session.Id))
                _streamClient?.RevokeSessionKey(session.Id.ToString());
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
            // Issue #1176 (Phase 1a): tear down the old stream client before rebuilding, so a settings
            // change never leaves two streams pushing for the same directorId.
            if (_streamClient is not null)
            {
                try { await _streamClient.StopAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] ReapplyGateway stream stop error: {ex.Message}"); }
                await _streamClient.DisposeAsync();
                _streamClient = null;
            }

            var gatewayConfig = GatewayConfig.Load();

            _gatewayClient = BuildGatewayClient(gatewayConfig);
            _gatewayClient.Start();
            // The pusher is built BEFORE the stream dials. The stream's first Hello carries the Gateway's
            // turn watermarks, and a pusher created after that call has already gone out would miss the
            // reconciliation entirely (found in review) - the same order StartAsync uses.
            _turnPusher ??= BuildTurnPusher(gatewayConfig);
            _streamClient = BuildStreamClient(gatewayConfig);
            _streamClient?.Start();
            _turnPusher?.Start();
            // Issue #1292: keep the async-vs-local numbering decision in step with the new config.
            _sessionManager.FleetNumberingActive = gatewayConfig.IsEnabled;
        }
        finally
        {
            _gatewayReapplyLock.Release();
        }
    }

    /// <summary>
    /// The clean-shutdown farewell to the Gateway (issue #2194): rules this Director's open
    /// work-history rows "Director stopped" while the tunnel is still up. The app's shutdown routine
    /// calls this FIRST - before killing sessions - so the ruling covers every session the shutdown
    /// takes with it. Best-effort, time-boxed inside the stream client, never throws.
    /// </summary>
    public Task NotifyDirectorStoppingAsync()
        => _streamClient?.NotifyDirectorStoppingAsync() ?? Task.CompletedTask;

    /// <summary>Stop the services and delete the registration file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[ControlApiHost] StopAsync");

        // Stop the injected-text refresh poll first so it does not tick against a tearing-down host.
        try { _injectedTextRefreshCts.Cancel(); _injectedTextRefreshCts.Dispose(); }
        catch (Exception ex) { FileLog.Write($"[ControlApiHost] injected-text poll cancel error: {ex.Message}"); }

        if (_gatewayClient is not null)
        {
            try { await _gatewayClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayClient.StopAsync error: {ex.Message}"); }
            _gatewayClient.Dispose();
            _gatewayClient = null;
        }
        // Issue #1176 (Phase 1a): stop the push stream on host shutdown.
        if (_streamClient is not null)
        {
            try { await _streamClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayStreamClient.StopAsync error: {ex.Message}"); }
            await _streamClient.DisposeAsync();
            _streamClient = null;
        }

        _terminalStateDetector?.Dispose();
        _terminalStateDetector = null;
        _activityUploader?.Dispose();
        _activityUploader = null;
        _repoStatePusher?.Dispose();
        _repoStatePusher = null;
        _sessionGitStatusMonitor?.Dispose();
        _sessionGitStatusMonitor = null;
        _activityProducer?.Dispose();
        _activityProducer = null;
        _activityOutbox = null;
        _transientErrorAutoResume?.Dispose();
        _transientErrorAutoResume = null;
        _turnReviewLogger?.Dispose();
        _turnReviewLogger = null;
        _recordsWatcher?.Dispose();
        _recordsWatcher = null;
        _piSessionRebinder?.Dispose();
        _piSessionRebinder = null;
        _conversationIngestor?.Dispose();
        _conversationIngestor = null;
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
        _pointerWatcher?.Dispose();
        if (_turnPusher is not null)
        {
            try { await _turnPusher.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] turn pusher dispose error: {ex.Message}"); }
            _turnPusher = null;
        }
        _pointerWatcher = null;
        _preambleMaintainer?.Dispose();
        _preambleMaintainer = null;

        _registration?.Unregister();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
