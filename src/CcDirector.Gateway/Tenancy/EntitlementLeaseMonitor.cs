using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The active-tenant sweep of the cancellation cutoff (MTR-15). Each cycle (driven on a ~60s timer by
/// GatewayHost, hosted-only) it FORCES a fresh entitlement read for every currently-active tenant and applies
/// the three-way rule via <see cref="HostedAccessLeaseService.RefreshAsync"/>: an Entitled tenant is renewed, a
/// NotEntitled tenant is revoked (durable device tombstone + teardown), and an Unknown (failed read) leaves any
/// unexpired lease alone and never tombstones.
///
/// "Active" = any tenant holding an unexpired positive lease, i.e. that made a hosted request within the lease
/// ttl. Because almost every live tenant has such a lease, the sweep - not the 5-minute lease ttl - is the
/// binding constraint on how fast a cancellation is enforced (worst case one cycle + a read). Tenants that hold
/// a live stream but have made no recent request are added to the active set when the tenant-indexed connection
/// / stream registries land; until then the durable tombstone still denies their next re-authentication.
///
/// Per-tenant isolated: one tenant's read failing does not abort the sweep for the others.
/// </summary>
public sealed class EntitlementLeaseMonitor
{
    private readonly HostedAccessLeaseService _leases;

    public EntitlementLeaseMonitor(HostedAccessLeaseService leases)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
    }

    /// <summary>Run one sweep cycle: force a fresh entitlement read for every active tenant. Safe to call on a
    /// timer; a per-tenant failure is logged (no PII) and does not stop the rest of the cycle.</summary>
    public async Task SweepOnceAsync(CancellationToken ct = default)
    {
        // One lease at a time: a personal tenant's, and in a team each person's (devthrottle_internal#2311), since a
        // team's answer is per person.
        foreach (var lease in _leases.LiveLeases())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _leases.RefreshAsync(lease, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[EntitlementLeaseMonitor] entitlement refresh failed for a tenant, continuing ({ex.GetType().Name})");
            }
        }
    }
}
