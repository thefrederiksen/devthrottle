using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1176 (Phase 1a): the Gateway's in-memory cache of the session state each Director pushes UP
/// over its persistent stream, replacing the on-demand pull for stream-connected Directors. One entry
/// per Director (keyed by directorId), holding the sessions that Director last pushed.
///
/// Tenant partitioning (Stage 3b of the multi-tenant plan): entries are partitioned by
/// <see cref="TenantId"/> first, then by directorId. A Director belongs to exactly one tenant, and the
/// cross-tenant scans (<see cref="TryLocate"/>, <see cref="SnapshotFresh"/>) only ever walk one
/// tenant's partition, so a session id from one tenant can never resolve a Director in another. Today the
/// core is single-tenant: callers pass <see cref="TenantId.Local"/> and behavior is unchanged. When the
/// resolver lands, the tenant is the one resolved for the Director's connection (ingest) or the request
/// (reads); nothing in this store changes.
///
/// Correctness rules this store enforces (Phase 1 plan, section 4.4):
///  - CONNECTION GENERATION: each stream connection is identified by its SignalR connection id. A new
///    connection becomes the active connection for its Director and its first message is authoritative.
///    This is what lets a RESTARTED Director - which dials a brand-new connection - reseed even though a
///    prior connection had pushed higher sequence numbers (a plain persistent integer epoch would wrongly
///    reject the restarted Director's snapshot).
///  - SEQUENCE ORDERING: within one connection, messages carry a monotonic sequence; a message with a
///    sequence at or below the last applied one is dropped as stale/duplicate.
///  - ACTIVE-CONNECTION OWNERSHIP: snapshot/delta/remove are accepted only from the currently active
///    connection. A late disconnect from a superseded connection (a reconnect overlap) does NOT clear the
///    active connection or the cache.
///  - SNAPSHOT IS AUTHORITATIVE: a full snapshot replaces the Director's whole session set, pruning any
///    session not present in it.
///
/// Reads (<see cref="TryGetFresh"/>) return deep COPIES so the aggregation may stamp them without
/// contaminating the cache, and recompute relative time fields (idle seconds) from the absolute
/// LastActivityAt so a quiet session's idle clock keeps advancing between pushes.
///
/// Thread-safe: a <see cref="ConcurrentDictionary{TKey,TValue}"/> per tenant of per-Director entries,
/// each entry guarded by its own lock.
/// </summary>
/// <summary>
/// What is known about one Director's fleet, as ONE answer rather than a boolean and a list.
///
/// The list is the same shape - empty - for three of these, and only one of them means the Director is
/// running nothing. A caller that reduces this to true-or-false writes down the wrong one.
/// </summary>
public enum FleetObservation
{
    /// <summary>This Gateway has never heard of the Director.</summary>
    Unknown,

    /// <summary>It is known and is not streaming. Whatever it is running, nobody here can see it.</summary>
    NotConnected,

    /// <summary>Connected, and has not yet said what it is running - the seconds between dialling in and
    /// its first push. An empty list here means "not asked yet", never "nothing".</summary>
    ConnectedButSilent,

    /// <summary>Connected and it has said. The sessions are what it is running, and an EMPTY list is a
    /// real answer that may be recorded.</summary>
    Observed,
}

public sealed class PushedSessionStore
{
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, DirectorEntry>> _byTenant = new();
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    /// Serialises MEMBERSHIP changes - a Director entering the store (<see cref="RegisterConnection"/>) against
    /// a Director being dropped from it (<see cref="ForgetIfDisconnected"/>). It is NOT taken by the read paths.
    ///
    /// Inspection 2, finding 1. The eviction cascade used to check whether a Director was connected and then,
    /// as a SECOND operation, destroy its state. A check followed by an action is two operations however
    /// carefully they are written, so a reconnect lands between them and the live machine is destroyed anyway.
    /// No guard fixes that; only making it ONE operation does, which is what this gate is for.
    ///
    /// It has to cover <see cref="RegisterConnection"/>'s <c>GetOrAdd</c> and not merely the removal, and that
    /// is the subtle half: without it a reconnect can be handed - by GetOrAdd - the very entry that is about to
    /// be removed, and would then write its sessions into a detached object that no read can ever reach. That
    /// failure is worse than the one being fixed, because the Director looks connected, reports success, and is
    /// invisible to every reader.
    ///
    /// Lock ORDER is this gate first, then <c>entry.Gate</c>, in both paths. Never the other way round.
    ///
    /// REASONED BUT NOT PROVEN, and that is stated here rather than left to be assumed. Deleting this gate
    /// from <see cref="RegisterConnection"/> leaves every test in this repository GREEN - it was tried - so
    /// nothing currently pins it. A two-threaded proof was written and then DELETED rather than shipped:
    /// its assertion was "the reconnect had not completed while the gate was held", which also passes when
    /// the second thread was merely slow to arrive, so it would have reported success whether or not the
    /// gate existed. A timing test whose failure direction is "pass" is not a proof; it is a future false
    /// green. Proving mutual exclusion here needs a rendezvous that can distinguish BLOCKED from ABSENT,
    /// and that was not something to invent at the end of a long night.
    ///
    /// So: the argument above is the justification, and there is no test behind it. Anyone removing this
    /// gate will see a green suite, and the green will mean nothing.
    /// </summary>
    private readonly object _membershipGate = new();

    /// <summary>
    /// Create the store. <paramref name="utcNow"/> is a test seam for the staleness and idle-clock logic;
    /// production passes null and the store reads <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public PushedSessionStore(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The per-Director map for one tenant, created on first use. Fails loud on an invalid tenant.</summary>
    private ConcurrentDictionary<string, DirectorEntry> DirectorsFor(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        return _byTenant.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, DirectorEntry>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The tenants that currently have partitions in the store (Hosted Multi-Tenancy, session-serving). A
    /// background loop that must act across all tenants - the fold, the role/display sweeps - iterates THESE
    /// on the hosted Gateway (entering each tenant's scope in turn) instead of a single Local pass, and
    /// without a per-tick database scan. A snapshot of the live keys; a tenant that appears or disappears
    /// between calls is picked up on the next sweep, which is the same eventual-consistency the sweeps already
    /// rely on. On self-host this is just the single Local partition.
    /// </summary>
    public IReadOnlyCollection<TenantId> KnownTenants() => _byTenant.Keys.ToArray();

    /// <summary>
    /// The director ids that have a partition entry under <paramref name="tenant"/> (Hosted Multi-Tenancy,
    /// session-serving). A director enters a tenant's partition when it binds a stream connection under that
    /// tenant (<see cref="RegisterConnection"/>) and STAYS after it disconnects (the entry is retained so a
    /// reconnect keeps roster continuity). So this is exactly "the directors this tenant owns" - the set a
    /// tenant's roster read must scope its Director list to on the hosted Gateway, so a request never even
    /// NAMES another tenant's directors: not as sessions, and not as "unreachable" machineError / reachability
    /// rows (which would otherwise leak another account's director ids and machine names). On self-host the
    /// registry already IS the single Local tenant's directors, so the roster does not consult this.
    /// </summary>
    public IReadOnlyCollection<string> DirectorIdsFor(TenantId tenant) => DirectorsFor(tenant).Keys.ToArray();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for <paramref name="directorId"/>
    /// within <paramref name="tenant"/>. Existing cached sessions are kept (so a fast reconnect keeps roster
    /// continuity), but the sequence baseline resets so the new connection's first message - at any sequence -
    /// is authoritative. The last-received timestamp is deliberately NOT refreshed here: until the new
    /// connection actually pushes a snapshot, the cache is treated as stale so aggregation falls back to pull.
    /// </summary>
    public void RegisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        // MEMBERSHIP gate first, then the entry gate - see _membershipGate for why GetOrAdd itself must be
        // inside it and not merely the writes below. Never take these in the other order.
        lock (_membershipGate)
        {
        var entry = DirectorsFor(tenant).GetOrAdd(directorId, _ => new DirectorEntry());
        lock (entry.Gate)
        {
            if (!entry.ConnectionEpochs.TryGetValue(connectionId, out var epoch))
            {
                epoch = entry.NextEpoch++;
                entry.ConnectionEpochs[connectionId] = epoch;
            }

            if (string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                // Repeated Hello from the CURRENT connection - keep the baseline (do not let an older
                // sequence replay over newer data).
                return;
            }

            if (epoch < entry.CurrentEpoch)
            {
                // A SUPERSEDED connection re-sending Hello (its periodic reseed) must NOT reclaim
                // ownership (inspection): otherwise two overlapping connections alternate the
                // authoritative session roster, and a stale snapshot omitting a live session would be
                // served as authoritative to the destructive reaper.
                FileLog.Write($"[PushedSessionStore] RegisterConnection IGNORED (superseded conn epoch {epoch} < current {entry.CurrentEpoch}): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}");
                return;
            }

            entry.ActiveConnectionId = connectionId;
            entry.CurrentEpoch = epoch;
            entry.LastSequence = -1;
            // Treat the cache as STALE until THIS new connection pushes its own snapshot (inspection
            // round 5). The previous connection's sessions are kept for reconnect continuity, but if we
            // left the old ReceivedAtUtc in place TryGetFresh would serve the PRIOR connection's roster
            // as fresh under the new active connection - a stale set that could omit a live session the
            // new connection knows about, which the destructive reaper would then act on. Resetting it
            // makes the register-to-first-snapshot interval fail closed: reads fall back to pull.
            entry.ReceivedAtUtc = DateTime.MinValue;
        }
        FileLog.Write($"[PushedSessionStore] RegisterConnection: tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)} is now the active connection");
        }
    }

    /// <summary>
    /// Drop <paramref name="directorId"/> from the read model IF it currently holds no stream connection.
    /// Returns true when the entry was removed, false when the Director is live (or was already gone).
    ///
    /// THIS IS THE WHOLE OF EVICTION'S DESTRUCTIVE WORK, and the narrowness is the point (inspection 2,
    /// finding 1). The eviction cascade used to also release the Director's session numbers and delete its
    /// snoozes, each guarded by a separate connection check - and a check followed by an action is two
    /// operations, so a reconnect between them destroyed a live machine anyway. Rather than build a cleverer
    /// guard, those two steps were DELETED: eviction's job is to drop a long-dead machine from the READ
    /// MODEL, and nothing else.
    ///
    /// Here the check and the removal are ONE operation under <see cref="_membershipGate"/>, which
    /// <see cref="RegisterConnection"/> also takes, so a reconnect should either happen entirely before this
    /// call (and the entry survives, because a connection is active) or entirely after it (and re-creates
    /// the entry through GetOrAdd), leaving no in-between for it to land in.
    ///
    /// REASONED, NOT PROVEN - and the difference matters here more than anywhere else on this branch. That
    /// is an argument from reading the source, not a demonstrated property: no test exercises the
    /// interleaving, and deleting the gate leaves the entire suite green (see the gate's own declaration).
    /// Do not upgrade this sentence to a guarantee in any document that quotes it; a previous version of the
    /// phase record did exactly that.
    ///
    /// What the deletions cost, recorded here because a silent cost is worse than a named one - and stated
    /// at its real size, because the first version of this paragraph called both costs "bounded" and one of
    /// them is not:
    ///
    /// A machine that never returns keeps every session number it held marked in use, one per session it was
    /// running, and nothing reclaims them (Adopt only ever marks in use). And it keeps its snooze rows in the
    /// database FOREVER: the snooze prune clears a Director's rows when that Director ANSWERS, and a
    /// permanently retired machine is exactly the one that never answers, so no prune ever reaches them.
    /// Each retirement adds its own set, with no ceiling and nothing that removes them.
    ///
    /// Both are accepted anyway, and the reason is worth more than the tidiness: the alternative was a race
    /// that could free a LIVE machine's numbers and delete a live owner's snoozes, and a deleted snooze is
    /// irrecoverable - nothing anywhere can reconstruct the intention behind it. Dead rows can be cleaned up
    /// later by something that establishes the machine is gone first. A destroyed snooze cannot be anything
    /// later at all.
    /// </summary>
    public bool ForgetIfDisconnected(TenantId tenant, string directorId)
    {
        if (string.IsNullOrEmpty(directorId))
            return false;

        lock (_membershipGate)
        {
            var directors = DirectorsFor(tenant);
            if (!directors.TryGetValue(directorId, out var entry))
                return false;

            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is not null)
                {
                    FileLog.Write($"[PushedSessionStore] ForgetIfDisconnected DECLINED: tenant={tenant.ToLogString()}, director={directorId} holds a live stream - the read model keeps it");
                    return false;
                }
            }

            directors.TryRemove(directorId, out _);
            FileLog.Write($"[PushedSessionStore] ForgetIfDisconnected: tenant={tenant.ToLogString()}, director={directorId} passed the eviction horizon with no connection; its sessions leave the roster");
            return true;
        }
    }

    /// <summary>
    /// Clear the active connection for <paramref name="directorId"/> IF <paramref name="connectionId"/> is
    /// still the active one. A late disconnect from a superseded connection is logged and ignored so it
    /// cannot wipe a newer active connection. Cached sessions are retained; while no connection is active,
    /// <see cref="TryGetFresh"/> returns null so aggregation pulls instead.
    /// </summary>
    /// <returns>
    /// True when this call actually cleared the active connection (so the Director now has NO live stream);
    /// false when it was ignored because a newer connection is active (a reconnect superseded this one) or
    /// the Director is unknown. Gateway Cleanup mission (tunnel-only): the caller uses this to decide whether
    /// to drop the Director from the registry - only a genuine last-connection close should.
    /// </returns>
    public bool UnregisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            entry.ConnectionEpochs.Remove(connectionId);
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedSessionStore] UnregisterConnection IGNORED (not active): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
                return false;
            }
            entry.ActiveConnectionId = null;
            // Drop the ownership epoch so a still-live older connection can reclaim on its next Hello -
            // it was only held back while a newer connection was present.
            entry.CurrentEpoch = 0;
        }
        FileLog.Write($"[PushedSessionStore] UnregisterConnection: tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)} cleared; aggregation will fall back to pull");
        return true;
    }

    /// <summary>
    /// Apply a full snapshot: replace the Director's whole session set, pruning any session not present.
    /// </summary>
    /// <returns>true if applied; false if rejected (not the active connection, or a stale sequence).</returns>
    public bool ApplySnapshot(TenantId tenant, string directorId, string connectionId, long sequence, IReadOnlyList<SessionDto> sessions)
    {
        if (sessions is null)
            throw new ArgumentNullException(nameof(sessions));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "snapshot"))
                return false;

            entry.Sessions.Clear();
            foreach (var s in sessions)
            {
                if (!string.IsNullOrEmpty(s.SessionId))
                {
                    DiscardInboundRole(s);
                    entry.Sessions[s.SessionId] = s;
                }
            }
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>Apply a single-session delta: upsert one session.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyDelta(TenantId tenant, string directorId, string connectionId, long sequence, SessionDto session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(session.SessionId))
            throw new ArgumentException("session.SessionId is required", nameof(session));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "delta"))
                return false;

            DiscardInboundRole(session);
            entry.Sessions[session.SessionId] = session;
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>
    /// Defect 5 - THE INGEST DISCARD. A Director's pushed session carries whatever
    /// <see cref="SessionDto.SessionRole"/> the Gateway last stamped down onto it (the Director echoes the
    /// role back up on its next delta, because it reports its whole session through one mapper). That echo
    /// is NOT an authority and must never be mistaken for one, so it dies here, at the boundary, before it
    /// can be read by anything.
    ///
    /// WHY THIS IS NOT BELT-AND-BRACES. Without it, "the Gateway is the only thing that computes the role"
    /// is true only by luck of statement ordering - it holds today purely because
    /// <see cref="Fleet.FleetRoleResolver.Stamp"/> assigns on every branch, so the echoed value happens to
    /// be overwritten before anything reads it. That is an accident, not a design. The moment someone adds
    /// a <c>?? s.SessionRole</c> to "preserve" a role the resolver could not determine, a Director's stale
    /// echo becomes the answer, and the whole defect class re-opens - silently, because the value will look
    /// correct almost always. Nulling it here makes the guarantee structural: a role that was not computed
    /// by this Gateway, on this pass, does not exist.
    ///
    /// Note the read paths that skip the fleet pass (the Exes page, GET /sessions/{sid}) now correctly
    /// report null rather than a stale echo - null means "nobody has resolved this", which is the truth.
    /// </summary>
    private static void DiscardInboundRole(SessionDto session) => session.SessionRole = null;

    /// <summary>Apply a remove/tombstone: drop one session from the Director's set.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyRemove(TenantId tenant, string directorId, string connectionId, long sequence, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, tenant, directorId, connectionId, sequence, "remove"))
                return false;

            entry.Sessions.Remove(sessionId);
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>
    /// Return deep COPIES of the Director's cached sessions when the stream is connected and the last push
    /// is within <paramref name="staleAfter"/>; otherwise null so the caller pulls. Copies are returned so
    /// the /sessions aggregation may stamp them without contaminating the cache. Relative time fields (idle
    /// seconds) are recomputed from the absolute LastActivityAt at serve time so a quiet session's idle
    /// clock keeps advancing between pushes.
    /// </summary>
    public IReadOnlyList<SessionDto>? TryGetFresh(TenantId tenant, string directorId, TimeSpan staleAfter)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return null;

        lock (entry.Gate)
        {
            if (entry.ActiveConnectionId is null)
                return null;
            if (entry.ReceivedAtUtc == DateTime.MinValue)
                return null;

            var now = _utcNow();
            if (now - entry.ReceivedAtUtc > staleAfter)
                return null;

            var copies = new List<SessionDto>(entry.Sessions.Count);
            foreach (var s in entry.Sessions.Values)
                copies.Add(RecomputeClocks(s.Clone(), now));
            return copies;
        }
    }

    /// <summary>
    /// How the Gateway's knowledge of one Director stands RIGHT NOW: what it last said, when it said it, and
    /// whether its tunnel is currently up. Never null-for-stale - staleness is a property reported here, not a
    /// reason to withhold the answer.
    /// </summary>
    /// <param name="Sessions">Deep copies of the last sessions this Director pushed, with idle clocks
    /// recomputed. Empty only when it has genuinely never pushed any.</param>
    /// <param name="AsOfUtc">When the Gateway last received a push from it, or null if it never has.</param>
    /// <param name="Connected">Whether its tunnel is up at this instant. This is the ground truth for whether
    /// a machine is reachable - NOT a countdown since the last push, which only ever measured chattiness.</param>
    public readonly record struct DirectorKnowledge(IReadOnlyList<SessionDto> Sessions, DateTime? AsOfUtc, bool Connected);

    /// <summary>
    /// What the Gateway last knew about a Director, WHATEVER ITS AGE (epic #1159, step A).
    ///
    /// THIS IS A SECOND METHOD BESIDE <see cref="TryGetFresh"/>, NOT A RELAXATION OF IT, and that distinction
    /// is the whole point. TryGetFresh has many callers - session location for command routing, the gate
    /// checks, the repository reads - and several of them are right to refuse a stale answer, because acting
    /// on one could route a command at a Director that is no longer there. Loosening it would hand every one
    /// of those callers a weaker guarantee they never asked for and could not see change. It stays exactly as
    /// strict as it is.
    ///
    /// WHAT THIS EXISTS TO FIX. The roster used to be served through the strict method, so a Director whose
    /// last push was older than the staleness window had its sessions DROPPED from the roster - not dimmed,
    /// not dated, gone, colours and all. The window is 20 seconds and the Director re-pushes every 10, so two
    /// missed ticks blanked a machine; with the tunnel dropping 35 times in a day (issue #1153) the owner's
    /// roster emptied several times an hour while the Gateway sat holding the very data it had just refused to
    /// show. Deleting good information to avoid admitting it is a few seconds old is never the right trade for
    /// a READ - the caller can say "updated 40s ago" perfectly well, and cannot say anything at all about an
    /// empty list.
    ///
    /// So: reads that DISPLAY use this and report the age. Reads that ACT keep using TryGetFresh.
    /// </summary>
    public DirectorKnowledge GetLastKnown(TenantId tenant, string directorId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return new DirectorKnowledge(Array.Empty<SessionDto>(), null, Connected: false);

        lock (entry.Gate)
        {
            var now = _utcNow();
            var copies = new List<SessionDto>(entry.Sessions.Count);
            foreach (var s in entry.Sessions.Values)
                copies.Add(RecomputeClocks(s.Clone(), now));
            return new DirectorKnowledge(
                copies,
                entry.ReceivedAtUtc == DateTime.MinValue ? null : entry.ReceivedAtUtc,
                entry.ActiveConnectionId is not null);
        }
    }

    /// <summary>
    /// Drop everything this store holds for one Director in one tenant (epic #1159 step A).
    ///
    /// Entries deliberately survive a disconnect - that is what lets the roster keep serving a machine whose
    /// tunnel has closed - so something must eventually release them, or "keep the sessions" would quietly
    /// become an unbounded memory leak keyed by every Director that ever connected. The eviction horizon is
    /// the event that ends a machine's life in the Gateway.
    ///
    /// THERE IS NO REMOVAL CASCADE ANY MORE, and this summary used to say there was - that eviction called
    /// this alongside a session-number release and a snooze clear. Both of those are DELETED (inspection 2,
    /// finding 1): a liveness check followed by a destructive action is two operations, and a Director
    /// reconnecting between them was destroyed anyway. Eviction now performs exactly one operation, and it
    /// is <see cref="ForgetIfDisconnected"/> below, not this method.
    ///
    /// Scoped to one (tenant, director) pair, so forgetting a machine in one account cannot reach another's.
    /// </summary>
    /// <remarks>
    /// UNCONDITIONAL. It does not look at whether the Director is connected, and it is deliberately NOT the
    /// eviction path - inspection 2 found that wiring a liveness check in front of a call like this cannot be
    /// made safe, because the check and the call are two operations. Eviction uses
    /// <see cref="ForgetIfDisconnected"/>, which is one. This remains only as a primitive for callers that
    /// have already established the Director is gone. Do not wire it to <c>OnDirectorRemoved</c>.
    /// </remarks>
    public void Forget(TenantId tenant, string directorId)
    {
        if (string.IsNullOrEmpty(directorId))
            return;
        if (DirectorsFor(tenant).TryRemove(directorId, out _))
            FileLog.Write($"[PushedSessionStore] Forget: tenant={tenant.ToLogString()}, director={directorId} passed the eviction horizon; its sessions leave the roster");
    }

    /// <summary>
    /// Issue #1177 (Phase 4a): find the Director that currently owns <paramref name="sessionId"/> in a FRESH
    /// pushed cache, without any HTTP pull. Scans <paramref name="tenant"/>'s Directors only (each under its
    /// own lock) and returns the first fresh match as (directorId, deep-copied session). Returns null when no
    /// stream-connected Director in this tenant holds the session, which post-cut means the session cannot be
    /// located at all - the pushed cache is the ONLY roster source, so the caller reports "not found" rather
    /// than dialling anything.
    ///
    /// This is the portless session-location primitive: a remotely-unreachable Director has an empty control
    /// endpoint, so an HTTP pull can never locate its sessions, but the pushed cache already records which
    /// Director pushed each session. Freshness and idle-clock recompute follow the same rules as
    /// <see cref="TryGetFresh"/>. Because it only walks one tenant's partition, a session id can never
    /// resolve a Director in a different tenant.
    /// </summary>
    public (string DirectorId, SessionDto Session)? TryLocate(TenantId tenant, string sessionId, TimeSpan staleAfter)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var now = _utcNow();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is null)
                    continue;
                if (entry.ReceivedAtUtc == DateTime.MinValue)
                    continue;
                if (now - entry.ReceivedAtUtc > staleAfter)
                    continue;
                if (entry.Sessions.TryGetValue(sessionId, out var s))
                    return (kvp.Key, RecomputeClocks(s.Clone(), now));
            }
        }
        return null;
    }

    /// <summary>
    /// Issue #2188: the owning Director for a session id IGNORING the freshness cut, plus how long ago that
    /// Director's newest push landed.
    ///
    /// This exists so a caller can tell two very different things apart, which <see cref="TryLocate"/>
    /// collapses into the same null:
    ///  - the session IS known to a Director whose push has simply gone stale (retryable - the Director is
    ///    expected back within one push cycle), and
    ///  - no Director in this tenant has ever pushed this session id (genuinely not found).
    ///
    /// Collapsing them is what made a live session report "session not found across any director" during a
    /// ten-second push gap, telling the user their session had been deleted when it was working the whole
    /// time. This read NEVER decides reachability - the tunnel send remains the authority on that. It only
    /// supplies the honest reason for a refusal.
    ///
    /// Scoped to one tenant, exactly like <see cref="TryLocate"/>, so a session id in one tenant can never
    /// name a Director in another.
    /// </summary>
    public (string DirectorId, TimeSpan PushAge)? TryLocateIgnoringFreshness(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var now = _utcNow();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (!entry.Sessions.ContainsKey(sessionId))
                    continue;
                // A Director that has never pushed has no meaningful age; report it as maximally stale
                // rather than as a nonsense negative or zero age.
                var age = entry.ReceivedAtUtc == DateTime.MinValue
                    ? TimeSpan.MaxValue
                    : now - entry.ReceivedAtUtc;
                return (kvp.Key, age < TimeSpan.Zero ? TimeSpan.Zero : age);
            }
        }
        return null;
    }

    /// <summary>
    /// Issue #1200 (auto-dismiss sweep): enumerate every FRESH pushed session across <paramref name="tenant"/>'s
    /// stream-connected Directors, each paired with its owning directorId so the caller can address a command
    /// DOWN that Director's stream. Applies the same active-connection + freshness rules as
    /// <see cref="TryGetFresh"/> and returns deep COPIES with recomputed idle clocks, so a caller may inspect
    /// them without touching the cache. A Director with no active connection or a stale cache contributes
    /// nothing. Scoped to one tenant - a sweep for one tenant never sees another's sessions.
    /// </summary>
    public IReadOnlyList<(string DirectorId, SessionDto Session)> SnapshotFresh(TenantId tenant, TimeSpan staleAfter)
    {
        var now = _utcNow();
        var result = new List<(string, SessionDto)>();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is null)
                    continue;
                if (entry.ReceivedAtUtc == DateTime.MinValue)
                    continue;
                if (now - entry.ReceivedAtUtc > staleAfter)
                    continue;
                foreach (var s in entry.Sessions.Values)
                    result.Add((kvp.Key, RecomputeClocks(s.Clone(), now)));
            }
        }
        return result;
    }

    /// <summary>
    /// Every pushed session across <paramref name="tenant"/>'s STREAM-CONNECTED Directors, regardless of how
    /// long ago the last push arrived. Same shape and same deep-copy/idle-recompute rules as
    /// <see cref="SnapshotFresh"/>; the ONLY difference is that it does not require the push to be recent.
    ///
    /// Inspection 1, finding 2. This exists because "may I nag about this?" and "is this data recent?" are
    /// different questions, and the display fold behind the phone's app-icon badge was asking the second one.
    /// It read SnapshotFresh with a THIRTY SECOND horizon, so a Director whose tunnel was up but whose pushes
    /// had merely gone quiet for half a minute fell out of the fold entirely and the persistent badge cleared
    /// itself - telling the owner there was nothing needing him on a machine he could have acted on at once.
    /// Connection, not freshness, is what decides whether a machine can be reached.
    ///
    /// A Director that has connected but not yet pushed under THIS connection still contributes nothing: its
    /// cached sessions belong to the previous connection and <see cref="RegisterConnection"/> deliberately
    /// resets the received stamp so they cannot be served as current. That check is kept for exactly the
    /// reason it was added, and only the AGE test is dropped here.
    /// </summary>
    public IReadOnlyList<(string DirectorId, SessionDto Session)> SnapshotConnected(TenantId tenant)
    {
        var now = _utcNow();
        var result = new List<(string, SessionDto)>();
        foreach (var kvp in DirectorsFor(tenant))
        {
            var entry = kvp.Value;
            lock (entry.Gate)
            {
                if (entry.ActiveConnectionId is null)
                    continue;
                if (entry.ReceivedAtUtc == DateTime.MinValue)
                    continue;
                foreach (var s in entry.Sessions.Values)
                    result.Add((kvp.Key, RecomputeClocks(s.Clone(), now)));
            }
        }
        return result;
    }

    /// <summary>
    /// ONE atomic answer to "is this Director connected, and what is it running?" - both read under the
    /// same entry lock, in one operation.
    ///
    /// The pair exists because asking the two questions separately is a check followed by an action, and
    /// the gap between them has a specific and dangerous shape: a Director that disconnects in between
    /// passes the connected check and then contributes no sessions, so the caller sees an EMPTY fleet.
    /// Empty is exactly what a Director that has finished looks like, so the answer "nothing was running"
    /// is indistinguishable from "I could not see it" - the failure this store's caller (the workspace
    /// capture) refuses precisely to avoid.
    ///
    /// FOUR ANSWERS, NOT TWO, and that is the point of the enum. "This Director is running nothing" and
    /// "I could not find out what it is running" are different facts with the same shape - an empty list -
    /// and a caller that cannot tell them apart writes the first when it means the second. The middle
    /// answer is the one that is easiest to miss: a Director can be CONNECTED and not yet have said
    /// anything, in the seconds between dialling in and its first push, and reading that as "connected,
    /// running nothing" is a genuine zero-seat record of a fleet nobody has looked at.
    /// </summary>
    /// <param name="tenant">The tenant the Director belongs to.</param>
    /// <param name="directorId">The Director to read.</param>
    public (FleetObservation Observation, IReadOnlyList<SessionDto> Sessions) ConnectedFleet(
        TenantId tenant, string directorId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return (FleetObservation.Unknown, Array.Empty<SessionDto>());

        var now = _utcNow();
        lock (entry.Gate)
        {
            if (entry.ActiveConnectionId is null)
                return (FleetObservation.NotConnected, Array.Empty<SessionDto>());

            if (entry.ReceivedAtUtc == DateTime.MinValue)
                return (FleetObservation.ConnectedButSilent, Array.Empty<SessionDto>());

            var sessions = entry.Sessions.Values
                .Select(x => RecomputeClocks(x.Clone(), now))
                .ToList();
            return (FleetObservation.Observed, sessions);
        }
    }

    /// <summary>
    /// True when this Director currently has an active stream connection.
    ///
    /// Reads under the entry gate. It used to read <c>ActiveConnectionId</c> without it, which inspection 2
    /// pointed out means even the OBSERVATION was unsynchronised with the register/unregister writes. That is
    /// no longer load-bearing for eviction - <see cref="ForgetIfDisconnected"/> makes its own decision inside
    /// the membership gate rather than calling this - but a liveness answer that can be read mid-write is a
    /// trap for the next caller, so it is fixed rather than left because the current callers happen to cope.
    /// </summary>
    public bool IsStreamConnected(TenantId tenant, string directorId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;
        lock (entry.Gate)
            return entry.ActiveConnectionId is not null;
    }

    /// <summary>
    /// True when <paramref name="connectionId"/> is the Director's CURRENTLY ACTIVE connection - the same
    /// ownership question <see cref="ApplySnapshot"/>, <see cref="ApplyDelta"/> and <see cref="ApplyRemove"/>
    /// answer before they accept anything, exposed as a read for a caller that changes no session state.
    ///
    /// Its caller is the clean-shutdown farewell. A farewell is a statement about a PROCESS, and a delayed
    /// one from a connection that has already been superseded by a reconnect is a statement about a process
    /// that is no longer the one registered - acting on it would stamp the LIVE Director as stopped, and a
    /// later genuine crash of that Director would then be reported as an orderly shutdown for a whole day.
    /// The registry's liveness gate cannot catch this: it serialises writes, it does not establish which
    /// connection owns the entry.
    /// </summary>
    public bool IsActiveConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return false;
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return false;
        lock (entry.Gate)
            return string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The active stream connection id for a Director, or null when none. The Gateway uses it to address a
    /// message DOWN the stream to that Director (issue #1176, Phase 1b down-channel).
    /// </summary>
    public string? GetActiveConnectionId(TenantId tenant, string directorId)
    {
        if (!DirectorsFor(tenant).TryGetValue(directorId, out var entry))
            return null;
        lock (entry.Gate)
            return entry.ActiveConnectionId;
    }

    private static bool IsAcceptable(DirectorEntry entry, TenantId tenant, string directorId, string connectionId, long sequence, string kind)
    {
        if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (not the active connection): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
            return false;
        }
        if (sequence <= entry.LastSequence)
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (stale sequence {sequence} <= last {entry.LastSequence}): tenant={tenant.ToLogString()}, director={directorId}, conn={Short(connectionId)}");
            return false;
        }
        return true;
    }

    private static SessionDto RecomputeClocks(SessionDto s, DateTime nowUtc)
    {
        if (s.LastActivityAt is DateTime last)
        {
            var idle = (nowUtc - last).TotalSeconds;
            s.IdleSeconds = idle > 0 ? idle : 0;
        }
        return s;
    }

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);

    private sealed class DirectorEntry
    {
        public readonly object Gate = new();
        public string? ActiveConnectionId;

        // Connection RECENCY (inspection): each connection is stamped with a monotonically increasing
        // epoch the first time it registers. A superseded (lower-epoch) connection re-sending Hello
        // cannot reclaim ownership, so two overlapping connections cannot alternate the authoritative
        // session roster - the same discipline PushedRepositoryStore uses. This matters because the
        // roster is a DESTRUCTIVE authority (the worktree reaper trusts it): a reclaimed old connection
        // would push a stale snapshot that omits a live session, and the reaper could then delete that
        // session's worktree.
        public long CurrentEpoch;                 // 0 = no owner
        public long NextEpoch = 1;
        public readonly Dictionary<string, long> ConnectionEpochs = new(StringComparer.Ordinal);

        public long LastSequence = -1;
        public DateTime ReceivedAtUtc = DateTime.MinValue;
        public readonly Dictionary<string, SessionDto> Sessions = new(StringComparer.Ordinal);
    }
}
