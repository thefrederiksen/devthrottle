using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// The per-tenant authority for the short three-digit session numbers (100-999) shown on the rail
/// (issue #1292). One instance lives on the Gateway, but its state is PARTITIONED BY TENANT: a number
/// names exactly ONE session across every Director a single tenant owns - unlike the old per-Director
/// allocator, where each Director counted independently from 100 and the same number appeared on several
/// Directors.
///
/// TENANT PARTITIONING (audit H2). Every piece of state - the session-to-number map, the in-use set, and
/// the 100-999 pool it draws from - lives inside a per-tenant partition, keyed by <see cref="TenantId"/>.
/// Every operation takes the caller's tenant (server-resolved at the endpoint from the authenticated
/// device key, never from client input) and only ever reads or writes that tenant's own partition. This
/// closes a whole family of cross-tenant faults the old bare-id store had, because a director id and a
/// session id are unique only WITHIN a tenant, not across the fleet:
///   - Allocate for tenant B's session could read tenant A's assignment for the same id and hand back A's
///     number; Release for one tenant could free the other's; a same-id Director removal in one tenant
///     freed the other's assignments (surfacing as a duplicated rail number for the innocent tenant).
///   - The pool was a single global 900-number space, so one tenant spinning up sessions exhausted
///     numbers for every other tenant. Each tenant now draws from its OWN 100-999 pool, so exhaustion -
///     if it ever happens - is confined to the tenant that caused it.
/// On self-host every caller resolves to <see cref="TenantId.Local"/>, so there is one partition and the
/// behavior is exactly as before.
///
/// Every Director asks the Gateway for a number when it creates a session (<see cref="Allocate"/>). The
/// Gateway hands out the lowest free number IN THAT TENANT'S PARTITION, records which session (and
/// Director) owns it, and guarantees it is unique across that tenant's fleet.
///
/// Two bands (the issue's refinement), applied per tenant:
///   - The COORDINATED band, <see cref="MinNumber"/>..<see cref="CoordinatedMaxNumber"/> (100-799),
///     is what the Gateway hands out. It fills from the low end.
///   - The OFFLINE band, <see cref="CoordinatedMaxNumber"/>+1..<see cref="MaxNumber"/> (800-999), is
///     left clear for Directors that cannot reach the Gateway at creation time. Those Directors pick a
///     random number in that band locally. Keeping the coordinated hand-outs in the low band means a
///     random offline pick is very unlikely to collide in normal use. The Gateway only spills into the
///     offline band when a tenant's coordinated band is fully exhausted (700 concurrent sessions).
///
/// The Gateway also learns numbers it did NOT hand out - a number a Director assigned offline, or any
/// number still in use after a Gateway restart - via <see cref="Adopt"/>, called as the fleet is
/// aggregated. Adopt only ever marks a number in use; it never frees one, so a momentarily-unreachable
/// Director (whose sessions drop out of the aggregation) can never have its numbers reclaimed and
/// re-handed. Numbers are freed only by an explicit <see cref="Release"/> when a session ends.
///
/// <see cref="ReleaseForDirector"/> USED TO free them when a whole Director was swept from the registry, and
/// that wiring is DELETED (epic #1159 step A, inspection 2 finding 1): the liveness check in front of it was
/// a separate operation from the release, so a Director reconnecting in between had numbers freed while it
/// was live - and a freed number can then be handed to a NEW session while the old one still holds it. The
/// cost of removing it is that a permanently retired machine keeps every number it held, one per session it
/// was running, out of the nine hundred, with nothing to reclaim them.
///
/// Thread-safe, and PARTITIONED for concurrency as well as correctness (audit H2). The tenant map is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>, and each tenant's partition carries its OWN lock. A
/// caller's allocate / adopt / release / removal only ever takes the lock of ITS OWN tenant's partition,
/// so two tenants operating at once never contend on a shared lock - one tenant flooding allocations
/// cannot block another tenant's allocation. There is no process-global lock across tenants.
/// </summary>
public sealed class FleetSessionNumberAllocator
{
    /// <summary>Lowest assignable number (inclusive).</summary>
    public const int MinNumber = 100;

    /// <summary>Highest number the Gateway hands out from before it spills into the offline band.</summary>
    public const int CoordinatedMaxNumber = 799;

    /// <summary>Highest assignable number (inclusive). 800-999 is the offline band.</summary>
    public const int MaxNumber = 999;

    /// <summary>One partition per tenant; each holds that tenant's own session map, in-use set (its own pool), and lock.</summary>
    private readonly ConcurrentDictionary<TenantId, TenantPool> _byTenant = new();

    private readonly record struct Assignment(int Number, string DirectorId);

    /// <summary>
    /// One tenant's private allocation state: its session-to-number map, its own 100-999 in-use set, and its
    /// OWN lock. Every mutation or read of this partition's fields is done under <see cref="Lock"/>, so a
    /// caller only ever contends with other callers for the SAME tenant - never across tenants.
    /// </summary>
    private sealed class TenantPool
    {
        public readonly object Lock = new();
        public readonly Dictionary<string, Assignment> BySession = new(StringComparer.Ordinal);
        public readonly HashSet<int> InUse = new();
    }

    /// <summary>The tenant's partition, creating it on first use. The map itself is thread-safe (no outer lock needed).</summary>
    private TenantPool PoolFor(TenantId tenant) => _byTenant.GetOrAdd(tenant, static _ => new TenantPool());

    /// <summary>Count of numbers currently reserved in <paramref name="tenant"/>'s partition. For tests and diagnostics.</summary>
    public int InUseCount(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var pool)) return 0;
        lock (pool.Lock)
            return pool.InUse.Count;
    }

    /// <summary>The number currently assigned to <paramref name="sessionId"/> in <paramref name="tenant"/>'s partition, or null if none.</summary>
    public int? NumberFor(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (!_byTenant.TryGetValue(tenant, out var pool)) return null;
        lock (pool.Lock)
            return pool.BySession.TryGetValue(sessionId, out var a) ? a.Number : (int?)null;
    }

    /// <summary>The Director <paramref name="sessionId"/>'s number in <paramref name="tenant"/>'s partition was handed to
    /// or adopted from, or null when the session holds no number or none was recorded. Inside a team this is whose the
    /// number is (devthrottle_internal#2311): freeing it, or asking for it again, is a person's only for their own
    /// Director's number.</summary>
    public string? DirectorFor(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (!_byTenant.TryGetValue(tenant, out var pool)) return null;
        lock (pool.Lock)
            return pool.BySession.TryGetValue(sessionId, out var a) && !string.IsNullOrEmpty(a.DirectorId) ? a.DirectorId : null;
    }

    /// <summary>
    /// Hand out a number for <paramref name="sessionId"/> IN <paramref name="tenant"/>'s partition, unique
    /// across that tenant's fleet, owned by <paramref name="directorId"/>. Idempotent: asking again for a
    /// session that already has a number returns the SAME number, so a retry never double-assigns. Fills the
    /// coordinated band (100-799) from the low end, spilling into the offline band (800-999) only when the
    /// coordinated band is full. Returns null only when every number 100-999 IN THIS TENANT is in use (that
    /// tenant's pool exhausted) - the Director then shows the session without a number rather than blocking
    /// real work over a cosmetic handle.
    /// </summary>
    public int? Allocate(TenantId tenant, string sessionId, string directorId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        var pool = PoolFor(tenant);
        lock (pool.Lock)
        {
            if (pool.BySession.TryGetValue(sessionId, out var existing))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {sessionId} already has {existing.Number} (idempotent, tenant={tenant.ToLogString()})");
                return existing.Number;
            }

            var chosen = FirstFree(pool, MinNumber, CoordinatedMaxNumber) ?? FirstFree(pool, CoordinatedMaxNumber + 1, MaxNumber);
            if (chosen is not int number)
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: pool exhausted ({pool.InUse.Count} in use), no number for {sessionId} (tenant={tenant.ToLogString()})");
                return null;
            }

            pool.InUse.Add(number);
            pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {number} -> {sessionId} (director={directorId}, {pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
            return number;
        }
    }

    /// <summary>
    /// Mark a number the Gateway did NOT hand out as in use IN <paramref name="tenant"/>'s partition - a
    /// number a Director assigned offline, or a number still live after a Gateway restart - learned as the
    /// fleet is aggregated. Only ever marks a number in use; never frees one, so it is safe to call from the
    /// (possibly partial) fleet view. A no-op when the session is already known, or when the number is out of
    /// range. If the number is already held by a DIFFERENT session in this tenant (a pre-existing offline
    /// collision the Gateway cannot resolve), the first owner keeps it and the conflict is logged.
    /// </summary>
    public void Adopt(TenantId tenant, string sessionId, string directorId, int number)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (number < MinNumber || number > MaxNumber) return;

        var pool = PoolFor(tenant);
        lock (pool.Lock)
        {
            if (pool.BySession.TryGetValue(sessionId, out var existing))
            {
                // Already tracked. Keep the number it already has (its own hand-out or a prior adopt).
                if (existing.Number != number)
                    FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {sessionId} already holds {existing.Number}, ignoring observed {number} (tenant={tenant.ToLogString()})");
                return;
            }

            if (pool.InUse.Contains(number))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} already held by another session; {sessionId} keeps its offline number (pre-existing collision, tenant={tenant.ToLogString()})");
                // Still record ownership so a later Allocate for this session is idempotent; the number
                // simply is not exclusively ours. (It used to say a ReleaseForDirector would clean this up.
                // Nothing calls that any more - see its remarks - so the ownership record is what a future
                // reclaim would read, not something that gets tidied on eviction today.)
                pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
                return;
            }

            pool.InUse.Add(number);
            pool.BySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} <- {sessionId} (director={directorId}, {pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>
    /// Free the number owned by <paramref name="sessionId"/> IN <paramref name="tenant"/>'s partition back to
    /// that tenant's pool when its session ends. The number may later be reused. Releasing an unknown session
    /// (or an unknown tenant) is a harmless no-op.
    /// </summary>
    public void Release(TenantId tenant, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!_byTenant.TryGetValue(tenant, out var pool)) return;
        lock (pool.Lock)
        {
            if (!pool.BySession.Remove(sessionId, out var a))
                return;
            // Only clear the shared reservation if no other session still holds this number (a
            // pre-existing offline collision could have two sessions on one number).
            if (!pool.BySession.Values.Any(x => x.Number == a.Number))
                pool.InUse.Remove(a.Number);
            FileLog.Write($"[FleetSessionNumberAllocator] Release: freed {a.Number} from {sessionId} ({pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>
    /// Free every number owned by <paramref name="directorId"/> IN <paramref name="tenant"/>'s partition.
    ///
    /// NOT CALLED FROM ANYWHERE IN PRODUCTION - see the remarks. It USED TO be called when a Director was
    /// removed from the registry, and that wiring is deleted; this summary said so in the present tense
    /// long after it stopped being true, which is precisely how a deleted cleanup gets restored.
    ///
    /// TENANT-SCOPED (audit H2). A director id is unique only within its tenant, so the tenant MUST be
    /// supplied. Only the named tenant's partition is touched, so one tenant's removal can never free
    /// another tenant's numbers.
    /// </summary>
    /// <remarks>
    /// NO PRODUCTION CALLER. The <see cref="DirectorRegistry.OnDirectorRemoved"/> subscriber that called this
    /// is DELETED (epic #1159 step A, inspection 2 finding 1) - the connection check and this release were
    /// two operations, and a Director reconnecting between them had its live sessions' numbers freed and
    /// re-handable. Kept as a primitive for a future reclaim that establishes the machine is gone FIRST. Do
    /// not wire it back to <c>OnDirectorRemoved</c>; the session-number assertion in
    /// <c>EvictionRaceAndCompositionTests.EvictionLeavesSnoozesAndNumbersAlone_OnTheRealHost</c> will redden.
    /// </remarks>
    public void ReleaseForDirector(TenantId tenant, string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        if (!_byTenant.TryGetValue(tenant, out var pool)) return;
        lock (pool.Lock)
        {
            var gone = pool.BySession.Where(kv => string.Equals(kv.Value.DirectorId, directorId, StringComparison.Ordinal))
                                     .Select(kv => kv.Key).ToList();
            foreach (var sid in gone)
                pool.BySession.Remove(sid);
            // Recompute the reserved set from what remains, so numbers shared by a surviving session stay held.
            pool.InUse.Clear();
            foreach (var a in pool.BySession.Values)
                pool.InUse.Add(a.Number);
            if (gone.Count > 0)
                FileLog.Write($"[FleetSessionNumberAllocator] ReleaseForDirector {directorId}: freed {gone.Count} number(s) ({pool.InUse.Count} in use, tenant={tenant.ToLogString()})");
        }
    }

    /// <summary>Lowest free number in [lo, hi] within the pool, or null when the whole band is in use. Caller holds the lock.</summary>
    private static int? FirstFree(TenantPool pool, int lo, int hi)
    {
        for (int n = lo; n <= hi; n++)
            if (!pool.InUse.Contains(n))
                return n;
        return null;
    }
}
