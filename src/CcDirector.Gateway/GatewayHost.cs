using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tailscale;
using CcDirector.Gateway.Util;
using CcDirector.HostedAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR; // Issue #1176: ClientProxyExtensions.InvokeAsync (client results) for the down-channel
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.Gateway;

/// <summary>
/// The Gateway's Kestrel host. One process per machine. Binds all interfaces (0.0.0.0:7878 by default) so a
/// tailnet or hosted client can reach it; every route except /healthz, /login, /logout requires auth, so the
/// bind is reachable-but-authenticated, not open. When CC_GATEWAY_HOSTED=1 the port follows the platform's
/// WEBSITES_PORT/PORT (see <see cref="GatewayHostedMode"/>).
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public const int DefaultPort = 7878;

    /// <summary>
    /// Passed as the port to mean "let the operating system assign a free one" (issue #2161). The bind and
    /// the assignment then happen in ONE step, and <see cref="Port"/> carries the assigned number the moment
    /// <see cref="StartAsync"/> returns.
    ///
    /// This exists because the alternative - ask the operating system for a free port, release it, and bind
    /// that number a moment later - leaves the port unheld in between, where any process on the machine can
    /// take it. Tests did that 92 times over and lost the race often enough to redden whole runs with
    /// "address already in use" in code nobody had touched.
    /// </summary>
    public const int OperatingSystemAssignedPort = 0;

    /// <summary>
    /// The port this Gateway listens on. Settable only from inside: when the host was constructed with
    /// <see cref="OperatingSystemAssignedPort"/> this holds 0 until the listener binds, and the ACTUAL
    /// assigned port from then on. Nothing may capture this value before <see cref="StartAsync"/> has
    /// bound - read it late (the collaborators below take a delegate for exactly that reason), or a
    /// consumer built at construction time keeps the placeholder forever.
    /// </summary>
    public int Port { get; private set; }
    public string Token { get; }
    public DirectorRegistry Registry { get; }

    /// <summary>
    /// The finalised route table, captured after every endpoint is mapped (StartAsync). Internal, for the
    /// route-surface guard tests: the shell-prefix auth allowlist (AuthMiddleware.IsPublicShellSurfaceRequest)
    /// is complete only while the endpoint set under /mobile, /m and /assets is exactly the set it was
    /// written against, and the guard test pins that set here.
    /// </summary>
    internal IReadOnlyList<Microsoft.AspNetCore.Http.Endpoint> MappedEndpoints { get; private set; }
        = Array.Empty<Microsoft.AspNetCore.Http.Endpoint>();

    // The process's ONE system capability, minted here in the composition root. Passed to the internal
    // system passes that legitimately read across tenants; never handed to a request handler. The guard
    // test (SystemScopeGuardTests) enforces that SystemScope.Grant() is called nowhere else.
    private readonly Tenancy.SystemScope _system = Tenancy.SystemScope.Grant();

    /// <summary>
    /// Issue #1292: the fleet-wide authority for the short three-digit session numbers. One instance for
    /// the whole Gateway, so a number names exactly one session across every Director on every machine.
    /// Directors ask it for a number at session creation (POST /session-numbers/allocate) and free it at
    /// session end; the /sessions aggregation adopts every observed number so the in-use set survives a
    /// Gateway restart.
    /// </summary>
    public Discovery.FleetSessionNumberAllocator SessionNumbers { get; } = new();

    /// <summary>
    /// Issue #1176 (Phase 1a): the Gateway's cache of session state pushed up by stream-connected
    /// Directors. The <c>/sessions</c> aggregation serves a Director from here (instead of pulling it)
    /// when that Director's stream is connected and fresh. Empty until Directors connect and push.
    /// </summary>
    public Streaming.PushedSessionStore PushedSessions { get; }

    /// <summary>Pushed repository/worktree snapshots per Director (repositories mission, #510 phase C).</summary>
    public Streaming.PushedRepositoryStore PushedRepositories { get; }

    /// <summary>Daily repository history behind the weekly report (repositories mission, #510 phase D).</summary>
    public Streaming.RepoHistoryStore RepoHistory { get; }

    /// <summary>
    /// Gateway Cleanup mission (Wave 4b): the ONLY store of Missions. Missions are a fleet-level concept
    /// (they span Directors and machines and nest), so the source of truth lives at the Gateway, like fleet
    /// messaging and scheduling - not on any one Director. It is the JSON-file-backed
    /// <see cref="Core.Sessions.MissionStore"/>, pointed at a Gateway-side file. The mission REST endpoints
    /// (POST/GET /missions) read and write this, and every spawn door validates against it and stamps the
    /// resolved NAME onto the create before forwarding it to a Director.
    ///
    /// A Director no longer keeps one at all: the local store, and the create-time bridge that consulted
    /// it, were removed with issue #2629 after a spawn door that forwarded the id WITHOUT the name sent the
    /// Director to that stale per-machine file, which reported a live mission as unknown.
    /// </summary>
    public Core.Sessions.MissionStore Missions { get; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the registry of live Director up-streams (terminal output
    /// and finite file/screenshot reads), keyed by the stream id the Gateway mints per browser request. The
    /// director-stream hub's StreamUp method pumps the Director's frames into this registry, which forwards
    /// them to the browser-facing sink with pull-then-forward backpressure. Phase 2 wires the browser-facing
    /// legs to it; Phase 0 delivers and unit-tests the machinery.
    /// </summary>
    public Streaming.GatewayStreamRegistry StreamRegistry { get; }

    /// <summary>
    /// DevThrottle Stats: the aggregate of every session's input tally (turns + character volume by
    /// modality and surface). Fed by the director-stream hub from the pushed
    /// <see cref="Contracts.SessionDto.InputStats"/> and read by the private Gateway dashboard at
    /// <c>/stats</c> with no cloud round-trip.
    ///
    /// NULL when statistics are unavailable, which the Gateway serves through - see
    /// <see cref="Stats.InputStatsHandle"/> for the incident that makes that non-negotiable. Read the
    /// handle (<see cref="InputStatsHandle"/>) rather than this property when the caller needs the reason.
    /// </summary>
    public Stats.GatewayInputStatsAggregator? InputStats => InputStatsHandle.Aggregator;

    /// <summary>
    /// DevThrottle Stats: the aggregator, or the named reason there is not one. Always present, so a
    /// statistics failure can never stop the roster or the tunnels being served.
    /// </summary>
    public Stats.InputStatsHandle InputStatsHandle { get; }

    /// <summary>
    /// Defect 20: the observer that starts a deferred snooze's clock when the Director pushes up the hold
    /// having LANDED. Shared with the DirectorHub through the container, like <see cref="InputStats"/>.
    /// </summary>
    internal Snooze.SnoozeLandingObserver SnoozeLandings { get; }

    /// <summary>
    /// Defect 5: the observer that stamps each session's Gateway-resolved role back DOWN to the Director
    /// that owns it, so the desktop rail folds the same role the phone and the Cockpit fold. Shared with the
    /// DirectorHub through the container, like <see cref="SnoozeLandings"/>.
    /// </summary>
    internal Fleet.FleetRoleObserver FleetRoles { get; }

    /// <summary>
    /// The observer that stamps each session's FOLDED display state (effective color, label, triage bucket,
    /// needs-you-since, the snooze clock, the snooze-ended marker) back DOWN to the Director that owns it, so
    /// the desktop rail renders the Gateway's answer instead of re-folding from local facts it cannot see.
    /// Shared with the DirectorHub through the container, like <see cref="FleetRoles"/>; also driven by a
    /// periodic sweep (<see cref="_displayStateSweepTimer"/>) as the backstop for Gateway-only overlay
    /// changes that arrive on no Director push.
    /// </summary>
    internal Fleet.FleetDisplayStateObserver FleetDisplayState { get; }

    /// <summary>
    /// The durable fleet CONCURRENCY record (how many sessions run at once, and how many are actively
    /// working at once) that the private Gateway dashboard and the agent API read. Fed from the same
    /// assembled /sessions roster as <see cref="InputStats"/>, so it is fleet-wide with no per-Director
    /// instrumentation - a session count is visible for every session on every machine, new build or old.
    ///
    /// NULL ON A HOSTED GATEWAY, and that is the point rather than an oversight. This implementation is the
    /// one backed by <c>gateway-concurrency-stats.json</c>, which is rewritten IN FULL from the hottest path
    /// in the system - and on 2026-07-30 two containers were writing it through the same window in which a
    /// database on the same shared storage was corrupted and the Gateway answered HTTP 500 to every client
    /// for thirty-two minutes. A hosted Gateway therefore never constructs it, so that file is never written
    /// there under any circumstance. The database-backed store that replaces it on the hosted path takes
    /// <see cref="Stats.Data.GatewayStatsStore.Factory"/>, which is published for exactly that purpose.
    ///
    /// Every consumer already treats it as optional (<c>concurrency?.Observe</c>,
    /// <c>concurrency?.Snapshot</c>), so an absent recorder is an absent series on the statistics surface -
    /// never a zero, never an invented figure, and never an exception on the roster path.
    /// </summary>
    /// <remarks>
    /// RE-ASKED ON EVERY READ, like <see cref="Stats.InputStatsHandle.Aggregator"/>. On hosted the answer
    /// comes from <see cref="Stats.LateStatsObservers"/>, so a statistics store that finished opening after
    /// the startup deadline starts recording concurrency instead of staying dead until a restart. Do not
    /// cache what this returns.
    /// </remarks>
    public Stats.ISessionConcurrencyRecorder? SessionConcurrency =>
        _hostedStatsObservers is not null ? _hostedStatsObservers.Concurrency : _selfHostConcurrency;

    private readonly Stats.LateStatsObservers? _hostedStatsObservers;
    private readonly Stats.GatewaySessionConcurrencyStats? _selfHostConcurrency;

    /// <summary>
    /// THE FAILURE-DOMAIN BOUNDARY around the statistics store: its provider selection, its connection and
    /// its migration chain, with every failure in all three CONTAINED so none of them can stop the Gateway
    /// from starting or from serving.
    ///
    /// Never null, and never a reason the Gateway does not start. When the statistics store is unavailable
    /// this object says so, with a named reason, and the Gateway boots and serves its roster and its tunnels
    /// exactly as it otherwise would. See <see cref="Stats.Data.GatewayStatsStore"/> for why that is a
    /// boundary rather than a fallback.
    /// </summary>
    public Stats.Data.GatewayStatsStore StatsStore { get; }

    /// <summary>
    /// launcher-persistent-join: the map of which machine's cc-launcher is currently joined over a
    /// persistent stream. When a launcher is stream-connected, the machine lifecycle relay pushes a command
    /// DOWN the open stream instead of dialing the launcher's REST API. Empty until launchers connect and
    /// only consulted when stream mode is on.
    /// </summary>
    public Streaming.LauncherConnectionRegistry LauncherConnections { get; }

    // Issue #1176 (Phase 1a): Gateway-side stream feature switch + staleness window, resolved from
    // config.json (or an explicit constructor override for tests). When off, the hub is not mapped and
    // /sessions never consults the pushed cache, so behaviour is byte-identical to today.
    private readonly TimeSpan _streamStaleAfter;

    public bool AuthEnabled { get; }

    /// <summary>
    /// Environment override for the host-wide auth gate (issue #917). As of Phase 1 the gate is ON by
    /// default, so this variable is now a DISABLE override for debugging: set <c>CC_GATEWAY_AUTH=0</c> to
    /// turn the gate off. (Setting it to <c>1</c> is a harmless no-op since enforcement is already the
    /// default.) A request that reaches the Gateway on the tailnet must present the shared token or a
    /// per-device key unless the gate is explicitly disabled.
    /// </summary>
    public const string AuthEnabledEnvVar = "CC_GATEWAY_AUTH";

    /// <summary>
    /// Environment override that turns the host-wide auth gate OFF for debugging (issue #917). Set
    /// <c>CC_GATEWAY_NO_AUTH=1</c> to disable enforcement on a Gateway that would otherwise enforce by
    /// default. This mirrors the <c>CC_GATEWAY_NO_TAILSCALE</c> env-toggle precedent.
    /// </summary>
    public const string AuthDisabledEnvVar = "CC_GATEWAY_NO_AUTH";

    /// <summary>
    /// Resolves whether the host-wide auth gate runs. As of issue #917 enforcement is ON by default:
    /// <list type="bullet">
    /// <item>An explicit constructor choice (<paramref name="explicitChoice"/> non-null) always wins - a
    /// test forces the gate on or off deterministically regardless of the environment.</item>
    /// <item>With no explicit choice (production), the gate is ON unless a disable override is set:
    /// <c>CC_GATEWAY_NO_AUTH=1</c> or <c>CC_GATEWAY_AUTH=0</c> turns it off for debugging.</item>
    /// </list>
    /// Pure and side-effect free so it is unit-tested directly.
    /// </summary>
    internal static bool ResolveAuthEnabled(bool? explicitChoice)
    {
        if (explicitChoice.HasValue)
            return explicitChoice.Value;

        if (string.Equals(Environment.GetEnvironmentVariable(AuthDisabledEnvVar), "1", StringComparison.Ordinal))
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable(AuthEnabledEnvVar), "0", StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Snooze Length mission: the Gateway-owned snooze registry. Exposed so a test can inject a pending
    /// (or already-expired) snooze and assert the /sessions overlay flips it back into "needs you".
    /// </summary>
    internal Snooze.SnoozeRegistry SnoozeRegistry => _snoozeRegistry;

    /// <summary>
    /// Issue #469: the registry of enrolled devices and their unique per-device keys - the single
    /// issuer and record of credentials in the per-device-key trust model. Persisted under the
    /// config root so issued keys survive a Gateway restart.
    /// </summary>
    public Pairing.DeviceRegistry Devices { get; }

    /// <summary>
    /// Remove-the-network-port mission, phase 1b: the registry of per-SESSION Gateway credentials. A Director
    /// registers one key per session over its tunnel and revokes it when the session is reaped; the auth gate
    /// verifies presented session keys against it, and the session-key guard limits what they may call. This
    /// is what lets an agent's command line reach the Gateway without being handed its Director's
    /// account-wide key.
    /// </summary>
    public Pairing.SessionKeyRegistry SessionKeys { get; }

    /// <summary>The Gateway's record of what each utterance upload transcribed (inspection finding I2-03), spent
    /// by the prompt route when a prompt claims to be that utterance. One per process, shared by the utterance
    /// completion route that writes it and the prompt route that spends it. In memory by design.</summary>
    public Voice.SpokenClaimRegistry SpokenClaims { get; } = new();

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
    /// Production-readiness B2 (process-control): the seam DELETE /directors/{id} FORCE-KILL calls to kill a
    /// Director's process tree by pid. Null (production) uses the real Process.GetProcessById(pid).Kill. A test
    /// injects a recorder that observes the kill WITHOUT killing anything, so a proof can assert the force-kill
    /// path was (self-host) or was NOT (hosted) reached with the client-supplied pid - a direct assertion,
    /// exactly as <see cref="OnShutdownRequested"/> lets the shutdown proof observe its handler.
    /// </summary>
    public Func<int, bool>? OnForceKillDirector { get; set; }

    /// <summary>
    /// Issue #331: registered cc-launcher processes. The relay endpoints use this to
    /// forward lifecycle verbs to the correct machine's launcher loopback REST API.
    /// </summary>
    public LauncherRegistry Launchers { get; } = new();

    /// <summary>
    /// Issue #636 (Gateway Centralization Phase 2 foundation): the Gateway-hosted DevThrottle credential
    /// service. Stores the access-plus-refresh token pair encrypted at rest under the Gateway config
    /// directory, answers "signed in?" locally with no network call, and reads the signed-in identity
    /// from the cached token. Reuses the Core <see cref="Core.Account.DevThrottleAccountService"/> as-is.
    /// On Windows it is backed by Windows Data Protection; on a non-Windows host (the operating-system
    /// credential store is Windows-only for now, per the issue's assumption) it is null and the Gateway
    /// holds no account credential until the macOS Keychain store is added.
    /// </summary>
    public Core.Account.DevThrottleAccountService? Account { get; }

    /// <summary>
    /// Issue #637 (Gateway Centralization Phase 2): the browser loopback sign-in flow relocated onto
    /// the Gateway. Built over <see cref="Account"/>, it opens the system browser at the configured
    /// sign-in address, captures the credential the sign-in completion hands back on the loopback
    /// callback, and stores it through the credential service. Null on a host with no credential
    /// service (a non-Windows host, where <see cref="Account"/> is null) - the tray then has nothing
    /// to prompt and stays inert. Tokens are never logged.
    /// </summary>
    public Account.GatewaySignInService? SignIn { get; }

    /// <summary>
    /// Issue #640 (Gateway Centralization Phase 2): the background token refresh service. Built over
    /// <see cref="Account"/>, it periodically renews the Gateway's access token when it has expired by
    /// exchanging the refresh token against the configured backend endpoint - in the background, never
    /// blocking startup or request handling. Null on a host with no credential service (a non-Windows host,
    /// where <see cref="Account"/> is null). Started in <see cref="StartAsync"/>, disposed in
    /// <see cref="StopAsync"/>. Tokens are never logged.
    /// </summary>
    private Account.GatewayTokenRefreshService? _tokenRefresh;

    /// <summary>
    /// Issue #857 (Gateway device registration): registers THIS Gateway as a device with the DevThrottle
    /// cloud account on sign-in and stores the cloud-issued per-device key locally. Built over
    /// <see cref="Account"/>, so it exists only when that service does (a non-Windows host has no
    /// credential service). Triggered immediately by the sign-in-completion hook on <see cref="SignIn"/>
    /// and, as the retry/first-launch safety net, by the heartbeat's first tick. Null on a host with no
    /// credential service. The per-device key is never logged (security rule DT-05).
    /// </summary>
    private Account.GatewayDeviceRegistrationService? _deviceRegistration;

    /// <summary>
    /// Issue #857: the Gateway's periodic last-seen heartbeat to the cloud (so the account device list's
    /// last-seen stays fresh), which also retries a registration that failed on sign-in. Built over
    /// <see cref="Account"/>; null on a host with no credential service. Started in <see cref="StartAsync"/>,
    /// disposed in <see cref="StopAsync"/>. Never blocks startup; a cloud failure only logs and retries.
    /// </summary>
    private Account.GatewayDeviceHeartbeatService? _deviceHeartbeat;

    /// <summary>
    /// Path B (device-gateway-topology.md Diagram 2b/2c): mirrors this Gateway's locally-paired children up
    /// to the cloud account roster on enrollment and reconciles them each sweep (drop children revoked on the
    /// account page, refresh child last-seen). Built over <see cref="Account"/>; null on a host with no
    /// credential service. Has no timer of its own - the enrollment endpoint fires its mirror-up and the
    /// device heartbeat sweep drives its reconcile. Tokens and per-device keys are never logged (DT-05).
    /// </summary>
    private Account.ChildDeviceMirrorService? _childMirror;

    // The single resolve-then-create path for spawning a session on a target machine (cron + the
    // interactive POST /machines/{machine}/sessions relay). Built in the constructor, used by both.
    private readonly Running.MachineSessionSpawner _machineSessionSpawner;
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private readonly KeyVault _keyVault;

    // Lost Dictations mission (#1593): the transcription owner the dictation endpoint uses. Null in
    // production - StartAsync then builds the real one over _keyVault. Only a test injects a stub, because
    // the dictation delivery arm sits BEHIND a successful transcribe and the hosted URL is a constant.
    private readonly Transcription.GatewayTranscriptionService? _dictationTranscription;
    // Issue #881: mints/ensures the DevThrottle inference key after sign-in and at startup. Null on a
    // host with no credential service (nothing to sign in to).
    private readonly Account.TranscriptionKeyAutoProvisioner? _transcriptionKeyProvisioner;
    private readonly WorkListStore _workLists;
    // Snooze Length mission: the Gateway-owned, restart-surviving snooze registry (the one piece of
    // new state). An expired snooze comes back on its own with no background timer: HoldStateFor reports
    // an elapsed entry as None on every read. Constructed here (load-on-construct re-arms every pending
    // snooze). There is no expiry sweep - an elapsed entry lingers as a durable returned-by-timer
    // tombstone and is retired only by an edge that ends a snooze (work, an owner turn, an exit, a
    // re-snooze), bounded by the live-session prune paths.
    private readonly Snooze.SnoozeRegistry _snoozeRegistry;
    private readonly Activity.ActivityEventStore _activityEvents;
    private readonly Reports.RepoStateStore _repoState;

    /// <summary>Whether the skills this Gateway serves can actually be READ on the machines it serves
    /// them to - reported by each Director, because only the machine can observe it.</summary>
    private readonly Skills.SkillPlacementStore _skillPlacement;
    private Activity.ActivityRetentionSweep? _activityRetentionSweep;
    // Fills account_hosted_ai_spend by periodically mirroring the cloud credit-debit ledger (issue #1771).
    private Governance.HostedAiSpendSweep? _hostedAiSpendSweep;
    // Mission Screen mission (Phase 1b, issue #1405): the Gateway-owned, restart-surviving store of each
    // mission's WHY, keyed by the mission's normalized name. Durable + shared so every Cockpit, the phone,
    // and the future Mission-Control chat/API read the same WHY. Constructed here (load-on-construct
    // re-serves every WHY after a restart); exposed to the client over MissionNotesEndpoint.
    private readonly MissionNotes.MissionNoteStore _missionNotes;
    private readonly Settings.TenantSettingsStore _tenantSettings;
    private readonly Settings.TenantSettingsResolver _tenantSettingsResolver;
    // Hosted Gateway mission, Step 1b: the EF data layer (gateway.db). The host owns ONE instance and the
    // structured stores that have moved off hand-rolled JSON read/write through it. On the single-tenant
    // local install every row is the "local" tenant (SingleTenantContext), so behavior is unchanged.
    // Hosted Multi-Tenancy increment 1: the tenant context GatewayDatabase reads. On the hosted Gateway it is
    // the AsyncLocalTenantContext (per-account, fail-closed, set at the auth boundary); on self-host it is the
    // SingleTenantContext (always Local). Assigned in the constructor from GatewayHostedMode.IsHosted.
    private readonly Core.Tenancy.ITenantContext _tenantContext;
    // The concrete ambient context - non-null ONLY on the hosted Gateway - the object the auth boundaries
    // enter per-account (and the reserved SYSTEM) scopes on. Null on self-host (Local is the ambient answer,
    // nothing to enter). It is the SAME instance GatewayDatabase reads, so a boundary's scope is what the
    // stores see.
    private readonly Core.Tenancy.AsyncLocalTenantContext? _hostedTenant;
    // The auth-boundary tenant binder (Hosted Multi-Tenancy increment 1): resolves an authenticated device
    // key to its tenant and enters the scope. Used by the tunnel Hello and the device-key HTTP middleware.
    // Inert on self-host (every authenticated caller is Local).
    private readonly Tenancy.HostedTenantBoundary _tenantBoundary;
    // The background-loop tenant seam (Hosted Multi-Tenancy, session-serving PR2). A sweep is on no request
    // and no tunnel connection, so it has no tenant of its own: it runs ONE PASS PER TENANT through this,
    // instead of the single implicit TenantId.Local pass the loops used to hard-code. Self-host runs exactly
    // one Local pass (unchanged); hosted enters each live tenant's scope in turn. Its Current is also what
    // every Gateway-internal store read and every down-channel command resolves to - null on hosted with no
    // scope, which is a DENY, never a fall back to Local.
    private readonly Tenancy.ITenantPass _tenantPass;
    private readonly Data.GatewayDatabase _gatewayDb;

    /// <summary>
    /// Whether this Gateway may serve anything beyond /healthz: the database is open AND the device
    /// credential authority has been established. False between the listener binding and the end of the
    /// startup work that follows it.
    ///
    /// Both halves are required and neither implies the other - the database opening is what lets the
    /// authority be read, and the authority is what makes an authenticated request answerable.
    /// </summary>
    /// <summary>
    /// Open the database and load every store that reads from it at startup. Called by
    /// <see cref="StartAsync"/> immediately AFTER the listener binds, and idempotent so a caller that
    /// cannot easily tell whether startup has run may call it safely.
    ///
    /// WHY IT IS A NAMED STEP RATHER THAN INLINE IN StartAsync. All of this used to happen during
    /// construction, in front of the bind, which is what let a slow database delay the bind past the
    /// platform's container-start deadline and make it stop the SITE - taking the healthy container with it
    /// (#2383, #2585). Naming the step keeps the ordering visible, lets a test bring a host to a usable
    /// state without binding a port, and means there is ONE definition of "the startup database work"
    /// rather than a copy in every caller that needs it.
    /// </summary>
    internal void EnsureStoresReady()
    {
        FileLog.Write("[GatewayHost] listener bound; opening the database");
        _gatewayDb.Open();
        FileLog.Write("[GatewayHost] database open");

        // Every store that LOADS FROM THE DATABASE AT STARTUP, initialised here rather than in the
        // constructor so none of it stands in front of the listener bind.
        //
        // INSIDE THE SYSTEM SCOPE, and that is not decoration. The constructor did this work inside
        // _hostedTenant.Enter(TenantId.System), and three of these stores CAPTURE the ambient tenant as
        // the partition that owns the shared library (skills, workflows, workflow runs). Initialising
        // them out here without re-entering that scope would capture whatever tenant happened to be
        // ambient and put the shared library in the WRONG PARTITION - silently, and visible only later
        // as one account seeing another's library. The scope is re-entered for exactly the span the
        // constructor had.
        //
        // Until every one of these returns, the readiness gate refuses every request but /healthz.
        using (_hostedTenant?.Enter(Core.Tenancy.TenantId.System))
        {
            Devices.Initialize();
            _workLists.Initialize();
            _cronJobs.Initialize();
            _skills.Initialize();
            _workflows.Initialize();
            _workflowRuns.Initialize();
            _instructionsStore.Initialize();
        }

        FileLog.Write("[GatewayHost] startup stores loaded; now serving");
    }

    internal bool IsReadyToServe()
        => _gatewayDb.IsOpen
        && Devices.IsInitialized
        && _workLists.IsInitialized
        && _cronJobs.IsInitialized
        && _skills.IsInitialized
        && _workflows.IsInitialized
        && _workflowRuns.IsInitialized
        && _instructionsStore.IsInitialized;

    /// <summary>The typed per-tenant runtime settings resolver. Every caller supplies the tenant explicitly;
    /// an unset override returns only the operator global default.</summary>
    internal Settings.TenantSettingsResolver TenantSettingsResolver => _tenantSettingsResolver;

    /// <summary>
    /// The auth-boundary tenant binder. Exposed to the test assembly so an isolation test can enter the same
    /// tenant scope a real request or tunnel connection would, and drive the production loop code inside it.
    /// </summary>
    internal Tenancy.HostedTenantBoundary TenantBoundary => _tenantBoundary;

    /// <summary>
    /// The per-tenant transcript store this host injects into every transcription path. Exposed to the test
    /// assembly so a refusal test can ask the store ITSELF whether a partition was written, rather than
    /// inferring it from a status code - a refused request must leave the Local partition at zero rows.
    /// </summary>
    internal Transcription.TranscriptStore Transcripts => _transcripts;

    /// <summary>
    /// The account-to-tenant resolver (Hosted Multi-Tenancy increment 1): owns the tenants mapping table and
    /// mints or looks up a tenant from a verified account subject. Exposed so the hosted enrollment boundary
    /// can resolve a tenant once, at the point the account token is validated. Unused on the single-tenant
    /// local install (everything resolves to "local" without a lookup).
    /// </summary>
    public Tenancy.TenantRegistry TenantRegistry { get; }

    /// <summary>The paid-entitlement gate read at hosted enrollment. Present on every host; consulted only
    /// where the hosted enrollment route is mapped, which is hosted only.</summary>
    public Tenancy.EntitlementRegistry EntitlementRegistry { get; }

    /// <summary>The free-trial ledger (issue #2117) - the 14-day Pro trial granted at an account's first
    /// arrival at the hosted Gateway. Read through <see cref="EntitlementRegistry"/> everywhere; exposed here
    /// because the hosted enrollment route is the one place that GRANTS a trial.</summary>
    public Tenancy.TrialRegistry TrialRegistry { get; }

    // The persisted workflow catalog (Workflows mission, phase 1): built-ins seeded at startup,
    // user-defined workflows beside them, served by Api.WorkflowEndpoints.
    private readonly Workflows.WorkflowStore _workflows;
    // Workflow runs (phase 4, issue #1771): one row per execution of a workflow definition, pinned to
    // the version that governed it. The governance outcome spine.
    private readonly Workflows.WorkflowRunStore _workflowRuns;
    // Workspaces (issue #2722): a named set of seats, authored by hand or captured from a running
    // Director. Held here, and not on the machine, because the one moment a workspace matters is the
    // moment that machine has just been restarted out from under its fleet. Served by
    // Api.WorkspaceEndpoints; REPLACES the Director-local workspace files.
    private readonly Workspaces.WorkspaceStore _workspaces;
    // The central skill library (devthrottle_internal issue 995): the capabilities agents reach for,
    // held here and fetched, instead of copied onto every machine by the installer. Served by
    // Api.SkillEndpoints - a separate register from workflows, sharing their storage shape.
    private readonly Skills.SkillStore _skills;
    // The standing instructions an account gives about its sessions, and the record of every time one
    // fired (Session Rules mission). Served by Api.SessionRuleEndpoints; read by the evaluator below.
    private readonly Rules.SessionRuleStore _sessionRules;
    // The append-only governance event ledger (issue #1771, spine item 2): immutable session/run state
    // transitions, the duration spine no run row can give.
    private readonly Governance.GovernanceEventLedger _governanceEvents;
    // Honest driver-normalized spend (issue #1771, spine item 3): per-session token effort + billing-mode
    // label, and the account-level hosted-AI service dollars mirrored from the credit-debit ledger.
    private readonly Governance.SessionSpendStore _sessionSpend;
    private readonly Governance.AccountHostedAiSpendStore _hostedAiSpend;
    // The append-only governance audit trail (issue #1771, spine item 4): structured intervention +
    // permission/sandbox decisions, recorded as events, never inferred from transcripts.
    private readonly Governance.GovernanceAuditLog _governanceAudit;
    // Fills session_spend at each turn-end from the pushed roster snapshot (issue #1771, spine item 3).
    private readonly Governance.SessionSpendEmitter _sessionSpendEmitter;
    // Fills the governance event ledger with session state transitions (issue #1771, spine item 2).
    private readonly Governance.SessionStateEventEmitter _sessionStateEmitter;
    // The weekly Outcome Ledger reporter (issue #1771, spine item 4): a read-only assembly over the run
    // tables, event ledger, spend, and audit trail - the first governance report that pays rent.
    private readonly Governance.OutcomeLedgerReporter _outcomeLedger;
    private readonly Reports.MorningReportBuilder _morningReport;
    private readonly CronJobStore _cronJobs;
    private readonly CronRunHistoryStore _cronRuns;
    private readonly Running.CronEngine _cronEngine;
    // G8 increment 2: the cron sweep now fires through the per-tenant tenancy seam (TenantScopedSweep) so it
    // can run ON hosted, tenant-isolated, instead of being disabled. Constructed alongside _cronEngine.
    private Running.CronTenantSweep? _cronSweep;
    // Reentrancy guard for the cron sweep. On hosted the per-tenant fan-out can take longer than the 60s tick,
    // so a later tick must NOT start a second overlapping sweep (that could double-fire a job in the window
    // between a tenant's ListAll and MarkFired). 0 = idle, 1 = a sweep is in flight (Interlocked-owned).
    private int _cronSweepInFlight;

    // MTR-15 cancellation cutoff (hosted-only): the per-tenant access lease, its device-revoker, and the 60s
    // active-tenant sweep that re-reads entitlement and cuts off a cancelled tenant within the sweep bound.
    private Tenancy.TenantAccessRevoker? _accessRevoker;
    private Tenancy.HostedAccessLeaseService? _accessLeases;
    private Tenancy.EntitlementLeaseMonitor? _leaseMonitor;
    private System.Threading.Timer? _leaseSweepTimer;
    private int _leaseSweepInFlight;
    private static readonly TimeSpan LeaseSweepInterval = TimeSpan.FromSeconds(60);
    // Tenant-indexed live Director tunnels (populated by DirectorHub); the cutoff aborts a revoked tenant's.
    private readonly Streaming.DirectorConnectionRegistry _directorConnections = new();
    // The cron firing sweep (epic #479, #483): wakes ~every minute and fires due jobs. Created in
    // StartAsync, disposed in StopAsync.
    private System.Threading.Timer? _cronTimer;
    private static readonly TimeSpan CronSweepInterval = TimeSpan.FromMinutes(1);

    // The activity ledger's 30-day retention purge (docs/PLAN-trustworthy-working-start-2026-07-24.md):
    // wakes a few times a day and deletes each tenant's events older than the retention window. Guarded
    // against overlap the same way the cron sweep is. Created in StartAsync, disposed in StopAsync.
    private System.Threading.Timer? _activityRetentionTimer;
    private int _activityRetentionInFlight;
    private static readonly TimeSpan ActivityRetentionInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ActivityRetentionStartupDelay = TimeSpan.FromMinutes(5);

    // The prompt log's retention purge (CR-3b, devthrottle_internal #1180): wakes a few times a day and
    // deletes every partition's daily files older than the retention window. Guarded against overlap the
    // same way the activity sweep is. Created in StartAsync, disposed in StopAsync.
    private Prompts.PromptLogRetentionSweep? _promptRetentionSweep;
    private System.Threading.Timer? _promptRetentionTimer;
    private int _promptRetentionInFlight;
    private static readonly TimeSpan PromptRetentionInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan PromptRetentionStartupDelay = TimeSpan.FromMinutes(7);

    // The daily dictionary-suggestion scan (devthrottle #2115): the timer ticks every few minutes; the sweep
    // decides PER TENANT whether that tenant's local 00:05 has passed since its last stored scan, so each
    // tenant scans at its own midnight from one timer. Cheap when nothing is due (one stored-row read per
    // tenant per tick). Guarded against overlap like the cron sweep. Created in StartAsync, disposed in
    // StopAsync.
    private System.Threading.Timer? _suggestionSweepTimer;
    private int _suggestionSweepInFlight;
    private static readonly TimeSpan SuggestionSweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SuggestionSweepStartupDelay = TimeSpan.FromMinutes(2);

    // Work history (issue #2194): the durable per-session record, its recorder on the push seam, the
    // Gateway summariser, and the background sweep that concludes "interrupted" from silence, generates
    // owed summaries and roll-ups (capped per pass - the cost rule), and prunes retention. The sweep
    // ticks every two minutes so an interrupted ruling lands within minutes of the threshold; guarded
    // against overlap like the cron sweep. Created in StartAsync, disposed in StopAsync.
    private readonly History.SessionHistoryStore _sessionHistory;
    private readonly History.KnownRepositoryStore _knownRepositories;
    /// <summary>The stored conversation (turn-push mission): what Directors push and every reader reads.</summary>
    private readonly History.SessionTurnStore _sessionTurns;
    /// <summary>Which Directors told this Gateway they send conversations (turn-push mission, phase 2).</summary>
    private readonly Streaming.TurnPushCapabilityRegistry _turnPushCapabilities = new();
    private readonly History.SessionHistoryRecorder _sessionHistoryRecorder;
    private History.SessionHistorySweep? _sessionHistorySweep;
    private System.Threading.Timer? _sessionHistoryTimer;
    private int _sessionHistorySweepInFlight;
    private static readonly TimeSpan SessionHistorySweepInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionHistorySweepStartupDelay = TimeSpan.FromMinutes(1);

    // Scheduled-run auto-dismiss (issue #1200): wakes ~every 15s and closes automated runs that declared
    // themselves done, over the Director stream. Created in StartAsync only when stream mode is on (the
    // feature has no REST fallback), disposed in StopAsync. Freshness matches the aggregation's pushed-cache
    // staleness so a session whose Director stopped pushing is not acted on from a stale snapshot.
    private System.Threading.Timer? _autoDismissTimer;
    private Running.AutoDismissSweeper? _autoDismissSweeper;
    private static readonly TimeSpan AutoDismissSweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AutoDismissStaleAfter = TimeSpan.FromSeconds(30);
    // The fold push backstop: the DirectorHub seam re-folds immediately on every Director push, which covers
    // every Director-driven change (activity, hold, desktop dictation). This periodic sweep catches the
    // GATEWAY-ONLY overlay changes that arrive on no push - voice generation, the Gateway's own
    // transcription, a phone dictation, a snooze expiring - so the desktop rail is never more than one
    // interval behind them. The observer's change gate keeps it quiet when nothing changed. Disposed in
    // StopAsync.
    private System.Threading.Timer? _displayStateSweepTimer;
    private static readonly TimeSpan DisplayStateSweepInterval = TimeSpan.FromSeconds(5);
    private int _displayStateSweepInFlight; // 0 = idle, 1 = a pass is running (overlap guard - see SweepDisplayState)

    // Voice-turn upload staging retention. The staging directory for a voice turn is deleted on the SUCCESS
    // path only, so every upload that ends any other way - a size refusal, a dropped connection, an assembly
    // that never completed, a caller that simply walked away - stays on disk with its recorded audio until
    // something removes it. This timer is that something; without it the staging grows without bound and
    // holds recorded speech for as long as the Gateway lives.
    private System.Threading.Timer? _voiceTurnUploadSweepTimer;
    private static readonly TimeSpan VoiceTurnUploadSweepInterval = TimeSpan.FromMinutes(15);
    // WHY FOUR HOURS, from what the staging is for rather than from a round number. A voice-turn staging
    // directory exists only between a register and that same upload's own complete: the phone records one
    // push-to-talk utterance, streams it in chunks, and the Gateway assembles it immediately. In normal
    // operation that whole life is seconds to a couple of minutes, and the worst legitimate case - a phone
    // on a failing mobile link retrying chunks with backoff - is minutes, tens of minutes at the extreme.
    // Four hours is an order of magnitude beyond that extreme, so no upload that is still genuinely trying
    // can be cut off, while abandoned audio has a hard retention ceiling measured in hours rather than in
    // the lifetime of the process. The sweep judges by an EXPLICIT last-activity signal that every
    // successful operation refreshes - a register or resume, an idempotent chunk, a real chunk, an assemble
    // (see VoiceUploadStore.EnsureFreshStaging) - not by whether a byte happened to be written, so a client
    // that resumes an upload without writing new bytes still counts as alive and only truly-idle staging
    // ages out. That is precisely the definition of abandoned.
    private static readonly TimeSpan VoiceTurnUploadMaxAge = TimeSpan.FromHours(4);
    /// <summary>
    /// Test seam: overrides the voice-turn upload sweep schedule (both the first tick and the period).
    /// Null in production and never assigned outside tests.
    ///
    /// It exists so the WIRING can be tested rather than assumed. The sweep method itself already had a
    /// direct unit test while nothing in production called it at all - which is exactly how an unbounded
    /// staging directory sat behind what read like coverage. A test that boots a real Gateway and watches
    /// stale staging disappear on its own can only pass when the timer below is really started, so removing
    /// the timer turns it red.
    /// </summary>
    internal static TimeSpan? VoiceTurnUploadSweepScheduleForTests;

    // Dictation tombstone retention (issue #1111). Distinct from the voice-turn sweep above in BOTH numbers
    // and in what it is allowed to touch: this one deletes only DELIVERED/ABANDONED records, never a PENDING
    // one, and it exists solely to bound tombstones whose client acknowledgment will never arrive.
    /// <summary>
    /// Remove-the-network-port phase 1b: the lapsed-session-key sweep. It changes NO authentication answer -
    /// the expiry is enforced on every resolution, so a lapsed key is already refused - it retires the rows
    /// so the table does not accumulate records that read as live, and an operator listing it sees the truth.
    /// Hourly is ample against a 12-hour lifetime. Not per-tenant: session_keys is a global table and the
    /// sweep is one statement across it.
    /// </summary>
    private System.Threading.Timer? _sessionKeySweepTimer;
    private static readonly TimeSpan SessionKeySweepInterval = TimeSpan.FromHours(1);

    private System.Threading.Timer? _dictationTombstoneSweepTimer;
    private static readonly TimeSpan DictationTombstoneSweepInterval = TimeSpan.FromHours(6);
    // WHY THIRTY DAYS, and why so much longer than the four hours next door. These two roots hold opposite
    // things. The voice-turn root holds RECORDED AUDIO that nothing else will ever remove, so its bound is a
    // privacy ceiling and wants to be tight. A dictation tombstone holds no audio at all - the bytes are
    // discarded when the record turns terminal - it is a few hundred bytes whose only job is to stop a
    // client re-driving an upload id it already delivered. So the risk here is inverted: deleting too EARLY
    // silently re-opens the door to a duplicate dictation, while keeping too long costs almost nothing.
    //
    // The number therefore comes from the client, not from the disk. A client holds its on-device copy until
    // it sees a terminal outcome and acks; a phone that is offline, out of battery, or simply not opened
    // might not come back for days. Thirty days is an order of magnitude beyond any plausible return, so no
    // client that could still re-drive an id is affected, while a tombstone nobody will ever ack stops being
    // immortal. The ack remains the real retirement path and retires records in seconds; this only catches
    // what the ack has permanently lost.
    private static readonly TimeSpan DictationTombstoneMaxAge = TimeSpan.FromDays(30);
    // WHY TWENTY-FOUR HOURS, and why it is NOT the thirty days above. That number protects a tombstone,
    // where keeping too long costs almost nothing. This one bounds a PENDING record, where keeping too long
    // costs the user their session: PENDING locks the session against human input, so every extra hour is an
    // hour the owner cannot type into a session that no client is coming back to. The risk is inverted
    // again, so the number is too.
    //
    // It is measured against LAST ACTIVITY, not creation, and every register, resume, chunk and assemble
    // refreshes it - so this is twenty-four hours of pure SILENCE, not twenty-four hours of dictating. A live
    // dictation is minutes; a client retrying over a bad connection still touches its staging and pushes its
    // own deadline out. A day of nothing means the client is gone: the app was closed, the phone replaced, or
    // the upload died against a full disk (which is exactly how seven of these came to be stuck on hosted on
    // 2026-08-28, the oldest five weeks old). Generous enough that no returning client is cut off, bounded
    // enough that a lock nobody can clear lasts a day rather than forever.
    private static readonly TimeSpan StalePendingMaxAge = TimeSpan.FromHours(24);
    /// <summary>
    /// Test seam: overrides the dictation tombstone sweep schedule (first tick and period). Null in
    /// production, assigned only by tests. It exists for the same reason its voice-turn sibling does - the
    /// sweep method having its own unit test proves nothing about whether anything CALLS it, which is
    /// precisely how this root grew unbounded while looking covered.
    /// </summary>
    internal static TimeSpan? DictationTombstoneSweepScheduleForTests;
    private readonly Running.WorkListRunnerManager _runnerManager = new();
    // Issue #218: Gateway-owned clock for when each session entered the red / NEEDS-YOU state.
    private readonly NeedsYouClock _needsYouClock = new();
    /// <summary>Issue #2576: how long each session has been waiting for its voice. A SECOND clock beside
    /// the needs-you one because they time different episodes - see VoiceWaitingClock.</summary>
    private readonly Wingman.VoiceWaitingClock _voiceWaitingClock = new();
    // Car Mode (Car Mode mission): the server-side, per-device conversation context behind the fleet
    // tool-calling brain (POST /carmode/turn), so multi-turn references ("the latest one") resolve.
    // In-memory by design; one instance for the whole Gateway.
    private readonly CarMode.CarModeConversationStore _carModeConversations = new();
    // Car Mode (decision 3): the per-device store of a destructive action armed and awaiting the owner's
    // spoken confirmation, so a delete never runs without a clear spoken "confirm".
    private readonly CarMode.CarModePendingStore _carModePending = new();
    // Car Mode (Voice-screen-actions phase, design B): the per-device "current subject" - the session the
    // owner is talking about - so "it" / "answer it" / "snooze it" resolve after a focus or a read.
    private readonly CarMode.CarModeSubjectStore _carModeSubjects = new();
    // Car Mode offline resilience Phase 4b (issue #1427): idempotency + single-flight cache for
    // POST /carmode/turn keyed by the client's turn id, so an already-sent turn whose result was lost in a
    // dead zone auto-retries and ACTS at most once. In-memory, one instance for the whole Gateway.
    private readonly CarMode.CarModeTurnCache _carModeTurnCache = new();
    // Gateway-owned set of sessions whose dictated utterance is being transcribed in the background
    // (the phone released the Speak dialog and the audio is uploading/transcribing). Stamps the
    // orange "Transcribing..." roster color so nobody else grabs the session mid-dictation.
    private readonly Transcription.TranscribingSessions _transcribingSessions = new();
    // Issue #549: the always-on turn-brief stamping pipeline (GatewayTurnBriefAgent) is retired.
    // TurnEndWatcher stays and runs unconditionally - its only job now is firing voice
    // auto-refresh on turn-end for voice sessions, and clearing the stale voice/text cache on
    // the Working transition. The wingman narration (text and voice) now runs on the stateless
    // HostedInferenceBrain (a hosted model call), not the spawned BrainSupervisor.
    private TurnEndWatcher? _turnEndWatcher;
    // The session supervisor (issue #915): the event-driven engine that auto-recovers a session which went
    // idle on a TRANSIENT TRANSPORT fault - the July 21 overnight ENOTFOUND that cost two and a half hours.
    // It rides the SAME Working -> idle boundary the watcher already observes, which is what makes it
    // non-interruptive by construction: a Working session is never evaluated and never touched.
    private Supervision.SessionSupervisor? _sessionSupervisor;

    // Issue #2662: the raised hands of supervised sessions. Constructed unconditionally and never null - a
    // null registry would make every hand read as "down", which is the shape of failure this whole mission
    // exists to remove (a check that passes because nothing is recording). In memory on purpose; see the
    // class for why a raised hand is not a promise that must outlive a restart.
    private readonly Fleet.HandRaiseRegistry _handRaises = new();

    // The rule evaluator (Session Rules mission, phase 2). Hangs off the SAME Working -> idle boundary the
    // supervisor does, so a working session is out of its reach by construction rather than by a rule
    // somebody has to remember.
    private Rules.RuleTurnEndLauncher? _ruleLauncher;

    // The turn log: one self-contained record per turn end, on the machines an administrator has switched
    // capture on for. It rides the SAME boundary as the supervisor and the rules engine but is deliberately
    // NOT part of either - the turns worth capturing most are the ones they never acted on, and a log living
    // inside a judgement writes nothing exactly when that judgement does nothing. Off unless switched on;
    // when off, nothing here runs and a turn ends identically.
    private TurnLog.TurnLogRecorder? _turnLogRecorder;
    private TurnLog.TurnLogSwitchStore? _turnLogSwitches;
    private Timer? _turnLogRetentionTimer;
    private Wingman.WingmanVoiceService? _voiceService;
    // Voice mode is a standing intent, not a one-time action: a tenant that is in voice mode wants EVERY one
    // of its sessions narrating, including the ones that do not exist yet. This timer is how that intent
    // reaches them - it walks each tenant that has voice mode on and switches on any session that is not a
    // voice session yet. Fifteen seconds, matching the reconcile poll beside it: a session joins the voice
    // queue on its own within a few seconds of appearing, without the phone having to sweep anything.
    private Timer? _voiceModeAllSweepTimer;
    private static readonly TimeSpan VoiceModeAllSweepInterval = TimeSpan.FromSeconds(15);
    private int _voiceModeAllSweepRunning;

    /// <summary>Test-only: the tenant-partitioned voice state, so an isolation test can seed a ready clip
    /// under one tenant and prove another tenant never reads it. Null until StartAsync builds it.</summary>
    internal Wingman.WingmanVoiceService? VoiceService => _voiceService;

    /// <summary>Test-only: the turn-end watcher, so an isolation test can drive a real session-state
    /// transition (Working -&gt; Waiting) into the REAL onTurnEnd / onSessionWorking callbacks rather than a
    /// re-implementation. Null until StartAsync builds it.</summary>
    internal TurnEndWatcher? TurnEndWatcherForTest => _turnEndWatcher;

    /// <summary>
    /// Put a conversation into this Gateway's store for one session, as the owning Director's push would
    /// (turn-push mission). Tests need it because the narration path reads the STORE now: before the mission
    /// they supplied a session's words by answering a "turns" command on the tunnel, and there is no such
    /// command any more. Enters the tenant's scope itself, so a test cannot seed one account's conversation
    /// into another's partition by forgetting to.
    /// </summary>
    internal void SeedStoredConversationForTest(TenantId tenant, string directorId, string sessionId,
        params (string Role, string Text)[] messages)
    {
        using var scope = _tenantBoundary?.EnterScope(tenant);
        var batch = new Contracts.TurnPushBatch
        {
            SessionId = sessionId,
            Generation = "seeded:" + sessionId,
            GenerationStartedUtc = DateTime.UtcNow,
            Agent = "ClaudeCode",
            StartOrdinal = 0,
            TotalCount = messages.Length,
            Turns = messages.Select((m, i) => new Contracts.PushedTurn
            {
                Ordinal = i,
                Role = m.Role,
                Parts = { new Contracts.HistoryPartDto { Kind = "Text", Text = m.Text } },
            }).ToList(),
        };
        _sessionTurns.Append(directorId, batch, DateTime.UtcNow);
    }

    /// <summary>
    /// Record a rule firing for one account, as the evaluator's own write would.
    ///
    /// A test needs this because there is NO ROUTE that creates a firing - the record is written by the
    /// evaluator, and the only firing route is the READ. Without a seeded firing, a cross-tenant probe of
    /// <c>GET /gateway/rules/{id}/firings</c> compares one empty list against another and passes whether or
    /// not the tenant filter is there at all: an absence standing in for a refusal. Seeding one gives the
    /// owner's read a specific fingerprint to assert, which is what makes the other account's empty list
    /// mean something.
    ///
    /// Enters the tenant's scope itself, so a test cannot write one account's firing into another's
    /// partition by forgetting to - the same reason <see cref="SeedStoredConversationForTest"/> does.
    ///
    /// WHAT A PROBE BUILT ON THIS DOES NOT COVER, said here so nobody reads more into a green than it
    /// carries (raised in review). It writes through the REAL store, so the entity mapping, the tenant
    /// column, the save guard and the read projection are all the production ones - which is what makes it
    /// evidence about the query filter. It says NOTHING about whether the evaluator enters the right
    /// ambient tenant before recording a firing of its own; that is the evaluator's own integration
    /// question and needs its own test.
    /// </summary>
    internal Rules.SessionRuleFiring SeedRuleFiringForTest(
        TenantId tenant, Guid ruleId, string sessionId, string reason, DateTime nowUtc)
    {
        using var scope = _tenantBoundary?.EnterScope(tenant);
        return _sessionRules.RecordFiring(
            ruleId, sessionId,
            screenText: "a screen the rule looked at",
            understanding: "what the rule made of it",
            // A dry-run rule may not claim to have typed anything, so the decision is the one that records
            // a rule choosing NOT to act. The store refuses anything outside act/decline/abandoned/refused.
            decision: "decline",
            reason: reason,
            primitiveRuns: Array.Empty<Rules.RulePrimitiveRun>(),
            typedText: "",
            outcome: "dry-run",
            grounding: "seeded by a test",
            nowUtc: nowUtc);
    }

    /// <summary>Test-only: the session supervisor (issue #915), so a test can drive a real Working -&gt; idle
    /// transition into the REAL engine. Null until StartAsync builds it.</summary>
    internal Supervision.SessionSupervisor? SessionSupervisorForTest => _sessionSupervisor;
    // Editable/versioned wingman instructions (issue #537); the voice translator reads the active set.
    // Constructed in the constructor body once the EF database is built (it persists to the data layer).
    private readonly Wingman.WingmanInstructionsStore _instructionsStore;
    private System.Threading.Timer? _voiceSweepTimer;
    // Durable dictation upload staging (issue #1006): the phone streams recorded audio here in chunks;
    // the Gateway assembles, transcribes, and injects the turn itself. Each upload id carries a durable
    // delivery record (issue #1183): PENDING chunks are retained until delivered/abandoned, and the
    // terminal tombstone de-dupes the upload id until the client acknowledges it. PENDING is never age-swept
    // here - it holds a live session lock and audio still owed. Issue #1111 added the one bound that does
    // apply: SweepResolvedTombstones retires TERMINAL records whose acknowledgment will never come, on a
    // thirty-day backstop, because otherwise a client that never returns leaves its tombstone immortal.
    // The tenant is named here because the store REQUIRES one - there is no constructor that picks a
    // partition on the author's behalf. This is the BASE handle only: the dictation endpoint re-scopes it
    // with ForTenant to the tenant it resolved from the authenticated device key, and does its work solely
    // inside that partition. Naming Local here reproduces exactly the path this store has always used.
    private readonly Voice.VoiceUploadStore _dictationUploads =
        new(CcDirector.Core.Storage.CcStorage.DictationUploads(), CcDirector.Core.Tenancy.TenantId.Local);
    // Store injection points (Hosted Gateway, Step 1b): the host owns ONE instance of each durable store
    // that was previously reached through a process-wide static, and hands it to the endpoint/service that
    // uses it, so a tenant id can reach the storage layer in a later pull request. Same default paths as
    // the retired statics; no behavior change. The voice-turn upload store (VoiceTurnUploads root) is
    // distinct from _dictationUploads above (DictationUploads root) - two roots, two subsystems.
    private readonly Prompts.GatewayPromptLog _promptLog;
    private readonly Transcription.TranscriptionHistoryLog _transcriptionHistory = new();
    private readonly Transcription.TranscriptionAudioArchive _transcriptionAudioArchive = new();
    private readonly Transcription.TranscriptStore _transcripts;
    // devthrottle #2075: the dismissed-suggestions store and the suggestions engine that mines the stored
    // transcripts per tenant. Both are tenant-scoped and constructed after _transcripts / _gatewayDb below.
    private readonly Transcription.DictionarySuggestionDismissalStore _dictionaryDismissals;
    private readonly Transcription.DictionarySuggestionVerdictStore _dictionaryVerdicts;
    private readonly Transcription.DictionarySuggestionScanStore _dictionaryScans;
    private readonly Transcription.DictionarySuggestionService _dictionarySuggestions;
    // Composes the daily email's suggestions block for a tenant, cadence included.
    private readonly Transcription.SuggestionEmailComposer _suggestionEmailComposer;
    private Transcription.DictionarySuggestionDailySweep? _suggestionDailySweep;
    // The voice-turn staging root, likewise bound to an explicitly-named partition. This path stages a clip
    // for the duration of one turn and then deletes it; it writes no delivery record and performs no
    // cross-request lookup by upload id, so it is unchanged by the dictation partition work. Per-tenant
    // scoping of the voice-turn path itself is a separate piece of work and is NOT claimed here.
    private readonly Voice.VoiceUploadStore _voiceTurnUploads =
        new(CcDirector.Core.Storage.CcStorage.VoiceTurnUploads(), CcDirector.Core.Tenancy.TenantId.Local);
    // The Gateway's stable per-machine install identity (issue #857), owned by the host and handed to the
    // device-registration service instead of the retired static GatewayInstallId.LoadOrCreate.
    private readonly Account.GatewayInstallId _installIdStore = new();
    // The Gateway bearer token store, owned by the host and used to resolve the token below instead of the
    // retired static GatewayAuth.LoadOrCreate. The tray's read-only path stays on GatewayAuth.DefaultTokenFile.
    private readonly Util.GatewayAuth _gatewayAuth = new();

    /// <summary>
    /// THE PRODUCER of the dictation phase label - the three facts that decide whether a session paints
    /// orange for a dictation. The roster's <c>dictationStatusFor</c> callback is exactly this method, so
    /// what a test drives here is what production runs.
    ///
    /// WHY IT IS A NAMED METHOD AND NOT AN INLINE LAMBDA. The rule (<see cref="Transcription.DictationPhase.For"/>)
    /// has regression tests; the WIRING that supplies its three facts had none - the callback appeared in
    /// zero test files. You could wire <c>progressing: true</c> as a constant and the whole Gateway suite
    /// stayed green while defect 19 returned in full, because a hard-true progress flag makes any
    /// undelivered record paint forever, which IS the defect. An unbindable seam is an untestable one, and
    /// this repository's signature failure is a live consumer with an unguarded producer - the rule pinned,
    /// the wiring not. Extracting it changes no behaviour and makes the seam bindable;
    /// <c>DictationOrangeProducerTests</c> binds it with the REAL collaborators and goes red if any of the
    /// three facts is replaced by a constant.
    ///
    /// Read the three facts as the two questions they answer, because conflating them was the whole defect:
    /// <paramref name="uploads"/> answers the DURABLE one ("are there undelivered words?" - must never
    /// expire), and <paramref name="marks"/> answers the BOUNDED one ("is anything actually happening right
    /// now?" - must always expire).
    /// </summary>
    internal static string? DictationStatusFor(
        CcDirector.Core.Tenancy.TenantId tenant, string sessionId,
        Transcription.TranscribingSessions marks, Voice.VoiceUploadStore uploads)
        => Transcription.DictationPhase.For(
            activelyTranscribing: marks.IsActivelyTranscribing(tenant, sessionId),
            undelivered: uploads.IsSessionLocked(sessionId),
            progressing: marks.IsTranscribing(tenant, sessionId));
    // Web Push (mobile app-icon "needs you" dot): the VAPID key pair, the set of subscribed devices,
    // the loopback HTTP client the notifier reads /sessions with, and the background notifier itself.
    // The stores are constructed in the ctor (load-on-construct); the notifier is built and started in
    // StartAsync and disposed in StopAsync.
    private readonly Push.WebPushVapidStore _vapidStore;
    private readonly Push.PushSubscriptionStore _pushSubscriptions;
    private Push.WebPushNeedsYouNotifier? _pushNotifier;
    // The per-tenant driver for the app-icon dot, and the timer that fires it. The notifier holds no timer of
    // its own: a bare timer has no tenant, which is exactly why hosted push had to be switched off before.
    private Push.PushNeedsYouTenantSweep? _pushNeedsYouSweep;
    private System.Threading.Timer? _pushNotifierTimer;
    private int _pushNeedsYouInFlight; // 0 = idle, 1 = a sweep is fanning out (overlap guard)
    private Api.NetDiagMonitor? _netDiagMonitor;
    private Api.NetDiagRollupStore? _netDiagRollup;
    private WebApplication? _app;
    private bool _stopped;

    /// <param name="instancesDirectory">
    /// Override the Director-discovery instances directory (see <see cref="DirectorRegistry"/>).
    /// Tests pass an isolated temp directory; production omits it for the shared default.
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
    /// <param name="account">
    /// Override the Gateway-hosted DevThrottle credential service (issue #636). Tests pass a service
    /// over an in-memory or temp-directory store so they never touch the real Windows Data Protection
    /// store; production omits it so the host builds the Windows-backed service on Windows (and leaves
    /// <see cref="Account"/> null on a non-Windows host, where the operating-system credential store is
    /// not yet implemented).
    /// </param>
    /// <param name="missionsPath">
    /// Override the Gateway-native mission store file (Gateway Cleanup mission, Wave 4b). Tests pass an
    /// isolated temp path; production omits it for the shared default at
    /// <c>CcStorage.Root()\missions.json</c>.
    /// </param>
    /// <param name="missionNotesPath">
    /// Override the mission-WHY store file (Mission Screen mission, Phase 1b, issue #1405). Tests pass an
    /// isolated temp path; production omits it for the shared default at
    /// <c>CcStorage.Root()\mission-notes.json</c>.
    /// </param>
    /// <param name="dictationTranscription">
    /// Override the transcription owner the DICTATION endpoint uses (Lost Dictations mission, issue #1593).
    /// The dictation complete path transcribes BEFORE it delivers, so an end-to-end test of the delivery
    /// arm cannot reach that arm at all without a transcription that succeeds - and the hosted base URL is a
    /// compile-time constant, so there is no way to point it at a local stub. Tests pass a service built over
    /// a stub HttpClient (and their own local history + audio archive, which otherwise default to process-wide
    /// Shared instances that write to the real user's directories). Production omits it and the host builds
    /// the service over its own key vault, exactly as before.
    /// </param>
    /// <summary>
    /// Open the input-statistics aggregator, or report the named reason there is not one.
    ///
    /// THREE things are contained here, and they are different states with different causes.
    ///
    /// 1. A HOSTED Gateway never opens a statistics FILE, under any circumstance. That is the same law
    ///    <see cref="Stats.Data.StatsConnectionSelection"/> states for the store proper, and it is stated
    ///    here too because this aggregator is constructed on its own line and would otherwise walk straight
    ///    past it. A hosted container writing gateway-stats.db writes it to an ephemeral disk, or onto a
    ///    share a second container can corrupt - which is the incident this whole mission exists to end.
    ///    So on hosted no FILE is ever opened, decided BEFORE anything is opened, not caught after.
    ///
    /// 2. A HOSTED Gateway WITH A HEALTHY STATISTICS STORE gets an aggregator built over that store's
    ///    context factory - PostgreSQL, pooled, no file anywhere (issue #1174). This is the case that used
    ///    to be missing, and its absence is the whole defect: "never opens a file" was implemented as
    ///    "never has statistics", so a hosted Gateway whose PostgreSQL store had opened and migrated
    ///    perfectly still answered a named 503 on <c>/stats</c> and <c>/stats/data</c>, and recorded
    ///    nothing to serve later either. The two are not the same statement, and only the first is a law.
    ///
    /// 3. A store that could not be opened - on EITHER deployment - leaves the Gateway running WITHOUT
    ///    statistics rather than refusing to start. Nothing is substituted, no numbers are invented, the
    ///    failure is logged with its own text, and the statistics surface reports itself unavailable with
    ///    the reason. It is a failure-domain boundary, not a fallback. The roster and the tunnels do not
    ///    need statistics, and a deploy that came down with "SQLite Error 14: unable to open database file"
    ///    proved what happens when they are allowed to share a fate.
    /// </summary>
    /// <param name="statsStore">The already-constructed statistics store boundary. On hosted it carries
    /// either the pooled PostgreSQL factory to build over, or the named reason there is not one - which is
    /// the SAME reason this returns, rather than a second spelling of it invented here.</param>
    private static Stats.InputStatsHandle OpenInputStats(string? inputStatsPath, Stats.LateStatsObservers? hostedObservers)
    {
        if (GatewayHostedMode.IsHosted || GatewayHostedMode.IsHostedImage)
        {
            // DEFERRED, not decided. The statistics store is allowed to publish its context factory AFTER
            // the startup deadline, and reading that factory once here - which is what the first version of
            // this wiring did - threw the late arrival away and left a merely slow PostgreSQL cold start
            // with no statistics until the process was restarted. The handle asks the resolver each time.
            FileLog.Write(
                "[GatewayHost] input statistics on hosted are resolved ON FIRST USE from the statistics store's " +
                "context factory; no local file is opened on this path under any circumstance");
            return Stats.InputStatsHandle.Deferred(hostedObservers!);
        }

        try
        {
            var aggregator = new Stats.GatewayInputStatsAggregator(inputStatsPath);
            FileLog.Write("[GatewayHost] input statistics opened on the local statistics file");
            return Stats.InputStatsHandle.Available(aggregator);
        }
        catch (Exception ex)
        {
            var reason =
                $"The local statistics file could not be opened ({ex.GetType().Name}: {ex.Message}). " +
                "Statistics are unavailable; the roster, the tunnels and every other Gateway surface are " +
                "unaffected.";
            FileLog.Write($"[GatewayHost] input statistics are UNAVAILABLE (StoreCouldNotBeOpened): {reason}");
            return Stats.InputStatsHandle.Unavailable(reason);
        }
    }

    private readonly TimeSpan? _directorLaunchTimeout;

    public GatewayHost(int port = DefaultPort, string? token = null, bool? authEnabled = null, string? instancesDirectory = null, string? turnBriefDirectory = null, string? keyVaultPath = null, string? workListsPath = null, string? cronJobsPath = null, string? cronRunsPath = null, string? devicesPath = null, Core.Account.DevThrottleAccountService? account = null, bool? streamMode = null, string? inputStatsPath = null, string? promptLogPath = null, string? snoozePath = null, string? pushSubscriptionsPath = null, string? wingmanInstructionsPath = null, string? missionsPath = null, string? missionNotesPath = null, Transcription.GatewayTranscriptionService? dictationTranscription = null, Core.Agents.AgentKind? brainTool = null, TimeSpan? directorLaunchTimeout = null)
    {
        var retiredFilesRemoved = Core.Configuration.LegacyPrivacyDataCleanup.Run();
        if (retiredFilesRemoved > 0)
            FileLog.Write($"[GatewayHost] Removed {retiredFilesRemoved} retired local tracking file(s).");

        // Resolve and VALIDATE the warm-brain tool up front, before any resource is opened: a brain tool
        // that cannot be hosted is a configuration error that must fail loudly at construction, not
        // silently later at the brain's first spawn. BrainToolConfig.Get reads config.json; a test passes
        // brainTool directly. Only ClaudeCode is hostable today (the hosted-agent path needs a preassigned
        // session id and transcript reads), so a non-hostable value throws here with the fix.
        BrainTool = Core.Configuration.BrainToolConfig.EnsureHostable(brainTool ?? Core.Configuration.BrainToolConfig.Get());

        Port = port;
        // How long a spawn waits for an auto-launched Director to appear. Production's default lives in
        // RegistryDirectorTargetResolver (90s). A test that only needs to prove a route's AUTH behaviour
        // passes a short one: the control request in the hosted deny-by-default theory used to sit through
        // the full ninety seconds for a Director that was never going to appear, which was the single
        // slowest test in the suite (issue #1156). Injected rather than a static test hook, so two hosts in
        // one process can hold different values.
        _directorLaunchTimeout = directorLaunchTimeout;
        Token = token ?? _gatewayAuth.LoadOrCreate();
        // Epic #1159 step A: the eviction horizon is the ONE elapsed-time rule that removes a session, so it
        // is read from configuration here rather than left as a constant only a test can move. Default is a
        // day; a zero or negative value in config.json is refused by the loader and the default stands, so a
        // typo cannot quietly restore the deleting roster.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        Registry = new DirectorRegistry(instancesDirectory)
        {
            EvictionHorizon = TimeSpan.FromHours(gatewayConfig.DirectorEvictionHorizonHours),
        };
        PushedSessions = new Streaming.PushedSessionStore();
        //
        // THE SESSION-NUMBER RELEASE ON EVICTION IS DELETED, and this comment is the record of why, because a
        // deletion with no explanation is the kind of thing a later tidy-up quietly restores.
        //
        // Issue #1292 released a removed Director's session numbers here so a Director that died without
        // releasing them did not leak the pool. Inspection 2 (finding 1) showed the guard protecting it could
        // not work: a connection check followed by a destructive action is two operations, so a Director
        // reconnecting between them had its numbers freed while it was live - and a freed number can be handed
        // to a NEW session while the old one still holds it. Rather than build a cleverer guard around a
        // cleanup, the cleanup goes. Eviction drops a long-dead machine from the READ MODEL and does nothing
        // else; see PushedSessionStore.ForgetIfDisconnected.
        //
        // The cost, stated plainly and NOT understated - an earlier version of this comment said the pool
        // shrinks by the count of retired MACHINES, which is wrong and flatteringly small. The deleted
        // release freed every number a machine owned, one per session it had running, so the pool shrinks by
        // the count of those NUMBERS. Adopt is additive and never frees one, so nothing reclaims them: a
        // retired machine that had eight sessions costs eight of the nine hundred, permanently, and every
        // subsequent retirement costs its own again. On a personal fleet that is small and slow; it is not
        // nothing, and at hosted scale it is the number to watch. Reclaiming them is a separate piece of
        // work needing its own proof, not a rider on an eviction path where getting it wrong reallocates a
        // live session's number.
        //
        // The primitive itself (FleetSessionNumberAllocator.ReleaseForDirector) still exists and now has NO
        // production caller - only tests. It is kept as a primitive for a future reclaim that establishes
        // the machine is gone first. Do not wire it back to OnDirectorRemoved.
        // Repositories mission (#510 phase C): the sibling store for pushed repository/worktree snapshots.
        PushedRepositories = new Streaming.PushedRepositoryStore();
        // Repositories mission (#510 phase D): the daily repository history behind the weekly report.
        // File-backed v1 (see the store's remarks); Postgres is a follow-up that changes persistence, not shape.
        RepoHistory = new Streaming.RepoHistoryStore(Path.Combine(CcStorage.Root(), "repo-history.jsonl"));
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store, at a Gateway-side file path
        // (CcStorage.Root(), the same location the cron and snooze stores use), NOT the Director's tool-config
        // missions.json. Reuses Core.Sessions.MissionStore unchanged.
        //
        // #1039: the store is partitioned by tenant, and the answer for rows written BEFORE it was differs by
        // deployment. Self-host has exactly one tenant, so its existing missions are Local's and it keeps
        // listing them unchanged. Hosted shares this one file across every account, so an unattributed row
        // cannot be attributed after the fact and is quarantined - readable by nobody, left on disk. Decided
        // from GatewayHostedMode.IsHosted, the same signal that picks the tenant context below.
        Missions = new Core.Sessions.MissionStore(
            missionsPath ?? Path.Combine(CcStorage.Root(), "missions.json"),
            adoptUnattributedAs: GatewayHostedMode.IsHosted ? null : Core.Tenancy.TenantId.Local);
        StreamRegistry = new Streaming.GatewayStreamRegistry();
        // The statistics store's failure-domain boundary. Constructed HERE, on its own, deliberately OUTSIDE
        // the main database's construction and outside anything that gates startup: its migration and its
        // connection failures are non-fatal, and coupling it to the main chain's transaction or startup gate
        // would recreate the shared failure domain that separating the two contexts existed to avoid. It
        // never throws for a configuration or database problem - it reports one, with a named reason.
        //
        // IT IS CONSTRUCTED BEFORE THE TWO STATISTICS OBSERVERS BELOW, and that order is load-bearing (issue
        // #1174). Both of them are built FROM its context factory on hosted, so a store constructed after
        // them could only ever hand them nothing - which is exactly the state this ordering fixes: the store
        // opened PostgreSQL and migrated it successfully, and the observers that should have used it had
        // already decided, two lines earlier, that hosted means no statistics.
        StatsStore = Stats.Data.GatewayStatsStore.FromEnvironment();
        if (!StatsStore.IsAvailable)
            FileLog.Write(
                $"[GatewayHost] statistics are UNAVAILABLE ({StatsStore.Availability.ReasonCode}): " +
                $"{StatsStore.Availability.Detail}");
        else
            FileLog.Write(
                $"[GatewayHost] statistics store: source={StatsStore.Availability.Source} " +
                $"target={StatsStore.Availability.Target}");
        // The hosted resolver, which owns BOTH hosted observers and builds them together the first time
        // anything asks and the store has a factory. Null on self-host, which has no late-arrival problem
        // because its file either opened in the constructor or did not.
        _hostedStatsObservers = GatewayHostedMode.IsHosted || GatewayHostedMode.IsHostedImage
            ? new Stats.LateStatsObservers(StatsStore)
            : null;
        InputStatsHandle = OpenInputStats(inputStatsPath, _hostedStatsObservers);
        _promptLog = new Prompts.GatewayPromptLog(promptLogPath);
        // The self-host fleet concurrency record, which is the only one constructed eagerly. A hosted
        // Gateway never constructs it, so gateway-concurrency-stats.json is never written on that path -
        // see the SessionConcurrency property for the incident that makes writing it there unacceptable -
        // and reads its recorder from the hosted resolver instead.
        _selfHostConcurrency = GatewayHostedMode.IsHosted || GatewayHostedMode.IsHostedImage
            ? null
            : new Stats.GatewaySessionConcurrencyStats();
        // Epic #1159 step A: when a machine passes the eviction horizon (or unregisters gracefully), forget
        // what it pushed. The pushed store keeps a Director's sessions across a disconnect on purpose - that
        // is what lets the roster serve a machine whose tunnel is down - so this is the one place those
        // entries are ever released, and without it "keep the sessions" would be an unbounded leak keyed by
        // every Director that ever connected. Scoped to the tenant the removal names, so forgetting one
        // account's Director cannot reach another's.
        //
        // The last-known-good roster cache that used to be forgotten here is DELETED. It was the second
        // staleness authority in the roster path and the one that declared a machine Offline and dropped its
        // sessions; the roster read now serves last-known state unconditionally and reports its age instead.
        //
        // THE ENTIRETY of what eviction destroys, and it is one atomic operation rather than a cascade
        // (inspection 2, finding 1). ForgetIfDisconnected checks liveness and removes the entry inside the
        // store's own membership gate, which RegisterConnection also takes - so a reconnect should either
        // complete before this call, and the entry survives because a connection is active, or after it, and
        // re-creates the entry. There is no window between a check and an act for it to land in, because
        // there is no longer a check and an act: there is one operation.
        //
        // That last step is REASONED, NOT PROVEN. Removing the gate leaves the whole suite green and no test
        // exercises the interleaving, so the argument rests on reading the source. It is the strongest claim
        // available, and it is not a demonstration - do not quote it as one.
        //
        // Without this the roster would keep every machine that ever connected, which is the unbounded leak
        // the horizon exists to stop. With it, and with the other two steps deleted, the worst an eviction can
        // do to a machine that is quietly back is nothing at all.
        //
        // THIS LINE IS THE WHOLE OF IT. One permanent subscriber. If you are here to add a second, read the
        // deletion records above it first - the two that were removed were removed because their guard could
        // not be made atomic with their destruction, and the tests will redden if they come back.
        Registry.OnDirectorRemoved += removal => PushedSessions.ForgetIfDisconnected(removal.Tenant, removal.DirectorId);
        LauncherConnections = new Streaming.LauncherConnectionRegistry();
        // Gateway Cleanup: the tunnel is mandatory; the streamMode parameter is ignored and retained only for existing test call sites (removed with the test rewrite).
        _streamStaleAfter = TimeSpan.FromSeconds(gatewayConfig.StreamStaleAfterSeconds);
        AuthEnabled = ResolveAuthEnabled(authEnabled);
        if (AuthEnabled)
            FileLog.Write($"[GatewayHost] auth gate booted ON (enforced by default, issue #917 - a per-device key or the shared token is required, even on the tailnet; set {AuthDisabledEnvVar}=1 to disable for debugging)");
        else
            FileLog.Write($"[GatewayHost] auth gate booted OFF (disabled via override - requests are accepted without a credential; this is a debugging mode, not the shipped default)");
        _serveProvisioner = new TailscaleServeProvisioner(Registry, () => Port);

        // The Gateway's in-process warm brain (issue #184): supervisor only - the chosen tool
        // spawns lazily. Its former consumers have since moved off it: the wingman narration now
        // runs on the stateless HostedInferenceBrain (a hosted model call), and the always-on
        // turn-brief agent was retired (issue #549). Today the only thing that spawns it is the
        // Settings "Restart Brain" action, so this supervisor is kept for that manual path only.
        // The tool and model remain an EXPLICIT Gateway-level choice (issue #393, building on the
        // pinned-model #204); both default to claude + opus when unset. A config change applies on
        // the next Gateway restart. BrainTool is resolved and validated hostable at the top of this
        // constructor.
        BrainModel = BrainModelConfig.Get();
        FileLog.Write($"[GatewayHost] brain tool: {BrainTool}, model: {BrainModel}");
        Brain = new BrainSupervisor(
            new HostedAgentOptions
            {
                WorkingDirectory = Path.Combine(CcStorage.Root(), "brain"),
                // Headless: the brain has no human to answer a permission prompt, so it takes the
                // full bypass rather than the interactive automatic default (see ClaudeDriver).
                AgentArgs = $"{ClaudeDriver.HeadlessDefaultArgs} --model {BrainModel}",
                Log = FileLog.Write,
            },
            // Construct the hosted brain through HostedAgent.For - the single guard for which agent kinds
            // can be hosted headless (only ClaudeCode today). BrainTool is already validated hostable at
            // the top of this constructor, so this never throws here; routing through For instead of
            // newing a HostedAgent with an arbitrary registry driver keeps that guard the one and only
            // path to a hosted brain, so a non-hostable tool can never slip through to fail at Start.
            agentFactory: o => CcDirector.HostedAgent.HostedAgent.For(BrainTool, o));
        // The production behaviour, assigned once here. See BrainRestartAction for why the indirection
        // exists; nothing in production ever reassigns it.
        BrainRestartAction = ct => Brain.RestartAsync(ct);
        // Production omits keyVaultPath for the shared default; tests pass an isolated path so
        // they never touch the real %LOCALAPPDATA% key store.
        _keyVault = new KeyVault(keyVaultPath);
        _dictationTranscription = dictationTranscription;
        // The EF data layer (Hosted Gateway mission, Step 1b): one gateway.db under the storage root,
        // migrated on open, fail-loud (no JSON fallback). The structured stores that have moved off JSON
        // (work lists, cron) read/write through it, so it is built before them. Tests get an isolated
        // database via CC_DIRECTOR_ROOT, exactly as gateway-stats.db is isolated today.
        // Hosted Multi-Tenancy increment 1: choose the tenant context. Hosted -> the AsyncLocalTenantContext
        // (per-account, fail-closed: a tenant-scoped op outside a resolved scope THROWS, never defaults to
        // local); self-host -> the SingleTenantContext (always Local, unchanged). The same instance is handed
        // to GatewayDatabase (below) and registered in DI, so a scope entered at an auth boundary is exactly
        // what the stores read.
        if (GatewayHostedMode.IsHosted)
        {
            _hostedTenant = new Core.Tenancy.AsyncLocalTenantContext();
            _tenantContext = _hostedTenant;
        }
        else
        {
            _tenantContext = new Core.Tenancy.SingleTenantContext();
        }

        // deferOpen: the listener must bind BEFORE any database work. Connecting and migrating used to sit
        // in front of the bind, so a slow database pushed the bind past the platform's container-start
        // deadline, the platform concluded no port would ever be bound, and it stopped the SITE - tearing
        // down the healthy container serving beside it. That is the 2 and 12 August 2026 outages (#2383,
        // #2585), and shortening the wait only ever made it less likely. StartAsync opens it immediately
        // after the bind; configuration faults still throw here, because waiting cannot fix those.
        _gatewayDb = new Data.GatewayDatabase(_tenantContext, deferOpen: true);
        // MTR-14B: the shared EF database is now the device registry authority. The legacy JSON path is
        // supplied only to the one-time importer; no runtime authentication or mutation reads or writes it.
        // deferInitialize: establishing the credential authority READS the database, and that read used to
        // sit in front of the listener bind. StartAsync initialises it immediately after the database opens,
        // and the readiness gate refuses everything but /healthz until it has.
        Devices = new Pairing.DeviceRegistry(_gatewayDb, devicesPath, GatewayHostedMode.IsHosted,
            deferInitialize: true);
        // Remove-the-network-port phase 1b: the per-session credential registry. A Director registers one key
        // per session over the tunnel it already holds, and an agent inside that session authenticates as the
        // session rather than with its Director's account-wide key. Same database and the same stored-hash
        // shape as the device registry above, because it is the same kind of credential one hop further in.
        SessionKeys = new Pairing.SessionKeyRegistry(_gatewayDb, GatewayHostedMode.IsHosted);
        // The account-to-tenant resolver (Hosted Multi-Tenancy increment 1): owns the tenants mapping table
        // and mints/looks up a tenant from a verified account subject. Built over the EF database; wired into
        // the hosted enrollment boundary (which validates the account token and stamps the resolved tenant on
        // the device) in the follow-up increment. Unused on the single-tenant local install.
        TenantRegistry = new Tenancy.TenantRegistry(_gatewayDb);
        // The free-trial ledger (issue #2117): the Gateway's OWN record of the 14-day Pro trial the public
        // pricing page promises every new account. It is passed INTO the entitlement registry rather than
        // consulted beside it, so the enrollment gate, the request-path lease and the 60s sweep all read one
        // decision and can never disagree about whether an account may use hosted today - which is also what
        // makes the trial EXPIRE on the request path instead of only at enrollment.
        TrialRegistry = new Tenancy.TrialRegistry(_gatewayDb);
        EntitlementRegistry = new Tenancy.EntitlementRegistry(_gatewayDb, trials: TrialRegistry);
        // MTR-15 cancellation cutoff, hosted-only. Reuses the SAME EntitlementRegistry as the enrollment gate
        // so the enrollment check and the ongoing check cannot drift. The revoker calls MTR-14B's tenant-wide
        // device tombstone; the lease is the O(1) hot-path check; the monitor is the 60s sweep.
        if (GatewayHostedMode.IsHosted)
        {
            _accessRevoker = new Tenancy.TenantAccessRevoker(Devices, _directorConnections);
            _accessLeases = new Tenancy.HostedAccessLeaseService(EntitlementRegistry, TenantRegistry, _accessRevoker);
            _leaseMonitor = new Tenancy.EntitlementLeaseMonitor(_accessLeases);
            // TEST ONLY: a hosted gateway that is NOT a real hosted image (a test forcing hosted mode via env,
            // with no baked image marker) auto-provisions the entitlement production requires at the paid
            // enrollment endpoint - which the low-level test enroll paths bypass - so the request-path cutoff
            // does not deny every test tenant. A real hosted IMAGE (IsHostedImage) never wires this, so
            // production always requires a genuine entitlement.
            if (!GatewayHostedMode.IsHostedImage)
                Devices.OnAccountBoundForTest = subject => SeedEntitlementForTest(subject);
        }
        // The auth-boundary tenant binder (Hosted Multi-Tenancy increment 1): the tunnel Hello and the
        // device-key HTTP middleware resolve a tenant from the AUTHENTICATED device key through this, and
        // enter the scope the stores read. Inert on self-host. Built over the same _tenantContext instance
        // the stores read (so a scope it enters is what they resolve) and the device registry.
        _tenantBoundary = new Tenancy.HostedTenantBoundary(_tenantContext, Devices);
        // The background-loop seam (Hosted Multi-Tenancy, session-serving PR2). Its tenant list is the live
        // push-store partition set - exactly the tenants with a Director bound to the tunnel, which is the
        // only fleet a push-store-driven sweep could act on - so a sweep costs no per-tick database scan.
        // (Loops driven by a DATABASE table rather than the push store - cron, hosted-AI spend mirroring,
        // web-push - are NOT converted here: KnownTenants would silently skip every tenant whose Director is
        // offline, which is the tenant most likely to need a scheduled job. They stay hosted-disabled until a
        // database-backed tenant enumeration and a fairness order exist. That is its own increment.)
        _tenantPass = new Tenancy.TenantPass(_tenantBoundary, _hostedTenant, PushedSessions.KnownTenants);
        // Hosted Multi-Tenancy increment 1: the store constructors below seed built-ins and import/re-arm from
        // the database at startup - legitimate SYSTEM operations with no per-account identity. On the hosted
        // Gateway (fail-closed ambient tenant) they must run inside the reserved SYSTEM scope, or the very
        // first construction-time read/write would fail closed at boot. On self-host _hostedTenant is null and
        // this is a no-op (SingleTenantContext already answers Local). The scope is disposed in the finally so
        // a construction fault cannot leak it, and so per-account and per-request scopes (never SYSTEM) govern
        // everything after startup.
        var startupSystemScope = _hostedTenant?.Enter(Core.Tenancy.TenantId.System);
        try
        {
        // Named work lists persist across a Gateway restart (issue #301) in the worklists table (stale
        // claims released on load). The path argument is the LEGACY worklists.json, imported once on first
        // upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real legacy file.
        _workLists = new WorkListStore(_gatewayDb, workListsPath ?? Path.Combine(CcStorage.Root(), "worklists.json"), deferInitialize: true);
        // The workflow catalog (Workflows mission, phase 1): persisted in the workflows tables, the
        // shipped built-ins seeded/upgraded at construction. No legacy JSON - the previous catalog was
        // compiled-in C# literals, so there is nothing on disk to import.
        _workflows = new Workflows.WorkflowStore(_gatewayDb, deferInitialize: true);
        // Workflow runs (phase 4, issue #1771): built after the catalog store so the built-ins a run
        // pins are already seeded.
        _workflowRuns = new Workflows.WorkflowRunStore(_gatewayDb, deferInitialize: true);
        // Workspaces (issue #2722): persisted in the workspaces table. No legacy import - the
        // Director-local workspace files it replaces live on each machine, under each Director's own
        // configuration directory, and the Gateway cannot see them. The desktop pushes them up.
        _workspaces = new Workspaces.WorkspaceStore(_gatewayDb);
        // The central skill library: persisted in the skills tables, the shipped built-ins
        // seeded/upgraded at construction. Nothing is deployed to any machine - agents fetch.
        _skills = new Skills.SkillStore(_gatewayDb, deferInitialize: true);
        // The rule store (Session Rules mission, phase 1). No built-ins to seed - every rule is one the
        // account said - and no legacy file to import.
        _sessionRules = new Rules.SessionRuleStore(_gatewayDb);
        // The governance event ledger (issue #1771, spine item 2): append-only session/run transitions on
        // the EF data layer, so a Gateway restart never loses a recorded transition.
        _governanceEvents = new Governance.GovernanceEventLedger(_gatewayDb);
        // Honest spend (issue #1771, spine item 3): per-session token effort + billing-mode label, and the
        // account-level hosted-AI service dollars mirrored from the credit-debit ledger.
        _sessionSpend = new Governance.SessionSpendStore(_gatewayDb);
        _hostedAiSpend = new Governance.AccountHostedAiSpendStore(_gatewayDb);
        // The governance audit trail (issue #1771, spine item 4): append-only intervention + permission/
        // sandbox decisions on the EF data layer, so a Gateway restart never loses a recorded audit fact.
        _governanceAudit = new Governance.GovernanceAuditLog(_gatewayDb);
        _sessionSpendEmitter = new Governance.SessionSpendEmitter(_sessionSpend);
        _sessionStateEmitter = new Governance.SessionStateEventEmitter(_governanceEvents, _tenantBoundary);
        // The weekly Outcome Ledger reporter (issue #1771, spine item 4): read-only over the run tables +
        // event ledger + spend + audit trail. No store of its own.
        _outcomeLedger = new Governance.OutcomeLedgerReporter(_gatewayDb);
        // The morning report (issue #2119): one honest report per account per day, assembled read-only from
        // the stores above. The pushed-session cache is passed for LABELS ONLY (a waiting row's friendly name
        // and repository path); the waiting fact itself comes from the durable governance ledger, so a
        // Director that happens to be offline at 07:00 costs a row its name, never its place in the email.
        // The repo-state feed (issue #2118): the latest branches and worktrees each Director reports for
        // each repository - the one git-hygiene fact the Gateway cannot observe for itself, and the source
        // of the morning report's stale-worktree and unmerged-branch recommendations.
        _repoState = new Reports.RepoStateStore(_gatewayDb);
        _skillPlacement = new Skills.SkillPlacementStore(_gatewayDb);
        _morningReport = new Reports.MorningReportBuilder(_gatewayDb, PushedSessions, _streamStaleAfter,
            // The repo-state store (issue #2118) is the hygiene rows' source. Passed here rather than
            // resolved inside the builder so the report reads the SAME store the push endpoint writes.
            repoState: _repoState,
            // The same per-tenant log the background monitoring writes, opened per tenant BY PATH so a
            // report can only ever read the measurements of the account it is about.
            microphoneQuality: Transcription.MicrophoneQualityLog.ForTenant);
        // Snooze Length mission: the persisted snooze registry (sessionId -> SnoozeUntilUtc), now in the
        // snoozes table of the EF data layer - a Gateway restart re-arms every pending snooze from the
        // database; an entry already past its time simply fires on the first sweep. The path argument is the
        // LEGACY snooze.json, imported once on first upgrade then renamed aside. Tests MUST pass an isolated
        // path so they never touch the real legacy file. The registry is NO LONGER bounded by eviction - see
        // the deletion record below, and the cost it carries.
        // The durable activity ledger (docs/PLAN-trustworthy-working-start-2026-07-24.md): tenant-scoped
        // evidence of why sessions enter/leave Working and why snoozes end, retained 30 days. Constructed
        // before the snooze registry because the registry appends its lifecycle decisions to it.
        _activityEvents = new Activity.ActivityEventStore(_gatewayDb);
        _snoozeRegistry = new Snooze.SnoozeRegistry(_gatewayDb, snoozePath ?? Path.Combine(CcStorage.Root(), "snooze.json"), _activityEvents);
        // Editable/versioned wingman instructions (issue #537) now persist in the wingman_instructions table
        // of the EF data layer. The path argument is the LEGACY wingman-instructions.json, imported once on
        // first upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real file.
        _instructionsStore = new Wingman.WingmanInstructionsStore(_gatewayDb, wingmanInstructionsPath ?? Path.Combine(CcStorage.Root(), "wingman-instructions.json"), deferInitialize: true);
        // THE SNOOZE CLEAR ON EVICTION IS DELETED, and of the three deletions this is the one that mattered
        // most (inspection 2, finding 1).
        //
        // It was the only IRRECOVERABLE loss in the eviction cascade. A released session number can be
        // re-adopted and a forgotten pushed entry is repopulated by the next Hello, but a deleted snooze is
        // simply gone - the owner set a machine aside until a particular time, and nothing anywhere can
        // reconstruct that intention. Guarding it with a connection check could not work, because a check and
        // a delete are two operations and a Director reconnecting between them lost its snoozes anyway.
        //
        // So the deletion is not performed at all, and the cost is REAL - an earlier version of this comment
        // called the leftover rows "bounded" by PruneNotLive, and that is exactly backwards. PruneNotLive
        // clears a Director's rows when that Director ANSWERS, and a permanently retired machine is precisely
        // the one that never answers again. Its rows are therefore never reached by any prune, and each
        // retirement adds its own set. They accumulate, durably, in the database, for as long as the Gateway
        // lives - not many rows on a personal fleet, but growing without a ceiling and with nothing that
        // removes them.
        //
        // That is accepted here rather than hidden, because the alternative was a race that destroyed a live
        // owner's snoozes irrecoverably, and a slowly growing table of dead rows is recoverable by any future
        // cleanup that first establishes the machine is gone. Reclaiming them is that separate piece of work.
        //
        // The primitive (SnoozeRegistry.ClearForDirector) still exists and now has NO production caller, only
        // tests. Do not wire it back to OnDirectorRemoved: that is the race, and the snooze assertion in
        // EvictionRaceAndCompositionTests.EvictionLeavesSnoozesAndNumbersAlone_OnTheRealHost will redden.
        //
        // (It was already skipped entirely on hosted, for tenancy reasons. Now it is not performed anywhere.)
        // THE PUSH SEAM where this Gateway drives the hold machine off the facts Directors report. The
        // DirectorHub (constructed per-invocation by SignalR) folds every pushed session through this one
        // instance, exactly as it does the input-stats aggregator.
        //
        // SINGLE WRITER OF HOLD (round 2 finding 1). This observer only mutates the registry now; it no
        // longer stamps a one-shot hold mirror down. That fire-and-forget could land a stale None after a
        // fresh Held and be suppressed by the reliable channel's change gate, leaving the desktop rail
        // permanently stale. The SINGLE writer of HoldState down to the Director is FleetDisplayStateObserver
        // (constructed just below), which folds the hold from the registry on the same push, is change-gated,
        // retried, and driven by the periodic display-state sweep - so every transition this observer makes
        // reaches the rail at fold cadence, with no racing second writer.
        SnoozeLandings = new Snooze.SnoozeLandingObserver(
            _snoozeRegistry,
            utcNow: null);
        // Defect 5: the push seam that stamps each session's resolved role down to its owning Director, so
        // the desktop stops being the one screen that cannot suppress a Worker's red. Reads the same fresh
        // fleet snapshot the auto-dismiss sweeper reads (roles need the WHOLE fleet - a controller may be on
        // another machine) and sends over the same down-channel. Like SnoozeLandings, the DirectorHub
        // (constructed per-invocation by SignalR) folds every pushed session through this ONE instance -
        // which matters here more than anywhere: the instance holds the change gate that stops the stamp
        // echoing back up and re-triggering itself forever.
        // Hosted Multi-Tenancy (issue #1966 follow-up): CONVERTED to the ambient tenant. FleetRoleObserver
        // now partitions its change gate per tenant scope, which unblocks reading the AMBIENT tenant here:
        // previously the snapshot was hard-coded to TenantId.Local, empty on the hosted Gateway, so role badges
        // (M/A) never pushed to a hosted Director - the same tenant-blindness that greyed the display-state rail
        // (see the display-state observer below). Roles are hub-only (no periodic sweep), and the DirectorHub
        // push path already runs inside the bound tenant's scope, so reading the ambient tenant is all that is
        // needed here; the per-tenant gate is what stops one tenant's pass pruning + re-storming the others.
        FleetRoles = new Fleet.FleetRoleObserver(
            () => AmbientSnapshotFresh(AutoDismissStaleAfter),
            SendCommandAsync,
            currentScopeKey: () => _tenantPass.Current?.Value);
        // The fold push seam: stamps each session's folded display state down to its owning Director, so the
        // desktop rail stops re-folding from local facts it cannot see. Folds through the SAME method the
        // roster serves from (StampFleetRolesAndFold with THIS host's NeedsYouClock and snooze registry), so
        // the answer pushed to the desktop is byte-identical to the answer every browser gets - one fold, one
        // authority. Like FleetRoles it holds the change gate that stops the stamp echoing back up forever.
        //
        // The snapshot carries only Director-owned facts (it is what the Director pushed up); the two voice
        // readiness booleans are Gateway-only (_voiceService), so they MUST be enriched onto each session
        // here BEFORE the fold - exactly as the roster handler does before its own StampFleetRolesAndFold
        // call - or the push seam folds VoiceAudioReady=false for every session and holds every voice-mode
        // session permanently "Preparing voice" (yellow), never moving it to red once the voice is ready.
        // The voice-ready flip arrives on no Director push, so only the 5s backstop sweep would catch it -
        // and without this enrichment the sweep re-derives the same yellow every tick and the change gate
        // suppresses the update forever. The roster path enriched these (GatewayHost roster map below), so
        // the browsers folded red while the push-only desktop stuck yellow. Same source, same answer.
        // Hosted Multi-Tenancy (issue #1966): CONVERTED to the per-tenant pass. The snapshot reads the AMBIENT
        // tenant (AmbientSnapshotFresh), the voice enrichment reads it too, and the change gate is partitioned
        // per tenant INSIDE the observer - so the periodic sweep (wrapped in _tenantPass.ForEachTenant below)
        // and the tunnel-scoped DirectorHub push both fold exactly one tenant's fleet and stamp it down to that
        // tenant's Directors. Before this the snapshot was hard-coded to TenantId.Local, which on the hosted
        // multi-tenant Gateway is EMPTY (the sessions live under the signed-in account's tenant), so the desktop
        // rail received no stamp and rendered grey while the roster - which resolves the request tenant - folded
        // blue. The gate partitioning is what unblocks the per-tenant pass (see FleetDisplayStateObserver): a
        // flat gate pruned against one tenant's pass would delete the others and stamp-storm every 5s.
        //
        // Inspection 1, finding 2: this snapshot is CONNECTION-scoped, not freshness-scoped, and the change is
        // deliberate. WebPushNeedsYouNotifier counts this fold to drive the phone's app-icon badge - the one
        // nag that persists when the app is closed - so a thirty-second push horizon meant a Director whose
        // tunnel was up but quiet dropped out of the fold and the badge cleared itself, telling the owner
        // nothing needed him on a machine he could have acted on immediately. The auto-dismiss sweeper still
        // takes AmbientSnapshotFresh and must: acting ON a session needs recent data, whereas TELLING THE
        // OWNER about one needs a reachable machine. Two questions, two snapshots.
        FleetDisplayState = new Fleet.FleetDisplayStateObserver(
            AmbientSnapshotConnected,
            sessions => EnrichVoiceThenFoldForPush(
                sessions,
                // MTR-10 Gap D: read the AMBIENT tenant of this per-tenant display pass, byte-identical to the
                // ROSTER's own enrichment (the roster map below resolves the REQUEST tenant and passes it to
                // IsGenerating/HasVoice). This fold runs inside a tenant scope in both drivers - the periodic
                // sweep wraps it in _tenantPass.ForEachTenant, and the DirectorHub push runs in the bound
                // tenant's scope - so _tenantPass.Current is the owning tenant, never null on hosted. The earlier
                // code read TenantId.Local, which #1973 made stale: the tenant-partitioned voice service IS live
                // on hosted, and a Local read there is an EMPTY partition, folding VoiceAudioReady=false for
                // every session and holding every voice-mode session permanently "Preparing voice" (yellow) on
                // the push-only desktop while the roster served red. A null Current is a DENY (false), never a
                // Local fall back, so an unscoped pass discloses nothing.
                //
                // WHY THE FIRST ATTEMPT (abf581ff) REGRESSED THE PUSH: the ambient tenant is whatever tenant a
                // Director is BOUND to, which need not be a minted voice partition. WingmanVoiceService REFUSES
                // to name a partition for such a tenant - IsGenerating/HasVoice THROW ArgumentException for it -
                // and this fold runs synchronously inside DirectorHub.PushSnapshot (which scopes the whole handler
                // to the bound tenant). An unminted-tenant Director's snapshot push therefore threw straight out
                // of PushSnapshot as a HubException and took the WHOLE fleet's display push down. The guard makes
                // an unnameable ambient tenant answer the design-documented "no voice state at all" (false)
                // instead of throwing - see WingmanVoiceService.CanNameVoicePartition. Minted account tenants
                // (production) and Local (self-host) are nameable, so Gap D's per-tenant read is unchanged there.
                voiceGeneratingFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                    && _voiceService?.IsGenerating(t, sid) == true,
                voiceAudioReadyFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                    && _voiceService?.HasVoice(t, sid) == true,
                // The needs-you clock is partitioned per tenant too (Gap C coupled state): pass this pass's
                // owning tenant so a session id shared across accounts keeps a per-tenant "waiting since".
                tenant: _tenantPass.Current ?? TenantId.Local,
                needsYouStampFor: (tenant, sid, isRed) => _needsYouClock.Stamp(tenant, sid, isRed),
                handRaises: _handRaises,
                snoozeRegistry: _snoozeRegistry,
                // Same tenant guard as the two booleans above: an ambient tenant that cannot name a voice
                // partition answers "no voice state at all" rather than throwing out of PushSnapshot.
                voiceUnavailableFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                        ? _voiceService?.VoiceUnavailableFor(t, sid)
                        : null,
                nothingToNarrateFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                    && _voiceService?.NothingToNarrateFor(t, sid) == true,
                directorCannotSendConversationFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                    && _voiceService?.DirectorCannotSendConversationFor(t, sid) == true,
                narrationAbandonedFor: sid => _tenantPass.Current is { } t
                    && Wingman.WingmanVoiceService.CanNameVoicePartition(t)
                    && _voiceService?.NarrationAbandonedFor(t, sid) == true,
                voiceWaitingStampFor: (sid, waiting) => _tenantPass.Current is { } t
                    ? _voiceWaitingClock.Stamp(t, sid, waiting)
                    : null),
            SendCommandAsync,
            currentScopeKey: () => _tenantPass.Current?.Value);
        // Mission Screen mission (Phase 1b, issue #1405): the mission-WHY store, at a Gateway-side file
        // (CcStorage.Root(), the same location the snooze and cron stores use). Loaded here so a Gateway
        // restart re-serves every WHY. Tests MUST pass an isolated path so they never touch the real store.
        // Mission WHY notes now persist in the mission_notes table of the EF data layer. The path argument is
        // the LEGACY mission-notes.json, imported once on first upgrade (quarantine-on-corrupt, boot empty -
        // a cosmetic store must not block boot) then renamed aside. Tests MUST pass an isolated path so they
        // never touch the real legacy file.
        _missionNotes = new MissionNotes.MissionNoteStore(_gatewayDb, missionNotesPath ?? Path.Combine(CcStorage.Root(), "mission-notes.json"));
        // The WHY moved ONTO the Mission record (keyed by mission id) - see Mission.Why. Adopt whatever the
        // old name-keyed note store still holds, once, so no owner loses a WHY they wrote. Idempotent and
        // non-overwriting, so it is safe to run on every boot and safe to run after somebody has already set
        // a WHY through the new route.
        //
        // The notes table is deliberately NOT dropped in the same change that starts reading from the
        // mission record. Migrate, stop reading, verify against the real data, and only then drop - a table
        // drop is the one step here that cannot be undone if the match turns out to be wrong.
        AdoptMissionNoteWhys();
        // Per-tenant settings (issue #2017): the store + typed resolver the settings-page endpoints read and
        // write through. No legacy file to import - an unset per-tenant override falls back to the operator
        // global default (the existing config.json value), so an existing install's settings are unchanged
        // until the owner explicitly overrides one.
        _tenantSettings = new Settings.TenantSettingsStore(_gatewayDb);
        _tenantSettingsResolver = new Settings.TenantSettingsResolver(_tenantSettings);
        // Per-tenant dictation transcript store (issue #509): every transcribed turn's raw and cleaned text
        // lands in the caller tenant's partition of the dictation_transcripts table, write-only, for later
        // mistranscription mining (devthrottle #2075). Same store on SQLite (self-host) and Postgres (hosted) -
        // no IsHosted branch; self-host is just the single Local tenant. No legacy file to import here (the old
        // flat dictation/sessions/*.jsonl log is retired, not migrated - it had no readers).
        _transcripts = new Transcription.TranscriptStore(_gatewayDb);
        // devthrottle #2075 / #2115: the dictionary-suggestions engine. A SCAN (daily per tenant, or the
        // page's "Scan now") mines the tenant's stored transcripts (_transcripts) against that tenant's
        // glossary (TenantGlossary.Load) and its dismissed terms (_dictionaryDismissals), sends never-judged
        // candidates to the screening model (the same hosted inference path as Wingman's fast leg, resolved
        // at call time so a settings change is honored without restart), persists the verdicts
        // (_dictionaryVerdicts - a term is judged at most once per tenant, ever), and stores the approved
        // result (_dictionaryScans - what the page and the badge read). Tenant-scoped throughout; same
        // stores on SQLite (self-host) and Postgres (hosted), no IsHosted branch.
        _dictionaryDismissals = new Transcription.DictionarySuggestionDismissalStore(_gatewayDb);
        _dictionaryVerdicts = new Transcription.DictionarySuggestionVerdictStore(_gatewayDb);
        _dictionaryScans = new Transcription.DictionarySuggestionScanStore(_gatewayDb);
        _dictionarySuggestions = new Transcription.DictionarySuggestionService(
            _transcripts, _dictionaryDismissals, _dictionaryVerdicts, _dictionaryScans,
            Transcription.TenantGlossary.Load,
            (tenant, ct) =>
            {
                // The screening brain: the same endpoint as Wingman, on the tenant's THINKING model (the
                // fast model was measured approving inflection clusters like "issue heard as issues" on the
                // owner's real corpus - judgment quality decides this feature, and a nightly background scan
                // does not care about speed), with a longer per-call deadline than a voice turn.
                var mode = Core.Configuration.TranscriptionModeConfig.Get();
                var ep = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode);
                var key = _keyVault.Get(ep.KeyName) ?? "";
                var model = _tenantSettingsResolver.WingmanModel(tenant, mode, Core.Configuration.WingmanModelRole.Thinking);
                CcDirector.AgentBrain.IAgentBrain brain = new Wingman.HostedInferenceBrain(
                    ep.BaseUrl, key, model, log: FileLog.Write, callTimeout: TimeSpan.FromMinutes(3));
                return Task.FromResult((brain, model.Value));
            });
        // The daily-email block composer. It folds the tenant's "Suggestions in my daily email" choice,
        // whether there is anything pending, and the once-per-batch cadence into ONE verdict, so whatever
        // composes the daily report renders the answer rather than deciding for itself (rule 7). It is handed
        // the READ of the stored scan, never the service, so it can never trigger a scan of its own.
        _suggestionEmailComposer = new Transcription.SuggestionEmailComposer(
            _dictionarySuggestions.GetSuggestions, _tenantSettingsResolver, GatewayPublicUrl.ResolveBase);
        // Cron-job definitions persist across a Gateway restart (epic #479, #482) in the cron_jobs table
        // (next-run times recomputed on load). The path argument is the LEGACY cronjobs.json, imported once
        // on first upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real
        // legacy file.
        _cronJobs = new CronJobStore(_gatewayDb, cronJobsPath ?? Path.Combine(CcStorage.Root(), "cronjobs.json"), deferInitialize: true);
        // Cron run history + the firing engine (epic #479, #483). The engine resolves each due job's
        // target Director from the registry and starts a session over the shared client (the same
        // path the work-list runner uses). The background sweep timer is started in StartAsync. The path
        // argument is the LEGACY cronruns.json, imported once then renamed aside.
        _cronRuns = new CronRunHistoryStore(_gatewayDb, cronRunsPath ?? Path.Combine(CcStorage.Root(), "cronruns.json"));
        }
        finally
        {
            startupSystemScope?.Dispose();
        }
        // The Gateway-hosted DevThrottle credential service (issue #636, Gateway Centralization Phase 2
        // foundation). Tests inject their own service over an isolated store; production builds the
        // service over the operating system credential store rooted under the Gateway config directory:
        // Windows Data Protection on Windows, the login Keychain on macOS. Linux has no local credential
        // store here (a headless container has no Keychain and no secret service), so on Linux Account
        // stays null - an explicit, logged null, not a silent fallback; a local install keeps its data
        // local and the hosted Supabase story owns Linux durable credentials later. The platform guards
        // also satisfy the platform-compatibility analyzer.
        if (account is not null)
            Account = account;
        else if (OperatingSystem.IsWindows())
            Account = CcDirector.Gateway.Account.GatewayAccountFactory.CreateForWindows();
        else if (OperatingSystem.IsMacOS())
            Account = CcDirector.Gateway.Account.GatewayAccountFactory.CreateForMac();
        else
            FileLog.Write("[GatewayHost] DevThrottle credential service not built: no local operating-system credential store on this platform (Linux); Account stays null");

        // A cron job targets a MACHINE (#503): resolve it to a Director at fire time, launching one
        // via the launcher (the shipped /machines/{m}/director/start relay, #331) if none is running.
        var cronTargetResolver = new Running.RegistryDirectorTargetResolver(
            // Audit H1 (gap audit-e): list the target machine's Directors within the FIRE's tenant only, via the
            // registry's tenant-scoped overload. The fleet-global ListDirectors() could match another tenant's
            // Director on the same machine and persist that cross-tenant DirectorId in this tenant's
            // CronRunRecord. The tenant is _tenantPass.Current - the scope the fire runs inside (the engine and
            // the drain runner read the same seam); on self-host it is always Local.
            tenant => Registry.ListDirectors(tenant),
            () => _tenantPass.Current,
            // The auto-launch runs IN-PROCESS through the shared launcher relay, carrying the resolved tenant
            // as an argument. It used to POST to this Gateway's own /machines/{m}/director/start over
            // loopback, which cannot carry a device key and so arrived with no tenant at all.
            new Running.RelayDirectorLauncher(Launchers, SendLauncherCommandAsync),
            launchTimeout: _directorLaunchTimeout);
        // The single resolve-then-create path shared by the cron firing engine and the interactive
        // POST /machines/{machine}/sessions relay ("start a session on another computer"). Gateway Cleanup
        // Phase 2 (PR E-B2): both the spawner and the work-list drain driver ride the tunnel. Tunnel-only:
        // an unconnected Director yields an error, never an HTTP dial.
        Api.DirectorCommandRouter.SendDirectorCommandAsync? spawnSendCommand = SendCommandAsync;
        _machineSessionSpawner = new Running.MachineSessionSpawner(cronTargetResolver, spawnSendCommand);
        // A work-list cron job (#484) drains a named list via the shipped #274 runner on the resolved
        // Director, launching the drain in the background on the shared runner manager. The tunnel-aware
        // driver factory closes over the stream hook so a cron drain creates + reads sessions down the tunnel.
        var cronWorkListRunner = new Running.DirectorCronWorkListRunner(
            _workLists,
            cronTargetResolver,
            _runnerManager,
            new Running.DirectorWorkListDrainLauncher(
                _workLists,
                (directorId, endpoint, repoPath) =>
                    new Running.DirectorImplSessionDriver(directorId, repoPath, spawnSendCommand)),
            // MTR (audit MED): partition the machine drain slot by the fire's tenant, the same seam the engine
            // and notifier read. On self-host this is always Local.
            resolveTenant: () => _tenantPass.Current);
        // Run-complete notifications (issue #622, the deferred "notify on completion" piece of #479).
        // The notifier rides the EXISTING fleet channel - the per-Director doorbell event ring
        // (DirectorEvents, #330) observed at GET /directors/{id}/events - and optionally POSTs the same
        // payload to a per-job webhook. The deep link is built from the resolved Director's tailnet
        // endpoint (the same source the /sessions aggregation uses for ViewUrl); the gw query roots on
        // this Gateway's loopback base. The webhook client is short-timeout, best-effort.
        var cronNotifier = new Running.GatewayCronNotifier(
            DirectorEvents,
            // Issue #2161: a delegate, not a string. This runs in the constructor, long before the listener
            // binds, so a formatted address here would freeze the pre-bind port into every deep link.
            //
            // This is now the ONLY root of a cron deep link. It used to be the Director's own registered
            // endpoint, resolved per notification out of the registry - and the remove-the-network-port
            // mission deleted that endpoint, so a current Director registers an empty one and every
            // run-complete notification carried NO link at all. Not a broken link somebody would report;
            // no link, which reads as a notification that simply does not have one.
            () => $"http://127.0.0.1:{Port}",
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            // MTR-01 (Codex round 1): file the run-complete event into the current cron pass's OWN tenant ring.
            // A null return is DENY - no tenant in scope means the best-effort ring event is skipped rather
            // than filed under a guessed owner. On self-host this is always Local.
            resolveTenant: () => _tenantPass.Current);
        var cronClock = new Running.SystemClock();
        _cronEngine = new Running.CronEngine(
            _cronJobs, _cronRuns, new Running.DirectorCronSessionStarter(_machineSessionSpawner, cronClock),
            cronWorkListRunner, cronNotifier, cronClock,
            // MTR (audit MED): partition the overlap guard by the tenant of the CURRENT unit of work - the
            // run-now request scope on hosted, the single Local scope on self-host - the same seam the notifier
            // reads. A run-now for tenant A's cj_ id must never be refused by tenant B's in-flight same-id job.
            resolveTenant: () => _tenantPass.Current);
        // G8 increment 2: wrap the cron engine in the per-tenant worker seam so the background sweep enters
        // each tenant's scope before it reads the tenant-scoped cron_jobs store.
        _cronSweep = new Running.CronTenantSweep(_tenantBoundary, TenantRegistry, _cronEngine);

        // The activity ledger's 30-day retention, on the same per-tenant worker seam.
        _activityRetentionSweep = new Activity.ActivityRetentionSweep(_tenantBoundary, TenantRegistry, _activityEvents);

        // The daily dictionary-suggestion scan (devthrottle #2115), on the same per-tenant worker seam. The
        // per-tenant body reads the tenant the seam entered from _tenantContext (the seam's sanctioned
        // pattern) and the tenant's time zone from the settings resolver.
        _suggestionDailySweep = new Transcription.DictionarySuggestionDailySweep(
            _tenantBoundary, TenantRegistry, _tenantContext, _dictionarySuggestions, _tenantSettingsResolver);

        // Work history (issue #2194): the durable session record and its observers. The summariser's
        // brain is the tenant's FAST wingman model over the same hosted inference path Wingman speaks
        // through, resolved at call time (the dictionary-screening precedent) - summarisation is a
        // background digest, and the fast leg is the cheap one. The per-pass caps live in the sweep.
        _sessionHistory = new History.SessionHistoryStore(_gatewayDb);
        _knownRepositories = new History.KnownRepositoryStore(_gatewayDb);
        _sessionTurns = new History.SessionTurnStore(_gatewayDb);
        // The machine name and the Director version are stamped from the CONNECTION record, not
        // from the pushed session: the pushed machine name is hard-coded empty on every client in
        // the field, and the version has never been on the session payload at all. Reading them
        // here means the fix lands for Directors that are already running and will never be
        // upgraded. Registry.Get is tenant-scoped, so one account can never stamp another's.
        _sessionHistoryRecorder = new History.SessionHistoryRecorder(
            _sessionHistory,
            (tenant, directorId) =>
            {
                var d = Registry.Get(tenant, directorId);
                return d is null ? History.DirectorFacts.Unknown
                                 : new History.DirectorFacts(d.MachineName, d.Version);
            },
            _knownRepositories);
        var historySummarizer = new History.SessionHistorySummarizer(_sessionHistory, _promptLog,
            (tenant, ct) =>
            {
                var mode = Core.Configuration.TranscriptionModeConfig.Get();
                var ep = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode);
                var key = _keyVault.Get(ep.KeyName) ?? "";
                var model = _tenantSettingsResolver.WingmanModel(tenant, mode, Core.Configuration.WingmanModelRole.Fast);
                CcDirector.AgentBrain.IAgentBrain brain = new Wingman.HostedInferenceBrain(
                    ep.BaseUrl, key, model, log: FileLog.Write, callTimeout: TimeSpan.FromSeconds(90));
                return Task.FromResult(brain);
            });
        _sessionHistorySweep = new History.SessionHistorySweep(
            _tenantBoundary, TenantRegistry, _tenantContext, _sessionHistory, _sessionTurns, historySummarizer);

        // Web Push (mobile app-icon "needs you" dot): load (or generate on first run) the VAPID key
        // pair and the set of subscribed devices. The notifier that fans out to these is built and
        // started in StartAsync, once this Gateway's own /sessions endpoint is reachable on loopback.
        _vapidStore = new Push.WebPushVapidStore();
        // Web Push subscriptions now persist in the push_subscriptions table of the EF data layer. The path
        // argument is the LEGACY push-subscriptions.json, imported once on first upgrade then renamed aside.
        // Tests MUST pass an isolated path so they never touch the real legacy file.
        _pushSubscriptions = new Push.PushSubscriptionStore(_gatewayDb, pushSubscriptionsPath ?? Path.Combine(CcStorage.ToolConfig("gateway"), "push-subscriptions.json"));

        // Gateway device registration (issue #857): on sign-in (and as a first-launch/retry safety net on
        // the heartbeat) register THIS Gateway as a device with the cloud account and store the issued
        // per-device key locally. Built over the credential service, so it exists only when that service
        // does. Egress reuses the SAME cloud base (DEVTHROTTLE_API_URL) and the SAME forwarding token the
        // rest of the account egress uses; the device-registry client gets its own short-timeout HttpClient
        // (the AccountDevicesEndpoint precedent). The install id is resolved lazily (no disk I/O at
        // construction). The per-device key is never logged (security rule DT-05).
        if (Account is not null)
        {
            var deviceRegistryClient = new Core.Account.DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            // Issue #1233 (following #1206): resolve THIS Gateway's own reachable front-door URLs as an
            // ORDERED LIST and publish the whole list (endpoint_urls) on register and every heartbeat, so a
            // joining machine can try them in order and use the first that answers rather than being stuck on
            // one address. Priority order (the reasoning in issue #1233):
            //   1. Machine name plus port (http://<MachineName>:<port>) - ALWAYS. The most stable and most
            //      direct name on a local network (it survives an IP change).
            //   2. The Tailscale front door - only when Tailscale is actually available on this machine. The
            //      reliable cross-network path.
            //   3. The local network IP plus port - the last-resort path (least stable; the IP can change).
            // So the list is three addresses when Tailscale is available and two when it is not, and every entry
            // is a real Gateway front door on this Gateway's own port. The Gateway applies NO operator endpoint
            // override here: gateway.tailnetEndpoint is the DIRECTOR's advertised-endpoint override and can
            // legitimately point at a Director on a different port (for example 7883). Feeding it into the
            // Gateway's published list ranked that Director URL FIRST, so item[0] was not reachable as a Gateway
            // at all (issue #1237). If a Gateway-specific operator override is ever wanted, add a NEW
            // gateway-only config key and pass it as overrideUrl - never reuse the Director's tailnetEndpoint.
            // The single endpoint_url (issue #1206) is the first list entry, so existing readers keep working.
            // Re-resolved on each call (never cached) so an address that appears after start heals within one
            // heartbeat cycle; the resolvers never throw (they report an unresolved address as unresolved).
            var tailnetResolver = new Core.Network.TailnetIdentityResolver();
            var lanResolver = new Core.Network.LanIdentityResolver();
            Func<IReadOnlyList<string>> resolveEndpointUrls = () =>
            {
                // Issue #2161: read the port HERE, per call. Hoisting it to a local outside this lambda captured
                // the pre-bind value, which is 0 on an operating-system-assigned port - so every published
                // endpoint URL would have advertised a port nothing listens on.
                var gatewayPort = Port;
                // Do the I/O here (probe Tailscale and the LAN), then hand the resolved pieces to the pure
                // BuildOrderedEndpointUrls assembler so the ordering/dedup/loopback-skip logic is unit-tested
                // without a real network. Passing no config override to the resolvers yields the PURE
                // discovered address, and the assembler is given no override either (see #1237 above).
                var tailnet = tailnetResolver.ResolveEndpoint(gatewayPort, configOverride: null);
                var lan = lanResolver.ResolveEndpoint(gatewayPort, configOverride: null);
                return BuildOrderedEndpointUrls(
                    // No operator override: gateway.tailnetEndpoint is the Director's key, not the Gateway's (#1237).
                    overrideUrl: null,
                    Core.Network.TailscaleIdentity.BuildMachineNameUrl(Environment.MachineName, gatewayPort),
                    tailnet.IsResolved ? tailnet.Endpoint : null,
                    lan.IsResolved ? lan.Endpoint : null);
            };
            _deviceRegistration = new Account.GatewayDeviceRegistrationService(
                Account,
                deviceRegistryClient,
                new Account.GatewayDeviceKeyStore(),
                machineName: Environment.MachineName,
                platform: ResolvePlatform(),
                appVersion: AppVersion.Semver,
                installIdProvider: _installIdStore.LoadOrCreate,
                endpointUrlsProvider: resolveEndpointUrls);
            _childMirror = new Account.ChildDeviceMirrorService(
                Account,
                deviceRegistryClient,
                Devices,
                appVersion: AppVersion.Semver);
            _deviceHeartbeat = new Account.GatewayDeviceHeartbeatService(
                _deviceRegistration,
                Account,
                deviceRegistryClient,
                appVersion: AppVersion.Semver,
                childMirror: _childMirror);
        }
        else
        {
            FileLog.Write("[GatewayHost] DevThrottle device registration not built: no credential service on this host");
        }

        // The browser loopback sign-in flow relocated onto the Gateway (issue #637). It is built over
        // the credential service, so it exists only when that service does - on a host with no
        // credential service (a non-Windows host) there is nothing to sign in to and the tray stays
        // inert. The reused Core coordinator opens the browser and captures the loopback hand-back.
        // Issue #857: on a successful sign-in it fires the device-registration hook (best-effort,
        // detached) so signing in registers this Gateway as a device.
        if (Account is not null)
        {
            // Issue #881: after sign-in, mint a DevThrottle inference key for the account and store it in
            // the vault so hosted transcription/TTS "just work" with zero configuration. The account JWT
            // authenticates the mint; a manual or already-minted vault key short-circuits it (manual
            // override + reuse across restarts, no key sprawl).
            _transcriptionKeyProvisioner = new Account.TranscriptionKeyAutoProvisioner(
                _keyVault,
                accessTokenProvider: Account.GetAccessTokenForForwarding,
                minter: new Account.AccountInferenceKeyProvisioner());

            SignIn = new Account.GatewaySignInService(
                Account,
                onSignedIn: async ct =>
                {
                    if (_deviceRegistration is not null)
                        await _deviceRegistration.EnsureRegisteredAsync(ct);
                    await _transcriptionKeyProvisioner.EnsureAsync(ct);
                });
        }
        else
            FileLog.Write("[GatewayHost] DevThrottle sign-in flow not built: no credential service on this host");

        // The Gateway-owned background token refresh (issue #640, Gateway Centralization Phase 2). Built
        // over the credential service, so it exists only when that service does - on a host with no
        // credential service there is no token to refresh. Constructed here; the timer is started in
        // StartAsync (so it never blocks construction) and disposed in StopAsync.
        if (Account is not null)
            _tokenRefresh = new Account.GatewayTokenRefreshService(Account);
        else
            FileLog.Write("[GatewayHost] DevThrottle token refresh not built: no credential service on this host");
    }

    /// <summary>
    /// Renders a request's query string for the access / exception log, REDACTING the query of the sign-in
    /// callback path (epic #1069, issue #1080). That path is the reachable front-door callback the cloud
    /// sign-in page redirects the browser back to, and it carries the handed-back access/refresh token in
    /// its query - so logging it verbatim (as every other request's query is logged) would write credential
    /// material to the gateway log, violating security rule DT-05. Every other path keeps its query
    /// unchanged so a remote-side problem stays traceable after the fact.
    /// </summary>
    /// <summary>
    /// Adopt the WHYs the old name-keyed note store still holds onto the Mission records they describe.
    ///
    /// Each account's notes go only to that account's own missions - the note store hands them over already
    /// grouped by tenant, and the mission store takes one tenant at a time, so there is no call shape here
    /// that could match a note to another account's mission by name.
    ///
    /// NON-FATAL BY DESIGN, and this is the one place a swallow is right: the WHY is cosmetic, and the store
    /// it comes from is already documented as quarantine-on-corrupt precisely so a bad WHY file cannot stop
    /// the Gateway booting. A migration that threw here would turn "one account's note is unreadable" into
    /// "nobody's Gateway starts". The failure is LOGGED with its reason rather than hidden, and the WHYs
    /// that did not come across are recoverable - the notes table is still there and is not dropped by this
    /// change.
    /// </summary>
    private void AdoptMissionNoteWhys()
    {
        try
        {
            var byTenant = _missionNotes.AllByTenantForMigration();
            if (byTenant.Count == 0)
                return;

            var adopted = 0;
            foreach (var (tenantValue, whys) in byTenant)
            {
                // The constructor is the validator and fails loud on a blank value; AllByTenantForMigration
                // has already dropped unattributed rows, so this only guards against a malformed one.
                if (string.IsNullOrWhiteSpace(tenantValue))
                {
                    FileLog.Write($"[GatewayHost] AdoptMissionNoteWhys: skipping {whys.Count} note(s) with " +
                                  $"no usable tenant");
                    continue;
                }
                adopted += Missions.ImportWhys(new Core.Tenancy.TenantId(tenantValue), whys);
            }
            FileLog.Write($"[GatewayHost] AdoptMissionNoteWhys: tenants={byTenant.Count} adopted={adopted}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] AdoptMissionNoteWhys FAILED (non-fatal, the notes table is " +
                          $"untouched and the WHYs can still be recovered): {ex.Message}");
        }
    }

    private static string SafeQueryForLog(PathString path, QueryString query)
    {
        if (string.Equals(path.Value, Api.AccountSignInCallbackEndpoint.Path, StringComparison.OrdinalIgnoreCase))
            return query.HasValue ? "?[redacted: sign-in callback credential, DT-05]" : "";
        return query.Value ?? "";
    }

    /// <summary>
    /// The authenticated caller's identity for the access log: which device asked, and for which
    /// account. The log already recorded WHAT was requested - including the file path on a session
    /// file read - but only the remote address for WHO, and an address attributes nothing when the
    /// caller is a phone on a mobile network or a tunnel. Without this, a log can show that a private
    /// key was read and still not say which device read it, which is the difference between knowing
    /// you were breached and knowing whose credential to revoke.
    ///
    /// Carries NO key material: DeviceCredentialIdentity holds neither the raw credential nor its
    /// stored hash, so DT-05 (a credential never reaches the log) still holds. Unauthenticated
    /// requests add nothing, so public routes are unchanged.
    ///
    /// The account is written through <see cref="TenantId.ToLogString"/>, the one-way hash tag every
    /// other tenant-bearing line uses. The first version of this method wrote the RAW account id, one
    /// day after #2343 had gone through the Gateway replacing exactly that with the hash - and it did
    /// so on the highest-volume line in the process, which would have undone that work at the worst
    /// possible site. The tag is stable, so two lines from one account still correlate, which is all
    /// this line needs to do.
    /// </summary>
    private static string DeviceForLog(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(Util.AuthMiddleware.AuthenticatedDeviceItemKey, out var value)
            && value is Pairing.DeviceCredentialIdentity identity)
        {
            var tenant = string.IsNullOrEmpty(identity.TenantId)
                ? "-"
                : new TenantId(identity.TenantId).ToLogString();
            return $" device={identity.DeviceId} type={identity.DeviceType} account={tenant}";
        }

        return "";
    }

    /// <summary>
    /// Resolves this device's platform string for cloud device registration (issue #857): a short, stable
    /// operating-system label sent as the device's <c>platform</c>. The Gateway credential service is
    /// Windows-only today, so this is "windows" in practice, but the label is computed (not hard-coded) so
    /// it is correct should the credential store land on another platform.
    /// </summary>
    private static string ResolvePlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    /// <summary>
    /// Assembles this Gateway's reachable front-door URLs into the ordered, de-duplicated list published as
    /// <c>endpoint_urls</c> (issue #1233). Pure and side-effect free (the caller does the probing and passes
    /// the resolved pieces), so the priority order and de-duplication are unit-tested without a real network.
    /// Order:
    /// <list type="number">
    /// <item>An explicit, non-loopback <paramref name="overrideUrl"/> (gateway.tailnetEndpoint) - the
    /// operator's hand-set reachable address - first; a loopback override is dropped (never advertised).</item>
    /// <item><paramref name="machineNameUrl"/> - the machine name plus port, always present, the most stable
    /// local-network name.</item>
    /// <item><paramref name="tailscaleUrl"/> - the Tailscale front door, only when it was resolved (null when
    /// Tailscale is unavailable), the reliable cross-network path.</item>
    /// <item><paramref name="lanUrl"/> - the local network IP plus port, only when it was resolved (null when
    /// no routable LAN IPv4 exists), the last-resort path.</item>
    /// </list>
    /// Blank entries are skipped and duplicates are collapsed case-insensitively (for example when the
    /// override equals the machine-name URL), so the list carries each distinct reachable address once.
    /// The operator's <paramref name="overrideUrl"/> is the one caller-supplied value, so it is additionally
    /// validated as a real http/https URL before being published (issue #334 contract: any non-http(s) or
    /// unparseable entry would make the account reject the WHOLE register/heartbeat with 400) - a malformed
    /// override is simply dropped, never sent, so it can never break device registration. The three
    /// discovered addresses are always well-formed by construction.
    /// </summary>
    internal static IReadOnlyList<string> BuildOrderedEndpointUrls(
        string? overrideUrl, string machineNameUrl, string? tailscaleUrl, string? lanUrl)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }

        // Only a valid, non-loopback http/https override is publishable. A loopback address is a lie to every
        // remote caller; a non-http(s) or unparseable string would 400 the whole request (issue #334).
        if (IsPublishableHttpUrl(overrideUrl) && !Core.Network.TailnetIdentityResolver.IsLoopback(overrideUrl))
            Add(overrideUrl);
        Add(machineNameUrl);
        Add(tailscaleUrl);
        Add(lanUrl);
        return urls;
    }

    /// <summary>
    /// True when <paramref name="url"/> is a publishable front-door address for the account endpoint_urls
    /// list (issue #334 contract): a non-blank, absolute http or https URL of at most 200 characters. Used to
    /// keep a malformed operator override out of the published list so it can never 400 the whole request.
    /// Pure - unit-tested.
    /// </summary>
    internal static bool IsPublishableHttpUrl([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 200)
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// One-time bootstrap of the central vault from the user environment. If the vault does not yet
    /// carry the DevThrottle account key, seed it from the environment (process, then User scope on
    /// Windows). Never clobbers an existing vault value.
    /// Key name matches <see cref="Core.Configuration.HostedAiKeyResolver.KeyName"/>.
    /// </summary>
    private void SeedKeyVaultFromEnvironment()
    {
        // HOSTED-GATED. The global key vault is denied in whole on hosted (VaultEndpoints), and a deny on the
        // read route alone is not enough: this writer would keep depositing key material behind the deny. On
        // hosted there is no per-account vault to seed into, so it no-ops. The gate reads the deployment
        // signal directly rather than an argument, so it cannot fail open by a caller omitting one.
        if (GatewayHostedMode.IsHosted)
        {
            FileLog.Write("[GatewayHost] hosted: NOT seeding the key vault from the environment - the global vault is denied on hosted");
            return;
        }

        const string keyName = Core.Configuration.TranscriptionEndpointResolver.DevThrottleKeyName;
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
    /// The title of a session, for the wingman to speak first (WingmanTranslator.FidelityPrompt
    /// v5.2). Reads the pushed-session store, so it costs no round trip and stays inside the same
    /// stream-freshness window as every other stream-mode read.
    ///
    /// Returns null when the session is unknown or has no name, and that is deliberate rather than
    /// a placeholder: the prompt rule no-ops on a missing title, so the listener gets an untitled
    /// narration - whereas inventing something ("unknown session", the raw id) would either mislead
    /// or read out an identifier, which the same instructions explicitly forbid.
    /// </summary>
    /// <summary>
    /// The fresh fleet snapshot for the tenant of the CURRENT unit of work (Hosted Multi-Tenancy,
    /// session-serving PR2) - the request scope, the tunnel connection scope, or the per-tenant background
    /// pass. Self-host always resolves Local, so this is the same read as before. On hosted with no scope in
    /// effect it is EMPTY, which is the deny: a sweep with no tenant sweeps nothing rather than reading a
    /// partition that is not its own.
    /// </summary>
    private IReadOnlyList<(string DirectorId, SessionDto Session)> AmbientSnapshotFresh(TimeSpan staleAfter)
        => _tenantPass.Current is { } tenant
            ? PushedSessions.SnapshotFresh(tenant, staleAfter)
            : Array.Empty<(string DirectorId, SessionDto Session)>();

    /// <summary>
    /// The CONNECTION-scoped fleet snapshot for the tenant of the current unit of work - the same tenant
    /// resolution and the same hosted deny as <see cref="AmbientSnapshotFresh"/>, but without the freshness
    /// horizon. Feeds the display-state fold, which decides whether the owner is told about work, and that
    /// question is answered by whether the machine can be reached and not by how long ago it last spoke
    /// (inspection 1, finding 2).
    /// </summary>
    private IReadOnlyList<(string DirectorId, SessionDto Session)> AmbientSnapshotConnected()
        => _tenantPass.Current is { } tenant
            ? PushedSessions.SnapshotConnected(tenant)
            : Array.Empty<(string DirectorId, SessionDto Session)>();

    /// <summary>
    /// Locate a session within the tenant of the CURRENT unit of work (see <see cref="AmbientSnapshotFresh"/>).
    /// Null when no tenant is in scope on hosted - the deny - as well as when the session is simply unknown.
    /// </summary>
    private (string DirectorId, SessionDto Session)? AmbientTryLocate(string sessionId, TimeSpan staleAfter)
        => _tenantPass.Current is { } tenant
            ? PushedSessions.TryLocate(tenant, sessionId, staleAfter)
            : null;

    /// <summary>
    /// One session's stored conversation, as the widget list the wingman reads. Null means nothing has been
    /// stored for it yet - which the narration path treats as "wait", not as a failure, and never as an
    /// attempt against the turn's retry budget.
    ///
    /// The tenant scope is entered HERE, synchronously, and the finished list is handed over. Narration runs
    /// fire-and-forget from a sweep, and an ambient scope does not survive into an asynchronous
    /// continuation - which is how a read ends up answering for the wrong account.
    /// </summary>
    private History.StoredConversation? ReadStoredConversation(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(sessionId)) return null;
        using var scope = _tenantBoundary?.EnterScope(tenant);
        var stored = _sessionTurns.ReadCurrent(sessionId);
        if (stored is null) return null;
        return new History.StoredConversation(
            stored.Value.Head.IsSupported,
            History.StoredConversationWidgets.From(stored.Value.Messages));
    }

    /// <summary>
    /// One session's stored conversation for the turn log: the whole contiguous prefix of its current
    /// generation, both sides, with the parts intact. Null when nothing has been stored for it - which the
    /// record writes down as a named gap rather than as an empty conversation, because "the push has not
    /// arrived" and "this session has said nothing" are different facts.
    ///
    /// The tenant scope is entered HERE, synchronously, and a finished snapshot is handed back, for the same
    /// reason <see cref="ReadStoredConversation"/> does it: the recorder runs fire-and-forget from a
    /// turn-end callback, and an ambient scope does not survive into an asynchronous continuation.
    ///
    /// It deliberately hands over EVERY message rather than the last ten turns. The cut belongs to the
    /// recorder, so there is exactly one place that decides what a full turn is and how many of them a
    /// record keeps.
    /// </summary>
    private TurnLog.StoredConversationSnapshot? ReadTurnLogConversation(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(sessionId)) return null;
        using var scope = _tenantBoundary?.EnterScope(tenant);
        var stored = _sessionTurns.ReadCurrent(sessionId);
        if (stored is null) return null;
        return new TurnLog.StoredConversationSnapshot(
            stored.Value.Head.IsSupported,
            stored.Value.Head.GenerationSource,
            stored.Value.Messages,
            // When this session's computer last pushed. Carried so a capture that landed between the state
            // change and the push that followed it can be SEEN to have stored a conversation one turn short,
            // rather than passing that off as the whole thing.
            DateTime.SpecifyKind(stored.Value.Head.UpdatedAtUtc, DateTimeKind.Utc));
    }

    /// <summary>
    /// Whether the computer that owns this session is CONNECTED but running a build that cannot send its
    /// conversation - the single reason the Gateway's store will never fill for it, and therefore the single
    /// reason a wingman narration is never coming without someone updating that machine.
    ///
    /// CONNECTION IS ASKED FIRST, and that ordering is the whole correctness of this method rather than a
    /// tidiness. <see cref="Streaming.TurnPushCapabilityRegistry"/> is in-memory and learns each Director's
    /// answer from its Hello, so for a Director it has not heard from - one that is away, and every Director
    /// alive in the seconds after a Gateway restart - it reports "does not push". That is the safe reading
    /// for Chat, which asks about connection first too and says "that computer has not checked in". Asked in
    /// the other order here it would be a slander: every session on the fleet would be told its machine
    /// needed updating for as long as it took the Directors to reconnect after a deploy. So a session whose
    /// Director is not currently located answers FALSE - no claim about anybody's build.
    /// </summary>
    private bool DirectorCannotSendConversation(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(sessionId)) return false;
        // not connected: "that computer is away", never "it is too old"
        if (PushedSessions.TryLocate(tenant, sessionId, _streamStaleAfter) is not { } located) return false;
        return !_turnPushCapabilities.PushesTurns(tenant, located.DirectorId);
    }

    private string? ResolveSessionTitle(TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A session title requires an explicit tenant.", nameof(tenant));
        var located = PushedSessions.TryLocate(tenant, sessionId, _streamStaleAfter);
        var name = located?.Session.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// The tenant that OWNS a Director (Hosted Multi-Tenancy voice-serving): the push store is the authoritative
    /// session-&gt;tenant map, so a Director id belongs to exactly the tenant whose partition lists it. The
    /// background voice callbacks (turn-end, session-working, session-state clear) carry a director id but no
    /// request scope, so they resolve the owning tenant HERE and enter it before touching the voice state -
    /// getting it wrong would leak audio across tenants. Null when no tenant claims the director (deny: the
    /// callback skips rather than defaulting to Local, which on hosted would be a wrong-tenant write).
    /// </summary>
    private TenantId? ResolveOwningTenant(string directorId) =>
        PushedSessions.KnownTenants()
            .FirstOrDefault(t => PushedSessions.DirectorIdsFor(t).Contains(directorId, StringComparer.OrdinalIgnoreCase))
            is { IsValid: true } t ? t : (TenantId?)null;

    /// <summary>
    /// How many narrations one sweep cycle may actually START. The wingman brain is one serialized resource,
    /// so this stays deliberately small and stays GLOBAL across tenants. Unchanged by issue #2675 - what
    /// changed is what counts against it (an attempt that reaches the model or speech legs, not an attempt
    /// that is dispatched).
    /// </summary>
    private const int MaxVoiceGenerationsPerSweep = 3;

    /// <summary>
    /// How many sessions one sweep cycle may LOOK AT WITHIN ONE ACCOUNT. Far larger than the generation cap
    /// because the arms it bounds are cheap - a dictionary lookup and one read of the Gateway's own turn
    /// store - but a bound all the same, because that store read is a database query and an unbounded pass
    /// over every voice session on a busy hosted Gateway every 45 seconds would be a real new cost
    /// introduced by fixing issue #2675.
    ///
    /// PER ACCOUNT, NOT GLOBAL, and that is the whole difference between a bound and a smaller version of
    /// the bug. A global ceiling is spent by whichever accounts the pass happens to reach first, so an
    /// account with enough sessions would consume it before the later accounts were visited at all - the
    /// exact starvation this issue is about, moved from three to sixty rather than removed (found in
    /// review). Charged to each account separately, no account can spend another's, and the only shared
    /// budget left is the generation cap - which is right, because that one rations a genuinely shared
    /// resource and this one does not.
    /// </summary>
    private const int MaxVoiceAttemptsPerTenantPerSweep = 60;

    /// <summary>
    /// Pre-build voice for voice sessions that are idle and missing it, so the session list shows
    /// them "voice ready" BEFORE the person enters - including after a gateway restart (the voice-
    /// session set is persisted). Gentle: at most a few per cycle, idle sessions only (a working
    /// session regenerates on its turn-end). Best-effort; never throws into the timer.
    /// </summary>
    internal Task SweepVoiceSessionsAsync()
    {
        var vs = _voiceService;
        if (vs is null) return Task.CompletedTask;
        try
        {
            if (Registry.ListDirectors(_system).Count == 0) return Task.CompletedTask;
            // Hosted Multi-Tenancy voice-serving: run ONE pass per tenant, each inside that tenant's own scope
            // (_tenantPass.ForEachTenant). Within a pass, locate each of THAT tenant's voice sessions in ITS
            // OWN partition (push-store TryLocate - no HTTP dial) and generate into that tenant's voice state,
            // with the tenant passed to GenerateAsync explicitly so the write lands in the right partition even
            // after this synchronous pass (and its scope) returns. Self-host runs exactly one Local pass,
            // unchanged. The generated cap is GLOBAL across tenants - the wingman brain is one serialized
            // resource, so the whole cycle stays gentle no matter how many tenants are live.
            var stale = TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
            Api.DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = SendCommandAsync;
            // TWO BUDGETS, BECAUSE THERE ARE TWO COSTS (issue #2675).
            //
            // What went wrong with one. The cap is three per cycle and it counted every attempt DISPATCHED,
            // not every attempt that cost anything. One account's four sessions were owned by a Director too
            // old to send its conversations, so each of them reached GenerateOnceAsync, found an empty store,
            // said so, and returned - having touched neither the model nor the speech provider. They still
            // took a slot each. On 2026-09-04 that account burned 4,356 slots and every other account on the
            // hosted Gateway got 5, so the one loop that recovers a session whose narration failed never
            // reached the sessions that needed it (companion issue #2676).
            //
            // Not fixed by dropping those sessions from the sweep, deliberately: keeping them is exactly what
            // makes their recovery free - the moment that computer is updated, the next pass narrates it with
            // nobody touching the Gateway. What they must stop doing is spending a budget they cost nothing
            // against. So GENERATIONS counts only attempts that reached the shared model / speech legs
            // (WingmanVoiceService announces that at its own commit point, which is the one place that knows),
            // and a second, far larger ATTEMPTS budget keeps the pass itself bounded - a no-op attempt is
            // cheap, but it is a store read, and an unbounded loop over every voice session on the Gateway
            // every 45 seconds would be a new cost introduced by fixing this one. That second budget is
            // charged PER ACCOUNT, so it cannot become a smaller copy of the starvation it bounds.
            var generated = 0;
            _tenantPass.ForEachTenant(() =>
            {
                if (_tenantPass.Current is not { } tenant) return;   // deny: no scope in effect -> sweep nothing
                var attempted = 0;                                   // this account's own, never shared
                foreach (var sid in vs.VoiceSessionIds(tenant))
                {
                    if (generated >= MaxVoiceGenerationsPerSweep) break;              // gentle on the serialized brain (global cap)
                    if (attempted >= MaxVoiceAttemptsPerTenantPerSweep) break;        // and this account's pass stays bounded
                    if (vs.HasVoice(tenant, sid)) continue;          // already cached, nothing to do
                    // A session whose agent exposes NO conversation history will not become readable by being
                    // asked again immediately, and asking is not free: this pass generates at most three per
                    // cycle across ALL tenants, so a handful of such sessions can hold those slots and starve
                    // the sessions that would actually produce audio (found in review). Its screen already
                    // says "Voice unavailable" from the terminal verdict the read recorded, so nothing is
                    // hidden by standing down for a while.
                    //
                    // BOUNDED, NEVER PERMANENT. The first version of this skip was an unbounded `continue`
                    // whose only escape was the Working transition - which on the hosted push path is
                    // observed by TurnEndWatcher's 15-second sampler and can be missed entirely by a quick
                    // turn. A stale terminal verdict would then have survived forever, and unlike before the
                    // skip existed, no later sweep could have found the recovery. ShouldSkipSweep carries its
                    // own revalidation deadline, so the read happens again on its own (found in review).
                    if (vs.ShouldSkipSweep(tenant, sid)) continue;
                    var located = PushedSessions.TryLocate(tenant, sid, stale);
                    if (located is not { } loc) continue;            // not owned by any of this tenant's directors
                    var director = Registry.Get(tenant, loc.DirectorId);
                    if (director is null) continue;
                    var st = loc.Session.ActivityState ?? "";
                    if (st is "Idle" or "WaitingForInput" or "WaitingForPerm")
                    {
                        FileLog.Write($"[GatewayHost] voice sweep: pre-building voice for idle session {sid} (tenant {tenant.ToLogString()})");
                        // A pre-build is not a new turn - generate quietly so an idle session a client
                        // may be listening to is never flipped yellow mid-play (issue #1322). Fire-and-forget.
                        var route = new Api.SessionVerbClient(director, sendCommand);
                        attempted++;
                        // The slot is spent by the CALLBACK, not by this line. It fires synchronously, before
                        // the returned task is handed back, at the point the attempt commits to the model and
                        // speech legs - so an attempt that returns having produced nothing and spent nothing
                        // leaves the budget where it was, and the next session in the loop still gets it.
                        _ = vs.GenerateAsync(tenant, sid, route, CancellationToken.None, showReadingWindow: false,
                            onProviderReached: () => generated++);
                    }
                }
            });
        }
        catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep error: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Carry each tenant's standing VOICE MODE intent to the sessions that did not exist when the switch was
    /// thrown. For every tenant with voice mode on, any session that is not yet a voice session gets the same
    /// two effects the fleet-wide fan-out gives: the owning Director's voice-mode flag set over the tunnel,
    /// and the Gateway voice marker set so the per-turn narration starts.
    ///
    /// This is what makes voice mode a switch rather than a one-time action. The fan-out at
    /// <c>POST /sessions/voice-mode/all</c> reaches only the sessions alive at that instant; a session created
    /// a minute later, or one whose computer was offline and has since come back, was never told - so it
    /// quietly never joined the voice queue and looked like the queue had lost it.
    ///
    /// Deliberately one-directional: it only ever switches sessions ON, and only for a tenant that asked for
    /// voice mode. Turning voice mode off is done by the endpoint's own fan-out, which unmarks every session
    /// in one pass; a sweep that also chased the off direction would fight anyone who had deliberately put a
    /// single session on voice while the fleet flag was off.
    ///
    /// Best-effort and never throws into the timer. A session whose computer is unreachable is left alone and
    /// picked up by a later pass, which is the whole point of a standing intent.
    /// </summary>
    internal async Task SweepVoiceModeAllAsync()
    {
        // One pass at a time. A pass that runs long (many sessions, slow tunnels) must not have a second
        // timer tick start on top of it and send the same session two voice-mode commands.
        if (Interlocked.Exchange(ref _voiceModeAllSweepRunning, 1) == 1) return;
        try
        {
            var vs = _voiceService;
            if (vs is null) return;
            var stale = TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
            Api.DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = SendCommandAsync;

            // Each tenant is decided AND ACTED ON inside its own scope - both halves, in one awaited pass.
            // The deciding half must be scoped because the flag, the roster and the voice marker are all that
            // tenant's; the ACTING half must be scoped too, because SendCommandAsync resolves the Director's
            // stream within the tenant of the current unit of work and treats no-scope as a DENY. Planning
            // here and sending after the pass returned is what broke this sweep on hosted: every command was
            // dropped before it reached the tunnel and logged as an unreachable Director, forever. Hence
            // ForEachTenantAsync, which holds the scope ACROSS the await. Self-host runs one Local pass.
            await _tenantPass.ForEachTenantAsync(async () =>
            {
                if (_tenantPass.Current is not { } tenant) return;   // deny: no scope in effect -> sweep nothing
                var on = _tenantSettingsResolver.VoiceModeAll(tenant);
                // ONE SNAPSHOT FEEDS BOTH DIRECTIONS. Taking it twice would let a session appear in one
                // pass and not the other, so the sweep could switch a row on and off across a single tick.
                var snapshot = PushedSessions.SnapshotFresh(tenant, stale);

                // OFF FIRST, and unconditionally - see PlanOff for why it is not gated on the fleet switch.
                // These are sessions marked for voice BEFORE the supervised rule existed (voice marking is
                // persisted, so they survive restarts); without this pass they would be narrated at the owner
                // for ever and the rule would look like it had done nothing.
                foreach (var (directorId, sid) in Wingman.VoiceModeAllSweep.PlanOff(
                             snapshot, s => vs.IsVoiceSession(tenant, s)))
                {
                    var offResult = await Api.DirectorCommandRouter.TrySendAsync(
                        sendCommand, directorId, "voice-mode", sid, new { enabled = false }, CancellationToken.None);
                    // UNMARK EVEN IF THE DIRECTOR DID NOT ANSWER, and note this is the OPPOSITE of the
                    // on-direction below, which marks only on Ok. The asymmetry is deliberate: marking a
                    // session the Director never heard about would make the Gateway spend narration on a
                    // session whose own view says it is not a voice session, but UNMARKING one it never heard
                    // about only stops the Gateway spending narration - the safe direction of the same
                    // disagreement. The Director's own flag is corrected by a later pass when it comes back.
                    vs.Unmark(tenant, sid);
                    FileLog.Write($"[GatewayHost] voice-mode sweep: switched OFF supervised sid={sid} " +
                                  $"director={directorId} tenant={tenant.ToLogString()} directorAck={(offResult is { Ok: true })}");
                }

                // Plan returns empty immediately for a tenant that is not in voice mode, so a fleet with the
                // switch off costs one flag read and nothing else.
                var plan = Wingman.VoiceModeAllSweep.Plan(
                    on,
                    snapshot,
                    sid => vs.IsVoiceSession(tenant, sid));
                if (plan.Count == 0) return;

                FileLog.Write($"[GatewayHost] voice-mode sweep: {plan.Count} session(s) to switch on for tenant={tenant.ToLogString()}");
                foreach (var (directorId, sid) in plan)
                {
                    var result = await Api.DirectorCommandRouter.TrySendAsync(
                        sendCommand, directorId, "voice-mode", sid, new { enabled = true }, CancellationToken.None);
                    if (result is { Ok: true })
                    {
                        // Mark only after the Director accepted. Marking a session the Director never heard
                        // about would make the Gateway spend narration on a session whose own view mode says
                        // it is not a voice session - the two halves must agree or the roster lies.
                        vs.Mark(tenant, sid);
                        FileLog.Write($"[GatewayHost] voice-mode sweep: switched on sid={sid} director={directorId} tenant={tenant.ToLogString()}");
                    }
                    else
                    {
                        // Left for a later pass on purpose - that is what a standing intent means. Says NOT
                        // SWITCHED ON rather than "not reachable": the sweep sees only that no Ok came back,
                        // and an unreachable Director is just one of the ways that happens. Claiming the
                        // Director was unreachable is how this line spent a day blaming the network for a
                        // Gateway-side deny; the preceding router line carries the outcome that was observed.
                        FileLog.Write($"[GatewayHost] voice-mode sweep: sid={sid} NOT switched on (director={directorId}) - will retry next pass");
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-mode sweep error: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _voiceModeAllSweepRunning, 0); }
    }

    /// <summary>
    /// The Gateway's warm brain (issue #184): a claude.exe this process hosts itself - no
    /// Director dependency. Dormant until first use; RestartAsync is the recovery verb.
    /// </summary>
    public BrainSupervisor Brain { get; }

    /// <summary>
    /// What <c>POST /gateway/brain/restart</c> actually performs. Defaults to the real warm-brain restart,
    /// and production never assigns it - the default IS the production behaviour, so this is a seam, not a
    /// switch, and there is no fallback path hiding behind it.
    ///
    /// It exists because that route is the one owner-settings route whose served-side proof could not
    /// otherwise be honest (issue #1863). Its whole job is to start a coding-agent process, so a test that
    /// drove the real handler would spawn one on the machine running the suite - which is exactly the
    /// capability the hosted deny exists to prevent, and not an acceptable thing for a proof harness to do.
    /// Proving the route by asking for it with the WRONG VERB and reading the 405 was the previous
    /// compromise, and it proved only that the route was REGISTERED - never that the POST reaches this
    /// handler.
    ///
    /// With this seam a test drives the exact path and verb, gets this handler's own receipt, and starts no
    /// process. The revert arms can drive it too, so no mutation run leaves a stray agent behind.
    /// </summary>
    internal Func<CancellationToken, Task> BrainRestartAction { get; set; }

    /// <summary>The agent tool the brain runs as (issue #393), resolved at construction from
    /// config.json "brain_tool" (default: <see cref="BrainToolConfig.Default"/>, Claude Code).
    /// A config change applies on the next Gateway restart.</summary>
    public Core.Agents.AgentKind BrainTool { get; }

    /// <summary>The model the brain is pinned to (issue #204), resolved at construction
    /// from config.json "brain_model" (default: <see cref="BrainModelConfig.Default"/>).
    /// Recorded on every brief; a config change applies on the next Gateway restart.</summary>
    public string BrainModel { get; }

    /// <summary>Gateway-side turn-brief storage (issue #185): append-only, fleet-wide.</summary>

    /// <summary>
    /// Build the wingman's brain for the CURRENTLY selected AI provider and requested model role. The
    /// wingman is a stateless hosted chat-completions call, not the warm <c>claude.exe</c> brain,
    /// because that agent speaks a different protocol and cannot run these hosted models.
    /// The provider, credential, and role-specific model are read at CALL time, so a settings change is
    /// honored on the next turn without a Gateway restart.
    /// </summary>
    internal string ResolveWingmanModel(TenantId tenant, Core.Configuration.WingmanModelRole role)
        => _tenantSettingsResolver.WingmanModel(tenant, Core.Configuration.TranscriptionModeConfig.Get(), role).Value;

    private Task<CcDirector.AgentBrain.IAgentBrain> WingmanBrainAsync(TenantId tenant, Core.Configuration.WingmanModelRole role, CancellationToken ct)
    {
        var mode = Core.Configuration.TranscriptionModeConfig.Get();
        var ep = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode);
        var key = _keyVault.Get(ep.KeyName) ?? "";
        var model = _tenantSettingsResolver.WingmanModel(tenant, mode, role);
        CcDirector.AgentBrain.IAgentBrain brain =
            new Wingman.HostedInferenceBrain(ep.BaseUrl, key, model, log: FileLog.Write);
        return Task.FromResult(brain);
    }

    /// <summary>
    /// Wire the session supervisor (issue #915) to the live Gateway. Every leg reuses machinery that already
    /// exists: the tunnel caller for the screen read, the menu check and the send; the pushed roster snapshot
    /// for the activity-state read, so liveness is NEVER established by dialing a session; the durable
    /// activity ledger for the recovery log; and the owner-notify channel the network-diagnostics alerts use
    /// for the escalation email.
    /// </summary>
    /// <summary>
    /// The rule evaluator's production wiring (Session Rules mission, phase 2). Every dependency is the same
    /// one the supervisor beside it uses, and for the same reasons: the Director is resolved WITHIN ITS OWN
    /// TENANT, a Director that is not connected resolves to null and every read treats that as "cannot tell",
    /// and the session's facts come from the pushed roster snapshot rather than from dialing the session.
    /// </summary>
    private Rules.GatewayRuleEnvironment BuildRuleEnvironment() =>
        new(
            store: _sessionRules,
            route: (tenant, directorId) =>
            {
                var director = Registry.Get(tenant, directorId);
                if (director is null) return null;
                Api.DirectorCommandRouter.SendDirectorCommandAsync sendCommand = SendCommandAsync;
                return new Api.SessionVerbClient(director, sendCommand);
            },
            session: (tenant, sessionId) => PushedSessions.TryLocate(tenant, sessionId, _streamStaleAfter)?.Session,
            brainProvider: WingmanBrainAsync,
            enterTenantScope: tenant => _tenantBoundary.EnterScope(tenant));

    private Supervision.GatewaySupervisorEnvironment BuildSupervisorEnvironment()
    {
        var notify = new Core.Account.AccountNotifyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        return new Supervision.GatewaySupervisorEnvironment(
            settings: _tenantSettingsResolver,
            // Resolve the owning Director within its OWN tenant (the registry is keyed by (tenant, director
            // id)); a Director that is not connected to the tunnel resolves to null, which every read treats
            // as "cannot tell" rather than as a fault.
            route: (tenant, directorId) =>
            {
                var director = Registry.Get(tenant, directorId);
                if (director is null) return null;
                Api.DirectorCommandRouter.SendDirectorCommandAsync sendCommand = SendCommandAsync;
                return new Api.SessionVerbClient(director, sendCommand);
            },
            activityState: (tenant, sessionId) =>
                PushedSessions.TryLocate(tenant, sessionId, _streamStaleAfter)?.Session.ActivityState,
            brainProvider: WingmanBrainAsync,
            ledger: _activityEvents,
            enterTenantScope: tenant => _tenantBoundary.EnterScope(tenant),
            sendOwnerEmail: async (subject, body, ct) =>
            {
                var token = Account?.GetAccessTokenForForwarding();
                if (string.IsNullOrEmpty(token))
                {
                    // Nobody is signed in, so there is no owner address to reach. Say so plainly rather than
                    // reporting a send that never happened - the escalation still stands in the recovery log.
                    FileLog.Write("[GatewayHost] supervisor escalation email SKIPPED: no signed-in account");
                    return false;
                }
                var result = await notify.SendOwnerAsync(token, subject, body, null, null, ct).ConfigureAwait(false);
                return result.Sent;
            });
    }

    /// <summary>
    /// The port Kestrel actually bound, read from the running server's own addresses (issue #2161). Only
    /// called when the caller asked for an operating-system-assigned port. Throws rather than returning a
    /// placeholder: every consumer of <see cref="Port"/> builds a URL from it, so a silent 0 here would
    /// surface far away as an unreachable address with no trace of where it came from.
    /// </summary>
    private static int ReadBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            throw new InvalidOperationException(
                "[GatewayHost] The listener reported no bound address, so the operating-system-assigned port " +
                "cannot be read back. The host is listening on an unknown port and nothing can reach it.");

        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }

        throw new InvalidOperationException(
            $"[GatewayHost] No bound address carried a usable port (addresses: {string.Join(", ", addresses)}).");
    }

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={(Port == OperatingSystemAssignedPort ? "operating-system-assigned" : Port.ToString())}");

        // Seed the central vault from a DevThrottle account-key environment value once when present.
        // The vault is the live source of truth thereafter and SetIfAbsent never clobbers it.
        SeedKeyVaultFromEnvironment();

        // Issue #881: an install that was already signed in before this shipped won't fire the
        // post-sign-in hook again, so ensure the hosted transcription key here too - detached and
        // best-effort, so a mint call never delays or blocks startup. No-op when a key is already
        // stored or the host has no credential service.
        if (_transcriptionKeyProvisioner is not null)
            _ = Task.Run(async () =>
            {
                try { await _transcriptionKeyProvisioner.EnsureAsync(); }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] startup transcription-key ensure failed (ignored, best-effort): {ex.Message}"); }
            });

        // Subscribe the Tailscale provisioner BEFORE Registry.Start() so the initial
        // file-discovery load fires OnDirectorAdded into it and every Director port
        // gets an HTTPS mapping without anyone re-running a script. Skipped when HOSTED: a
        // hosted Gateway is reached by its public URL, not a tailnet, and the image bundles
        // no tailscale binary, so provisioning would only produce noise.
        if (!GatewayHostedMode.IsHosted)
            _serveProvisioner.Start();
        Registry.Start();

        // Issue #331: start the stale-launcher sweep timer so launchers that crash
        // without unregistering are evicted after 90 s.
        Launchers.StartSweep();

        // Registry is now loaded with the current Director set: run the first self-healing
        // reconcile - re-assert the front door, drop serve mappings for Directors that died
        // while the Gateway was down (orphans -> 502 from a phone), and sweep any leaked
        // ephemeral-port mappings (issue #179). The provisioner repeats this on a timer.
        // Skipped when HOSTED (no tailnet, no tailscale binary).
        if (!GatewayHostedMode.IsHosted)
            _serveProvisioner.Reconcile();

        // Gateway Cleanup mission (post-cut): the advertised-endpoint re-verification monitor (issue #325)
        // is DELETED. It HTTP-probed each Director's advertised /healthz; post-cut liveness is the tunnel
        // connection itself, so there is no advertised HTTP endpoint to re-verify.

        // Issue #549: the always-on turn-brief stamping pipeline is retired. TurnEndWatcher stays
        // and runs unconditionally - a small always-running watcher whose only job is firing voice
        // auto-refresh for voice sessions on turn-end, and clearing the stale voice/text cache on
        // the Working transition. It no longer depends on a brief agent existing. PUSH-fed since
        // #186 by Director doorbell pings and heartbeat snapshots (wired into the endpoints below);
        // the only pull left is the one-time startup catch-up sweep.
        FileLog.Write("[GatewayHost] StartAsync: starting the turn-end watcher (voice auto-refresh only; turn-brief pipeline retired in #549)");
        // sessionTitleResolver: the wingman opens every narration with the session's title, so a
        // listener with the phone in a pocket knows WHICH session is talking before anything else
        // (WingmanTranslator.FidelityPrompt v5.2). Push-store read - no dial. See ResolveSessionTitle.
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, _tenantSettingsResolver,
            instructionsProvider: () => _instructionsStore.ActiveContent,
            sessionTitleResolver: ResolveSessionTitle,
            // The narration's words now come from the Gateway's own store (turn-push mission, phase 3), not
            // from a tunnel command asking the Director to re-read the user's transcript. See
            // ReadStoredConversation for why the tenant scope is entered there rather than inside the service.
            conversationReader: ReadStoredConversation,
            directorCannotSendConversation: DirectorCannotSendConversation);

        // The session supervisor (issue #915). It hangs off the SAME turn-end boundary as the voice refresh
        // below, deliberately: that event is the only thing that can wake it, so a Working session is out of
        // its reach by construction rather than by a rule somebody has to remember.
        _sessionSupervisor ??= new Supervision.SessionSupervisor(BuildSupervisorEnvironment());
        FileLog.Write("[GatewayHost] StartAsync: session supervisor armed (auto-recovery on a transient transport fault)");

        // The rule evaluator (Session Rules mission, phase 2). It hangs off the SAME turn-end boundary, so
        // a working session cannot be reached by a rule at all - the only thing that wakes it is a session
        // crossing into idle.
        _ruleLauncher ??= new Rules.RuleTurnEndLauncher(
            new Rules.RuleEvaluator(BuildRuleEnvironment()),
            enterTenantScope: tenant => _tenantBoundary.EnterScope(tenant));
        FileLog.Write("[GatewayHost] StartAsync: rule evaluator armed (standing instructions, dry run unless promoted)");

        // The turn log (the turn-end research plan, area A). It hangs off the same boundary as the two
        // judgements above and is deliberately independent of both: the whole reason it exists is to make a
        // turn end VISIBLE that nothing acted on, and a log that lived inside the supervisor would fall
        // silent in exactly that case. Constructed unconditionally and cheap when off - with no switch row
        // for a machine it reads one cached boolean per turn end and returns.
        _turnLogSwitches ??= new TurnLog.TurnLogSwitchStore(_gatewayDb);
        // Prime the decisions and keep them fresh in the background. The recorder's switch check then costs
        // a memory read, so a Gateway with capture off puts NOTHING blocking on the turn-end path.
        _turnLogSwitches.Start();
        _turnLogRecorder ??= new TurnLog.TurnLogRecorder(new TurnLog.GatewayTurnLogEnvironment(
            switches: _turnLogSwitches,
            writer: new TurnLog.TurnLogWriter(Core.Storage.CcStorage.TurnLog()),
            route: (tenant, directorId) =>
            {
                var director = Registry.Get(tenant, directorId);
                if (director is null) return null;
                Api.DirectorCommandRouter.SendDirectorCommandAsync sendCommand = SendCommandAsync;
                return new Api.SessionVerbClient(director, sendCommand);
            },
            locate: (tenant, sessionId) => PushedSessions.TryLocate(tenant, sessionId, _streamStaleAfter)?.Session,
            // The tenant scope is entered inside this reader, synchronously, for the same reason the
            // narration path does it there: the recorder runs on a background task and an ambient scope does
            // not survive into an asynchronous continuation, which is how a read ends up answering for the
            // wrong account.
            conversation: ReadTurnLogConversation,
            settings: _tenantSettingsResolver,
            isVoiceSession: (tenant, sessionId) => _voiceService?.IsVoiceSession(tenant, sessionId) ?? false,
            // The SAME source the session listing uses to fill this in (see GatewayEndpoints, where a
            // Director-supplied name wins and an empty one is enriched from the registration). A capture
            // reads the raw pushed snapshot, which never passes through that step.
            machineName: (tenant, directorId) => Registry.Get(tenant, directorId)?.MachineName),
            // Every read the capture makes is partitioned, exactly as the supervisor's are. Without this the
            // capture runs with no tenant in scope, every read is denied, and the corpus fills with records
            // whose screen and conversation are permanently missing.
            enterTenantScope: tenant => _tenantBoundary.EnterScope(tenant));
        FileLog.Write("[GatewayHost] StartAsync: turn log armed (records nothing unless an administrator has switched a machine on)");

        // Retention on the captured records. Without it the copy on the Gateway is permanent - every screen
        // and both sides of every conversation, for every account capture was switched on for, on a shared
        // file system behind a management endpoint. Anything worth keeping has already been pulled daily,
        // so a bounded window costs nothing and turns an unbounded exposure into a bounded one.
        var turnLogRetention = new TurnLog.TurnLogRetention(Core.Storage.CcStorage.TurnLog());
        _turnLogRetentionTimer = new Timer(_ =>
        {
            try { turnLogRetention.Sweep(); }
            catch (Exception ex) { FileLog.Write($"[GatewayHost] turn-log retention sweep FAILED: {ex.Message}"); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(6));

        _turnEndWatcher = new TurnEndWatcher(
            onTurnEnd: signal =>
            {
                // Hosted Multi-Tenancy voice-serving (MTR-10 Gap C): the OWNING tenant was resolved BEFORE the
                // transition decision and is carried on the signal, so act within it directly - no second
                // resolution. An invalid tenant never reaches here (the resolver denies before Observe, so the
                // watcher never fires), but guard defensively rather than fall back to Local on hosted.
                var tenant = signal.Tenant;
                if (!tenant.IsValid)
                {
                    FileLog.Write($"[GatewayHost] turn-end: invalid tenant on signal for director {signal.DirectorId} sid={signal.SessionId} - skipped");
                    return;
                }

                // The turn log goes FIRST, and the order is load-bearing rather than tidy. The supervisor
                // below can type "continue" into this session, and the rules engine can act on it; either
                // changes the screen. A capture started after them would record the screen AFTER we
                // intervened while the record says it is the screen the turn ended on - the corpus would
                // then teach us that faults recover themselves. Starting first does not make the capture
                // instantaneous, but it puts the read ahead of our own keystroke rather than behind it.
                // Returns immediately; nothing below waits on it.
                _turnLogRecorder?.OnTurnEnd(signal);

                // Governance capture (issue #1771, spine item 3): record this session's cumulative spend at
                // turn-end from the pushed roster snapshot. Runs for EVERY session (not just voice), and is
                // isolated so a spend hiccup never breaks the voice refresh below - the failure is logged loud,
                // not swallowed into a fabricated value.
                try
                {
                    if (PushedSessions.TryLocate(tenant, signal.SessionId, _streamStaleAfter) is { } spendLoc)
                        _sessionSpendEmitter.Emit(spendLoc.Session);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayHost] turn-end spend emit FAILED: sid={signal.SessionId}: {ex.Message}");
                }

                // Session supervision (issue #915): evaluate this idle transition for a terminating transport
                // fault and, if there is one, recover it. Runs for EVERY session, not just voice ones, and is
                // isolated so a supervision fault never breaks the voice refresh below. It returns
                // immediately - the waiting and the re-send happen on its own background task.
                try
                {
                    // ENTER THE OWNING TENANT'S SCOPE, exactly as the voice generation below does, and for the
                    // same reason: everything the supervisor then touches is partitioned - the per-tenant
                    // settings read, the tunnel connection lookup that carries its screen read and its send,
                    // and the activity-ledger write. The scope is an async-local, so the background ladder the
                    // engine starts inside this call inherits it and keeps it for the whole episode. Without
                    // it the pass runs with no tenant in scope and every one of those reads is denied - the
                    // engine would wake up and silently do nothing.
                    using (_tenantBoundary.EnterScope(tenant))
                        _sessionSupervisor?.OnTurnEnd(signal);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayHost] turn-end supervision FAILED: sid={signal.SessionId}: {ex.Message}");
                }

                // Session Rules (mission phase 2): evaluate this idle transition against the account's
                // standing instructions. The launch is a piece of that FEATURE and lives with it - see
                // RuleTurnEndLauncher, which holds the tenant scope, the fire-and-forget and the isolation,
                // and which the feature's own guards can therefore see. It never throws.
                _ruleLauncher?.OnTurnEnd(tenant, signal.DirectorId, signal.SessionId);

                // Voice sessions (issue #531): the turn just finished on its own, so re-make the
                // spoken summary + audio in the background. It is then "voice ready" in the session
                // list with no wait. Non-voice sessions do nothing here - the watcher is voice-only.
                if (_voiceService is { } vs && vs.IsVoiceSession(tenant, signal.SessionId))
                {
                    // Gateway Cleanup mission, Phase 2: reach the owning Director (carried on the signal as
                    // its DirectorId) through the tunnel-first SessionVerbClient - no HTTP dial. The Director
                    // may be push-only (empty control URL); the tunnel path still reaches it by id.
                    // Hosted Multi-Tenancy voice-serving: resolve the Director in its OWNING tenant (the
                    // registry is keyed by (tenant, director id)); Local would miss it on a hosted Gateway.
                    var director = Registry.Get(tenant, signal.DirectorId);
                    if (director is null) return;
                    Api.DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = SendCommandAsync;
                    var route = new Api.SessionVerbClient(director, sendCommand);
                    FileLog.Write($"[GatewayHost] turn-end -> voice auto-refresh: sid={signal.SessionId} director={signal.DirectorId} newTurn={signal.IsNewTurn} tenant={tenant.ToLogString()}");
                    // Show the yellow "wingman reading" hold only for a genuinely new turn; a startup
                    // catch-up of an earlier turn refreshes quietly so a listening client is not
                    // dropped out of the speaking screen (issue #1322). Enter the owning tenant's scope so the
                    // generation runs within it; the tenant is also passed explicitly so the write lands in the
                    // right partition even after this fire-and-forget returns.
                    using (_tenantBoundary.EnterScope(tenant))
                        _ = vs.GenerateAsync(tenant, signal.SessionId, route, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
                }
            },
            onSessionWorking: (tenant, sid, directorId) =>
            {
                // Working again: the cached voice/text summary is now stale - clear it so the list stops
                // showing it ready and nothing stale plays (issue #531). It regenerates on the next turn-end.
                // Hosted Multi-Tenancy voice-serving (MTR-10 Gap C): the OWNING tenant was resolved before the
                // transition decision and is passed in, so clear it in that partition directly; an invalid
                // tenant never reaches here (the resolver denies before Observe) and never falls back to Local.
                _ = directorId;
                if (tenant.IsValid)
                    _voiceService?.OnSessionWorking(tenant, sid);
                // Issue #915: the session is working again, so any recovery wait in flight for it is over -
                // whether our "continue" landed or it came back on its own. This is the cancel that makes the
                // engine incapable of sending into a session that is working.
                if (tenant.IsValid)
                    _sessionSupervisor?.OnSessionWorking(tenant, sid);
            },
            // Gateway Cleanup mission, Phase 2: under stream mode the catch-up / reconcile reads the push
            // store instead of HTTP-pulling each Director's session list (no dial).
            pushedSessions: PushedSessions);
        // First tick = the startup catch-up sweep; then the 15s reconcile poll for
        // Directors that never push (file-discovered locals, old builds).
        _turnEndWatcher.Start();

        // The voice-mode sweep: carry each tenant's standing "my fleet is in voice mode" intent to the
        // sessions that did not exist when the switch was thrown. Runs unconditionally and costs nothing on a
        // fleet with voice mode off - VoiceModeAllSweep.Plan returns an empty plan for such a tenant, so no
        // roster is even walked for it.
        FileLog.Write("[GatewayHost] StartAsync: starting the voice-mode sweep (standing voice-mode intent -> new sessions)");
        _voiceModeAllSweepTimer = new Timer(
            _ => _ = SweepVoiceModeAllAsync(),
            null, VoiceModeAllSweepInterval, VoiceModeAllSweepInterval);

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
        // Issue #806 (mobile foundation): emit an OpenAPI document at /openapi/v1.json. The mobile
        // app's build-time codegen (openapi-typescript) turns it into a typed TypeScript client, so
        // the C# DTOs stay the single source of truth for the front-end.
        builder.Services.AddOpenApi();

        // Issue #1176 (Phase 1a): the Director-push stream. The hub and its two collaborators are
        // registered as singletons so the hub (constructed per-invocation by SignalR's container) and the
        // /sessions aggregation (wired explicitly below) share the one PushedSessionStore instance.
        // Gateway Cleanup mission, Phase 0 (up-stream): set the DirectorHub's message-size and stream-buffer
        // bounds from the shared DirectorStreamLimits so the hub and the Director's producer can never drift
        // (Architect ruling 2 + 1). MaximumReceiveMessageSize must admit a full-size framed binary frame
        // (the cap plus a small envelope allowance); StreamBufferCapacity is small so a slow browser sink
        // pushes back onto the producer's yield - the backpressure invariant, not an optimization.
        // AddMessagePackProtocol is ADDITIVE: the hub now negotiates MessagePack OR JSON, so a MessagePack
        // Director gets binary framing (a 48KB byte[] up-stream frame stays ~48KB, not ~64KB base64 that would
        // blow past MaximumReceiveMessageSize and drop the connection) while a JSON-only client still connects
        // (backward-compat for the fleet rollout). It removes the 33% base64 tax on every tunnel byte.
        builder.Services.AddSignalR()
            .AddMessagePackProtocol()
            .AddHubOptions<Streaming.DirectorHub>(o =>
            {
                // The receive cap must admit the LARGER of a framed up-stream message (chunked at
                // MaxBinaryFrameBytes) and a single unary command REPLY (turns / full buffer can be MBs, sent as
                // one client-result message). Using only the frame size tore the tunnel down on any large read
                // ("maximum message size ... exceeded"), which 500'd voice/History and dropped the roster. The
                // up-stream producer still chunks at MaxBinaryFrameBytes, so backpressure is unchanged.
                o.MaximumReceiveMessageSize = Contracts.DirectorStreamLimits.MaxInboundMessageBytes;
                o.StreamBufferCapacity = Contracts.DirectorStreamLimits.StreamBufferCapacity;
                // Tunnel liveness (issue #1153). SET EXPLICITLY, and set from the SAME shared constants the
                // Director's client uses, so the two halves of one timing budget cannot drift apart and
                // neither side is left running on a framework default nobody chose. Before this, both ends
                // ran on defaults and a Director hung up 35 times in a day on a Gateway that was alive.
                o.KeepAliveInterval = Contracts.DirectorStreamLimits.KeepAlivePing;
                o.ClientTimeoutInterval = Contracts.DirectorStreamLimits.SilenceTolerance;
            });
        builder.Services.AddSingleton(PushedSessions);
        builder.Services.AddSingleton(PushedRepositories);
        builder.Services.AddSingleton(RepoHistory);
        // Issue #2194: the work-history recorder, so the SignalR-constructed DirectorHub folds every
        // accepted push into the durable session record (throttled inside the recorder).
        builder.Services.AddSingleton(_sessionHistoryRecorder);
        // The stored conversation (turn-push mission): DirectorHub.PushTurns writes it, Hello reads its watermarks.
        builder.Services.AddSingleton(_sessionTurns);
        // Which Directors say they send conversations - recorded at Hello, read when Chat finds nothing stored.
        builder.Services.AddSingleton(_turnPushCapabilities);
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store, so the mission endpoints and
        // spawn validation share the one instance.
        builder.Services.AddSingleton(Missions);
        // Gateway Cleanup Phase 0: the one up-stream registry the DirectorHub (constructed per-invocation by
        // SignalR) pumps StreamUp frames into.
        builder.Services.AddSingleton(StreamRegistry);
        // DevThrottle Stats: the hub (constructed per-invocation by SignalR) folds each pushed session's
        // tally into this one aggregator instance, which the /stats dashboard reads.
        // The HANDLE is registered, never the aggregator itself, so the container can construct the
        // DirectorHub whether or not statistics are available. Registering the aggregator directly would
        // make an absent statistics store a hub-construction failure, which is the coupling this handle
        // exists to break.
        builder.Services.AddSingleton(InputStatsHandle);
        // Defect 20: the hub lands a deferred snooze's clock through this one observer instance the moment
        // the Director pushes up "the hold landed".
        builder.Services.AddSingleton(SnoozeLandings);
        builder.Services.AddSingleton(FleetRoles);
        builder.Services.AddSingleton(FleetDisplayState);
        // Register the tenancy seam as the SAME instance GatewayDatabase reads (Hosted Multi-Tenancy
        // increment 1), so a scope a SignalR-hosted boundary enters is exactly what the stores resolve. On
        // self-host this is the SingleTenantContext (always Local); on hosted it is the AsyncLocalTenantContext.
        builder.Services.AddSingleton<CcDirector.Core.Tenancy.ITenantContext>(_tenantContext);
        // The auth-boundary tenant binder, so the SignalR-constructed DirectorHub resolves a tenant from the
        // authenticated device key at Hello and enters that scope on every push (Hosted Multi-Tenancy incr 1).
        builder.Services.AddSingleton(_tenantBoundary);
        builder.Services.AddSingleton(_directorConnections);
        // Registered as a FACTORY, under the interface, so the container asks the same resolver everything
        // else asks. Registering the INSTANCE would freeze the answer at container-build time - a third
        // place the hosted late-open decision got frozen, alongside the roster and the route mapping - and
        // registering it only "when there is one" made that freeze permanent for a store still opening.
        //
        // It stays nullable rather than being omitted: an absent statistics recorder must never be a
        // Gateway that does not start, which is the inversion this whole boundary exists to prevent. Every
        // consumer already treats it as optional.
        builder.Services.AddSingleton<Func<Stats.ISessionConcurrencyRecorder?>>(_ => () => SessionConcurrency);
        // The statistics failure-domain boundary, so anything resolved from the container can ask whether
        // there is a statistics store and, when there is not, why not.
        builder.Services.AddSingleton(StatsStore);
        builder.Services.AddSingleton(Registry);
        // Remove-the-network-port phase 1b: the DirectorHub (constructed per-invocation by SignalR) registers
        // and revokes session keys through the SAME registry the auth gate verifies against.
        builder.Services.AddSingleton(SessionKeys);
        // launcher-persistent-join: the LauncherHub (constructed per-invocation by SignalR) and
        // SendLauncherCommandAsync share this one connection registry.
        builder.Services.AddSingleton(LauncherConnections);

        // Honor X-Forwarded-Proto/Host/For from the TLS-terminating front end so ctx.Request.Scheme
        // reflects the public scheme the user actually used. Which senders may be believed differs
        // between the two deployments - self-host trusts loopback only (Tailscale Serve), hosted must
        // also accept the Azure App Service front end, which forwards from a non-loopback platform
        // address (issue #1870). See ForwardedHeadersPolicy for why that is safe there and only there.
        var isHostedDeployment = _tenantBoundary.IsHosted;
        builder.Services.Configure<ForwardedHeadersOptions>(
            o => Tenancy.ForwardedHeadersPolicy.Apply(o, isHostedDeployment));

        _app = builder.Build();

        _app.UseForwardedHeaders();

        // THE READINESS GATE. Nothing but /healthz is served until the database is open AND the device
        // credential authority has been established.
        //
        // This exists because the listener now binds BEFORE that work, which is the fix for the deploy
        // outages (#2383, #2585): the database open used to sit in front of the bind, so a slow database
        // pushed the bind past the platform's container-start deadline, the platform decided no port would
        // ever appear, and it stopped the SITE - tearing down the healthy container serving beside it.
        //
        // Binding early stops the platform giving up on the site. It is NOT permission to serve before the
        // Gateway knows which device keys are valid, and without this gate that is exactly what would
        // happen: requests would arrive at an uninitialised credential authority. So every request but the
        // probe is refused, loudly and cheaply, until both are up.
        //
        // FIRST in the pipeline, ahead of auth and the access log: a request that must not be served must
        // not be authenticated first either, and this decision cannot depend on anything downstream.
        //
        // /healthz is the ONE exemption, and it must be - it is what the deploy's warm-up polls and what
        // the platform's swap gate pings, and it reports the not-ready state itself (503 with
        // status "starting"), so the swap waits rather than promoting a Gateway that cannot serve.
        _app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || IsReadyToServe())
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            // Tell a client it is worth coming back, and roughly when. The open is bounded by the database
            // retry window, so a fixed small hint is honest without promising a time we cannot keep.
            ctx.Response.Headers.RetryAfter = "5";
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync("{\"error\":\"starting\",\"detail\":\"The Gateway is listening but not ready to serve yet.\"}");
        });

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
                FileLog.Write($"[GatewayHost] unhandled exception: {ctx.Request.Method} {ctx.Request.Path}{SafeQueryForLog(ctx.Request.Path, ctx.Request.QueryString)}: {ex}");
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
                // Load-test Stage 0 (issue #1173): the server-side roster latency, recorded beside the
                // access log so the load driver's outside view has an inside twin.
                if (path.Equals("/sessions", StringComparison.OrdinalIgnoreCase)
                    && HttpMethods.IsGet(ctx.Request.Method))
                    Diagnostics.LoadTestMetrics.RosterRequestObserved(sw.Elapsed, ctx.Response.StatusCode);
                if (!path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    && !path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                    // The keep-warm heartbeat (P2) hits /diag/ping every ~25s per client - warming traffic,
                    // not a request worth logging; skip it so it does not flood the access log.
                    && !path.Equals("/diag/ping", StringComparison.OrdinalIgnoreCase)
                    // The React Cockpit's hashed static assets would flood the log.
                    && !path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[GatewayHost] {ctx.Request.Method} {path}{SafeQueryForLog(ctx.Request.Path, ctx.Request.QueryString)} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client} host={ctx.Request.Host}{DeviceForLog(ctx)}");
                }
            }
        });

        if (AuthEnabled)
        {
            // Issue #469: a per-device key issued at enrollment is a valid Bearer credential
            // alongside the shared machine token, so an enrolled Director authenticates with its
            // own unique key. The shared token still authenticates the host's own browser/cookie
            // surface, but it is no longer the path a NEW device uses to get in (that is account
            // sign-in - see SignedInEnrollmentEndpoint).
            var requireToken = new AuthMiddleware.RequireToken { Token = Token, Devices = Devices, Leases = _accessLeases, Boundary = _tenantBoundary, Sessions = SessionKeys };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        // Hosted Multi-Tenancy increment 1: the device-key HTTP boundary. After auth, resolve this request's
        // tenant from the AUTHENTICATED device key the auth middleware stashed, and enter that scope for the
        // rest of the pipeline, so tenant-scoped stores read the caller's tenant - derived from the verified
        // credential, never from client input. Only device-key-authenticated requests carry a key; a
        // shared-token or public request enters no scope, so any tenant-scoped store access it makes fails
        // closed on hosted (deny-by-default), while non-tenant endpoints (enrollment, health) still work.
        // Registered only on the hosted Gateway; on self-host Local is the ambient answer (nothing to enter).
        if (_tenantBoundary.IsHosted)
        {
            _app.Use(async (ctx, next) =>
            {
                var tenant = _tenantBoundary.ResolveRequestTenant(ctx);
                if (tenant is { } resolved)
                {
                    using (_tenantBoundary.EnterScope(resolved))
                        await next();
                }
                else
                {
                    await next();
                }
            });
        }

        // Mobile front door (issue #806, docs/architecture/mobile/): a phone browser-navigation
        // (Accept: text/html, phone User-Agent) not already under the mobile app gets a 302 to the mobile
        // app at /mobile/; a desktop UA falls through unchanged to the Cockpit. After auth, before the
        // Cockpit's browser-page routes - so a phone never reaches the Cockpit sitemap.
        Mobile.MobileRedirect.UseMobileRedirect(_app);

        // Browser-aware front door (the Cockpit sitemap): a PERSON navigating to /sessions,
        // /directors, or /cockpit (Accept: text/html) gets the React Cockpit shell; programs keep
        // getting JSON from the explicit endpoints below. After auth, before routing.
        Cockpit.CockpitReactApp.UseBrowserPageRoutes(_app);

        // Enable ASP.NET WebSocket support so the per-session proxy can recognize an inbound WS
        // upgrade (ctx.WebSockets.IsWebSocketRequest) and accept it (AcceptWebSocketAsync) for the
        // hand-rolled terminal/dictation stream proxy. The old YARP forwarder used the raw upgrade
        // feature and needed no middleware; the manual proxy (SessionWsForwarder) does. Pass-through
        // for upgrades it does not accept, so the YARP-forwarded Cockpit/Blazor circuit is unaffected.
        _app.UseWebSockets();

        _app.UseRouting();

        // Issue #1176 (Phase 1a): the Director-push stream endpoint (the tunnel). The tunnel/hubs are
        // mandatory and always mapped. Mapped after the host-wide auth middleware above, so the handshake
        // is token-gated exactly like every other route; a Director's .NET SignalR client presents its
        // Bearer token on the handshake.
        _app.MapHub<Streaming.DirectorHub>("/director-stream");
        FileLog.Write("[GatewayHost] DirectorHub mapped at /director-stream; /sessions serves from the push cache when fresh");

        // launcher-persistent-join: the launcher-push stream endpoint. When a launcher joins, the machine
        // lifecycle relay pushes commands DOWN this stream instead of dialing the launcher's REST API.
        //
        // MAPPED EVERYWHERE, INCLUDING HOSTED. It was denied on hosted, alongside #1917's deny of the
        // /launchers + /machines HTTP family, and this is the last piece of that deny to come down.
        //
        // WHAT THE DENY WAS FOR. The hub was the one launcher/machine writer the HTTP deny did not cover, and
        // the reason was specific: LauncherHub.Hello resolved NO tenant, and LauncherConnectionRegistry keyed
        // one active connection per BARE MACHINE NAME. So a launcher saying Hello for machine X overwrote the
        // row for machine X whoever owned it - one subscriber's launcher could supersede another's active
        // connection just by claiming the same name, and then receive the commands meant for it. That was a
        // real leak and denying the hub genuinely closed it.
        //
        // WHY IT IS NOT THE ANSWER ANY MORE - AND WHY THIS IS NOT SIMPLY A REVERT. Both conditions the deny
        // named are now false, fixed by the tenant-scoping work that replaced the HTTP deny:
        //   * LauncherHub.Hello resolves the tenant from the AUTHENTICATED DEVICE KEY and ABORTS the connection
        //     when it resolves to none - exactly as DirectorHub.Hello does. The comparison the deny drew
        //     against DirectorHub no longer distinguishes them.
        //   * LauncherConnectionRegistry keys on (TenantId, Machine), not on the bare name. Two subscribers
        //     naming the same machine occupy two different rows; neither can see, overwrite or receive on the
        //     other's.
        // So the supersession the deny existed to prevent is no longer expressible. Leaving the hub unmapped
        // would not be caution, it would be a gate standing in front of a hole that is already filled - while
        // costing every subscriber the ability to reach their own machines.
        //
        // AND THE COST WAS TOTAL, NOT PARTIAL, WHICH IS WHY THIS MATTERS MORE THAN IT LOOKS. The stream is
        // the ONLY arm that can reach a launcher - everywhere, since phase 6 of the remove-the-network-port
        // mission deleted the launcher's listener and the REST fallback that dialed it (and even before that,
        // the launcher's Kestrel bound loopback only, so a hosted Gateway could never dial a remote machine).
        // With the hub unmapped, a subscriber's launcher could register and heartbeat and appear in the
        // machine list, and then never receive a single command: observed in the field as a launcher retrying
        // the hub thousands of times a day while looking healthy.
        //
        // THE REGISTRY PURGE THE DENY REQUIRED IS DISCHARGED BY CONSTRUCTION. It asked for the launcher and
        // launcher-connection registries to be purged of rows written under the bare-name scheme. Both are
        // process-lifetime IN-MEMORY dictionaries - GatewayHost.Launchers is a plain new(), LauncherConnections
        // is a plain new(), there is no launcher entity in the database and nothing reloads either at startup -
        // so the restart that ships this performs the purge. On hosted there is additionally nothing to purge:
        // the hub was never mapped there, so no bare-name-keyed connection row was ever written on hosted.
        //
        // THE STANDARD THIS IS HELD TO, both halves at once: a subscriber may drive EVERY machine registered to
        // their own account, and may never reach one registered to another. HostedLauncherHubTenantTests proves
        // both on a real hosted Gateway with two real tenants - including that both may hold a live connection
        // for the SAME machine name simultaneously, which is the precise case the deny said was impossible.
        _app.MapHub<Streaming.LauncherHub>("/launcher-stream");
        FileLog.Write("[GatewayHost] LauncherHub mapped at /launcher-stream (hosted and self-host); connections are keyed (tenant, machine) and Hello aborts on an unresolved tenant");

        // Product version stamped by Directory.Build.props; full form carries the commit SHA.
        var version = AppVersion.Full;
        // Network Diagnostics mission (P1): the shared hourly quality rollup - POST /diag/result folds
        // client results into it, and the monitor (started below) folds its per-tick observations into the
        // same instance, so both writers share one thread-safe in-memory state + file.
        _netDiagRollup = new Api.NetDiagRollupStore(Path.Combine(CcStorage.Root(), "netdiag-rollup.json"));

        // Gateway Cleanup mission: the cut removed the DirectorEndpointClient argument (_client) - the
        // Gateway no longer dials Directors over HTTP, so Map no longer takes an HTTP client. The
        // network-diagnostics rollup store is threaded in as a named argument on the tunnel-only signature.
        GatewayEndpoints.Map(_app, Registry, version, Token,
            // The auth-boundary tenant binder - REQUIRED (finding CR-7): request-scoped reads resolve the
            // caller's tenant through it, and on hosted a request with no bound tenant is denied, never Local.
            _tenantBoundary,
            AuthEnabled,
            netDiagRollup: _netDiagRollup,
            // Issue #2017: the snooze-default consumer at POST /sessions/{sid}/hold reads the caller tenant's
            // default through the resolver instead of the process-global config.
            tenantSettings: _tenantSettingsResolver,
            // Inspection finding I2-03: the prompt route spends spoken claims against this record.
            spokenClaims: SpokenClaims,
            // Issue #2022: the live process diagnostics the About page shows read-only on both surfaces,
            // after the machine settings left the Cockpit Settings page.
            gatewayStartedAtUtc: StartedAtUtc,
            // Issue #2161: a delegate - Map runs before the listener binds.
            gatewayPort: () => Port,
            // Not-ready until the database is open - see the /healthz handler.
            databaseReady: () => _gatewayDb.IsOpen,
            // Per-subsystem readiness on /healthz, so a deploy can tell "the process is up" apart from
            // "the pages work". Statistics is the one subsystem that is designed to fail on its own without
            // stopping the Gateway, so it is the one that can be silently down after a green deploy - which
            // is exactly what happened on 2 September 2026. Asked on every request, never cached: the store
            // can come up late and can now come BACK on its own. Status words only; the reason names the
            // database host and this endpoint is public. See HealthDto.Subsystems.
            subsystems: () => new Dictionary<string, string>
            {
                ["statistics"] = InputStatsHandle.IsAvailable ? "available" : "unavailable",
            },
            knownRepositories: _knownRepositories,
            // Store injection points: hand the phone-recorder ingest (RecordingEndpoints) the host's single
            // key vault + transcription history + audio archive, so it stops newing its own copies.
            recordingKeyVault: _keyVault,
            transcriptionHistory: _transcriptionHistory,
            transcriptionAudioArchive: _transcriptionAudioArchive,
            // devthrottle #2075: the dictionary-suggestions engine + dismissal store, so the
            // /ingest/dictionary/suggestions routes serve per tenant.
            dictionarySuggestions: _dictionarySuggestions,
            dictionaryDismissals: _dictionaryDismissals,
            // The daily-email block route for the caller's tenant.
            suggestionEmailComposer: _suggestionEmailComposer,
            // Issue devthrottle_internal#1195: the wingman brain judges the menu guard's refusals - the
            // same translator (and verdict cache) the narration path uses, so an unchanged screen is
            // answered from the cached per-turn verdict without a second model call.
            wingmanTranslator: _voiceService?.Translator,
            // Remove-the-network-port mission, phase 2: the fleet-message steward for POST
            // /sessions/{sid}/message. Its own instance, on its own options, because it keeps per-sender
            // counters and windows: sharing one with a Director in the same process would let two paths spend
            // each other's budget, and on hosted there is no Director in the process to share with anyway.
            messageSteward: new Core.Fleet.MessageSteward(new Core.Configuration.MessageStewardOptions()),
            requestShutdown: () =>
            {
                var handler = OnShutdownRequested;
                if (handler is null) return false;
                handler();
                return true;
            },
            // Production-readiness B2: the DELETE /directors/{id} force-kill seam. Read at REQUEST time (like
            // requestShutdown above) so a test can inject its recorder after StartAsync; in production the
            // property is null and this performs the real Process.GetProcessById(pid).Kill(entireProcessTree).
            forceKillDirectorTree: pid =>
            {
                var seam = OnForceKillDirector;
                if (seam is not null) return seam(pid);
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                return true;
            },
            // Issue #186: doorbell pings and heartbeat snapshots feed the turn tracker;
            // the aggregated /sessions view carries the Gateway-owned assessedState.
            onSessionState: (directorId, sessionId, newState) =>
            {
                // Hosted Multi-Tenancy voice-serving (MTR-10 Gap C): resolve the OWNING tenant ONCE, HERE,
                // BEFORE the transition decision, and thread it through both the broad stale-cache clear and the
                // watcher. Null = deny (skip everything): a director with no claiming tenant must never fall back
                // to Local, which on hosted would clear/refresh another partition. Self-host resolves to Local.
                if (ResolveOwningTenant(directorId) is not { } owningTenant)
                {
                    FileLog.Write($"[GatewayHost] session-state: no owning tenant for director {directorId} sid={sessionId} - skipped");
                    return;
                }
                // Any observed Working state means a new turn is in progress, so the cached voice/text
                // summary is stale - clear it (broad net for turns started outside the voice app, e.g.
                // the desktop cockpit). The voice-turn endpoint also clears deterministically on send.
                if (string.Equals(newState, "Working", StringComparison.OrdinalIgnoreCase))
                    _voiceService?.OnSessionWorking(owningTenant, sessionId);
                if (_turnEndWatcher is null) return;
                // Gateway Cleanup mission, Phase 2: the doorbell/heartbeat already carries the owning
                // directorId, so feed THAT to the watcher (the voice-refresh path reaches the Director
                // through the tunnel by id) instead of converting it to a dialable control URL. MTR-10 Gap C:
                // the owning tenant resolved above scopes the watcher's transition memory.
                _turnEndWatcher.Observe(owningTenant, sessionId, newState, directorId);

                // Governance capture (issue #1771, spine item 2): record this session's state transition on
                // the append-only ledger (emits only on a real change; isolated so a ledger hiccup never
                // breaks the turn tracking above).
                try
                {
                    _sessionStateEmitter.Observe(owningTenant, sessionId, newState);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayHost] session-state event emit FAILED: sid={sessionId}: {ex.Message}");
                }
            },
            // Issue #549: the assessed-state refutation (issue #186) is dropped with the pipeline
            // (Option A) - "needs you" reverts to the Director's raw mechanical signal. The
            // turn-brief stamping (issue #187 briefStampFor) is gone too; the brief agent that
            // wrote those fields no longer exists.
            // Voice mode (issue #531): while the gateway's wingman is producing a session's spoken
            // summary, paint it yellow ("not ready yet") and back to red. Independent of any brief
            // agent and never via the Director's --print explain.
            voiceGeneratingFor: (tenant, sid) => _voiceService?.IsGenerating(tenant, sid) == true,
            // Issue #553: whether the gateway has fetchable, playable cached audio for this session -
            // the single truthful "voice you can play right now" signal. Holds a voice-mode waiting
            // session yellow until this is true, then lets it go red (SessionOrdering.IsVoicePreparing).
            voiceAudioReadyFor: (tenant, sid) => _voiceService?.HasVoice(tenant, sid) == true,
            // Issue #939: when turn-end voice could not be kept because hosted AI is unavailable (out
            // of credits / cap / no key), stamp the shared unavailable state onto the session so the UI
            // shows the consistent add-credit / add-key message instead of a silently missing triangle.
            voiceUnavailableFor: (tenant, sid) => _voiceService?.VoiceUnavailableFor(tenant, sid),
            // The last turn has no text reply to read aloud (waiting on a prompt / menu). Feeds the folded
            // VoiceDisplay so the screen shows an honest "nothing to read aloud" instead of a Generate
            // button that cannot work - the client no longer rules on this.
            nothingToNarrateFor: (tenant, sid) => _voiceService?.NothingToNarrateFor(tenant, sid) == true,
            // The owning computer is connected but cannot send its conversation, so no narration will ever
            // be made for this session until it is updated. Feeds the folded VoiceDisplay so the screen says
            // which computer to update instead of counting a wait that would not end (2026-09-02).
            directorCannotSendConversationFor: (tenant, sid) => _voiceService?.DirectorCannotSendConversationFor(tenant, sid) == true,
            // The model leg did not answer and the voice path's bounded re-attempts for this turn are spent,
            // so nothing further is scheduled. Feeds the folded VoiceDisplay so the screen reports a turn that
            // was not narrated instead of an arrival nobody is working on (issue #2676).
            narrationAbandonedFor: (tenant, sid) => _voiceService?.NarrationAbandonedFor(tenant, sid) == true,
            // TTS fallback: this session's ready clip was made by the backup voice provider (the primary
            // was overloaded and the cloud proxy failed over). Feeds the folded VoiceDisplay so the screen
            // shows the generic backup-voice notice. A success-with-a-note, never an outage state.
            servedViaFallbackFor: (tenant, sid) => _voiceService?.ServedViaFallbackFor(tenant, sid) == true,
            // Issue #2576: the wait-for-voice clock, so a stuck session can finally say HOW LONG. Separate
            // from the needs-you clock below because that one only runs on red and this state is yellow.
            voiceWaitingStampFor: (tenant, sid, waiting) => _voiceWaitingClock.Stamp(tenant, sid, waiting),
            // Issue #218: stamp the Gateway-owned NeedsYouSince entry clock onto each session. MTR-10 Gap C:
            // the clock is partitioned per tenant, so the roster's request tenant (threaded into the fold)
            // scopes each stamp - a session id shared across accounts keeps a per-tenant "waiting since".
            needsYouStampFor: (tenant, sid, isRed) => _needsYouClock.Stamp(tenant, sid, isRed),
            // Stamp the orange "Transcribing..." flag while a dictated utterance is being uploaded
            // and transcribed in the background for this session (mobile Speak -> Send).
            transcribingFor: (tenant, sid) => _transcribingSessions.IsTranscribing(tenant, sid),
            // Issue #1181, Task 4: the honest phase label. "Transcribing" while the server is actively
            // turning the uploaded audio into text (a bounded run); "Uploading from phone" while the durable
            // PENDING marker stands AND the phone is still making progress; null when no dictation is
            // inbound.
            //
            // ONE FLAG WAS ANSWERING TWO QUESTIONS, and that was defect 19 (fixed 14 July 2026, mission
            // "Session State Truth"). The durable PENDING marker answers "is there an undelivered dictation
            // for this session?" - a durable fact that must NEVER expire, or a phone out of signal loses its
            // words. It was ALSO being used to answer "should this session be painted orange right now?" - a
            // presentation question that must ALWAYS be bounded. So an upload that stopped progressing left
            // the session orange indefinitely, reading "Uploading from phone" about an upload that was not
            // happening.
            //
            // The colour is now bounded by the SAME idle rule the transcribing mark already uses
            // (TranscribingSessions.IdleTimeout): the phone refreshes the mark on every stored chunk and
            // every completion attempt, so a genuinely slow upload keeps its label and is never cut short,
            // while one that goes quiet drops back to the session's true colour within the idle window.
            //
            // THAT "REFRESHES ON EVERY CHUNK" IS TRUE BY INSPECTION AND UNGUARDED BY ANY TEST. It is
            // GatewayDictationEndpoint.cs: Begin on register, Refresh in the chunk route, Refresh on the
            // completion attempt. The producer tests below drive TranscribingSessions directly, so they
            // prove the RULE reads the mark - they do not prove the ROUTES still write it. Delete the
            // Refresh in the chunk route and every test in this repository stays green while a slow real
            // upload stops painting mid-flight. It is the milder cousin of defect 19 (a label that goes
            // quiet too early, rather than one that never shuts up) and it loses no words, but it is
            // written here as a known gap rather than left to read as covered: proving it needs an
            // endpoint-level test that drives the real chunk route, and there is no host harness for these
            // routes yet. Raised by review of pull request 1588; accepted deliberately, not overlooked.
            //
            // The user's AUDIO is retained either way and still delivers whenever the phone returns - the
            // delivery submits text, which makes the agent work, which is blue. Note the precise claim:
            // the CHUNKS are kept and the record is never discarded or expired. The record itself is NOT
            // untouched - a retryable or out-of-credits transcription now parks it Pending -> Failed, and
            // Failed is a resting state that keeps the audio and re-drives on the next register/complete,
            // not a terminal one. Nothing is lost except the lie on the dot.
            //
            // The earlier comment here claimed this "never wedges because the marker clears only on
            // delivery/abandon". That was half-true and the half it left out WAS the bug: the marker does
            // clear on delivery, so the normal path never wedges - but the paths that reach no terminal state
            // at all never clear it. Observed: upload f13cb4b6d9d0 stood PENDING 1h30m on 12 July 2026,
            // orange the whole time, across four Gateway restarts (so "it clears on restart" is false too -
            // the record is on disk), before finally delivering 362 characters.
            // The rule itself lives in DictationPhase.For so it is testable without a running Gateway, and
            // the WIRING that supplies its three facts lives in DictationStatusFor so it is testable too -
            // see that method for why an inline lambda here was a hole rather than a style choice.
            // Read the CALLER'S tenant partition (issue #1884, Gap B/finding 2): IsSessionLocked enumerates
            // the partition root, so a fresh hosted tenant's PENDING dictation is visible in its own durable
            // "Uploading from phone" status instead of being masked by the Local/base handle. Self-host resolves
            // Local, so ForTenant(Local) is the base root and this is byte-identical to before.
            dictationStatusFor: (tenant, sid) => DictationStatusFor(tenant, sid, _transcribingSessions, _dictationUploads.ForTenant(tenant)),
            // The mobile Speak flow marks/clears this via POST /sessions/{sid}/transcribing.
            transcribingSessions: _transcribingSessions,
            // The Gateway turn-brief store was removed: issue #549 retired the writer, so the store only ever
            // served untenanted legacy data and is superseded by Wingman voice. The Interrupted-list rail-line
            // enrichment and the restore continuation-history now carry no brief context (both already returned
            // nothing on hosted); the restore endpoint still works with less context.
            interruptedBriefFor: _ => (null, null),
            briefHistoryFor: _ => new List<TurnBriefDto>(),
            // Issue #288: record session->Director ownership as the fleet is aggregated, so the WS
            // proxy can return 503 (owner offline) rather than 404 for a session whose Director went dark.
            owners: SessionOwners,
            // Issue #330: doorbell event-vocabulary pings land in the per-director event ring.
            directorEvents: DirectorEvents,
            // Issue #376: async voice-turn submit/poll rides the host-owned job store.
            turnJobs: TurnJobs,
            // Issue #1045: pass the per-device-key registry so the voice-turn routes' own token
            // check (issue #369) accepts a phone's enrolled device key, not just the shared token.
            devices: Devices,
            // Issue #1176 (Phase 1a): serve /sessions from the Director-push cache when the stream is
            // fresh; null when stream mode is off, keeping /sessions byte-identical to today.
            pushedSessions: PushedSessions,
            // Repositories mission (#510 phase C): serve GET /repositories and /worktrees from the
            // pushed repository snapshots, tenant-scoped like everything else.
            pushedRepositories: PushedRepositories,
            // Repositories mission (#510 phase D): the weekly-report read.
            repoHistory: RepoHistory,
            streamStaleAfter: _streamStaleAfter,
            // Issue #1177 (Phase 1): route per-session commands DOWN the Director's stream when stream mode
            // is on. Null when off, so every command endpoint stays on its HTTP path (byte-identical).
            sendCommand: SendCommandAsync,
            // Issue #1215 (Cockpit plan phase 6): the last-known-good roster cache absorbs a transient poll
            // failure as Wobbly (served stale through a short grace window) instead of blinking the
            // Director's sessions out of the roster; only a sustained failure reads as Offline.
            // Issue #1292: the fleet-wide session-number authority backs POST /session-numbers/allocate
            // (Directors ask here at session creation) and the /sessions adopt-reconcile.
            sessionNumbers: SessionNumbers,
            // DevThrottle Stats: feed the input-tally aggregator from the assembled /sessions roster, so
            // "Your Throttle" is populated whether stream mode is on or off (the DirectorHub push fold only
            // runs in stream mode, which is off in production).
            // Passed as RESOLVERS, not as instances. Reading these two properties HERE would evaluate them
            // once, while mapping, and hand the roster whatever the answer was at that instant - which on
            // hosted is "nothing" whenever the statistics store is still opening. The properties are cheap
            // and re-ask the resolver, so the roster sees a late-published store on the very next request.
            inputStats: () => InputStats,
            // DevThrottle Stats: record fleet concurrency (live + actively-working session counts) from the
            // same assembled roster, so the peak is captured fleet-wide regardless of stream mode.
            concurrency: () => SessionConcurrency,
            // Snooze Length mission: the Gateway-owned snooze registry. POST /sessions/{sid}/hold records
            // (or clears) a snooze-until here, and the /sessions fold overlays an EXPIRED snooze back into
            // "needs you" so the session returns on its own even after its Director dies.
            handRaises: _handRaises,
            snoozeRegistry: _snoozeRegistry,
            // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store backs POST/GET /missions and
            // mission-scoped spawn validation. Missions are a fleet concept, so their source of truth is here.
            missions: Missions,
            // Round 4 finding 1: the hold endpoint triggers a prompt display-state push through this ONE
            // channel after a snooze / unsnooze, instead of sending its own second hold command - the single
            // writer of the Director's raw hold.
            fleetDisplayState: FleetDisplayState,
            // Workflows mission (phase 4, issue #1771): creating a mission also opens a workflow run of
            // the built-in "mission" workflow, pinned to its published version - the outcome spine.
            workflowRuns: _workflowRuns);

        // Issue #268: the two raw per-session WebSocket legs (live Terminal stream + dictation)
        // proxied through the Gateway so a remote Cockpit talks same-origin to the Gateway and
        // never needs a Director's own (possibly loopback) address. Mapped endpoints win over the
        // fallback Cockpit proxy below.
        // Pass the fleet token (issue #457): the proxy injects it as the Bearer on every forward
        // so an auth-enabled Director (LAN mode) accepts the call. Harmless for auth-off Directors.
        // Gateway Cleanup mission (the cut): the browser-facing per-session legs (terminal, file, screenshot
        // bytes/list/delete) and the director-level backfill all ride the tunnel - no HTTP dial to a Director
        // remains. StreamRegistry is the SAME singleton the DirectorHub pumps StreamUp frames into.
        SessionWsProxyEndpoints.Map(_app,
            pushedSessions: PushedSessions,
            streamRegistry: StreamRegistry,
            sendCommand: SendCommandAsync,
            // MTR-01 (Codex round 1): the director-scoped backfill leg resolves the owned Director in the
            // request's tenant before dispatch, so it needs the registry.
            registry: Registry,
            streamStaleAfter: _streamStaleAfter,
            // Hosted Multi-Tenancy (session-serving PR1): the per-session tunnel legs resolve the request's
            // tenant at the session locate, so a wrong-tenant session is never located and a request with no
            // bound tenant is denied (403) - never a Local read on hosted.
            tenantBoundary: _tenantBoundary);

        // GET /devices: the host-readable device registry listing. Mapped after the WS proxy so its
        // literal route wins over the catch-all session forwarder, same as the other literal routes.
        // MTR-12: the listing is tenant-scoped to the caller (403 on hosted with no bound tenant), so an
        // authenticated account never reads back another tenant's devices - it resolves the request's tenant
        // from the same authenticated device key the other read routes use.
        Api.DeviceEnrollmentEndpoint.Map(_app, Devices, _tenantBoundary);

        // POST /devices/enroll-signed-in (issue #1069): the sign-in replacement for the pairing code -
        // a co-located Director mints its own per-device key by having the Gateway signed in to
        // DevThrottle, gated on a loopback caller. Same-machine only; remote via tailnet is a follow-up.
        Api.SignedInEnrollmentEndpoint.Map(_app, Devices, SignIn, _childMirror);

        // The hosted-mint dependencies, built ONCE for every hosted entry point that mints a tenant-scoped
        // device key: the hosted Director enrollment below and the hosted /mobile/enroll branch both pass this same
        // bundle into the ONE mint (HostedEnrollmentEndpoint.Enroll). Non-null only on a hosted Gateway; null
        // keeps every entry point on its self-host path. Building the account-token validator here once means
        // both share the identical signature/audience/issuer configuration - there is no second place that
        // validates an account token.
        Api.HostedEnrollDependencies? hostedEnrollDeps = GatewayHostedMode.IsHosted
            ? new Api.HostedEnrollDependencies(
                Devices, TenantRegistry,
                CcDirector.Gateway.Account.GatewayAccountFactory.BuildAuthorizationValidator(),
                EntitlementRegistry, TrialRegistry)
            : null;

        // POST /devices/enroll-hosted (Hosted Multi-Tenancy increment 1): the HOSTED counterpart - a REMOTE
        // Director enrolls by presenting its OWN verified Supabase account token; the Gateway validates it,
        // resolves the account's tenant, and binds the minted device key to it. Only mapped on the hosted
        // Gateway (self-host uses the loopback signed-in route above and stays single-tenant Local).
        if (hostedEnrollDeps is not null)
        {
            // The paid gate rides along here and ONLY here. This route is mapped on hosted only, so passing
            // the entitlement registry means the gate is active wherever enrollment is possible - self-host
            // never maps this route at all and therefore cannot be gated by accident.
            Api.HostedEnrollmentEndpoint.Map(_app, hostedEnrollDeps.Devices, hostedEnrollDeps.Tenants,
                hostedEnrollDeps.AccountTokenValidator, entitlements: hostedEnrollDeps.Entitlements,
                trials: hostedEnrollDeps.Trials);
        }

        // Wingman-voice surface for the Cockpit's Voice tab (issue #531): drive one turn of a
        // session and have the persistent wingman brain translate the reply into speakable form,
        // plus the direct-to-wingman path. Backed by the same warm Brain the brief agent uses.
        // sessionTitleResolver: the wingman opens every narration with the session's title, so a
        // listener with the phone in a pocket knows WHICH session is talking before anything else
        // (WingmanTranslator.FidelityPrompt v5.2). Push-store read - no dial. See ResolveSessionTitle.
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, _tenantSettingsResolver,
            instructionsProvider: () => _instructionsStore.ActiveContent,
            sessionTitleResolver: ResolveSessionTitle,
            // The narration's words now come from the Gateway's own store (turn-push mission, phase 3), not
            // from a tunnel command asking the Director to re-read the user's transcript. See
            // ReadStoredConversation for why the tenant scope is entered there rather than inside the service.
            conversationReader: ReadStoredConversation,
            directorCannotSendConversation: DirectorCannotSendConversation);
        // The session's conversation, served from THIS Gateway's store (turn-push mission, phase 2). A
        // literal route, so it outranks the /sessions/{sid}/{**rest} catch-all - whose "history" verb entry
        // is removed in the same change, because the whole point is that reading a conversation no longer
        // travels down the tunnel to re-parse a transcript on the user's disk once every 2.5 seconds.
        Api.SessionConversationEndpoint.Map(_app, _sessionTurns, PushedSessions, _turnPushCapabilities,
            _streamStaleAfter, _tenantBoundary);

        GatewayWingmanVoiceEndpoint.Map(_app, Registry, WingmanBrainAsync, _keyVault, _voiceService, _tenantSettingsResolver,
            // The manual "generate the narration now" route reads the stored conversation too, through the
            // same reader the automatic path uses - one source for both, which is the point of the mission.
            conversationReader: ReadStoredConversation,
            pushedSessions: PushedSessions,
            sendCommand: SendCommandAsync,
            owners: SessionOwners,
            instructionsProvider: () => _instructionsStore.ActiveContent,
            // Store injection points: hand the endpoint the host's single voice-turn upload store and the
            // host's transcription history + audio archive, so it stops newing its own copies.
            uploadStore: _voiceTurnUploads,
            // Inspection finding I2-03: the utterance completion route records each transcription here.
            spokenClaims: SpokenClaims,
            // The same transcription service the dictation endpoint uses, when the host was handed one (tests
            // fake its provider); null builds the production service as before.
            transcriptionService: _dictationTranscription,
            history: _transcriptionHistory,
            audioArchive: _transcriptionAudioArchive,
            tenantBoundary: _tenantBoundary,
            transcripts: _transcripts);

        // The fleet brain: the tool-calling loop behind POST /assistant/turn. The chat transport resolves the
        // fast wingman model + the vault key at CALL
        // time (a settings change applies on the next turn, no restart); the fleet tools reach THIS
        // Gateway's own endpoints over loopback (the same aggregated roster every client sees); the
        // conversation context is kept server-side per device. Inherits the host-wide auth gate (the
        // caller's per-device key), like every other data route.
        var carModeChat = new CarMode.HostedCarModeChat(CarMode.HostedCarModeChat.DefaultResolver(_keyVault.Get, _tenantSettingsResolver));
        // The fleet view is created PER TURN, as the CALLING DEVICE (issue #2129): the loopback calls
        // authenticate with the caller's own credential, so on hosted every read and act resolves to the
        // caller's tenant exactly as it would for any client - the machine token (which hosted rejects,
        // and which carries no tenant) never authenticates a tenant's fleet read. The empty-credential arm
        // exists ONLY for self-host with the auth gate off (single-tenant Local): there is no caller
        // credential on the request at all, and the machine token is the same identity every client uses.
        Func<string, CarMode.ICarModeFleet> carModeFleetForCaller = callerCredential =>
            new CarMode.LoopbackCarModeFleet(Port, string.IsNullOrEmpty(callerCredential) ? Token : callerCredential);
        // The Assistant (POST /assistant/turn) is the ONE surface on this brain now: Car Mode was removed from
        // the product (#1028) and its own brain instance and turn door went with it. The loop, the tools, the
        // per-device stores and the turn cache were always shared and are untouched.
        var assistantBrain = new CarMode.CarModeBrain(carModeChat, carModeFleetForCaller, _carModeConversations, _carModePending, _carModeSubjects, _tenantSettingsResolver.SpokenLanguage);
        // Keep-warm (Car Mode performance round): warm the SAME hosted model the brain uses and the SAME
        // text-to-speech target /wingman/tts uses, resolved fresh each warmup so a settings change applies.
        var carModeWarmup = new CarMode.CarModeWarmup(
            CarMode.HostedCarModeChat.DefaultResolver(_keyVault.Get, _tenantSettingsResolver),
            tenant =>
            {
                var mode = Core.Configuration.TranscriptionModeConfig.Get();
                var tts = Core.Configuration.TranscriptionEndpointResolver.ResolveTts(mode);
                var key = _keyVault.Get(tts.KeyName) ?? "";
                return (tts.BaseUrl, _tenantSettingsResolver.TtsVoice(tenant, mode), _tenantSettingsResolver.TtsModel(tenant, mode), key);
            });
        Api.FleetBrainEndpoint.Map(_app, assistantBrain, _carModeTurnCache, carModeWarmup, _tenantBoundary);

        // The browser error channel (client error logging build): every error a browser app shows the
        // user is also reported here and lands in the Gateway log, tenant-partitioned, with a queryable
        // recent ring - no on-screen error exists only on the user's screen.
        Api.ClientErrorEndpoints.Map(_app, _tenantBoundary);
        // Editable/versioned wingman instructions settings surface (issue #537), incl. A/B test
        // over saved training sessions (reads the shared training store; uses the hosted wingman brain).
        WingmanInstructionsEndpoint.Map(_app, _instructionsStore, WingmanBrainAsync);
        // The gateway OWNS keeping voice sessions' summaries pre-built (issue #531): a gentle
        // background sweep regenerates voice for any idle voice session that is missing it, so the
        // list shows it ready BEFORE you enter - including after a gateway restart (the voice-session
        // set is persisted). Turn-end regeneration + the deterministic voice-turn path also feed it.
        _voiceSweepTimer = new System.Threading.Timer(_ => { _ = SweepVoiceSessionsAsync(); }, null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));

        // Durable, server-owned dictation upload (issue #1006): the phone streams recorded audio here
        // in resumable chunks and the Gateway assembles → transcribes → injects the turn into the
        // owning session itself, so a refresh / dropped connection cannot lose a recorded utterance.
        GatewayDictationEndpoint.Map(_app, Registry, SessionOwners, Token,
            _dictationTranscription ?? new Transcription.GatewayTranscriptionService(_keyVault, history: _transcriptionHistory, audioArchive: _transcriptionAudioArchive, transcripts: _transcripts), _transcribingSessions, new Api.DictationTenantGate(_dictationUploads, _tenantBoundary), Devices,
            pushedSessions: PushedSessions,
            sendCommand: SendCommandAsync);
        // Durable per-upload-id dictation record (issue #1183): a PENDING upload's chunks are retained
        // until it becomes DELIVERED or ABANDONED, and the delivered/abandoned tombstone (the durable
        // de-dupe marker) is retained until the client acknowledges it - so an undelivered dictation
        // survives time and a restart, and a delivered upload id is de-duplicated forever. There is
        // deliberately NO age sweep here: a fixed age cut would reopen exactly the hole this closes - a
        // phone out of signal for longer than the cut would lose its already-uploaded chunks, or re-inject
        // an already-delivered turn. The record is retired only by the client ack
        // (POST /dictation/{uploadId}/ack). The unrelated voice-turn upload staging is transient and IS age
        // swept - by the timer started in StartAsync (see the voice-turn upload sweep there). That sweep was
        // written long before it was ever started: for its whole life this comment said the voice-turn
        // staging "keeps its own transient SweepAbandoned" while nothing in production called the method, so
        // a reader checking whether the staging was bounded found a confident answer here and stopped
        // looking. It is bounded now because a timer runs it, not because a method exists.

        // Central key vault (docs/architecture/gateway/GATEWAY_KEY_VAULT.md): set keys once
        // here (via the Cockpit Keys page); Directors pull them on demand. Inherits the
        // host-wide token middleware above.
        VaultEndpoints.Map(_app, _keyVault);

        // The AI model catalog + test surface for the Settings AI tab (list the selected provider's
        // models, test a chat model, save the chosen wingman/speech model). Uses the vault credential.
        Api.AiModelsEndpoint.Map(_app, _keyVault, _tenantSettingsResolver, _tenantBoundary);

        // The workflow catalog (issue #1617; persisted by the Workflows mission): the shapes of work
        // the fleet knows how to run - Mission, Standalone, Standalone with review, plus user-defined
        // workflows. The Gateway is the home for these; Directors and the Cockpit ask it rather than
        // each carrying a private copy. Served from the persisted store (built-ins seeded at startup);
        // authoring routes are the next phase. Inherits the host-wide token middleware above.
        Api.WorkflowEndpoints.Map(_app, _workflows);

        // Workspaces (issue #2722, Phase 3 of #2719): the stored object a restart index and a workspace
        // both are. The capture verb folds a Director's live sessions here rather than letting a caller
        // assemble the facts, because this document is read after those sessions are gone.
        Api.WorkspaceEndpoints.Map(
            _app,
            _workspaces,
            AmbientSnapshotConnected,
            directorId => _tenantPass.Current is { } tenant ? Registry.Get(tenant, directorId) : null);
        Api.SkillEndpoints.Map(_app, _skills);

        // The standing instructions an account gives about its sessions, and the record of every firing
        // (Session Rules mission). A device-authed client route under the "/gateway/..." prefix, gated by
        // the host-wide token middleware like every other client data endpoint.
        Api.SessionRuleEndpoints.Map(_app, _sessionRules);

        // Workflow runs (phase 4, issue #1771): the outcome spine's REST surface. One row per
        // execution of a workflow, pinned to the exact published version that governed it.
        Api.WorkflowRunEndpoints.Map(_app, _workflowRuns);

        // The governance event ledger (issue #1771, spine item 2): append-only session/run state
        // transitions - the duration timeline the weekly Outcome Ledger reads. Append and read only.
        Api.GovernanceEventEndpoints.Map(_app, _governanceEvents);

        // Honest driver-normalized spend (issue #1771, spine item 3): per-session token effort + billing-mode
        // label, and the account-level hosted-AI service dollars read from the mirrored credit-debit ledger.
        Api.GovernanceSpendEndpoints.Map(_app, _sessionSpend, _hostedAiSpend);

        // The governance audit trail (issue #1771, spine item 4): append-only intervention +
        // permission/sandbox decisions - the safety and attention-burden audit. Append and read only.
        Api.GovernanceAuditEndpoints.Map(_app, _governanceAudit);

        // The weekly Outcome Ledger (issue #1771, spine item 4): the first report that pays rent - verified
        // yield, aging WIP, and high-effort/no-outcome runs with cost + attention-burden. Read-only.
        Api.GovernanceReportEndpoints.Map(_app, _outcomeLedger);

        // The morning report (issue #2119, slice 2 of #2096): GET /gateway/reports/morning - the JSON the
        // website's 07:00 cron renders into the daily email. Does NOT inherit the host-wide token middleware
        // (the caller is a server with no device key); it carries its own bearer service token from
        // REPORT_SERVICE_TOKEN and resolves the named account to exactly one tenant. Read-only.
        Api.MorningReportEndpoint.Map(_app, _morningReport, TenantRegistry, _tenantBoundary);
        // Who that report goes to: every account on this Gateway that has an address and has not turned the
        // report off (issue #1000), behind the same service token.
        Api.ReportRecipientsEndpoint.Map(_app, TenantRegistry, _tenantSettingsResolver);

        // Gateway Centralization Phase 2 (issue #638): GET /account/status answers "is the Gateway
        // signed in to DevThrottle, and as whom?" computed ENTIRELY LOCALLY from the Gateway-hosted
        // credential service (issue #636, the reused DevThrottleAccountService exposed as Account) -
        // no cloud call. A Director's future startup gate (a separate issue) reads this. The response
        // carries only the boolean + identity, never the access/refresh token (security rule DT-05).
        // Inherits the host-wide token middleware above (the existing gateway.token convention). On a
        // host with no credential service (a non-Windows host, Account null) it truthfully reports
        // not-signed-in. Issue #1357: it also resolves the signed-in user's chosen nickname (cached,
        // best-effort) through the cloud nickname client, so a session's preamble can name the human;
        // the identity email/provider path stays entirely local.
        // Issue #1856: the boundary and the tenant registry make this endpoint tenant-bearing on hosted, where
        // it must answer about the CALLER's enrollment rather than about a Gateway credential hosted does not
        // hold. On self-host the boundary reports not-hosted and the endpoint behaves exactly as before.
        AccountStatusEndpoint.Map(_app, Account, _tenantBoundary,
            nickname: new Core.Account.AccountNicknameClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
            tenants: TenantRegistry);

        // devthrottle_internal #1508: GET /account/mobile-qr.png renders the scannable code for the mobile
        // app's address on THIS Gateway, which is what the Cockpit's Phone panel shows. It carries only a
        // plain address, never a credential, and it refuses rather than encoding a loopback address no
        // phone could reach. Inherits the host-wide token middleware exactly like /account/status.
        MobileQrEndpoint.Map(_app);

        // devthrottle_internal #1509: POST/DELETE /account/device-cookie move the HttpOnly cc-gateway-token
        // cookie onto the calling account, or clear it. A browser can do NEITHER from JavaScript, which is
        // why the multi-account switch and the new sign-out both needed a server route: without it the
        // cookie channel silently stayed on the previous account, so the WebSockets and bare image/iframe
        // loads that authenticate by cookie kept running as an account the person had left or signed out
        // of. The cookie is set to the credential the gate already accepted, so this grants nothing new.
        DeviceCookieEndpoint.Map(_app);

        // Gateway Centralization Phase 3 (issue #648): POST /account/logout CLEARS the Gateway-hosted
        // DevThrottle credential through the same reused DevThrottleAccountService (Account). The account
        // lives on the gateway, so the logout action lives here too: the Cockpit account surface calls
        // it, and afterward GET /account/status reports signedIn=false and the gateway returns to its
        // sign-in prompt. The clear is entirely local (no cloud call) and the response carries only the
        // post-logout boolean, never the access/refresh token (security rule DT-05). Inherits the
        // host-wide token middleware above. On a host with no credential service (Account null) there is
        // nothing to clear and it reports not-signed-in.
        // Issue #881: revoke the auto-minted inference key on sign-out (before the credential is cleared),
        // so a signed-out install leaves no live key behind. A manually-pasted key has no recorded id and
        // is left untouched. Best-effort - never blocks logout.
        // Issue #984: on hosted there is no Gateway credential to clear, so the tenant boundary is passed in
        // and the route refuses truthfully instead of reporting a sign-out that never happened.
        AccountLogoutEndpoint.Map(_app, Account,
            onBeforeLogout: _transcriptionKeyProvisioner is null ? null : ct => _transcriptionKeyProvisioner.RevokeMintedKeyAsync(ct),
            tenantBoundary: _tenantBoundary, tenants: TenantRegistry);

        // Account device list + revoke proxy (issue #854): GET /account/devices and
        // DELETE /account/devices/{id}. The Cockpit Account page needs the account-wide device list with
        // last-seen and a per-device revoke, but the Cockpit must never hold the account token or call the
        // cloud directly - the token lives here on the Gateway. So the Gateway proxies: it reads its own
        // stored account token (the SAME GetAccessTokenForForwarding credential it uses to forward
        // authenticated account operations) and calls the cloud device registry through DeviceRegistryClient,
        // returning a local token-free DTO (security rule DT-05). Signed-out yields an explicit
        // signedIn:false envelope (never a fabricated empty list) and an unreachable cloud yields a clear
        // 502 (logged). The injectable HttpClient is the test seam. This is distinct from the LOCAL pairing
        // registry GET /devices (issue #469), which is left unchanged. Inherits the host-wide token
        // middleware above, exactly like the other /account routes.
        var accountDevicesClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        AccountDevicesEndpoint.Map(_app, Account, new Core.Account.DeviceRegistryClient(accountDevicesClient), Environment.MachineName,
            Devices, _tenantBoundary);

        // Account credit-balance proxy (issue #884): GET /account/credits. Same proxy shape as the device
        // list - the Gateway reads the balance from the cloud with its own stored account token (JWT) and
        // returns a token-free DTO, so the Settings account section shows the balance without the Cockpit
        // ever holding the token. Genuinely signed out -> explicit signedIn:false; unreachable cloud -> clear
        // 502. Issue #984: this is a BILLING surface and it was telling hosted customers they were not signed
        // in, because it reported "this Gateway holds no credential" as a fact about the caller. The tenant
        // boundary is passed in so the hosted path answers about the CALLER: signed in, balance unreadable
        // here, account and balance unaffected.
        AccountCreditsEndpoint.Map(_app, Account, new Core.Account.AccountCreditsClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
            tenantBoundary: _tenantBoundary, tenants: TenantRegistry);

        // The free Pro trial read (issue #1243): GET /account/trial. NOT a proxy - unlike the two routes above
        // there is no cloud call and no account token, because the trial ledger is this Gateway's own
        // account_trials table. The trial was already being granted at enrolment and stored here; nothing
        // could ask about it, so no screen ever said a trial was running. This is that read path. Every
        // answer, including the denials, carries a three-way state so no surface has to decide for itself
        // what a missing answer means. Inherits the host-wide token middleware like the other /account routes.
        AccountTrialEndpoint.Map(_app, TrialRegistry, tenantBoundary: _tenantBoundary, tenants: TenantRegistry);

        // The administrator trial EXTENSION: POST /gateway/admin/trials/extend. The write twin of the read
        // above, and the only way a trial's end date moves. It lives here rather than as a database grant to
        // the website because the row belongs to this Gateway's role: handing that role's UPDATE to another
        // system would have put the capability where the data is not, split one rule across two codebases,
        // and left the permission outside either system's migrations, where a rebuild of this schema silently
        // removes it. Does NOT inherit the host-wide token middleware - the caller is a SERVER holding no
        // device key - and carries its own bearer service token (ADMIN_SERVICE_TOKEN), deliberately a
        // different secret from the read-only report token.
        AdminTrialEndpoint.Map(_app, TrialRegistry);
        // The turn-log switch. The store is created here if the turn-end wiring has not run yet, so the
        // route is never mapped against a null - a switch screen that answers 503 because the routes were
        // mapped in the wrong order would read as a broken feature rather than as an ordering mistake.
        _turnLogSwitches ??= new TurnLog.TurnLogSwitchStore(_gatewayDb);
        AdminTurnLogEndpoint.Map(_app, _turnLogSwitches, TenantRegistry, Registry);
        // The bridge between an administrator's world (emails) and the Gateway's (account ids, Directors).
        // Without it the turn-log switch above can only be addressed for the administrator's OWN fleet.
        AdminAccountLookupEndpoint.Map(_app, TenantRegistry, Registry);

        // "DevThrottle emails me" relay (issue #1318 consumer): POST /account/email. A session or scheduled
        // run passes a subject + body (+ optional attachments); the Gateway injects its own stored account
        // token and forwards to the cloud primitive (POST /api/v1/account/notify-owner, devthrottle_internal
        // #338), which resolves the recipient from the token and sends via Resend. The Gateway holds NO
        // Resend key and runs no email code - it only relays the account's own token. Single-recipient by
        // construction (no recipient field). Genuinely signed out -> 401; cloud failure -> clear 502. Inherits
        // the host-wide token middleware above like the other /account routes. Issue #984: the tenant boundary
        // is passed in so the hosted path reports the truth - the caller IS signed in and this shared Gateway
        // holds no credential of theirs to send with - instead of the old 401 "sign in from the Gateway tray".
        // devthrottle_internal #986: on hosted there is no account token to forward, so the send names the
        // caller's TENANT and the cloud resolves the recipient. Wired unconditionally - the route decides by
        // hosted state, and the client itself fails closed when NOTIFY_OWNER_SERVICE_TOKEN is unset rather
        // than calling the service unauthenticated.
        AccountEmailEndpoint.Map(_app, Account, new Core.Account.AccountNotifyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }),
            tenantBoundary: _tenantBoundary, tenants: TenantRegistry,
            byTenant: new Core.Account.AccountNotifyByTenantClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));


        // The credential-free cloud sign-in START front door (epic #1069, issue #1076): GET + POST
        // /account/sign-in-start. This pair is on the public-paths
        // allow-list (AuthMiddleware) so a SIGNED-OUT browser with no Gateway token can reach it to BEGIN
        // cloud sign-in - breaking the deadlock where cloud sign-in sat behind the raw gateway-token wall.
        // It reads/echoes no credential and returns no account data; the POST reuses the same host-local
        // browser loopback flow (SignIn) as the authenticated endpoint above (security rule DT-05). Every
        // other /account/* data endpoint stays gated (the allow-list is exact-match).
        AccountSignInStartEndpoint.Map(_app, SignIn);

        // The reachable front-door sign-in CALLBACK (epic #1069, issue #1080): GET /account/sign-in-callback.
        // This is the routable address the cloud sign-in page redirects the user's OWN browser back to after
        // sign-in - the remote-capable counterpart to the host-local loopback listener. It completes remote
        // sign-in: a person reaching the Gateway front door from ANOTHER machine over Tailscale is redirected
        // by the sign-in START to the cloud page carrying THIS callback as the redirect_uri, and the cloud
        // completion redirects the browser back here with the token pair, which the Gateway stores. Public
        // (allow-listed) because the browser completing sign-in has no Gateway credential yet; the captured
        // token never leaves the Gateway and is never logged (security rule DT-05 - the access logger below
        // redacts this path's query so the handed-back credential never reaches the gateway log). On a host
        // with no sign-in flow (SignIn null) it reports an explicit "not available" result.
        AccountSignInCallbackEndpoint.Map(_app, SignIn);

        // The single Gateway speech-to-text endpoint (issue #839): a caller POSTs raw audio and gets
        // text back. The phone Notes worker, the Settings "Test it" button, and on-device mode all go
        // through this one endpoint - it resolves the mode + key and runs the right provider (in-process
        // Whisper, or the resolved provider-compatible batch endpoint). Optional ?correct=true also runs
        // the validated dictionary correction, keeping that out of the callers too.
        TranscriptionBatchEndpoint.Map(_app, _keyVault, _tenantBoundary, _transcriptionHistory, _transcriptionAudioArchive, _transcripts);

        // Read-only analysis over the LOCAL minimized transcription history: latency percentiles, cleanup
        // behaviour, most-corrected terms, and word frequencies, so any agent can query the Gateway to
        // see how fast and how good transcription is - all from data on this machine, never a server.
        Api.TranscriptionAnalysisEndpoint.Map(_app, _tenantBoundary);

        // The Test microphone / Test transcription checks: a user records a passage we put on screen,
        // hears it back, and sees how much of it came back correctly. The clips are kept per tenant so
        // transcription quality can be compared across languages, headsets and releases - the question
        // no single run can answer.
        Api.VoiceTestEndpoint.Map(
            _app,
            _dictationTranscription ?? new Transcription.GatewayTranscriptionService(
                _keyVault, history: _transcriptionHistory, audioArchive: _transcriptionAudioArchive, transcripts: _transcripts),
            _tenantBoundary);

        // Background microphone-quality monitoring: the browser measures every dictation it sends and
        // posts a handful of numbers here, so the Cockpit can say WHICH microphone is letting the user
        // down. No audio and no transcript reach this route.
        Api.VoiceQualityEndpoint.Map(_app, _tenantBoundary);

        // Text-in / text-out cleanup: run ONLY dictionary correction over supplied
        // text + a supplied term list (no audio). The engine the multilingual eval harness drives, and
        // a way for any agent to test cleanup on arbitrary text/terms.
        Api.TranscriptionCleanupEndpoint.Map(_app, _keyVault);

        // Named work lists (issue #273, child of #270): an ordered list of structured item refs
        // { source, id, area? } + a single-consumer claim, the object the product skill writes to,
        // the Cockpit views, and the queue runner drains. Persisted to worklists.json across
        // Gateway restarts since issue #301 (write-through + reload-on-start with stale-claim
        // release). Inherits the host-wide token middleware above and is reachable cross-machine
        // like the rest of the Gateway surface.
        WorkListEndpoints.Map(_app, _workLists);

        // Per-work-item title + status for the Cockpit Lists view (issue #275, moved behind the
        // Gateway for the React rebuild, issue #970). The React Cockpit is a browser SPA that holds
        // no secret, so the GitHub resolve lives here: the browser calls GET
        // /gateway/lists/item-status?source&id same-origin and the GitHub token never leaves the
        // Gateway host. Inherits the host-wide token middleware above.
        Api.ItemStatusEndpoint.Map(_app, Api.GitHubItemStatusResolver.CreateDefault());

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
        WorkListRunnerEndpoints.Map(_app, _workLists, Registry, _runnerManager,
            SendCommandAsync, tenantBoundary: _tenantBoundary);

        // Issue #331: launcher registration + cross-machine Director lifecycle relay.
        // Launchers POST /launchers/register on startup; relay callers POST
        // /machines/{machine}/director/restart|start|stop to reach that machine's Director.
        // launcher-persistent-join: the stream-send hook is the ONLY delivery path (phase 6 of the
        // remove-the-network-port mission deleted the REST fallback along with the launcher's listener) -
        // a null from it is reported to the caller as the launcher being offline, never dialed around.
        MachineEndpoints.Map(_app, Launchers, _machineSessionSpawner,
            // Tenant boundary - REQUIRED (finding CR-7): every launcher-registry read/write and relay is
            // scoped to the calling tenant, and on hosted an unbound request is denied, never Local.
            _tenantBoundary,
            SendLauncherCommandAsync,
            // Gateway Cleanup mission (Wave 4b): validate a mission-scoped spawn against the Gateway store and
            // stamp the resolved mission name onto the create request forwarded to the Director.
            missions: Missions,
            // Workflows mission (phase 5b): seat spawns on workflow runs and record participants.
            workflowRuns: _workflowRuns);

        // The Cockpit Settings page surface (docs/architecture/gateway/SETTINGS_OWNERSHIP.md):
        // one snapshot GET plus brain-restart and autostart actions. Reads this host directly
        // for status/brain; run mode + autostart come from SettingsHooks (GatewayApp-owned).
        SettingsEndpoints.Map(_app, this);

        // The Gateway turn-brief surface (issues #185/#217) was removed. Its writer was retired in #549, so
        // it only served untenanted legacy data and is superseded by Wingman voice; the store, its endpoints,
        // and the Cockpit Feedback page are deleted. Shared DTOs/contracts (TurnBriefDto, TurnPackage, the
        // Wingman contracts) stay - the live Wingman/Director/phone paths use them.

        // Mission Screen mission (Phase 1b, issue #1405): the mission-WHY read/set surface. A device-authed
        // client route under /gateway/missions/notes - the host-wide token middleware gates it (proven by
        // MissionNotesEndpointTests). Deliberately NOT in GatewayEndpoints.cs and NOT on the bare /missions
        // prefix (the Gateway-native mission store owns that), so it stays clear of the Gateway Cleanup work.
        // The mission-WHY routes are RETIRED. The WHY now lives on the Mission record and is written through
        // PATCH /missions/{mid}; GET /missions carries it. The old routes were keyed by the mission's
        // lower-cased NAME and did no tenant resolution of their own, so they are not kept as a compatibility
        // surface - a second way to write the WHY, under a weaker boundary and a key a rename would orphan,
        // is exactly the kind of leftover that made the missions area need untangling in the first place.
        //
        // The STORE stays for now (it is what AdoptMissionNoteWhys reads), and so does its table. Migrate,
        // stop serving, verify against the real data, then drop.

        // Issue #806 (mobile foundation): the OpenAPI document the mobile codegen consumes, and the
        // mobile app static serving at /mobile (built shell + token-injected index.html). Mapped before
        // the fallback proxy so these explicit routes win over the Cockpit catch-all.
        _app.MapOpenApi();

        // Web Push (mobile app-icon "needs you" dot): the phone fetches the VAPID public key and
        // registers/removes its push subscription here. Inherits the host-wide token middleware
        // (the mobile app attaches the per-machine Bearer). A new subscription nudges the notifier
        // so the fresh device gets the current dot promptly. Mapped before the mobile shell and the
        // Cockpit catch-all so these explicit routes win.
        Api.WebPushEndpoints.Map(_app, _vapidStore.PublicKey, _pushSubscriptions,
            onSubscribed: () => _pushNotifier?.ResetDedupe());

        // Mobile device enrollment (issue #908): POST /mobile/enroll (with the legacy /m/enroll kept as a
        // back-compat alias). A phone that signed in on devthrottle.com and received its per-device key
        // hands that key here; the Gateway confirms (account-scoped, by key hash) that the key belongs to
        // its OWN signed-in account and issues the phone a LOCAL device key it validates offline - so the
        // master token is no longer injected into the mobile shell. Under /mobile/ so it is reachable
        // before the phone holds any credential; it carries its own authorization (the account-scoped
        // device key), exactly like /devices/register. Mapped before the mobile shell so the explicit POST
        // route wins over the shell's GET catch-all.
        var mobileEnrollmentClient = new Core.Account.DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        // On a HOSTED Gateway (hostedEnrollDeps non-null) /mobile/enroll takes a human's account access token
        // in the Bearer header and runs the ONE hosted mint; self-host (null) keeps the cloud-device-key-in-body path.
        Api.MobileEnrollmentEndpoint.Map(_app, new Account.MobileDeviceEnrollmentService(Account, mobileEnrollmentClient, Devices), hostedEnrollDeps);

        // DevThrottle Stats: the always-available private dashboard (/stats) and its JSON (/stats/data).
        // A self-contained embedded page, so it works even on a plain dev build with no React wwwroot.
        // Mapped before the mobile/cockpit catch-alls so the explicit routes win.
        // The work-history store rides along (devthrottle_internal issue #982) as the source of the
        // session-ORIGIN counts: how sessions came to exist, which only the durable per-session record
        // can answer - the in-memory aggregates behind the rest of this feed count turns, and a session
        // is born exactly once.
        // Mapped only when there IS an aggregator. When statistics are unavailable the two routes answer
        // 503 with the named reason instead of not existing at all: a surface that vanishes reads as a
        // broken deploy, and the reason is what tells an operator whether to fix a setting or a database.
        // MAPPED ONCE, DECIDED PER REQUEST. The routes used to be chosen here from whether an aggregator
        // existed at startup - the real feed, or two lambdas answering a permanent 503. That is a second
        // place the hosted late-open decision was frozen: a statistics store that finished opening a moment
        // after the startup deadline could report itself available while these two routes went on answering
        // 503 for the life of the process. The handle is passed in instead and asked on each request, so the
        // routes start serving the moment there is something to serve, and still answer the named 503 with
        // the store's own reason while there is not.
        // Every count of turns the feed serves comes from the submission ledger through the one definition
        // in Throttle.ThrottleDefinition (mission "Clean up Your Throttle", ruling R9); the reader below is
        // the only thing that feeds it from the store. The statistics handle still supplies what counts no
        // turn - concurrency, token spend, the per-model split - and is asked per request as before.
        Stats.StatsPageEndpoint.Map(_app, InputStatsHandle, _tenantBoundary,
            new Throttle.ThrottleLedgerReader(_gatewayDb), () => SessionConcurrency,
            _tenantSettingsResolver, _sessionHistory);

        // The prompt log (issue #1551): Directors push what they captured to POST /prompts, and anyone
        // wanting history reads GET /prompts. It lives here, not on a Director, because the Gateway is
        // what the whole fleet reports to - so the history is already present rather than scattered
        // across machines - and because the Gateway is what moves to the server.
        Prompts.PromptEndpoints.Map(_app, _promptLog, _tenantBoundary, _sessionHistoryRecorder);

        // Work history (issue #2194): the range report, the flat session records, and the seal verb.
        History.HistoryEndpoints.Map(_app, _sessionHistory, _tenantBoundary);

        // The activity ledger (docs/PLAN-trustworthy-working-start-2026-07-24.md): producers push observed
        // activity/snooze evidence to POST /activity-events/batch (idempotent by producer-minted event id),
        // and diagnosis reads GET /activity-events. Tenant-scoped exactly like the prompt log.
        Activity.ActivityEventEndpoints.Map(_app, _activityEvents, _tenantBoundary);

        // The repo-state feed (issue #2118): POST /gateway/repostate, where a Director pushes its
        // repositories' branches and worktrees. Device-authenticated and tenant-scoped from the caller's
        // own key; write-only, because the sole consumer (the morning report) reads the store in-process.
        Api.RepoStateEndpoints.Map(_app, _repoState, _tenantBoundary);
        Api.SkillPlacementEndpoints.Map(_app, _skillPlacement, _tenantBoundary);

        Mobile.MobileApp.Map(_app, Token);
        // The legacy /m mount: 301 to the canonical /mobile equivalent so installed phone PWAs and
        // bookmarks (and the sign-in callback devthrottle.com still hands back to /m/device-callback) keep
        // working. Mapped after the /mobile serving and the explicit POST /m/enroll route (both win by
        // being GET vs the same verb / a different verb), before the Cockpit catch-all.
        Mobile.MobileApp.MapLegacyRedirect(_app);

        // One URL (epic #967 cutover, issue #979): the React desktop Cockpit is the Gateway's
        // canonical front door. Everything no explicit endpoint above claimed - the shell at "/",
        // client-side routes, and the hashed static assets (built into wwwroot/c by the release-gated
        // MSBuild target) - resolves here. Mapped LAST by design, exactly like /mobile above. The Blazor
        // Server Cockpit and its fallback reverse-proxy were retired in this cutover.
        Cockpit.CockpitReactApp.Map(_app);

        // Every route is now mapped, which is the earliest moment the FINALISED route space exists - and the
        // only moment a conflict between a hosted refusal and anything else can be seen. A refusal that ties
        // with another refusal answers 500 on a denied route; a refusal that ties with a live route takes an
        // undenied route off the air. Both would otherwise surface as a request-time failure on the one path
        // nobody exercises until a caller does. Fail the start instead. Inert on self-host, where no refusal
        // endpoint exists.
        //
        // The finalised endpoints are read from the app's OWN data sources, not from the DI-resolved
        // CompositeEndpointDataSource. The composite is not populated with the minimal-API / MapGroup
        // endpoints until StartAsync builds the endpoint middleware, so reading it HERE - before StartAsync -
        // returns an EMPTY set and the validation silently does nothing. The app's own
        // IEndpointRouteBuilder.DataSources carry the group endpoints (prefix and metadata conventions
        // applied) as soon as they are mapped, which is what lets this fail the start BEFORE any listener
        // binds rather than after. That source selection lives in ONE place - ValidateBeforeStart - shared
        // with the pre-start test harness, so reverting it to the DI composite reddens the tie tests rather
        // than silently regressing this path.
        Tenancy.HostedRefusalRouteSpace.ValidateBeforeStart(_app);

        // The finalised route table, kept for the route-surface guard tests (the auth allowlist for the
        // shell prefixes is complete ONLY while the set of endpoints mapped under /mobile, /m and /assets
        // stays exactly what the allowlist was written against - the guard pins that set, so a new route
        // under a shell prefix fails a test until it is consciously ruled public or gated).
        MappedEndpoints = Tenancy.HostedRefusalRouteSpace.SelectFinalisedEndpoints(_app);

        await _app.StartAsync();

        // THE DATABASE IS OPENED HERE, AFTER THE BIND, and the order is the whole point.
        //
        // Until now this ran inside the GatewayDatabase constructor, far above - so on every deploy the new
        // container connected and migrated while the platform was still waiting for it to listen. A slow
        // open pushed the bind past the container-start deadline; the platform then stopped the SITE, and
        // stopping the site tore down the healthy container that was serving traffic beside it. Both the 2
        // August (38.5s) and 12 August (46.7s) outages were that stop, not the swap.
        //
        // Nothing about the failure behaviour changes: the same bounded retry window, and the same loud
        // throw when it is spent, which GatewayService turns into a Failed state and GatewayWorker turns
        // into a process exit. What changes is that the platform can already see a listening port while all
        // of that happens, so a database problem can no longer take the site down with it.
        //
        // /healthz answers 503 for the duration, so the deploy's warm-up waits rather than swapping onto a
        // Gateway that cannot serve data.
        // The database and every store that loads from it. Named and called here, immediately after the
        // bind - see EnsureStoresReady for why the order matters.
        EnsureStoresReady();

        // Issue #2161: when the caller asked for an operating-system-assigned port, the number only exists
        // once Kestrel has bound - so read it back from the server itself. This is the whole point of the
        // mode: assignment and bind are one atomic step, so there is no window in which another process can
        // take the port out from under us. Fail loudly if the address cannot be read: a Gateway whose Port
        // still says 0 would hand that 0 to every consumer below and produce unreachable URLs.
        if (Port == OperatingSystemAssignedPort)
            Port = ReadBoundPort(_app);
        FileLog.Write($"[GatewayHost] listening on http://0.0.0.0:{Port} (all interfaces, auth-gated; version {version})");

        // The bind is the finish line the platform's startup probe is waiting for, so it is also where the
        // hosted standard-output mirror stops. It exists to make the STARTUP sequence readable in the
        // platform's per-container log (issue #2203 - three deploys produced containers that reached
        // "Application started" and then never got here, with no record of where they stopped). Leaving it
        // on past this point would copy the Gateway's whole running log - tens of megabytes a day - into
        // the platform log mount, which is a different outage. From here the per-container file is the
        // record, and FileLog.DroppedLines plus its "LOG GAP" marker report if any of it is lost.
        if (FileLog.MirrorToConsole)
        {
            FileLog.Write("[GatewayHost] startup complete - standard-output log mirror off; the per-container file is the record from here");
            FileLog.MirrorToConsole = false;
        }

        // Cron firing sweep (epic #479, #483): wake ~every minute and fire due jobs. The first tick
        // also catches up a fire that came due while the Gateway was down (at most once per job).
        //
        // G8 increment 2: the cron drain reads the tenant-scoped cron_jobs store. It now fires through the
        // per-tenant worker seam (CronTenantSweep / TenantScopedSweep), which enters each tenant's ambient
        // scope before reading, so the sweep runs ON hosted (tenant-isolated) instead of being disabled. On
        // self-host the seam fires once under Local - the same single fire as before.
        _cronTimer = new System.Threading.Timer(_ => SweepCron(), null, CronSweepInterval, CronSweepInterval);
        FileLog.Write($"[GatewayHost] cron sweep started: every {CronSweepInterval.TotalSeconds:0}s ({(GatewayHostedMode.IsHosted ? "hosted, per-tenant via TenantScopedSweep" : "self-host, single Local tenant")})");

        // The activity ledger's 30-day retention purge. A daily window enforced a few times a day is ample;
        // the first pass is delayed so startup work is never contended by a bulk delete.
        _activityRetentionTimer = new System.Threading.Timer(_ => SweepActivityRetention(), null,
            ActivityRetentionStartupDelay, ActivityRetentionInterval);
        FileLog.Write($"[GatewayHost] activity retention sweep started: every {ActivityRetentionInterval.TotalHours:0}h, retention {Activity.ActivityRetentionSweep.RetentionPeriod.TotalDays:0} days");

        // The prompt log's retention purge (CR-3b): same footing as the other bounded stores. The window
        // resolves from the deployment mode - hosted is always the product default; self-host may override
        // via the environment (a malformed override throws HERE, loudly, at startup, not mid-sweep).
        _promptRetentionSweep = new Prompts.PromptLogRetentionSweep(_promptLog,
            Prompts.PromptLogRetentionSweep.ResolveRetention(GatewayHostedMode.IsHosted,
                Environment.GetEnvironmentVariable(Prompts.PromptLogRetentionSweep.RetentionDaysEnvVar)));
        _promptRetentionTimer = new System.Threading.Timer(_ => SweepPromptRetention(), null,
            PromptRetentionStartupDelay, PromptRetentionInterval);
        FileLog.Write($"[GatewayHost] prompt-log retention sweep started: every {PromptRetentionInterval.TotalHours:0}h, retention {_promptRetentionSweep.Retention.TotalDays:0} days");

        // The daily dictionary-suggestion scan (devthrottle #2115): each tick asks, per tenant, whether that
        // tenant's local 00:05 has passed since its last stored scan; only then does it mine and screen. The
        // first pass is delayed so startup is never contended, and a tenant with no stored scan yet is seeded
        // on that first pass.
        _suggestionSweepTimer = new System.Threading.Timer(_ => SweepSuggestions(), null,
            SuggestionSweepStartupDelay, SuggestionSweepInterval);
        FileLog.Write($"[GatewayHost] dictionary-suggestion sweep started: tick every {SuggestionSweepInterval.TotalMinutes:0}m, daily per tenant at local 00:05");

        // Work history (issue #2194): conclude "interrupted" from silence, generate owed summaries and
        // roll-ups (capped per pass), prune retention. A short tick so an interrupted ruling lands within
        // minutes of the threshold; the first pass is delayed so startup is never contended.
        _sessionHistoryTimer = new System.Threading.Timer(_ => SweepSessionHistory(), null,
            SessionHistorySweepStartupDelay, SessionHistorySweepInterval);
        FileLog.Write($"[GatewayHost] session history sweep started: every {SessionHistorySweepInterval.TotalMinutes:0}m, interrupted after {History.SessionHistorySweep.InterruptedThreshold.TotalMinutes:0}m of silence, retention {History.SessionHistorySweep.Retention.TotalDays:0} days");

        // MTR-15 cancellation cutoff: the hosted active-tenant entitlement sweep. Forces a fresh entitlement
        // read for every tenant with a live lease every ~60s and revokes any that has become NotEntitled, so a
        // cancelled tenant loses access within roughly one sweep cycle (never past the paid period end).
        if (GatewayHostedMode.IsHosted && _leaseMonitor is not null)
        {
            _leaseSweepTimer = new System.Threading.Timer(_ => SweepEntitlementLeases(), null, LeaseSweepInterval, LeaseSweepInterval);
            FileLog.Write($"[GatewayHost] entitlement lease sweep started: every {LeaseSweepInterval.TotalSeconds:0}s (hosted cancellation cutoff)");
        }

        // Scheduled-run auto-dismiss (issue #1200): close automated runs that declared themselves done, by
        // sending the kill verb DOWN the Director stream. The close has no REST fallback by design (the
        // Gateway owns session lifecycle and reaches the Director through its stream).
        _autoDismissSweeper = new Running.AutoDismissSweeper(
            () => AmbientSnapshotFresh(AutoDismissStaleAfter),
            SendCommandAsync,
            // Partition the close-marks by the tenant of the pass currently running (see AutoDismissSweeper).
            // Null (no tenant in scope on hosted) is a DENY inside the sweeper, not an empty prefix.
            tenantKey: () => _tenantPass.Current?.Value);
        _autoDismissTimer = new System.Threading.Timer(_ => SweepAutoDismiss(), null, AutoDismissSweepInterval, AutoDismissSweepInterval);
        FileLog.Write($"[GatewayHost] auto-dismiss sweep started: every {AutoDismissSweepInterval.TotalSeconds:0}s");

        // The fold push backstop: re-fold the fleet and stamp changed answers down, catching the Gateway-only
        // overlays (voice, transcription, dictation, snooze expiry) that arrive on no Director push. Never
        // throws into the timer.
        _displayStateSweepTimer = new System.Threading.Timer(
            _ => SweepDisplayState(),
            null, DisplayStateSweepInterval, DisplayStateSweepInterval);
        FileLog.Write($"[GatewayHost] display-state sweep started: every {DisplayStateSweepInterval.TotalSeconds:0}s");

        // Un-deny safety gate (issue #1884). Now that the /dictation and /wingman/utterance upload families
        // are served on hosted (tenant-partitioned), any pre-partition upload directory sitting directly under
        // a shared base root is legacy and unattributable - it predates the partition and belongs to no
        // resolvable tenant. On hosted, move those aside ONCE at startup so no pass (the age sweep below, the
        // durable PENDING projection, or any base-handle read) ever treats them as live. It MOVES, never
        // deletes, and is idempotent/re-entrant, so it is safe under restart and concurrent workers. Self-host
        // legitimately keeps its uploads at the root, so this runs only on hosted.
        if (GatewayHostedMode.IsHosted)
        {
            var quarantinedDictation = _dictationUploads.QuarantineLegacyUploads();
            var quarantinedVoiceTurn = _voiceTurnUploads.QuarantineLegacyUploads();
            FileLog.Write($"[GatewayHost] hosted un-deny safety: quarantined legacy upload dirs " +
                $"dictation={quarantinedDictation} voiceTurn={quarantinedVoiceTurn}");
        }

        // Voice-turn upload staging retention. The success path deletes an upload's staging directory when
        // the turn starts; everything else - a refused, dropped or never-completed upload - is bounded here
        // and only here. It runs on every deployment, self-host and hosted alike, because the audio it
        // removes is recorded speech and there is no deployment where keeping it forever is right.
        var voiceTurnUploadSchedule = VoiceTurnUploadSweepScheduleForTests ?? VoiceTurnUploadSweepInterval;
        _voiceTurnUploadSweepTimer = new System.Threading.Timer(
            _ =>
            {
                // PER-TENANT retention (issue #1884, finding 2). /wingman/utterance now stages account uploads
                // in tenant partitions (base/tenants/<id>), and SweepAbandoned deliberately does NOT descend
                // into the partition container - so sweeping only the Local/base handle would retain every
                // hosted tenant's interrupted utterance audio forever, breaking the four-hour privacy bound
                // (#1952). Run one pass per live tenant, each inside that tenant's own partition. Self-host runs
                // exactly one Local pass (ForTenant(Local) is the base root), byte-identical to before.
                try
                {
                    _tenantPass.ForEachTenant(() =>
                    {
                        if (_tenantPass.Current is not { } tenant) return; // deny: no scope -> sweep nothing
                        _voiceTurnUploads.ForTenant(tenant).SweepAbandoned(VoiceTurnUploadMaxAge);
                    });
                }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-turn upload sweep error: {ex.Message}"); }
            },
            null, voiceTurnUploadSchedule, voiceTurnUploadSchedule);
        FileLog.Write($"[GatewayHost] voice-turn upload sweep started: every " +
            $"{voiceTurnUploadSchedule.TotalMinutes:0.###}min, removing staging idle longer than " +
            $"{VoiceTurnUploadMaxAge.TotalHours:0.###}h");

        // Dictation tombstone retention (issue #1111). The client ack is what normally retires a terminal
        // record, and it does so within seconds; this only bounds the ones whose ack will NEVER arrive -
        // a client that dropped its queue, was reinstalled, or never came back. Without it those records are
        // immortal and the store grows forever, which is what was observed live (28 records, all DELIVERED,
        // the oldest three weeks old). Per-tenant for the same reason the sweep above is: the pass does not
        // descend into the partition container, so sweeping only the base handle would leave every hosted
        // tenant's tombstones unbounded. Self-host runs exactly one Local pass over the base root.
        var dictationTombstoneSchedule = DictationTombstoneSweepScheduleForTests ?? DictationTombstoneSweepInterval;
        _dictationTombstoneSweepTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    _tenantPass.ForEachTenant(() =>
                    {
                        if (_tenantPass.Current is not { } tenant) return; // deny: no scope -> sweep nothing
                        var uploads = _dictationUploads.ForTenant(tenant);
                        // Release stale session locks BEFORE retiring tombstones: expiring turns a PENDING
                        // record into an ABANDONED one, and running it first means the tombstone sweep in the
                        // same pass can age out anything that was already past both bounds, instead of
                        // leaving it for the next tick six hours later.
                        uploads.ExpireStalePending(StalePendingMaxAge);
                        uploads.SweepResolvedTombstones(DictationTombstoneMaxAge);
                    });
                }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] dictation tombstone sweep error: {ex.Message}"); }
            },
            null, dictationTombstoneSchedule, dictationTombstoneSchedule);
        FileLog.Write($"[GatewayHost] dictation tombstone sweep started: every " +
            $"{dictationTombstoneSchedule.TotalHours:0.###}h, retiring unacknowledged terminal records older " +
            $"than {DictationTombstoneMaxAge.TotalDays:0.###} days, and abandoning PENDING records with no " +
            $"activity for {StalePendingMaxAge.TotalHours:0.###}h so their session lock is released");

        // Remove-the-network-port phase 1b: retire lapsed session keys. This is HOUSEKEEPING, and saying so
        // matters - a lapsed key is ALREADY refused, because the expiry is checked on every resolution, so
        // nothing about who can call the Gateway depends on this timer running. What it prevents is a table
        // that fills with rows an operator would read as live credentials. A retention policy with no caller
        // is not a policy, which is the only reason this is wired rather than left as a method to call later.
        _sessionKeySweepTimer = new System.Threading.Timer(
            _ =>
            {
                try { SessionKeys.SweepExpired(); }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] session key sweep error: {ex.Message}"); }
            },
            null, SessionKeySweepInterval, SessionKeySweepInterval);
        FileLog.Write($"[GatewayHost] session key sweep started: every {SessionKeySweepInterval.TotalHours:0.###}h, " +
            "retiring keys past their expiry (a lapsed key is already refused at resolution - this is housekeeping)");

        // Issue #640: start the Gateway-owned background token refresh. Start() returns immediately (the
        // first sweep runs after a short delay), so this never blocks startup. When the cached access
        // token has expired and a refresh endpoint is configured, the sweep exchanges the refresh token
        // for a fresh pair; otherwise it is a no-op or keeps the cached credential. Null on a host with no
        // credential service.
        _tokenRefresh?.Start();

        // Issue #857: start the Gateway's background device heartbeat. The first tick (after a short delay)
        // also acts as the first-launch/retry safety net for device registration - if the Gateway is signed
        // in but not yet registered (or a sign-in-time registration failed against an unreachable cloud), it
        // registers here and then advances last-seen on every tick. Never blocks startup; a cloud failure
        // only logs and retries next tick. Null on a host with no credential service.
        _deviceHeartbeat?.Start();

        // No snooze expiry sweep. There used to be a 15-second watchdog here that retired an elapsed entry;
        // it was removed once the Gateway owned both the state and the clock, because expiry is a local fact
        // (HoldStateFor reports an elapsed entry as None on every read) and deleting the entry on a timer
        // erased the returned-by-timer badge before any consumer could see it. An elapsed entry now lingers
        // as a durable tombstone, retired only by an edge that ends a snooze; the registry is bounded by the
        // live-session prune paths. A no-op timer would be a smell, so there is none.

        // Governance capture (issue #1771, spine item 3): periodically mirror the account's real hosted-AI
        // service debits from the cloud credit-debit ledger into account_hosted_ai_spend, so the weekly
        // report shows an honest account-level "Hosted-AI services: $X" figure. Signed out is an expected
        // no-op (no fabricated spend); the store dedups the ledger's rolling window so over-polling is safe.
        // Skipped when HOSTED (Hosted Multi-Tenancy): mirroring writes the tenant-scoped account_hosted_ai_spend
        // store, which has no ambient tenant on a background timer and would fail closed. Per-tenant/account
        // spend mirroring lands in the session-serving increment.
        if (!GatewayHostedMode.IsHosted)
        {
            _hostedAiSpendSweep = new Governance.HostedAiSpendSweep(
                accessToken: () => Account?.GetAccessTokenForForwarding(),
                credits: new Core.Account.AccountCreditsClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
                store: _hostedAiSpend);
            _hostedAiSpendSweep.Start();
        }

        // Web Push (mobile app-icon "needs you" dot): start the background notifier now that this
        // Gateway's fold is live. The notifier counts the sessions that "need you" in the tenant's OWN folded
        // fleet and pushes the count to that tenant's subscribed phones. It self-gates on having at least one
        // subscription, so it is free until a phone opts in.
        //
        // RUNS ON HOSTED TOO. It used to be skipped there, because its own bare timer had no ambient tenant and
        // would fail closed against the tenant-scoped push_subscriptions store every tick. Skipped meant a phone
        // talking to the hosted Gateway never got a dot when a session needed it, and never got the single
        // falling-edge zero that CLEARS a dot. Now the per-tenant worker seam owns the fan-out
        // (PushNeedsYouTenantSweep) and this host owns the timer, so each pass runs inside one tenant's scope:
        // that tenant's phones, that tenant's fleet. Self-host is one Local pass, unchanged.
        var pushSender = new Push.VapidWebPushSender(
            _vapidStore.PublicKey, _vapidStore.PrivateKey, "mailto:support@devthrottle.com");
        _pushNotifier = new Push.WebPushNeedsYouNotifier(
            _pushSubscriptions,
            GetNeedsYouCountAsync,
            pushSender,
            // The tenant of the pass now running - what the dot state is keyed by, so one tenant's rising edge
            // cannot clear another's. Null is only possible on hosted with no scope entered, which cannot happen
            // inside the seam; Local keeps the key total without inventing a partition to READ (nothing is read
            // by tenant id here - the stores resolve the ambient scope themselves).
            () => _tenantPass.Current ?? TenantId.Local);
        _pushNeedsYouSweep = new Push.PushNeedsYouTenantSweep(_tenantBoundary, TenantRegistry, _pushNotifier);
        _pushNotifierTimer = new System.Threading.Timer(
            _ => SweepPushNeedsYou(), null,
            Push.WebPushNeedsYouNotifier.StartupDelay,
            Push.WebPushNeedsYouNotifier.PollInterval);
        FileLog.Write($"[GatewayHost] push needs-you sweep started: every {Push.WebPushNeedsYouNotifier.PollInterval.TotalSeconds:0}s (while subscribed)");

        // Network Diagnostics monitor (Network Diagnostics mission, Phase 1): on a timer, watch each
        // connected device's direct-vs-relay path and log persistent home-relay drift QUIETLY. Alert
        // channels (doorbell + owner email) are wired in P5 onto the same Decide-machine state.
        //
        // Skipped when HOSTED: the monitor's collector shells out to the tailscale CLI, which the container
        // image does not bundle, so every poll would throw. A hosted Gateway has no tailnet to diagnose.
        if (!GatewayHostedMode.IsHosted)
        {
            var netDiagDeviceStore = new Api.NetDiagDeviceStore(Path.Combine(CcStorage.Root(), "netdiag-devices.json"));
            // P5: deliver the monitor's drift/resolve to the doorbell + owner email. The alert service owns the
            // 401-explicit "not signed in" path, the daily email cap, and one-email-per-episode; the monitor
            // just hands it the device name on the machine's rising/falling edges.
            var netDiagNotify = new Core.Account.AccountNotifyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            var netDiagAlerts = new Api.NetDiagAlertService(
                DirectorEvents,
                () => Account?.GetAccessTokenForForwarding(),
                async (token, subject, bodyText) =>
                {
                    var r = await netDiagNotify.SendOwnerAsync(token, subject, bodyText, null, null, default).ConfigureAwait(false);
                    return r.Sent;
                });
            _netDiagMonitor = new Api.NetDiagMonitor(
                Api.TailscaleDiagnostics.Collect, Api.LanPresenceProbe.TryResolveMac, netDiagDeviceStore, _netDiagRollup,
                netDiagAlerts.OnDrift, netDiagAlerts.OnResolve);
            _netDiagMonitor.Start();
        }
    }

    /// <summary>
    /// Read THIS Gateway's own aggregated roster over loopback and compute the notifier's snapshot: the
    /// count of sessions that "need you" plus the ids of those that just returned from an expired snooze.
    /// Going through the real <c>/sessions</c> endpoint (rather than re-implementing the fan-out) keeps
    /// the notifier's verdict identical to what every client sees - same aggregation, same effective-red
    /// fold, same <see cref="Contracts.SessionDto.SnoozeExpired"/> overlay. The per-machine Bearer is
    /// attached so it works whether or not global Gateway auth is on.
    /// </summary>
    /// <summary>
    /// How many of the CURRENT TENANT's sessions need the user right now, for the phone's app-icon dot - read
    /// from the fleet fold (<see cref="Fleet.FleetDisplayStateObserver.FoldedFleet"/>), the same snapshot and
    /// the same fold that decide the colour the desktop rail paints and the roster serves. One authority for
    /// the verdict, two readers of it.
    ///
    /// It used to fetch the Gateway's own <c>/sessions</c> endpoint over loopback with the host token, to be
    /// byte-identical to the roster. That is unavailable to a background pass on the hosted Gateway: the roster
    /// resolves its tenant from the CALLER'S authenticated device key, and a loopback request carries no device
    /// key and starts a fresh request context that the pass's ambient tenant scope does not reach - so it would
    /// be denied, not served the wrong tenant. Reading the fold in-process is scoped to the running pass's
    /// tenant by construction and drops the HTTP hop entirely.
    /// </summary>
    private Task<Push.WebPushNeedsYouNotifier.NeedsYouSnapshot> GetNeedsYouCountAsync(CancellationToken cancellationToken)
    {
        var sessions = FleetDisplayState.FoldedFleet();
        return Task.FromResult(new Push.WebPushNeedsYouNotifier.NeedsYouSnapshot(
            Push.WebPushNeedsYouNotifier.CountNeedsYou(sessions),
            Push.WebPushNeedsYouNotifier.ExpiredNeedsYouIds(sessions)));
    }

    /// <summary>
    /// The push notifier's timer callback (a boundary - it owns the overlap guard and the try/catch so one slow
    /// or failing fan-out never stacks up or crashes the timer thread). One sweep at a time; a skipped tick
    /// simply decides on the next one, eight seconds later, which the dot is indifferent to.
    /// </summary>
    private void SweepPushNeedsYou()
    {
        if (Interlocked.CompareExchange(ref _pushNeedsYouInFlight, 1, 0) != 0)
            return;
        _ = RunPushNeedsYouSweepAsync();
    }

    private async Task RunPushNeedsYouSweepAsync()
    {
        try
        {
            if (_pushNeedsYouSweep is not null)
                await _pushNeedsYouSweep.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] push needs-you sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _pushNeedsYouInFlight, 0);
        }
    }

    /// <summary>
    /// The cron sweep timer callback (a boundary - it owns the try/catch so a sweep failure never
    /// crashes the timer thread). Fires due jobs; per-job failures are isolated inside the engine.
    /// </summary>
    /// <summary>
    /// The stamp delegate for the display-state PUSH seam (FleetDisplayStateObserver). The pushed snapshot
    /// carries only Director-owned facts, but the color fold reads two Gateway-only voice booleans
    /// (VoiceGenerating / VoiceAudioReady from _voiceService). This enriches each session with them BEFORE
    /// folding - the same order the roster handler uses (GatewayEndpoints roster map) - so the push seam and
    /// the roster fold IDENTICAL inputs and therefore the identical color.
    ///
    /// Without this the push seam sees VoiceAudioReady=false for every session and, since #1841 made
    /// IsVoicePreparing key on <c>!VoiceAudioReady</c>, holds every voice-mode waiting session yellow
    /// ("Preparing voice") forever, never moving to red once the voice is ready. The voice-ready flip
    /// arrives on no Director push, so only the 5s backstop sweep could carry it - and unenriched the sweep
    /// re-derives the same yellow every tick and the change gate suppresses the update permanently. This is
    /// exercised directly by the tests so the enrichment cannot silently regress again.
    /// </summary>
    internal static void EnrichVoiceThenFoldForPush(
        List<SessionDto> sessions,
        Func<string, bool> voiceGeneratingFor,
        Func<string, bool> voiceAudioReadyFor,
        TenantId tenant,
        Func<TenantId, string, bool, DateTime?>? needsYouStampFor,
        Snooze.SnoozeRegistry? snoozeRegistry,
        // Issue #2662: threaded so the PUSH path folds the same inputs as the roster. #1843 is the whole
        // reason this is not optional-and-forgotten: that seam already shipped once folding without two of
        // the roster's inputs, and the two surfaces disagreed silently for days. Sharing the fold function
        // is not sharing the answer - the INPUTS have to match.
        Fleet.HandRaiseRegistry? handRaises,
        Func<string, Core.HostedAi.HostedAiState?>? voiceUnavailableFor = null,
        Func<string, bool>? nothingToNarrateFor = null,
        Func<string, bool>? directorCannotSendConversationFor = null,
        Func<string, bool>? narrationAbandonedFor = null,
        Func<string, bool, DateTime?>? voiceWaitingStampFor = null)
    {
        foreach (var s in sessions)
        {
            s.VoiceGenerating = voiceGeneratingFor(s.SessionId);
            s.VoiceAudioReady = voiceAudioReadyFor(s.SessionId);
            // The REASON there is no voice, carried on the push exactly as the roster carries it. Without
            // these two the pushed row holds only the two readiness booleans, so the desktop can be told
            // "no audio" but never WHY - and SessionOrdering.VoiceHoldLabel, which renders the reason the
            // Gateway already folded, has nothing to render and falls back to "Preparing voice" forever.
            // The roster path stamps both (GatewayEndpoints); this is the same stamp so the two surfaces
            // cannot say different things about one session. Null delegates leave the fields unset, which
            // is the pre-existing behaviour and keeps every non-production caller compiling unchanged.
            var unavailable = voiceUnavailableFor?.Invoke(s.SessionId);
            if (unavailable is Core.HostedAi.HostedAiState reason)
                s.VoiceUnavailable = HostedAi.HostedAiHttp.Dto(reason);
            var working = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);
            // The clock is stamped from the SAME facts the fold below reads, so the elapsed time and the
            // words can never disagree about whether this session is waiting at all.
            s.VoiceWaitingSince = voiceWaitingStampFor?.Invoke(
                s.SessionId, Wingman.VoiceDisplayFold.IsWaitingForVoice(s.VoiceMode, s.VoiceAudioReady, working));
            s.VoiceDisplay = Wingman.VoiceDisplayFold.Fold(
                voiceMode: s.VoiceMode,
                agentWorking: working,
                hasAudio: s.VoiceAudioReady,
                generating: s.VoiceGenerating,
                unavailable: unavailable,
                nothingToNarrate: nothingToNarrateFor?.Invoke(s.SessionId) ?? false,
                directorCannotSendConversation: directorCannotSendConversationFor?.Invoke(s.SessionId) ?? false,
                narrationAbandoned: narrationAbandonedFor?.Invoke(s.SessionId) ?? false,
                waitingSince: s.VoiceWaitingSince);
        }
        Api.GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, needsYouStampFor, snoozeRegistry, tenant, handRaises);
    }

    /// <summary>
    /// The display-state sweep timer callback (a boundary - it owns the try/catch so a sweep failure never
    /// crashes the timer thread). One pass per tenant, each inside that tenant's scope (issue #1966), exactly
    /// like the auto-dismiss sweep. Self-host runs it once (Local); hosted runs it once per live tenant, so
    /// the observer's ambient snapshot and per-tenant gate resolve to each tenant in turn. The try/catch is
    /// OUTSIDE the per-tenant loop only as a backstop - ForEachTenant itself does not isolate a throwing
    /// tenant here, but Sweep is fire-and-forget internally and does not throw on a single bad send.
    /// </summary>
    /// <remarks>Internal (not private) so a test can drive the REAL pass - the per-tenant fold the timer
    /// runs - rather than only the guard around it.</remarks>
    internal void SweepDisplayState()
        => SweepDisplayStateTick(() => _tenantPass.ForEachTenant(() => FleetDisplayState.Sweep()));

    /// <summary>
    /// THE OVERLAP GUARD (issue #2323, read-model epic #1159), and the counting that proves it works.
    ///
    /// A <see cref="System.Threading.Timer"/> fires on the thread pool every five seconds whether or not the
    /// last callback finished, so a pass slower than the interval simply gets another one on top of it. The
    /// 31 July load-test baseline measured what that costs: 91 of 98 ticks overlapped a prior tick, with up
    /// to 36 sweeps in flight at once, each folding every session of every tenant behind the snooze
    /// registry's process-wide monitor. SKIPPING is the right behaviour rather than queueing - this sweep is
    /// a BACKSTOP re-fold, so a tick arriving while the last is still running has nothing to add that the
    /// running pass will not already carry, and the next tick is five seconds away.
    ///
    /// THE GUARD SITS OUTSIDE THE PER-TENANT LOOP deliberately: one whole pass at a time. Guarding per tenant
    /// would let the next tick start on the other tenants while a slow one was still running, which is the
    /// same contention wearing a smaller number.
    ///
    /// A SKIPPED TICK STAYS COUNTABLE. <c>sweepOverlaps</c> is the instrument that measured this defect, and
    /// a guard that made a skipped tick invisible would destroy it - the count would fall to zero because
    /// nothing was being observed, which is indistinguishable from falling to zero because the guard works.
    /// So a skip is counted as its own fact (<c>sweepSkipped</c>), the tick itself is still counted
    /// (<c>sweepTicks</c> means "the timer fired", ran or not), and <c>sweepOverlaps</c> is left exactly as it
    /// was so it can still report a non-zero if this guard ever fails to hold.
    ///
    /// <paramref name="pass"/> is the work to do, and it is a parameter only so the guard can be proved where
    /// it lives: a test holds a pass open and calls this concurrently. Production has exactly one caller,
    /// <see cref="SweepDisplayState"/>, which supplies the real per-tenant pass.
    /// </summary>
    internal void SweepDisplayStateTick(Action pass)
    {
        if (Interlocked.CompareExchange(ref _displayStateSweepInFlight, 1, 0) != 0)
        {
            Diagnostics.LoadTestMetrics.SweepSkipped();
            FileLog.Write("[GatewayHost] display-state sweep tick SKIPPED: the previous pass is still running");
            return;
        }

        var sweepStart = Diagnostics.LoadTestMetrics.SweepStarting();
        try { pass(); }
        catch (Exception ex) { FileLog.Write($"[GatewayHost] display-state sweep error: {ex.Message}"); }
        finally
        {
            Diagnostics.LoadTestMetrics.SweepFinished(sweepStart);
            Interlocked.Exchange(ref _displayStateSweepInFlight, 0);
        }
    }

    private void SweepCron()
    {
        // Skip this tick if the previous sweep is still fanning out (hosted, many tenants). Overlapping sweeps
        // could double-fire a non-overlap-guarded job; one at a time is correct and a skipped tick simply
        // fires on the next one (cron granularity is a minute, not a second).
        if (Interlocked.CompareExchange(ref _cronSweepInFlight, 1, 0) != 0)
            return;
        _ = RunCronSweepAsync();
    }

    private async Task RunCronSweepAsync()
    {
        try
        {
            // G8 increment 2: fire through the per-tenant seam (fans out over the tenant census on hosted,
            // once under Local on self-host) instead of calling the engine with no ambient tenant. Awaited
            // here (not fire-and-forget) so the guard is released only when the whole fan-out is done and so
            // a fault in any tenant's async work is logged rather than lost.
            if (_cronSweep is not null)
                await _cronSweep.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] cron sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _cronSweepInFlight, 0);
        }
    }

    /// <summary>
    /// The activity-retention timer callback (a boundary - it owns the overlap guard and the try/catch so a
    /// purge failure never crashes the timer thread). One sweep at a time; a skipped tick simply purges on
    /// the next one, which retention granularity is indifferent to.
    /// </summary>
    private void SweepActivityRetention()
    {
        if (Interlocked.CompareExchange(ref _activityRetentionInFlight, 1, 0) != 0)
            return;
        _ = RunActivityRetentionSweepAsync();
    }

    private async Task RunActivityRetentionSweepAsync()
    {
        try
        {
            if (_activityRetentionSweep is not null)
                await _activityRetentionSweep.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] activity retention sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _activityRetentionInFlight, 0);
        }
    }

    /// <summary>
    /// The prompt-log retention timer callback (a boundary - it owns the overlap guard and the try/catch so
    /// a purge failure never crashes the timer thread). One sweep at a time; a skipped tick simply purges on
    /// the next one, which retention granularity is indifferent to.
    /// </summary>
    private void SweepPromptRetention()
    {
        if (Interlocked.CompareExchange(ref _promptRetentionInFlight, 1, 0) != 0)
            return;
        try
        {
            _promptRetentionSweep?.Sweep();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] prompt-log retention sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _promptRetentionInFlight, 0);
        }
    }

    /// <summary>
    /// The dictionary-suggestion timer callback (a boundary - it owns the overlap guard and the try/catch so
    /// a scan failure never crashes the timer thread). One sweep at a time; a skipped tick simply checks on
    /// the next one, which a daily schedule is indifferent to.
    /// </summary>
    private void SweepSuggestions()
    {
        if (Interlocked.CompareExchange(ref _suggestionSweepInFlight, 1, 0) != 0)
            return;
        _ = RunSuggestionSweepAsync();
    }

    private async Task RunSuggestionSweepAsync()
    {
        try
        {
            if (_suggestionDailySweep is not null)
                await _suggestionDailySweep.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] dictionary-suggestion sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _suggestionSweepInFlight, 0);
        }
    }

    /// <summary>
    /// The work-history timer callback (issue #2194) - a boundary: it owns the overlap guard and the
    /// try/catch so a sweep failure never crashes the timer thread. One sweep at a time; a pass slowed
    /// by model calls simply makes the next tick skip.
    /// </summary>
    private void SweepSessionHistory()
    {
        if (Interlocked.CompareExchange(ref _sessionHistorySweepInFlight, 1, 0) != 0)
            return;
        _ = RunSessionHistorySweepAsync();
    }

    private async Task RunSessionHistorySweepAsync()
    {
        try
        {
            if (_sessionHistorySweep is not null)
                await _sessionHistorySweep.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] session history sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _sessionHistorySweepInFlight, 0);
        }
    }

    private void SweepEntitlementLeases()
    {
        // Skip this tick if the previous entitlement sweep is still running (many active tenants). One at a
        // time is correct; a skipped tick simply runs on the next one.
        if (Interlocked.CompareExchange(ref _leaseSweepInFlight, 1, 0) != 0)
            return;
        _ = RunEntitlementSweepAsync();
    }

    private async Task RunEntitlementSweepAsync()
    {
        try
        {
            if (_leaseMonitor is not null)
                await _leaseMonitor.SweepOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] entitlement lease sweep FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _leaseSweepInFlight, 0);
        }
    }

    /// <summary>
    /// TEST SEAM (internal): seed a <c>gateway.entitlements</c> row so an enrolled test tenant is ENTITLED.
    /// Production requires an active entitlement to enroll (the 402 gate on the paid endpoint), but the
    /// low-level test enroll helper registers a device directly, bypassing that endpoint - so it must seed the
    /// entitlement itself, or the MTR-15 request-path cutoff would deny every test tenant. Creates the
    /// entitlements table if the test database has none (it is excluded from migrations - website-owned).
    /// </summary>
    internal void SeedEntitlementForTest(string subject, string status = "active", DateTime? currentPeriodEnd = null, bool livemode = true, string? tier = "hosted")
    {
        // Concrete, non-null values so ExecuteSqlRaw has a type mapping for every parameter (a DBNull.Value or a
        // null array element trips EF's parameter type resolution). A far-future period end keeps an entitled
        // test tenant entitled for the whole run.
        var periodEnd = currentPeriodEnd ?? DateTime.UtcNow.AddYears(1);
        var tierValue = tier ?? Tenancy.EntitlementRegistry.TierHosted;
        using var ctx = _gatewayDb.CreateUnscopedContext();
        // Provider CONDITIONAL, same signal the model shape uses (issue #1173 made this seam run on
        // Postgres for the first time - the load-test rig seeds synthetic tenants through it against a
        // throwaway Postgres, and the SQLite-only SQL below was a hard syntax error there). On Postgres
        // the table is the website-owned shape the model maps: gateway.entitlements with a uuid subject,
        // upserted with the Postgres conflict clause. SQLite keeps its original SQL, byte-identical.
        if (ctx.Database.IsNpgsql())
        {
            ctx.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS gateway");
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS gateway.entitlements (" +
                "subject uuid NOT NULL PRIMARY KEY, status text NOT NULL, " +
                "current_period_end timestamptz NULL, stripe_subscription_id text NULL, updated_at timestamptz NULL, " +
                "livemode boolean NULL, tier text NULL)");
            // Guid.Parse is the same demand the model's subject-to-uuid value converter makes of every
            // read: on Postgres a subject that is not a uuid could never be entitled anyway, so a bad
            // test subject fails HERE, loudly, not later as a mysterious 402.
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO gateway.entitlements (subject, status, current_period_end, livemode, tier) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}) " +
                "ON CONFLICT (subject) DO UPDATE SET status = EXCLUDED.status, " +
                "current_period_end = EXCLUDED.current_period_end, livemode = EXCLUDED.livemode, tier = EXCLUDED.tier",
                Guid.Parse(subject), status, periodEnd, livemode, tierValue);
        }
        else
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
            ctx.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                subject, status, periodEnd, livemode, tierValue);
        }
    }

    /// <summary>
    /// The auto-dismiss sweep timer callback (issue #1200; a boundary - it owns the try/catch so a sweep
    /// failure never crashes the timer thread). Closes automated runs that declared themselves done, over the
    /// Director stream. Fire-and-forget: the async sweep runs on the thread pool so the timer thread returns.
    /// </summary>
    internal void SweepAutoDismiss()
    {
        // Hosted Multi-Tenancy (session-serving PR2): one pass per tenant, each inside that tenant's scope,
        // so the sweeper reads that tenant's fleet and its kill verbs go down that tenant's own Directors.
        // The try/catch is INSIDE the pass so one tenant failing does not skip the rest.
        _tenantPass.ForEachTenant(() =>
        {
            try
            {
                _ = _autoDismissSweeper?.SweepAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayHost] auto-dismiss sweep FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Issue #1176 (Phase 1b): the down-channel proof. Sends a message DOWN a Director's stream and awaits
    /// its reply over the SAME connection (SignalR client results), demonstrating that the Gateway can
    /// both push to and request from a Director on the one outbound-dialed connection. Returns null when
    /// that Director has no active stream. NOTE: this is a synthetic proof, not a production command path -
    /// the retired assessed-state producer meant there was no live down-path to migrate (plan 4.7b).
    /// </summary>
    public async Task<string?> PingDirectorAsync(string directorId, string message, CancellationToken ct = default)
    {
        var connectionId = PushedSessions.GetActiveConnectionId(TenantId.Local, directorId);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] PingDirectorAsync: no active stream for director={directorId}");
            return null;
        }
        return await hub.Clients.Client(connectionId).InvokeAsync<string>("Ping", message, ct);
    }

    /// <summary>
    /// Issue #1177 (Phase 1): send a command DOWN a Director's stream and await its result over the SAME
    /// connection (SignalR client results), modeled exactly on <see cref="PingDirectorAsync"/>. Returns
    /// null when that Director has no active stream connection (or the hub is unavailable). Gateway Cleanup
    /// (the cut) made the tunnel MANDATORY and deleted the HTTP command path, so null means the command is
    /// UNROUTABLE and the caller surfaces it as a 502 - there is nothing to fall back to. Any non-null
    /// result - success OR a typed failure - means the stream handled the command and its outcome is
    /// authoritative.
    ///
    /// This does not bound its own wait. The wait lives at the ONE chokepoint every command routes through,
    /// <c>DirectorCommandRouter.TrySendAsync</c>, which passes a token already linked to its timeout - so a
    /// Director that holds the tunnel open and answers nothing cancels the InvokeAsync below rather than
    /// hanging forever. Do not add a second timeout here; two would drift.
    /// </summary>
    public async Task<DirectorCommandResult?> SendCommandAsync(string directorId, DirectorCommand command, CancellationToken ct = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        // Hosted Multi-Tenancy (session-serving PR2): resolve the Director's stream connection within the
        // tenant of the CURRENT unit of work - the request scope, the tunnel connection scope, or the
        // per-tenant background pass - never a hard-coded TenantId.Local. Local here would be a genuine
        // cross-tenant hazard, not just a wrong answer: it is the single lookup EVERY down-channel command
        // routes through, so one tenant's sweep resolving Local could address a Director it does not own.
        // No scope on hosted is a DENY: send nothing, exactly as an unconnected Director behaves.
        if (_tenantPass.Current is not { } commandTenant)
        {
            // Say plainly that this is OUR bug and that the command was destroyed, not delayed. The deny is a
            // safety net, not a normal path: a request and a tunnel connection both arrive already scoped, so
            // the only way here is a background loop that failed to enter a tenant scope - and the cost is a
            // command silently dropped on every tick, forever, with nothing downstream able to tell that from
            // an offline Director. Name the fix in the line, so whoever greps it does not have to rediscover it.
            FileLog.Write($"[GatewayHost] SendCommandAsync: GATEWAY BUG - command DROPPED, no tenant scope in effect. " +
                          $"director={directorId}, verb={command.Verb}. A background loop must run its work inside " +
                          $"ITenantPass.ForEachTenant/ForEachTenantAsync; this is NOT an unreachable Director.");
            return null;
        }
        var connectionId = PushedSessions.GetActiveConnectionId(commandTenant, directorId);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] SendCommandAsync: no active stream for director={directorId}, verb={command.Verb}");
            return null;
        }
        FileLog.Write($"[GatewayHost] SendCommandAsync: director={directorId}, verb={command.Verb}, sid={command.SessionId}, cmdId={command.CommandId}");
        return await hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", command, ct);
    }

    /// <summary>
    /// launcher-persistent-join: push a lifecycle command DOWN a machine's launcher stream and await its
    /// result over the SAME connection (SignalR client results), modeled exactly on <see cref="SendCommandAsync"/>.
    /// Returns null when that machine's launcher has no active stream connection (or the hub is unavailable),
    /// which the caller reports as the launcher being unreachable - the stream is the only path to a launcher,
    /// so there is nothing to fall back to. Any non-null result - success OR a typed failure - means the
    /// stream handled the command and its outcome is authoritative.
    /// </summary>
    public async Task<LauncherCommandResult?> SendLauncherCommandAsync(Core.Tenancy.TenantId tenant, string machineName, LauncherCommand command, CancellationToken ct = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        var connectionId = LauncherConnections.GetActiveConnectionId(tenant, machineName);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.LauncherHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.LauncherHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] SendLauncherCommandAsync: no active stream for machine={machineName}, verb={command.Verb}");
            return null;
        }
        FileLog.Write($"[GatewayHost] SendLauncherCommandAsync: machine={machineName}, verb={command.Verb}");
        return await hub.Clients.Client(connectionId).InvokeAsync<LauncherCommandResult>("Command", command, ct);
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        try { _cronTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] cron timer dispose error: {ex.Message}"); }
        _cronTimer = null;
        try { _activityRetentionTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] activity retention timer dispose error: {ex.Message}"); }
        try { _promptRetentionTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] prompt-log retention timer dispose error: {ex.Message}"); }
        _promptRetentionTimer = null;
        try { _suggestionSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] dictionary-suggestion timer dispose error: {ex.Message}"); }
        try { _sessionHistoryTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] session history timer dispose error: {ex.Message}"); }
        _sessionHistoryTimer = null;
        _activityRetentionTimer = null;
        try { _leaseSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] lease sweep timer dispose error: {ex.Message}"); }
        _leaseSweepTimer = null;
        try { _autoDismissTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] auto-dismiss timer dispose error: {ex.Message}"); }
        _autoDismissTimer = null;
        try { _displayStateSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] display-state sweep timer dispose error: {ex.Message}"); }
        _displayStateSweepTimer = null;
        try { _voiceTurnUploadSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-turn upload sweep timer dispose error: {ex.Message}"); }
        _voiceTurnUploadSweepTimer = null;
        try { _dictationTombstoneSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] dictation tombstone sweep timer dispose error: {ex.Message}"); }
        _dictationTombstoneSweepTimer = null;
        try { _sessionKeySweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] session key sweep timer dispose error: {ex.Message}"); }
        _sessionKeySweepTimer = null;


        // Issue #640: stop the background token refresh timer.
        try { _tokenRefresh?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] token refresh dispose error: {ex.Message}"); }
        _tokenRefresh = null;

        // Issue #857: stop the background device heartbeat timer.
        try { _deviceHeartbeat?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] device heartbeat dispose error: {ex.Message}"); }
        _deviceHeartbeat = null;

        // Web Push: stop the per-tenant needs-you sweep timer, then the notifier (which also disposes its
        // VAPID push sender). Subscriptions are already on disk (written through on every change), so stopping
        // loses nothing.
        try { _pushNotifierTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] push needs-you timer dispose error: {ex.Message}"); }
        _pushNotifierTimer = null;
        try { _pushNotifier?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] push notifier dispose error: {ex.Message}"); }
        try { _netDiagMonitor?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] netdiag monitor dispose error: {ex.Message}"); }
        try { _hostedAiSpendSweep?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] hosted-ai spend sweep dispose error: {ex.Message}"); }
        _pushNotifier = null;
        _pushNeedsYouSweep = null;

        // Turn-end watcher + voice sweep first (they drive the brain), then the brain itself - the
        // supervisor's dispose gracefully stops the hosted claude.exe (never leaked).
        try { _voiceSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep dispose error: {ex.Message}"); }
        _voiceSweepTimer = null;
        try { _turnLogRecorder?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] turn log dispose error: {ex.Message}"); }
        _turnLogRecorder = null;
        try { _turnLogSwitches?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] turn log switch dispose error: {ex.Message}"); }
        _turnLogSwitches = null;
        try { _turnLogRetentionTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] turn log retention dispose error: {ex.Message}"); }
        _turnLogRetentionTimer = null;
        try { _turnEndWatcher?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] watcher dispose error: {ex.Message}"); }
        // Issue #915: cancel any recovery wait in flight, so a Gateway shutdown does not leave a background
        // ladder holding a token and re-sending into a fleet this process no longer owns.
        try { _sessionSupervisor?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] session supervisor dispose error: {ex.Message}"); }
        try { _voiceModeAllSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-mode sweep timer dispose error: {ex.Message}"); }
        _turnEndWatcher = null;
        try { Brain.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] brain dispose error: {ex.Message}"); }

        // The database-backed device facade owns no context or plaintext cache, but it must stop being used
        // before the pooled database factory is disposed.
        try { Devices.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] device registry dispose error: {ex.Message}"); }

        // The EF data layer: dispose the pooled context factory and release the SQLite connections so a
        // restart (and a test) can reopen or delete the database file cleanly.
        try { _gatewayDb.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] gateway db dispose error: {ex.Message}"); }

        // Unsubscribe from registry events. We deliberately do NOT tear down the serve
        // mappings: the Directors are still alive and reachable, and a Gateway restart
        // re-asserts every mapping on Start().
        _serveProvisioner.Dispose();
        Registry.Dispose();
        Launchers.Dispose();

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
