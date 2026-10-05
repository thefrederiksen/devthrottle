using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The Fleet Manager events reconcile (<see cref="FleetManagerEventService.ReconcileAsync"/>), run for every account
/// through the per-tenant worker seam, on a timer in <c>GatewayHost</c>: first a few seconds after start, then every
/// <see cref="Interval"/>. The first pass is the one that catches what a restart forgot - stops left waiting for a
/// reading, and owned sessions that died while the Gateway was down.
/// </summary>
public sealed class FleetManagerEventSweep : TenantScopedSweep
{
    /// <summary>The cadence, the Directors' heartbeat.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Test seam: when false the host builds the sweep but starts no timer, so a host test decides when a reconcile
    /// runs and a hook under test cannot be covered by a timer that happened to fire. Production never touches it.
    /// </summary>
    public static bool Enabled = true;

    private readonly ITenantContext _tenantContext;
    private readonly FleetManagerEventService _service;
    private readonly FleetManagerLessonsObserver? _lessons;
    private int _running;

    /// <param name="lessons">Issue #3559: also make sure each account's marked Fleet Manager holds its current lessons
    /// for the session-start hook - the backstop for a mark moved to a session that has not pushed since. Null in a test
    /// of the events alone.</param>
    public FleetManagerEventSweep(HostedTenantBoundary boundary, TenantRegistry tenants, ITenantContext tenantContext,
        FleetManagerEventService service, FleetManagerLessonsObserver? lessons = null)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _lessons = lessons;
    }

    /// <summary>One reconcile pass over every account.</summary>
    public Task SweepAsync(CancellationToken ct = default)
        => ForEachTenantAsync(async () =>
        {
            var tenant = _tenantContext.Current;
            await _service.ReconcileAsync(tenant).ConfigureAwait(false);
            if (_lessons is not null) await _lessons.Refresh(tenant).ConfigureAwait(false);
        }, ct);

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
            FileLog.Write($"[FleetManagerEventSweep] sweep FAILED: {ex.GetType().FullName}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
