using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// Carries on a Fleet Manager restart or move that a previous Gateway process left under way
/// (<see cref="FleetManagerPlacementService.ResumePendingAsync"/>), for every account through the per-tenant worker
/// seam, on a timer in <c>GatewayHost</c>. The replacement itself is recorded in the account's settings, so a restart
/// forgets nothing; this only starts watching it again.
/// </summary>
internal sealed class FleetManagerReplacementSweep : TenantScopedSweep
{
    /// <summary>The cadence: the Directors' heartbeat, as the event reconcile uses.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly ITenantContext _tenantContext;
    private readonly FleetManagerPlacementService _service;
    private int _running;

    public FleetManagerReplacementSweep(HostedTenantBoundary boundary, TenantRegistry tenants, ITenantContext tenantContext,
        FleetManagerPlacementService service)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>One pass over every account.</summary>
    public Task SweepAsync(CancellationToken ct = default)
        => ForEachTenantAsync(() => _service.ResumePendingAsync(_tenantContext.Current), ct);

    /// <summary>The timer's entry point: never overlaps itself and never throws.</summary>
    public async Task SweepSafeAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            await SweepAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerReplacementSweep] sweep FAILED: {ex.GetType().FullName}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
