using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The worker seam of the structural tenancy gate (G8 increment 2). These tests pin the fan-out contract that
/// lets a background job run tenant-isolated: self-host fires the body once under Local; hosted fires it once
/// per tenant, EACH inside that tenant's ambient scope (so the body's tenant-scoped reads resolve to that
/// tenant and no other). The hosted fan-out test is also the revert-proof: if <c>ForEachTenantAsync</c> stopped
/// entering the scope, the body's <c>Current</c> read would throw (deny-by-default) and the test would go red.
/// </summary>
public sealed class TenantScopedSweepTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>A minimal sweep that runs a supplied body through the seam, for assertions.</summary>
    private sealed class ProbeSweep : TenantScopedSweep
    {
        private readonly Func<Task> _body;
        public ProbeSweep(HostedTenantBoundary boundary, TenantRegistry tenants, Func<Task> body)
            : base(boundary, tenants) => _body = body;
        public Task RunAsync(CancellationToken ct = default) => ForEachTenantAsync(_body, ct);
    }

    [Fact]
    public async Task SelfHost_RunsBodyExactlyOnce_UnderLocal_IgnoringTheCensus()
    {
        var ctx = new SingleTenantContext();
        var registry = new TenantRegistry(_harness.Open(ctx));
        var boundary = new HostedTenantBoundary(ctx, new DeviceRegistry(), hosted: false);
        // Even with tenants in the census, self-host must fire ONCE under Local, never enumerate them.
        registry.MintOrLookupBySubject("sub-a", "a@example.com");
        registry.MintOrLookupBySubject("sub-b", "b@example.com");

        var seen = new List<string>();
        await new ProbeSweep(boundary, registry, () => { seen.Add(ctx.Current.Value); return Task.CompletedTask; })
            .RunAsync();

        Assert.Single(seen);
        Assert.Equal(TenantId.Local.Value, seen[0]);
    }

    [Fact]
    public async Task Hosted_FiresOncePerTenant_EachUnderItsOwnScope()
    {
        var ambient = new AsyncLocalTenantContext();
        var registry = new TenantRegistry(_harness.Open(ambient));
        var boundary = new HostedTenantBoundary(ambient, new DeviceRegistry(), hosted: false);
        var tA = registry.MintOrLookupBySubject("sub-a", "a@example.com").Value;
        var tB = registry.MintOrLookupBySubject("sub-b", "b@example.com").Value;

        var seen = new List<string>();
        // The body reads the AMBIENT tenant. It resolves ONLY because the seam entered a scope; without the
        // scope this read throws (deny-by-default) - which is exactly the revert-proof.
        await new ProbeSweep(boundary, registry, () => { seen.Add(ambient.Current.Value); return Task.CompletedTask; })
            .RunAsync();

        Assert.Equal(2, seen.Count);
        Assert.Contains(tA, seen);
        Assert.Contains(tB, seen);
        // No body run saw a tenant other than the two in the census (no leaked/fixed/Local tenant).
        Assert.All(seen, t => Assert.Contains(t, new[] { tA, tB }));
    }

    [Fact]
    public async Task Hosted_OneTenantBodyThrowing_DoesNotAbortTheOthers()
    {
        var ambient = new AsyncLocalTenantContext();
        var registry = new TenantRegistry(_harness.Open(ambient));
        var boundary = new HostedTenantBoundary(ambient, new DeviceRegistry(), hosted: false);
        var tA = registry.MintOrLookupBySubject("sub-a", "a@example.com").Value;
        var tB = registry.MintOrLookupBySubject("sub-b", "b@example.com").Value;
        var tC = registry.MintOrLookupBySubject("sub-c", "c@example.com").Value;

        var seen = new List<string>();
        await new ProbeSweep(boundary, registry, () =>
        {
            var current = ambient.Current.Value;
            seen.Add(current);
            if (current == tB) throw new InvalidOperationException("tenant B body fails");
            return Task.CompletedTask;
        }).RunAsync();

        // Every tenant was visited; B's failure did not abort A or C (per-tenant isolation).
        Assert.Equal(3, seen.Count);
        Assert.Contains(tA, seen);
        Assert.Contains(tC, seen);
    }

    [Fact]
    public async Task Hosted_EmptyCensus_RunsTheBodyZeroTimes()
    {
        var ambient = new AsyncLocalTenantContext();
        var registry = new TenantRegistry(_harness.Open(ambient));
        var boundary = new HostedTenantBoundary(ambient, new DeviceRegistry(), hosted: false);

        var count = 0;
        await new ProbeSweep(boundary, registry, () => { count++; return Task.CompletedTask; }).RunAsync();

        Assert.Equal(0, count);
    }
}
