using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Messaging;

/// <summary>
/// The thirty-day retention on read and stuck fleet messages (the Message Load mission, ruling 16). Runs
/// through the per-tenant worker seam exactly like the judged-stop retention beside it: on hosted it enters
/// each tenant's scope in turn, on self-host it fires once under Local. An unread message is never purged.
/// </summary>
public sealed class FleetMessageRetentionSweep : TenantScopedSweep
{
    private readonly FleetMessageStore _store;
    private readonly ITenantContext _tenantContext;
    private readonly TimeSpan _retention;

    public FleetMessageRetentionSweep(
        HostedTenantBoundary boundary,
        TenantRegistry tenants,
        ITenantContext tenantContext,
        FleetMessageStore store,
        TimeSpan retention)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retention = retention;
    }

    /// <summary>Purge every tenant's read and stuck messages older than the retention window.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var cutoffUtc = DateTime.UtcNow - _retention;
        var total = 0;
        await ForEachTenantAsync(() =>
        {
            total += _store.PurgeOlderThan(_tenantContext.Current, cutoffUtc);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        FileLog.Write($"[FleetMessageRetentionSweep] SweepAsync: purged={total} cutoff={cutoffUtc:o}");
    }
}
