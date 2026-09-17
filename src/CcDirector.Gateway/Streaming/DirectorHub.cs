using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.SignalR;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1176 (Phase 1a): the SignalR hub each Director dials OUT to. It receives the session state a
/// Director pushes UP - a full snapshot on connect/reconnect, per-session deltas, and removes - and
/// records it in the <see cref="PushedSessionStore"/> so the <c>/sessions</c> aggregation can serve that
/// Director from cache instead of pulling it.
///
/// Identity binding (review #9): the first message MUST be <see cref="Hello"/>, which binds this
/// connection to one Director id. Every later message uses that bound id - never a Director id re-sent by
/// the client - so a connection can only ever affect the Director it declared, and a message that arrives
/// before Hello is rejected. The transport itself is already authenticated by the host-wide token
/// middleware (a valid shared token or per-device key); binding a credential cryptographically to a
/// specific Director id is future work that rides the account/device epic.
///
/// The hub holds no state of its own; it is a thin adapter onto <see cref="PushedSessionStore"/> and
/// <see cref="DirectorRegistry"/>. SignalR constructs it per invocation via dependency injection (the
/// one place this codebase uses a container, because the framework requires it).
/// </summary>
public sealed class DirectorHub : Hub
{
    private const string DirectorIdItemKey = "cc.directorId";
    private const string TenantIdItemKey = "cc.tenantId";

    private readonly PushedSessionStore _store;
    private readonly DirectorRegistry _registry;
    // The statistics aggregator, or null when statistics are unavailable. The hub is constructed from the
    // HANDLE rather than the aggregator so a statistics store that could not be opened cannot stop a
    // Director connecting - see Stats.InputStatsHandle.
    private readonly GatewayInputStatsAggregator? _inputStats;
    private readonly GatewayStreamRegistry _streamRegistry;
    private readonly Snooze.SnoozeLandingObserver? _snoozeLandings;
    private readonly Fleet.FleetRoleObserver? _fleetRoles;
    private readonly Fleet.FleetDisplayStateObserver? _fleetDisplayState;
    // Hosted Multi-Tenancy increment 1: resolves THIS connection's tenant from the authenticated device key
    // at Hello and enters that tenant's scope on every push (so the EF-writing observers stamp the right
    // tenant). Null for older callers/tests -> the self-host Local behavior, unchanged.
    private readonly HostedTenantBoundary? _tenantBoundary;

    private readonly DirectorConnectionRegistry? _connections;

    // The boundary is REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional tail so it
    // cannot sit in a defaulted position: constructing this hub without one must be a compile error, never a
    // silent default. In production SignalR resolves it from dependency injection (GatewayHost registers the
    // singleton); a self-host process registers a boundary built over the SingleTenantContext, which always
    // resolves Local. The FIELD stays nullable because a miswire is still expressible with a forced null, and
    // the runtime gate in ResolveConnectionTenant must hold even then.
    public DirectorHub(PushedSessionStore store, DirectorRegistry registry, Stats.InputStatsHandle inputStats,
        GatewayStreamRegistry streamRegistry, HostedTenantBoundary tenantBoundary,
        Snooze.SnoozeLandingObserver? snoozeLandings = null,
        Fleet.FleetRoleObserver? fleetRoles = null, Fleet.FleetDisplayStateObserver? fleetDisplayState = null,
        DirectorConnectionRegistry? connections = null,
        PushedRepositoryStore? repositoryStore = null, RepoHistoryStore? repoHistory = null,
        History.SessionHistoryRecorder? sessionHistory = null,
        Pairing.SessionKeyRegistry? sessionKeys = null,
        History.SessionTurnStore? sessionTurns = null,
        TurnPushCapabilityRegistry? turnPushCapabilities = null,
        Briefing.TurnEndWatcher? turnEnds = null,
        FleetManagerHomeCapabilityRegistry? fleetManagerHomeCapabilities = null)
    {
        _fleetManagerHomeCapabilities = fleetManagerHomeCapabilities;
        _turnEnds = turnEnds;
        _turnPushCapabilities = turnPushCapabilities;
        _sessionTurns = sessionTurns;
        _sessionKeys = sessionKeys;
        _store = store;
        _registry = registry;
        _inputStats = inputStats.Aggregator;
        _streamRegistry = streamRegistry;
        _snoozeLandings = snoozeLandings;
        _fleetRoles = fleetRoles;
        _fleetDisplayState = fleetDisplayState;
        _tenantBoundary = tenantBoundary;
        _connections = connections;
        _repositoryStore = repositoryStore;
        _repoHistory = repoHistory;
        _sessionHistory = sessionHistory;
    }

    private readonly PushedRepositoryStore? _repositoryStore;

    /// <summary>The turn-end watcher an accepted delta feeds before the display fold. Null (older callers, tests that
    /// do not exercise turn ends) leaves the watcher to its 15-second reconcile sweep alone.</summary>
    private readonly Briefing.TurnEndWatcher? _turnEnds;

    /// <summary>
    /// Remove-the-network-port phase 1b: the per-session credential registry. Null in tests and older
    /// callers, which makes <see cref="RegisterSessionKey"/> and <see cref="RevokeSessionKey"/> refuse
    /// loudly rather than pretend to work - a registration that silently vanished would leave a session
    /// holding a key the Gateway will never accept, which reads as an agent whose tools are broken.
    /// </summary>
    private readonly Pairing.SessionKeyRegistry? _sessionKeys;

    private readonly RepoHistoryStore? _repoHistory;
    // Issue #2194: the durable work-history recorder, fed from the same accepted pushes as the other
    // observers. Throttled internally (it is NOT a write per push) and never throws.
    private readonly History.SessionHistoryRecorder? _sessionHistory;
    /// <summary>The stored conversation (turn-push mission). Null only in tests that do not exercise it.</summary>
    private readonly History.SessionTurnStore? _sessionTurns;
    /// <summary>Which connected Directors send their conversations - learned here, from Hello.</summary>
    private readonly TurnPushCapabilityRegistry? _turnPushCapabilities;
    private readonly FleetManagerHomeCapabilityRegistry? _fleetManagerHomeCapabilities;

    /// <summary>
    /// A full repository/worktree snapshot from the bound Director (repositories mission, #510
    /// phase C). Snapshots only; tenant comes from the connection binding, never the payload.
    /// Accepted pushes also fold into the daily history (phase D) - rejected/stale ones never do.
    /// </summary>
    public void PushRepoSnapshot(long sequence, RepoStatusDto[] repositories)
    {
        var directorId = RequireBoundDirector();
        var set = repositories ?? Array.Empty<RepoStatusDto>();
        var accepted = _repositoryStore?.ApplySnapshot(RequireBoundTenant(), directorId, Context.ConnectionId,
            sequence, set) ?? false;
        if (accepted)
            _repoHistory?.ObserveSnapshot(RequireBoundTenant(), directorId, set);
        FileLog.Write($"[DirectorHub] PushRepoSnapshot: director={directorId} seq={sequence} repos={set.Length} accepted={accepted}");
    }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the Director streams a byte/frame stream UP under the
    /// stream id the Gateway minted when it opened the stream over the tunnel. This is native client-to-server
    /// streaming (the Director is the SignalR client). The registry pumps the frames into the browser-facing
    /// sink with pull-then-forward backpressure, so a slow browser blocks the pull, which - with a small
    /// StreamBufferCapacity - blocks the Director's producer. One primitive serves both the live terminal and a
    /// finite file/screenshot read (keyed by the stream id). Requires the connection be bound first (Hello).
    ///
    /// Issue #1923: binding proves WHO is calling; it does not prove the caller owns THIS stream. The bound
    /// identity (tenant resolved from the authenticated device key at Hello, plus the bound Director id) is
    /// handed to the registry, which authorizes it against the owner recorded when the stream was opened and
    /// REFUSES a caller that does not match. Without that, any authenticated account that learned or guessed a
    /// live stream id could write frames into another account's terminal, claim the stream ahead of the real
    /// Director, or tear it down.
    /// </summary>
    public async Task StreamUp(string streamId, IAsyncEnumerable<DirectorStreamFrame> frames)
    {
        var directorId = RequireBoundDirector();
        var caller = new StreamOwner(RequireBoundTenant(), directorId);
        FileLog.Write($"[DirectorHub] StreamUp: director={directorId}, stream={streamId}, conn={Short(Context.ConnectionId)}");
        try
        {
            await _streamRegistry.ConsumeAsync(streamId, caller, frames, Context.ConnectionAborted);
        }
        catch (StreamOwnershipDeniedException ex)
        {
            // Surface the refusal to the calling Director as a hub error (HubException is the one exception
            // type SignalR relays verbatim). A refusal is never swallowed: an operator reading either side's
            // log must be able to tell a cross-account injection attempt from an ordinary closed-stream race.
            FileLog.Write($"[DirectorHub] StreamUp REFUSED: director={directorId}, stream={streamId}, conn={Short(Context.ConnectionId)}");
            throw new HubException(ex.Message);
        }
    }

    /// <summary>
    /// Bind this connection to a Director. Must be the first message; aborts the connection on a bad id.
    ///
    /// RETURNS this Gateway's capabilities, so a Director learns at the moment of connection what the
    /// Gateway it just reached can do - see <see cref="GatewayCapabilities"/> for why a null answer
    /// (an older Gateway, whose Hello returns nothing) is the useful case rather than a problem.
    /// Returning a value changes nothing for an older Director: it invokes Hello non-generically and
    /// discards the result.
    ///
    /// The rejection paths return null. The connection is being aborted, so nothing is waiting for an
    /// answer on it; a capabilities object there would describe a Gateway the caller is not bound to.
    /// </summary>
    public GatewayCapabilities? Hello(DirectorStreamHello hello)
    {
        if (hello is null || string.IsNullOrWhiteSpace(hello.DirectorId))
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (missing directorId): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return null;
        }

        var directorId = hello.DirectorId.Trim();
        var alreadyBound = BoundDirectorId();
        if (alreadyBound is not null && !string.Equals(alreadyBound, directorId, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (conn already bound to {alreadyBound}, cannot re-claim {directorId}): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return null;
        }

        // Hosted Multi-Tenancy increment 1: resolve the tenant this connection belongs to ONCE, here at bind
        // time, from the AUTHENTICATED device key the auth layer stashed on the negotiate request - NEVER from
        // the Hello payload. On self-host (or no boundary) this is Local, unchanged. On hosted a device key
        // with no bound tenant is a DENY: abort rather than bind a wrong or defaulted tenant (deny-by-default).
        // THIS resolution is the isolation line - reverting it to a fixed tenant makes two accounts share one.
        var resolved = ResolveConnectionTenant();
        if (resolved is not { } tenant)
        {
            FileLog.Write($"[DirectorHub] Hello REJECTED (hosted: the authenticated device key resolves to no tenant): conn={Short(Context.ConnectionId)}");
            Context.Abort();
            return null;
        }
        Context.Items[DirectorIdItemKey] = directorId;
        Context.Items[TenantIdItemKey] = tenant;
        _store.RegisterConnection(tenant, directorId, Context.ConnectionId);
        // The repository store follows the same ownership discipline: only this - the current -
        // connection may push repository snapshots from now on.
        _repositoryStore?.RegisterConnection(tenant, directorId, Context.ConnectionId);
        // MTR-15 cancellation cutoff: index this live tunnel by tenant with a server-side abort, so a revoked
        // tenant's connection is severed the moment the sweep (or a request re-read) finds it NotEntitled. The
        // durable device tombstone denies NEW auth; this ends the tunnel already up. Cleared on disconnect.
        var abortContext = Context;
        _connections?.Register(tenant, Context.ConnectionId, () => abortContext.Abort());
        // Gateway Cleanup mission (tunnel-only): the stream IS the registration now (HTTP register is gone).
        // Register this Director from the Hello identity so registry.Get(tenant, id) - the gate on create-session
        // and the other director-level routes - resolves it. Source="stream", no dialable endpoint.
        // Issue #1847: register it UNDER THE RESOLVED TENANT. The tenant is half of the registry key, so this
        // Hello can only create or refresh THIS account's own entry - naming another account's Director is
        // structurally impossible, however the client chose hello.DirectorId. It also makes the entry visible
        // to this account's /directors list and to no other. The tenant is the one resolved above from the
        // authenticated device key - never the Hello payload, which the client writes.
        // The credential this Hello authenticated with is recorded against the id, so a route can tell this
        // Director's own calls from another key of the same account (the Message Load mission, inspection 11).
        _registry.RegisterFromStream(directorId, hello.MachineName, hello.User, hello.Version, hello.Pid, hello.StartedAt, tenant,
            hello.DisplayName, AuthMiddleware.RegisteringCredential(Context.GetHttpContext()));
        // What this build can do, kept against the CONNECTION: the same machine can come back on an older
        // or a newer Director, and a stale answer here would put the wrong sentence on an empty Chat screen.
        _turnPushCapabilities?.Record(tenant, directorId, hello.PushesTurns, hello.ChecksIdleBeforeTyping);
        _fleetManagerHomeCapabilities?.Record(tenant, directorId, hello.CreatesFleetManagerHome, hello.ChangesOwnerIfExpected);
        FileLog.Write($"[DirectorHub] Hello: director={directorId} bound to conn={Short(Context.ConnectionId)} (version={hello.Version}, machine={hello.MachineName})");
        return CapabilitiesFor(tenant, directorId);
    }

    /// <summary>
    /// The shared capability record plus THIS Director's turn watermarks (turn-push mission): what the
    /// Gateway already holds of each of its sessions' conversations, so a reconnecting Director resends
    /// only what is missing. Read inside the bound tenant's scope, because the store answers through the
    /// tenant query filter.
    /// </summary>
    private GatewayCapabilities CapabilitiesFor(TenantId tenant, string directorId)
    {
        var shared = Capabilities;
        var answer = new GatewayCapabilities { Version = shared.Version, Commit = shared.Commit, HubMethods = shared.HubMethods };
        if (_sessionTurns is null) return answer;
        // BEST EFFORT, and never allowed to throw. Hello is the first thing a Director does on every dial,
        // and the Director treats a failed Hello as a failed RESEED - so a database that is slow, not open
        // yet (this Gateway binds its port BEFORE the database is connected), or simply unhappy would stop
        // the Director pushing its session snapshot at all, and its whole fleet would go missing from every
        // screen. The watermarks are an optimisation: without them the Director pushes each conversation
        // from the start, which the store is idempotent about. Losing them costs a little bandwidth once;
        // losing the reseed costs the roster.
        try
        {
            using var tenantScope = EnterBoundTenantScope();
            answer.TurnWatermarks = _sessionTurns.WatermarksFor(directorId).ToList();
            answer.TurnWatermarksKnown = true;
            if (answer.TurnWatermarks.Count > 0)
                FileLog.Write($"[DirectorHub] Hello: handing director={directorId} {answer.TurnWatermarks.Count} turn watermark(s) (tenant {tenant.ToLogString()})");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorHub] Hello: could not read turn watermarks for director={directorId} (tenant {tenant.ToLogString()}): {ex.Message}. "
                        + "Answering with none - the Director will push each conversation from the start, which the store is idempotent about. The reseed is NOT affected.");
            answer.TurnWatermarks = new List<TurnWatermark>();
            answer.TurnWatermarksKnown = false;   // a silence, not an answer - the Director keeps what it has
        }
        return answer;
    }

    /// <summary>
    /// The turn-push mission's one write: a Director pushes a contiguous run of one session's conversation
    /// messages, and the Gateway stores them (<see cref="History.SessionTurnStore.Append"/>) and answers the
    /// watermark the Director should continue from. From here Chat, the transcript view, and the wingman
    /// read the stored rows; the Gateway never asks the Director to re-read the transcript.
    ///
    /// The <paramref name="sequence"/> is the Director's push sequence, carried for the log and for
    /// symmetry with <see cref="PushDelta"/>; ordering inside the conversation is the batch's own ordinals,
    /// and the store is idempotent, so a batch that arrives twice or late is harmless - a superseded
    /// connection pushing the same rows again stores nothing new and cannot move the session backwards
    /// (the store switches generation only to a strictly later source).
    ///
    /// Null means REFUSED: the batch disagreed with itself (a Director bug, logged with the reason), or this
    /// Gateway has no store. The Director stops that run and logs; nothing was written.
    /// </summary>
    public TurnWatermark? PushTurns(long sequence, TurnPushBatch batch)
    {
        var directorId = RequireBoundDirector();
        if (_sessionTurns is null)
        {
            FileLog.Write($"[DirectorHub] PushTurns ignored (this Gateway has no turn store): director={directorId}");
            return null;
        }
        if (batch is null || string.IsNullOrEmpty(batch.SessionId))
        {
            FileLog.Write($"[DirectorHub] PushTurns ignored (no session id): director={directorId}, seq={sequence}");
            return null;
        }
        using var tenantScope = EnterBoundTenantScope();
        try
        {
            return _sessionTurns.Append(directorId, batch, DateTime.UtcNow);
        }
        catch (ArgumentException ex)
        {
            FileLog.Write($"[DirectorHub] PushTurns REFUSED a malformed batch: director={directorId} session={batch.SessionId} seq={sequence}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// This Gateway's capabilities, computed ONCE from the hub itself.
    ///
    /// The method list is REFLECTED, never hand-maintained. A hand-written list is a second statement
    /// of the same fact, and the two drift the moment someone adds a hub method and does not think to
    /// update it - at which point the Director is told a method is missing that is right there, or
    /// worse, told one exists that does not. Reflection cannot be wrong about what this class exposes.
    /// </summary>
    private static readonly GatewayCapabilities Capabilities = BuildCapabilities();

    private static GatewayCapabilities BuildCapabilities()
    {
        // Public instance methods declared on the hub ARE the callable surface; anything inherited
        // from Hub itself (Dispose, ToString, and friends) is not something a Director invokes.
        //
        // The OVERRIDES have to go too, and they are the non-obvious part: OnConnectedAsync and
        // OnDisconnectedAsync are declared right here, so DeclaredOnly keeps them - but they are
        // lifecycle callbacks the server calls on itself, and a Director can no more invoke them than
        // it can invoke Dispose. Detected by asking whether the method's base definition still points
        // at this type: an override's does not. That covers any future override without naming it.
        var methods = typeof(DirectorHub)
            .GetMethods(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(DirectorHub))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new GatewayCapabilities
        {
            Version = Core.Utilities.AppVersion.Semver,
            // The same source /healthz reads, so the commit a Director is told over the tunnel and the
            // commit a deploy verifies over HTTP are the same string by construction rather than by
            // coincidence.
            Commit = Environment.GetEnvironmentVariable("COCKPIT_COMMIT") ?? "",
            HubMethods = methods,
        };
    }

    /// <summary>
    /// Register (or refresh) the Gateway credential for ONE of the bound Director's sessions
    /// (Remove-the-network-port mission, phase 1b).
    ///
    /// This is the whole reason an agent can call the Gateway as itself. The Director mints a key for a
    /// session, keeps the raw value on its own machine, and sends the HASH up this connection - so the key
    /// exists in exactly two places, the Director process and the one session's environment, and never on
    /// the wire.
    ///
    /// THE TENANT IS THE CONNECTION'S, NOT THE PAYLOAD'S. It is taken from
    /// <see cref="RequireBoundTenant"/> - the tenant this connection bound to at Hello, resolved there from
    /// the AUTHENTICATED device key. There is deliberately no tenant field in the registration message: a
    /// tenant a client can name is a tenant a client can choose, and choosing one would mint a working
    /// credential inside somebody else's account.
    ///
    /// Requires the connection to be bound first, exactly like every other message on this hub. A hub method
    /// is a boundary, so this catches: a registry failure must surface to the calling Director as a hub
    /// error it can log and retry on the next reseed, never as a faulted connection that drops the tunnel.
    /// </summary>
    public void RegisterSessionKey(SessionKeyRegistration registration)
    {
        var directorId = RequireBoundDirector();
        var tenant = RequireBoundTenant();

        if (registration is null
            || string.IsNullOrWhiteSpace(registration.SessionId)
            || string.IsNullOrWhiteSpace(registration.KeyHash))
        {
            FileLog.Write($"[DirectorHub] RegisterSessionKey REJECTED (incomplete registration): director={directorId}");
            throw new HubException("a session key registration needs a session id and a key hash");
        }

        if (_sessionKeys is null)
        {
            FileLog.Write($"[DirectorHub] RegisterSessionKey REFUSED (this Gateway has no session key registry): director={directorId}, session={registration.SessionId}");
            throw new HubException("this Gateway has no session key registry");
        }

        // THE TENANT IS VALIDATED HERE, like the session id and the key hash above it.
        //
        // It was not, and the registry's own first guard - `if (!tenant.IsValid) throw new
        // ArgumentException(...)` - sits ABOVE that method's try block, so an invalid tenant escaped
        // untyped. Every other refusal on this method is a HubException carrying a sentence; that one
        // reached the Director as SignalR's "An unexpected error occurred invoking 'RegisterSessionKey'",
        // which names nothing.
        if (!tenant.IsValid)
        {
            FileLog.Write($"[DirectorHub] RegisterSessionKey REJECTED (the connection's bound tenant is not valid): director={directorId}, session={registration.SessionId}");
            throw new HubException("the connection's bound tenant is not valid");
        }

        bool registered;
        try
        {
            registered = _sessionKeys.Register(
                tenant, directorId, registration.SessionId, registration.KeyHash, registration.ExpiresAtUtc);
        }
        catch (Exception ex)
        {
            // A HUB METHOD IS A BOUNDARY, AND THIS ONE SAID SO WITHOUT DOING IT.
            //
            // The summary above already promises that "a registry failure must surface to the calling
            // Director as a hub error it can log and retry ... never as a faulted connection". Only the
            // registry's INTERNAL failures were wrapped, by its own filtered catch; anything that filter
            // did not match - every ArgumentException guard above its try among them - propagated raw.
            //
            // The cost was not the throw, it was the SILENCE about it. On 2026-09-14 this produced 2,310
            // identical untyped errors on one machine, refusing every agent session key on the fleet, and
            // not one of them said which argument was wrong. The Gateway knew; the Director could not be
            // told. Rethrown as a HubException, the reason travels.
            FileLog.Write($"[DirectorHub] RegisterSessionKey FAILED UNEXPECTEDLY: director={directorId}, session={registration.SessionId}, {ex.GetType().Name}: {ex.Message}");
            throw new HubException($"the session key for {registration.SessionId} could not be registered: {ex.GetType().Name}: {ex.Message}");
        }

        if (!registered)
        {
            FileLog.Write($"[DirectorHub] RegisterSessionKey REFUSED: director={directorId}, session={registration.SessionId}");
            throw new HubException($"the session key for {registration.SessionId} was not registered");
        }
    }

    /// <summary>
    /// End one session's Gateway credential (Remove-the-network-port mission, phase 1b) - sent when the
    /// Director reaps the session. Scoped to the connection's bound tenant, so a Director can only ever end
    /// its own account's keys.
    ///
    /// Revoking a key that is already gone is NOT an error and does not throw: the reap path and the expiry
    /// sweep can both reach the same key, and a shutdown that revokes twice must not be reported as a
    /// failure the Director then retries forever.
    /// </summary>
    public void RevokeSessionKey(string sessionId)
    {
        var directorId = RequireBoundDirector();
        var tenant = RequireBoundTenant();

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            FileLog.Write($"[DirectorHub] RevokeSessionKey REJECTED (no session id): director={directorId}");
            throw new HubException("a session key revocation needs a session id");
        }

        if (_sessionKeys is null)
        {
            FileLog.Write($"[DirectorHub] RevokeSessionKey REFUSED (this Gateway has no session key registry): director={directorId}, session={sessionId}");
            throw new HubException("this Gateway has no session key registry");
        }

        var revoked = _sessionKeys.Revoke(tenant, sessionId, Pairing.SessionKeyRegistry.ReasonSessionReaped);
        FileLog.Write($"[DirectorHub] RevokeSessionKey: director={directorId}, session={sessionId}, revoked={revoked}");
    }

    /// <summary>A full snapshot: replaces the bound Director's session set (pruning anything absent).</summary>
    public void PushSnapshot(long sequence, SessionDto[] sessions)
    {
        // Load-test Stage 0 (issue #1173): count and time every push handler - the store apply AND the
        // synchronous fold observers below run inline on this hub invocation, so this duration is the
        // real ingress cost. The in-flight gauge is the push-pressure number the plan requires.
        var pushStart = Diagnostics.LoadTestMetrics.HubPushStarting();
        try
        {
            PushSnapshotCore(sequence, sessions);
        }
        finally
        {
            Diagnostics.LoadTestMetrics.HubPushFinished(pushStart);
        }
    }

    private void PushSnapshotCore(long sequence, SessionDto[] sessions)
    {
        var directorId = RequireBoundDirector();
        // Hosted Multi-Tenancy increment 1: run the whole handler in the bound tenant's scope, so the
        // EF-writing observers below (snooze landings, spend) stamp and filter by this connection's tenant.
        using var tenantScope = EnterBoundTenantScope();
        var set = sessions ?? Array.Empty<SessionDto>();
        var accepted = _store.ApplySnapshot(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, set);
        // DevThrottle Stats: fold each session's input tally into the always-available aggregate, under this
        // connection's bound tenant (MTR-08) so one account's tallies never coalesce with another's.
        //
        // CONTAINED (failure review M2), and this is the call site where containment matters most: the store
        // mutation ABOVE has already committed by the time the fold runs, so a statistics failure escaping
        // here would fail the invocation AFTER part of the operation had succeeded - the Director would be
        // told its push failed when its sessions had in fact landed. Contained, never swallowed.
        if (_inputStats is not null)
        {
            var boundTenant = RequireBoundTenant();
            Stats.StatsObservation.Contain(_inputStats.Health, "DirectorHub.PushSnapshot",
                () => _inputStats.ObserveSnapshot(set, tenant: boundTenant));
        }
        // A push the store REJECTED (from a superseded connection, or a stale sequence) is NOT authoritative,
        // so it must not drive the snooze observer - whose edges MUTATE the authoritative registry
        // (ClearIfArmed deletes an armed snooze, Land converts a deferral). A rejected stale Working push
        // could otherwise delete a snooze the current connection owns, and a rejected settled push could land
        // a deferral while the authoritative session is still working. The roles/display observers below read
        // the STORE - unchanged by a rejected push - so they are self-correcting and need no gate.
        // Defect 20: a deferred snooze whose hold landed while this Director was disconnected arrives in
        // the reconnect snapshot, not as a delta - so the snapshot must be watched too, or the landing is
        // missed until the sweep's backstop notices.
        if (accepted)
            _snoozeLandings?.ObserveSnapshot(set);
        // Defect 5: a reconnecting Director's whole roster can change roles across the FLEET (its sessions
        // re-enter the liveness set, so their controllers' and workers' roles move with them). Re-resolve
        // and stamp down whatever changed, or the desktop keeps folding a role from before the reconnect.
        _fleetRoles?.ObserveSnapshot(set);
        // The fold seam: a reconnecting Director's whole roster can change any session's folded display state
        // (its own, and others' across the fleet). Re-fold and stamp down whatever changed, so the desktop
        // rail renders the Gateway's answer rather than one from before the reconnect.
        _fleetDisplayState?.ObserveSnapshot(set);
        // Issue #2194: fold the accepted roster into durable work history. Gated on acceptance like the
        // snooze observer - a rejected stale snapshot must not close rows the current connection owns.
        // The snapshot is authoritative, so this also reconciles sessions removed while the tunnel was
        // down (the Director's per-session remove no-ops when disconnected).
        if (accepted)
            _sessionHistory?.ObserveSnapshot(RequireBoundTenant(), directorId, set);
    }

    /// <summary>A single-session delta: upserts one session for the bound Director.</summary>
    public void PushDelta(long sequence, SessionDto session)
    {
        // Load-test Stage 0 (issue #1173): same count-and-time as PushSnapshot above.
        var pushStart = Diagnostics.LoadTestMetrics.HubPushStarting();
        try
        {
            PushDeltaCore(sequence, session);
        }
        finally
        {
            Diagnostics.LoadTestMetrics.HubPushFinished(pushStart);
        }
    }

    private void PushDeltaCore(long sequence, SessionDto session)
    {
        var directorId = RequireBoundDirector();
        if (session is null || string.IsNullOrEmpty(session.SessionId))
        {
            FileLog.Write($"[DirectorHub] PushDelta ignored (no session id): director={directorId}, conn={Short(Context.ConnectionId)}");
            return;
        }
        // Hosted Multi-Tenancy increment 1: run the handler in the bound tenant's scope (the landing seam
        // below writes the snooze/spend EF stores, which must stamp and filter by this connection's tenant).
        using var tenantScope = EnterBoundTenantScope();
        var accepted = _store.ApplyDelta(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, session);
        // DevThrottle Stats: fold this session's tally into the always-available aggregate, under this
        // connection's bound tenant (MTR-08). Contained for the same reason as PushSnapshot above - the
        // delta has already been applied to the store by this line.
        if (_inputStats is not null)
        {
            var boundTenant = RequireBoundTenant();
            Stats.StatsObservation.Contain(_inputStats.Health, "DirectorHub.PushDelta",
                () => _inputStats.Observe(session, tenant: boundTenant));
        }
        // A push the store REJECTED (superseded connection, or a stale sequence) is NOT authoritative, so it
        // must not drive the snooze observer, whose edges MUTATE the authoritative registry (ClearIfArmed
        // deletes an armed snooze, Land converts a deferral). See the note in PushSnapshot. The roles/display
        // observers read the store and are self-correcting.
        // Defect 20: THE landing seam. A settled activity from the Director arrives here within milliseconds
        // of the turn ending, which is the exact moment a deferred snooze's clock must start.
        if (accepted)
            _snoozeLandings?.Observe(session);
        // Defect 5: THE ROLE SEAM. This session's own facts may have changed its role (it gained a
        // controller), and its arrival may change ANOTHER session's role on ANOTHER Director (this session
        // just exited, so the worker it controlled is no longer a Worker and its red must surface). Either
        // way the resolution is fleet-wide, and the changed roles are stamped back down so every desktop
        // folds the same answer the phone does.
        _fleetRoles?.Observe(session);
        // THE TURN-END SEAM (the Wingman-on-every-turn mission, owner ruling 2026-09-15: no red frame before the Wingman
        // reads). An ACCEPTED delta reaches the turn-end watcher HERE, immediately before the fold below pushes its
        // colour, so a stop that will be judged is stamped "reading" by the verdict seat before the first fold of that
        // stop, and the first colour pushed is yellow rather than the detector's red. The watcher's 15-second sweep
        // stays as the reconcile; its transition memory makes that replay of a stop already observed here a no-op. A
        // rejected push is not authoritative and does not reach it.
        if (accepted && _turnEnds is not null && !string.IsNullOrEmpty(session.ActivityState))
        {
            try
            {
                _turnEnds.Observe(RequireBoundTenant(), session.SessionId, session.ActivityState, directorId);
            }
            catch (Exception ex)
            {
                // A turn-end consumer's fault must not take the display fold below down with it; the fold still runs,
                // and the fault is written where it can be found.
                FileLog.Write($"[DirectorHub] PushDelta turn-end observe FAILED: director={directorId}, sid={session.SessionId}: {ex.GetType().FullName}: {ex.Message}");
            }
        }
        // THE FOLD SEAM. This delta changed this session's raw facts (activity, hold, dictation) and may
        // change another session's fold across the fleet. Re-fold and stamp the changed answers down, so the
        // desktop rail shows the Gateway's colour/label/triage within milliseconds of the change.
        _fleetDisplayState?.Observe(session);
        // Issue #2194: durable work history rides the same accepted delta. Throttled inside; a
        // rejected push from a superseded connection must not touch the record.
        if (accepted)
            _sessionHistory?.Observe(RequireBoundTenant(), directorId, session);
    }

    /// <summary>A remove/tombstone: drops one session from the bound Director's set.</summary>
    public void RemoveSession(long sequence, string sessionId)
    {
        var directorId = RequireBoundDirector();
        if (string.IsNullOrEmpty(sessionId))
        {
            FileLog.Write($"[DirectorHub] RemoveSession ignored (no session id): director={directorId}, conn={Short(Context.ConnectionId)}");
            return;
        }
        // Hosted Multi-Tenancy increment 1: scope the handler to the bound tenant (the removal re-folds and
        // can touch tenant-scoped state through the observers below).
        using var tenantScope = EnterBoundTenantScope();
        var accepted = _store.ApplyRemove(RequireBoundTenant(), directorId, Context.ConnectionId, sequence, sessionId);
        // DevThrottle Stats: its contribution stays in the totals; drop only its high-water entry, scoped to
        // this connection's bound tenant (MTR-08) so it cannot drop another tenant's same-id high-water.
        //
        // CONTAINED, and this call is NOT the in-memory tidy-up its name suggests: Forget clears the mirror
        // and then goes to the database writer to delete the stored high-water rows. So it fails for exactly
        // the reasons the other two observations fail, and it sits after ApplyRemove has already committed
        // the authoritative removal - the partial-success shape the snapshot and delta catches were added to
        // remove. Containing two of the three hub paths and leaving this one was the hole an inspection
        // found; the claim was about the hub, not about two of its methods.
        //
        // GATED ON ACCEPTANCE, which it was not. A rejected remove is one this store has just ruled is NOT
        // about the roster it holds - a superseded connection, or a sequence it has already passed - so the
        // session is still there and still counting. Forgetting its high-water anyway deletes the baseline
        // the counting is measured from, and the next fold then inserts a fresh row whose previous value is
        // zero and appends the session's ENTIRE standing tally to the ledger as new activity. That error is
        // permanent: nothing rewrites an appended delta. Two lines below, the work-history observer was
        // already gated on exactly this flag for exactly this reason. Measured on the owner's 2026-W35 week
        // in docs/missions/clean-up-your-throttle-2026-09-05/task3-defect-two-mechanism.md, where 240
        // buckets show a whole cumulative re-added with no reset recorded against them.
        //
        // THIS CLOSES THE HOLE, NOT THE DEFECT. On the day of the hosted log that was examined every
        // dropped push was a snapshot and none was a remove, so this path is not what fired there. The
        // wider fault - that an ABSENT high-water row is read as "this session has never been counted" at
        // all - is unchanged, and is held for the ruling on where Your Throttle's figure comes from.
        if (_inputStats is not null && accepted)
        {
            var boundTenant = RequireBoundTenant();
            Stats.StatsObservation.Contain(_inputStats.Health, "DirectorHub.RemoveSession",
                () => _inputStats.Forget(sessionId, boundTenant));
        }
        // A DEPARTURE RE-ROLES THE SURVIVORS, exactly as an arrival does: a controller leaving should stop
        // its workers being Workers. Must run AFTER ApplyRemove so the sweep resolves the fleet that now
        // exists rather than the one that just left.
        _fleetRoles?.ObserveRemoval(sessionId);
        // A departure re-folds the survivors too (a controller leaving un-suppresses its workers' red).
        _fleetDisplayState?.ObserveRemoval(sessionId);
        // Issue #2194: a per-session remove from the CURRENT connection is the session's farewell -
        // the recorder rules "finished" or "closed" from the last pushed facts and stamps the row.
        // Gated on acceptance, unlike the self-correcting observers above: an ending stamped from a
        // superseded connection's stale remove would stick, because only "interrupted" reopens.
        if (accepted)
            _sessionHistory?.ObserveRemoval(RequireBoundTenant(), directorId, sessionId);
        // The Fleet Manager's events (step 4): a session removed without an exit is a death to its Fleet Manager.
        // Gated on acceptance for the same reason: a stale remove from a superseded connection removed nothing.
        if (accepted)
            _turnEnds?.ObserveRemoval(RequireBoundTenant(), sessionId, directorId);
    }

    /// <summary>
    /// The Director's clean-shutdown farewell (issue #2194, the #1862 ending design). Sent by a
    /// stopping Director just before it closes the tunnel, AFTER its per-session removes have flowed:
    /// every still-open work-history row of this Director is ruled "director-stopped". A Director that
    /// never says goodbye is caught by the history sweep's silence rule instead ("interrupted") - the
    /// two rulings are exactly what tells a clean stop from a power cut. Older Directors simply never
    /// call this; nothing here is required for the stream to function.
    ///
    /// THE SAME GOODBYE ALSO RETIRES THE REGISTRATION. It used to tell only the history recorder, so
    /// discovery went on expecting a Director that had politely announced it was leaving - and reported
    /// it "unreachable" for the full day until the eviction horizon swept it. One signal, both readers:
    /// history rules the work rows, the registry marks the entry not-running. Best-effort, exactly like
    /// the history call beside it: a farewell that cannot be recorded must never fail the shutdown.
    /// </summary>
    public void DirectorStopping()
    {
        var directorId = RequireBoundDirector();
        using var tenantScope = EnterBoundTenantScope();
        FileLog.Write($"[DirectorHub] DirectorStopping: director={directorId} conn={Short(Context.ConnectionId)}");
        _sessionHistory?.ObserveDirectorStopping(RequireBoundTenant(), directorId);
        // ONLY THE CURRENTLY ACTIVE CONNECTION MAY RETIRE THE REGISTRATION - the same ownership gate the
        // snapshot, delta and remove paths apply, for a sharper reason. A farewell is a statement about a
        // PROCESS. A delayed one arriving on a connection a reconnect has already superseded describes a
        // process that is no longer the registered one, and stamping it would mark the LIVE Director stopped;
        // when that Director later died for real, its crash would be reported as an orderly shutdown for the
        // whole eviction horizon - silencing exactly the fault this state exists to keep visible.
        //
        // The history ruling above is deliberately NOT gated the same way: it closes rows belonging to the
        // connection that is saying goodbye, and its own throttle already tolerates a late one.
        if (_store.IsActiveConnection(RequireBoundTenant(), directorId, Context.ConnectionId))
            _registry.MarkStopped(RequireBoundTenant(), directorId);
        else
            FileLog.Write($"[DirectorHub] DirectorStopping IGNORED for the registry (not the active connection): director={directorId} conn={Short(Context.ConnectionId)}");
    }

    public override Task OnConnectedAsync()
    {
        // Load-test Stage 0 (issue #1173): the held-socket count, one of the numbers the plan requires.
        Diagnostics.LoadTestMetrics.HubConnectionOpened();
        FileLog.Write($"[DirectorHub] connected: conn={Short(Context.ConnectionId)}");
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Diagnostics.LoadTestMetrics.HubConnectionClosed();
        var directorId = BoundDirectorId();
        var tenant = BoundTenant();
        if (directorId is not null && tenant is { } t)
        {
            // Clear the active connection so aggregation falls back to the cached roster. Gateway Cleanup
            // mission (tunnel-only): do NOT drop the registry entry here - a dead Director's cached roster must
            // survive the sweep window (so a Gateway-owned snooze still fires it back to "needs you" from the
            // cache) and a brief reconnect blip must not flap the roster. The stale sweeper ages out a Director
            // that stops refreshing LastSeen (HttpHeartbeatTimeout); a reconnect re-Hellos and refreshes it.
            // The tenant is the one bound at Hello (Hello sets both, so a bound director always has a tenant).
            _store.UnregisterConnection(t, directorId, Context.ConnectionId);
            _repositoryStore?.UnregisterConnection(t, directorId, Context.ConnectionId);
        }
        // MTR-15: drop this connection's abort entry (it is gone now). Keyed by connection id, so it is cleared
        // whether or not a tenant was bound.
        _connections?.Unregister(Context.ConnectionId);
        FileLog.Write($"[DirectorHub] disconnected: conn={Short(Context.ConnectionId)}, director={directorId ?? "(unbound)"} ({exception?.Message ?? "clean"})");
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Resolve this connection's tenant from the AUTHENTICATED device key the auth layer stashed on the
    /// negotiate request, via the hosted tenant boundary - NEVER from the Hello payload. Self-host (or no
    /// boundary) resolves to Local, unchanged. Hosted resolves to the key's bound tenant, or null (a DENY)
    /// when the key has no binding. This is the isolation resolution the whole design rests on.
    /// </summary>
    private TenantId? ResolveConnectionTenant()
    {
        // GATED ON GatewayHostedMode.IsHosted ITSELF, never on whether a boundary was wired (finding I1-01,
        // the same shape GatewayEndpoints.ResolveReadTenant carries). Deciding on the field fails OPEN: a
        // hosted process whose hub was constructed without a boundary would resolve TenantId.Local and bind
        // the Director's whole push stream into the shared partition. On hosted, a missing or
        // non-hosted-wired boundary resolves to null, and null is a REFUSAL - Hello aborts the connection.
        // The second defence is the required non-nullable constructor parameter.
        if (!GatewayHostedMode.IsHosted)
        {
            if (_tenantBoundary is null)
                return TenantId.Local;
            var selfHostContext = Context.GetHttpContext();
            return selfHostContext is null ? null : _tenantBoundary.ResolveRequestTenant(selfHostContext);
        }

        if (_tenantBoundary is null || !_tenantBoundary.IsHosted)
            return null;
        var httpContext = Context.GetHttpContext();
        return httpContext is null ? null : _tenantBoundary.ResolveRequestTenant(httpContext);
    }

    /// <summary>Enter the bound tenant's scope for the duration of a push handler, so the EF-writing observers
    /// stamp the right tenant. A no-op on self-host (Local is ambient) or when there is no boundary.</summary>
    private IDisposable EnterBoundTenantScope() =>
        _tenantBoundary is null ? NoScope.Instance : _tenantBoundary.EnterScope(RequireBoundTenant());

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private string? BoundDirectorId() =>
        Context.Items.TryGetValue(DirectorIdItemKey, out var value) && value is string id ? id : null;

    private string RequireBoundDirector() =>
        BoundDirectorId() ?? throw new HubException("Director stream not initialized: send Hello first.");

    private TenantId? BoundTenant() =>
        Context.Items.TryGetValue(TenantIdItemKey, out var value) && value is TenantId t ? t : null;

    private TenantId RequireBoundTenant() =>
        BoundTenant() ?? throw new HubException("Director stream not initialized: send Hello first.");

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
