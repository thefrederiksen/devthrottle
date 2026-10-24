using System;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// An active plan whose recorded period end has already passed (issue #4411).
///
/// The read grants such a row - an active subscription is entitled whatever its recorded end says - and the lease
/// used to be clipped to that past instant, so it expired the moment it was taken and every request read the
/// database again. On production one account in this state cost about 13 reads a second.
///
/// HOW THESE FAIL ON PURPOSE: put back the old clip (expire at the period end whenever it is earlier than the ttl)
/// and the two "is not read again" tests go RED - the second request reads the changed row. The future-end and
/// sweep tests stay green in both versions, which is what proves the change did not loosen the paid boundary.
/// </summary>
public sealed class HostedAccessLeasePastPeriodEndTests : IDisposable
{
    private const string Subject = "sub-past-period-end";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _harness.Dispose();

    private sealed class RecordingRevoker : ITenantAccessRevoker
    {
        public int Count { get; private set; }

        public Task RevokeAsync(TenantId tenant, string reason, CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private GatewayDatabase OpenWithActiveRow(DateTimeOffset periodEnd)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            Subject, EntitlementRegistry.StatusActive, periodEnd.UtcDateTime, true, EntitlementRegistry.TierPro);
        return db;
    }

    private static void SetStatus(GatewayDatabase db, string status)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("UPDATE entitlements SET status = {0} WHERE subject = {1}", status, Subject);
    }

    private (HostedAccessLeaseService leases, TenantId tenant, RecordingRevoker revoker) Wire(GatewayDatabase db)
    {
        var tenants = new TenantRegistry(db);
        var tenant = tenants.MintOrLookupBySubject(Subject, "payer@example.com");
        var revoker = new RecordingRevoker();
        var leases = new HostedAccessLeaseService(
            new EntitlementRegistry(db, requireLivemode: true), tenants, revoker, _clock);
        return (leases, tenant, revoker);
    }

    [Fact]
    public async Task AnActiveRowPastItsPeriodEnd_IsServedFromTheLease_NotReadAgain()
    {
        var db = OpenWithActiveRow(Now.AddDays(-17));
        var (leases, tenant, revoker) = Wire(db);

        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(tenant));

        // The row changes underneath. A second request that READ would see it and deny; one served from the
        // lease cannot. Allow here is the proof that no read happened.
        SetStatus(db, "canceled");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(tenant));
        Assert.Equal(0, revoker.Count);
    }

    [Fact]
    public async Task AnActiveRowPastItsPeriodEnd_HoldsALiveLeaseTheSweepWalks()
    {
        // The old clip left this account with no live lease at all, so the sweep never saw it.
        var (leases, tenant, _) = Wire(OpenWithActiveRow(Now.AddDays(-17)));

        await leases.AuthorizeAsync(tenant);

        Assert.Contains(leases.TenantsWithLiveLease(), t => t == tenant);
    }

    [Fact]
    public async Task AnActiveRowPastItsPeriodEnd_ACancellationIsStillCaughtByTheSweep()
    {
        var db = OpenWithActiveRow(Now.AddDays(-17));
        var (leases, tenant, revoker) = Wire(db);
        await leases.AuthorizeAsync(tenant);

        SetStatus(db, "canceled");

        Assert.Equal(HostedAccessDecision.DenyNotEntitled, await leases.RefreshAsync(tenant));
        Assert.Equal(1, revoker.Count);
        Assert.Equal(HostedAccessDecision.DenyNotEntitled, await leases.AuthorizeAsync(tenant));
    }

    [Fact]
    public async Task APeriodEndStillAhead_StillClipsTheLease()
    {
        // The paid boundary, unchanged: a period that ends inside the lease lifetime ends the lease with it, and the
        // first request after that boundary reads again.
        var db = OpenWithActiveRow(Now.AddMinutes(1));
        var (leases, tenant, _) = Wire(db);
        Assert.Equal(HostedAccessDecision.Allow, await leases.AuthorizeAsync(tenant));

        SetStatus(db, "canceled");
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(HostedAccessDecision.DenyNotEntitled, await leases.AuthorizeAsync(tenant));
    }

    [Theory]
    [InlineData(-17 * 24 * 60, 5 * 60)]   // ended weeks ago -> the plain ttl
    [InlineData(0, 5 * 60)]               // ends exactly now -> nothing ahead to clip to
    [InlineData(1, 60)]                   // ends in a minute -> clipped to it
    [InlineData(10, 5 * 60)]              // ends after the ttl -> the plain ttl
    public void LeaseExpiry_ClipsOnlyToAnEndStillAhead(int endInMinutes, int expectedSeconds)
    {
        var (leases, _, _) = Wire(OpenWithActiveRow(Now));

        var expiry = leases.LeaseExpiry(Now, Now.AddMinutes(endInMinutes).UtcDateTime);

        Assert.Equal(Now.AddSeconds(expectedSeconds), expiry);
    }

    [Fact]
    public void LeaseExpiry_NoRecordedEnd_IsThePlainTtl()
    {
        var (leases, _, _) = Wire(OpenWithActiveRow(Now));

        Assert.Equal(Now.AddMinutes(5), leases.LeaseExpiry(Now, null));
    }
}
