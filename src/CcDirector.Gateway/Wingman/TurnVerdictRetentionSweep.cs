using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The seven-day retention on judged stops (the Wingman-on-every-turn mission, slice B). Runs through the
/// per-tenant worker seam (<see cref="TenantScopedSweep"/>), exactly like the activity ledger's purge: on
/// hosted it enters each tenant's scope and purges that tenant's expired rows in isolation, and on
/// self-host it fires once under Local. Driven by a timer in <c>GatewayHost</c>.
///
/// The store takes its tenant EXPLICITLY rather than from the ambient scope (see
/// <see cref="TurnVerdictStore"/> for why it has no ambient read at all), so this sweep reads the tenant
/// the seam entered from the tenant context and passes it in - the pattern the dictionary-suggestion
/// sweep established.
/// </summary>
public sealed class TurnVerdictRetentionSweep : TenantScopedSweep
{
    private readonly TurnVerdictStore _store;
    private readonly ITenantContext _tenantContext;

    public TurnVerdictRetentionSweep(
        HostedTenantBoundary boundary,
        TenantRegistry tenants,
        ITenantContext tenantContext,
        TurnVerdictStore store)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Purge every tenant's judged stops older than the retention window. One pass over the census.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var cutoffUtc = DateTime.UtcNow - TurnVerdictStore.RetentionPeriod;
        var total = 0;
        await ForEachTenantAsync(() =>
        {
            total += _store.PurgeOlderThan(_tenantContext.Current, cutoffUtc);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        if (total > 0)
            FileLog.Write($"[TurnVerdictRetentionSweep] purged {total} rows older than {cutoffUtc:O}");
    }
}
