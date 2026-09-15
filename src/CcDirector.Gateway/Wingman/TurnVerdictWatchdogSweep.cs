using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The carrying-on clock's tick (the Wingman-on-every-turn mission, slice D), run through the per-tenant worker
/// seam (<see cref="TenantScopedSweep"/>) exactly like <see cref="TurnVerdictRetentionSweep"/>: on hosted it
/// enters each account's scope in turn, on self-host it fires once under Local. Driven by a timer in
/// <c>GatewayHost</c>. The rule itself is <see cref="TurnVerdictWatchdog"/>; the store under the seat's gate is
/// <see cref="TurnVerdictService.ExpireCarryingOn"/>.
///
/// Nothing is held in memory between ticks. Every tick reads the stored verdicts, so a Gateway restart forgets
/// no clock that was running.
/// </summary>
public sealed class TurnVerdictWatchdogSweep : TenantScopedSweep
{
    private readonly Func<TurnVerdictService> _service;
    private readonly ITenantContext _tenantContext;

    /// <param name="service">The seat. A function because the seat is built lazily, and a clock that ran before
    /// the first stop of this process must still expire verdicts stored before a restart.</param>
    public TurnVerdictWatchdogSweep(
        HostedTenantBoundary boundary,
        TenantRegistry tenants,
        ITenantContext tenantContext,
        Func<TurnVerdictService> service)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>Expire every account's carrying-on verdicts whose clock has run out. One pass over the census.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var total = 0;
        await ForEachTenantAsync(() =>
        {
            total += _service().ExpireCarryingOn(_tenantContext.Current);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        if (total > 0)
            FileLog.Write($"[TurnVerdictWatchdogSweep] {total} carrying-on verdict(s) ran out of time and went back to needing a person");
    }
}
