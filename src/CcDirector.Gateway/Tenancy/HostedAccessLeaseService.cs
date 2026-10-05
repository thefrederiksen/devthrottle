using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>What a hosted-access check concluded for one request or stream.</summary>
public enum HostedAccessDecision
{
    /// <summary>Entitled (a live lease, or a fresh Entitled read). Serve the request.</summary>
    Allow,

    /// <summary>The read SUCCEEDED and the tenant is not entitled (cancelled / unpaid / period elapsed). A
    /// terminal deny - the caller returns 402 and the tenant's devices are being revoked. Never retry into a
    /// grant.</summary>
    DenyNotEntitled,

    /// <summary>The read FAILED (database unreadable) and no unexpired lease covers the caller. A TEMPORARY
    /// deny - the caller returns 503 + Retry-After. NOTHING is tombstoned: a failed read is not proof of
    /// cancellation.</summary>
    RetryUnknown,

    /// <summary>In a TEAM's tenant, the request names nobody who is a member of the team (devthrottle_internal#2311).
    /// A refusal of the PERSON - the caller returns 403 - and never a verdict on the team's bill: NOTHING is revoked,
    /// so one stranger's request can never tombstone the team's keys.</summary>
    DenyNotAMember,
}

/// <summary>The revocation the lease service and the sweep trigger when a tenant reads NotEntitled. Kept as an
/// interface so the lease logic is testable without the full teardown, and so the durable device tombstone
/// (the load-bearing step) can be exercised in isolation.</summary>
public interface ITenantAccessRevoker
{
    Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default);
}

/// <summary>
/// The per-tenant positive-lease cache that turns "is this tenant still entitled?" into an O(1) in-memory
/// check on the hot path, and is the moment the cancellation cutoff (MTR-15) enforces on every hosted request.
///
/// Keyed by <see cref="TenantId"/> (NOT by device or subject) so all of a tenant's devices, requests, and
/// streams share ONE lease - a busy tenant causes one entitlement read per <see cref="_leaseTtl"/>, not one
/// per request. The database is always the authority; the lease is only an optimization and is always
/// subordinate to the durable device-credential status (a revoked device is denied regardless of any lease).
///
/// The three outcomes are kept strictly apart (see <see cref="EntitlementOutcome"/>): a successful
/// NotEntitled read revokes; a FAILED read (Unknown) never revokes and never shortens an existing paid lease.
///
/// The clip is the load-bearing line: a positive lease expires at <c>min(now + ttl, current_period_end)</c>,
/// so caching can never extend access one moment past the paid boundary.
///
/// A TEAM'S TENANT IS NOT ONE PERSON'S (devthrottle_internal#2311, Gateway step 2). Whether a tenant is a team is read
/// from the teams table (<see cref="Teams.TeamMemberEntitlement.IsTeam"/>), never inferred from a missing personal
/// subject. In a team the answer is the PERSON's - their membership and the team's bill, through
/// <see cref="Teams.TeamMemberEntitlement.Decide"/> - so a team lease is keyed by the tenant AND the person. A member is
/// never refused for the bill (the free tier keeps the hosted Gateway). A request that names nobody, or a non-member,
/// is <see cref="HostedAccessDecision.DenyNotAMember"/> and is NEVER fed to the revoke branch. A failed read is Unknown,
/// exactly as for a personal tenant. A personal tenant takes the same path as before, keyed by its tenant alone.
/// </summary>
public sealed class HostedAccessLeaseService
{
    private readonly record struct Lease(EntitlementOutcome Outcome, DateTimeOffset ExpiresAtUtc);

    // A personal tenant's lease is keyed by its tenant alone, as it always was; a team's by tenant and person. The
    // separator is a newline, which neither a tenant id nor an account subject contains.
    private const char TeamKeySeparator = '\n';

    private readonly EntitlementRegistry _entitlements;
    private readonly TenantRegistry _tenants;
    private readonly ITenantAccessRevoker _revoker;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseTtl;
    private readonly Teams.TeamMemberEntitlement? _teams;

    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    // Per-tenant single-flight: coalesce concurrent refreshes so an expiry does not stampede the database with
    // one read per in-flight request. One gate per tenant; created on demand, never removed (bounded by the
    // tenant census).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);

    public HostedAccessLeaseService(
        EntitlementRegistry entitlements,
        TenantRegistry tenants,
        ITenantAccessRevoker revoker,
        TimeProvider? clock = null,
        TimeSpan? leaseTtl = null,
        Teams.TeamMemberEntitlement? teams = null)
    {
        _teams = teams;
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
        _clock = clock ?? TimeProvider.System;
        _leaseTtl = leaseTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Authorize a tenant for hosted access. Serves from a live positive lease with no database touch; on a
    /// miss (no lease, or an expired one) it single-flights one entitlement read and applies the three-way
    /// rule. On a successful NotEntitled it triggers revocation before denying. On a failed read it denies
    /// only if no unexpired lease covers the caller, and never tombstones.
    /// </summary>
    /// <param name="person">Who the request is from, asked only when the tenant is a team's
    /// (<see cref="Teams.TeamCallerOwnership.PersonOf"/>). Null, or answering null, names nobody.</param>
    public async Task<HostedAccessDecision> AuthorizeAsync(TenantId tenant, Func<string?>? person = null, CancellationToken ct = default)
    {
        if (!tenant.IsValid)
            return HostedAccessDecision.DenyNotEntitled;

        var key = tenant.Value;

        // Fast path: a live positive lease, no database, no gate. Only a personal tenant's lease is stored under the
        // tenant alone, so a hit here is a personal tenant's.
        if (_leases.TryGetValue(key, out var cached)
            && cached.Outcome == EntitlementOutcome.Entitled
            && _clock.GetUtcNow() < cached.ExpiresAtUtc)
            return HostedAccessDecision.Allow;

        if (_teams is not null && _teams.IsTeam(tenant))
        {
            var who = person?.Invoke();
            if (string.IsNullOrWhiteSpace(who))
            {
                FileLog.Write($"[HostedAccessLease] DENIED: a request in team {tenant.ToLogString()} names no person (refused as a person, nothing revoked)");
                return HostedAccessDecision.DenyNotAMember;
            }
            var teamKey = TeamKey(tenant, who);
            if (_leases.TryGetValue(teamKey, out var teamLease)
                && teamLease.Outcome == EntitlementOutcome.Entitled
                && _clock.GetUtcNow() < teamLease.ExpiresAtUtc)
                return HostedAccessDecision.Allow;
            return await ReadUnderGateAsync(tenant, teamKey, who, honorFreshLease: true, ct).ConfigureAwait(false);
        }

        return await ReadUnderGateAsync(tenant, key, null, honorFreshLease: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// FORCE one entitlement read for a tenant, ignoring any still-valid lease, and apply the three-way rule
    /// (renew / revoke / leave-on-unknown). This is what the 60s sweep calls: unlike <see cref="AuthorizeAsync"/>
    /// it never short-circuits on a fresh lease, so a cancellation is caught within a sweep cycle instead of
    /// waiting out the up-to-5-minute lease. Returns the same decision so a caller can act on a revoke.
    /// </summary>
    public Task<HostedAccessDecision> RefreshAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (!tenant.IsValid)
            return Task.FromResult(HostedAccessDecision.DenyNotEntitled);
        return ReadUnderGateAsync(tenant, tenant.Value, null, honorFreshLease: false, ct);
    }

    /// <summary>
    /// <see cref="RefreshAsync(TenantId, CancellationToken)"/> for one lease the sweep found: a personal tenant's
    /// (<paramref name="person"/> null) or one person's in a team (devthrottle_internal#2311).
    /// </summary>
    public Task<HostedAccessDecision> RefreshAsync(LiveLease lease, CancellationToken ct = default)
    {
        if (!lease.Tenant.IsValid)
            return Task.FromResult(HostedAccessDecision.DenyNotEntitled);
        return lease.Person is null
            ? ReadUnderGateAsync(lease.Tenant, lease.Tenant.Value, null, honorFreshLease: false, ct)
            : ReadUnderGateAsync(lease.Tenant, TeamKey(lease.Tenant, lease.Person), lease.Person, honorFreshLease: false, ct);
    }

    // The single-flighted read + three-way apply, shared by the request path and the sweep. The gate coalesces
    // concurrent refreshes per tenant so an expiry (or a sweep coinciding with a request) does not stampede the
    // database. <paramref name="honorFreshLease"/> lets the request path return early if a concurrent read just
    // filled a fresh lease, while the sweep always performs the read.
    private async Task<HostedAccessDecision> ReadUnderGateAsync(TenantId tenant, string key, string? teamPerson,
        bool honorFreshLease, CancellationToken ct)
    {
        var gate = _refreshGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (honorFreshLease
                && _leases.TryGetValue(key, out var cached)
                && cached.Outcome == EntitlementOutcome.Entitled
                && now < cached.ExpiresAtUtc)
                return HostedAccessDecision.Allow;

            if (teamPerson is not null)
                return ApplyTeamRead(tenant, key, teamPerson, now);

            // The lease is tenant-keyed but the reader reads by subject: bridge tenant -> subject first. A
            // tenant with no subject mapping is not a failed read - it is "no such entitled tenant" -> deny.
            var subject = _tenants.SubjectForTenant(tenant);
            if (string.IsNullOrWhiteSpace(subject))
                return HostedAccessDecision.DenyNotEntitled;

            var decision = _entitlements.Evaluate(subject, now.UtcDateTime);
            switch (decision.Outcome)
            {
                case EntitlementOutcome.Entitled when !EntitlementScopes.GrantsHostedGateway(decision.Tier):
                {
                    // PAYING, BUT NOT FOR THIS. Enrolment is not the only door into hosted capacity: an account
                    // that already holds a tenant keeps reaching this gate on every request, so a customer who
                    // MOVES from a hosted plan to a self-host one would otherwise go on being served hosted
                    // service indefinitely on a plan that excludes it. The entitlement row is still active, so
                    // the outcome above is legitimately Entitled - it is the PLAN, not the payment, that
                    // refuses. Same table as the enrolment gate, so the two can never disagree about a plan.
                    //
                    // Denied, and deliberately NOT revoked. Revocation tombstones device credentials, and the
                    // reason for this refusal is a value written by the payment side: a plan renamed or
                    // mistyped there would destroy a paying customer's devices, which a later correction cannot
                    // undo. The deny is re-evaluated on every request and is enough - it is the same 402 the
                    // caller gets for a cancelled subscription.
                    //
                    // The lease is overwritten TERMINAL-NEGATIVE for the same reason the cancellation branch
                    // does it: it erases any positive lease this tenant still held from its hosted days, so a
                    // later FAILED read cannot ride that stale positive lease into an Allow.
                    FileLog.Write("[HostedAccessLease] DENIED: the tenant's entitlement is active but its plan does " +
                                  "not include hosted gateway capacity (denied, not revoked)");
                    _leases[key] = new Lease(EntitlementOutcome.NotEntitled, now);
                    return HostedAccessDecision.DenyNotEntitled;
                }

                case EntitlementOutcome.Entitled:
                {
                    // The clip: never past the paid boundary. A null period end means the row recorded none
                    // (an active row need not), so fall back to the plain ttl.
                    var ttlExpiry = now + _leaseTtl;
                    var expiry = decision.CurrentPeriodEnd is { } end && new DateTimeOffset(end, TimeSpan.Zero) < ttlExpiry
                        ? new DateTimeOffset(end, TimeSpan.Zero)
                        : ttlExpiry;
                    _leases[key] = new Lease(EntitlementOutcome.Entitled, expiry);
                    return HostedAccessDecision.Allow;
                }

                case EntitlementOutcome.NotEntitled:
                {
                    // A successful non-granting read. Mark the lease terminal-negative and revoke the tenant's
                    // durable device credentials (the tiebreaker that denies even a racing stale lease), then
                    // deny. Revocation is idempotent.
                    _leases[key] = new Lease(EntitlementOutcome.NotEntitled, now);
                    await _revoker.RevokeAsync(tenant, TenantAccessRevokeReasons.EntitlementLost, ct).ConfigureAwait(false);
                    return HostedAccessDecision.DenyNotEntitled;
                }

                default: // Unknown - a FAILED read. Ignorance is never a verdict.
                {
                    // If a positive lease is still unexpired, honour the remainder (ignorance never shortens a
                    // paid lease). Otherwise deny temporarily (503) and DO NOT tombstone - a failed read is not
                    // proof of cancellation.
                    if (_leases.TryGetValue(key, out var existing)
                        && existing.Outcome == EntitlementOutcome.Entitled
                        && now < existing.ExpiresAtUtc)
                        return HostedAccessDecision.Allow;
                    return HostedAccessDecision.RetryUnknown;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The team half of the read (devthrottle_internal#2311). Called under the tenant-and-person gate. A member is
    /// answered Allow on whichever tier the team's bill gives them; a non-member is refused as a person and nothing is
    /// revoked; a failed read is Unknown, honouring an unexpired lease and otherwise a temporary deny. There is no
    /// revoke here at all: the team's keys are cut by the team's own membership changes, never by a lease read.
    /// </summary>
    private HostedAccessDecision ApplyTeamRead(TenantId tenant, string key, string person, DateTimeOffset now)
    {
        // A membership read that failed is not known - Unknown, never "not a member", which is a verdict.
        if (_teams!.DecideOrUnknown(tenant, person, now.UtcDateTime) is not { } decision)
            return UnknownFor(key, now);

        if (!decision.IsMember)
        {
            FileLog.Write($"[HostedAccessLease] DENIED: the person is not a member of team {tenant.ToLogString()} (refused as a person, nothing revoked)");
            _leases.TryRemove(key, out _);
            return HostedAccessDecision.DenyNotAMember;
        }

        var entitlement = decision.Entitlement!;
        switch (entitlement.Outcome)
        {
            case EntitlementOutcome.Entitled when EntitlementScopes.GrantsHostedGateway(entitlement.Tier):
            {
                var ttlExpiry = now + _leaseTtl;
                var expiry = entitlement.CurrentPeriodEnd is { } end && new DateTimeOffset(end, TimeSpan.Zero) < ttlExpiry
                    ? new DateTimeOffset(end, TimeSpan.Zero)
                    : ttlExpiry;
                _leases[key] = new Lease(EntitlementOutcome.Entitled, expiry);
                return HostedAccessDecision.Allow;
            }
            case EntitlementOutcome.Entitled:
            case EntitlementOutcome.NotEntitled:
                // EvaluateTeamTenant never answers a member NotEntitled, and every tier it answers grants the hosted
                // Gateway. Either arriving here means the team rule changed under this code: stop, do not guess.
                throw new InvalidOperationException(
                    $"A team member's entitlement came back {entitlement.Outcome} on tier '{entitlement.Tier}', which the team rule never answers - the lease will not guess a grant or a revoke.");
            default:
                return UnknownFor(key, now);
        }
    }

    private HostedAccessDecision UnknownFor(string key, DateTimeOffset now)
    {
        if (_leases.TryGetValue(key, out var existing)
            && existing.Outcome == EntitlementOutcome.Entitled
            && now < existing.ExpiresAtUtc)
            return HostedAccessDecision.Allow;
        return HostedAccessDecision.RetryUnknown;
    }

    private static string TeamKey(TenantId tenant, string person) => tenant.Value + TeamKeySeparator + person;

    /// <summary>Drop any cached lease for a tenant - a personal tenant's, or every person's in a team (used by the
    /// revoker so a torn-down tenant cannot ride a stale positive lease). Idempotent.</summary>
    public void InvalidateLease(TenantId tenant)
    {
        if (!tenant.IsValid)
            return;
        _leases.TryRemove(tenant.Value, out _);
        var teamPrefix = tenant.Value + TeamKeySeparator;
        foreach (var key in _leases.Keys)
            if (key.StartsWith(teamPrefix, StringComparison.Ordinal))
                _leases.TryRemove(key, out _);
    }

    /// <summary>The tenants that currently hold an unexpired positive lease - part of the active-tenant set the
    /// sweep re-reads (a tenant that made a hosted request in the last ttl is swept even with no live stream). A team
    /// is listed once however many of its people hold a lease.</summary>
    public IReadOnlyList<TenantId> TenantsWithLiveLease() =>
        LiveLeases().Select(l => l.Tenant).Distinct().ToList();

    /// <summary>Every unexpired positive lease: a personal tenant's (no person) and each person's in a team. What the
    /// sweep re-reads, one lease at a time.</summary>
    public IReadOnlyList<LiveLease> LiveLeases()
    {
        var now = _clock.GetUtcNow();
        var result = new List<LiveLease>();
        foreach (var kv in _leases)
        {
            if (kv.Value.Outcome != EntitlementOutcome.Entitled || now >= kv.Value.ExpiresAtUtc)
                continue;
            var cut = kv.Key.IndexOf(TeamKeySeparator);
            result.Add(cut < 0
                ? new LiveLease(new TenantId(kv.Key), null)
                : new LiveLease(new TenantId(kv.Key[..cut]), kv.Key[(cut + 1)..]));
        }
        return result;
    }
}

/// <summary>One unexpired positive lease: a personal tenant's (<paramref name="Person"/> null) or one person's in a
/// team's tenant. Personally identifying: never logged.</summary>
public sealed record LiveLease(TenantId Tenant, string? Person);

/// <summary>The fixed reason strings a revocation records - a closed enum of causes, never PII.</summary>
public static class TenantAccessRevokeReasons
{
    public const string EntitlementLost = "entitlement_lost";
}
