using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// The Gateway timer that settles every session still holding dev report items (issue #2958, phase 2 review High 1
/// and High 2), on the per-tenant worker seam. Each pass, per tenant: every distinct session with an item queued,
/// held or sending goes through <see cref="DevReportDelivery.SettleAsync"/> - ended refuses, idle delivers, busy
/// waits.
///
/// WHY A TIMER AND NOT ONLY THE TURN END. The turn-end watcher raises a turn end on a CHANGE of state. A Director
/// that drops and reconnects with its session already waiting reports the same state the watcher last saw, so no
/// turn end is ever raised and the items held while it was away would wait for the next full turn. A session that
/// exits or fails raises no turn end at all. This timer is the mechanism that reaches both.
///
/// Timer cadence and lifecycle are owned by GatewayHost (the SessionHistorySweep pattern).
/// </summary>
internal sealed class DevReportSettleSweep : TenantScopedSweep
{
    /// <summary>How often the Gateway settles held items when nothing else did.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ITenantContext _tenantContext;
    private readonly DevReportStore _store;
    private readonly DevReportDelivery _delivery;

    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    public DevReportSettleSweep(HostedTenantBoundary boundary, TenantRegistry tenants,
        ITenantContext tenantContext, DevReportStore store, DevReportDelivery delivery)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        await ForEachTenantAsync(async () =>
        {
            var tenant = _tenantContext.Current;
            var sessions = _store.SessionsWithOpenItems(tenant);
            if (sessions.Count == 0) return;

            var sent = 0;
            foreach (var sessionId in sessions)
            {
                ct.ThrowIfCancellationRequested();
                sent += await _delivery.SettleAsync(tenant, sessionId, ct).ConfigureAwait(false);
            }
            FileLog.Write($"[DevReportSettleSweep] settled {sessions.Count} session(s), sent {sent} item(s)");
        }, ct).ConfigureAwait(false);
    }
}
