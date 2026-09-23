using System;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The keys the withdrawn "trial ended = cut off" rule cancelled are given back - and nothing else is.
///
/// On 2026-09-02 the Gateway cancelled the device keys of a member whose trial had just ended. He went on using
/// DevThrottle every day; his Director kept knocking with the cancelled key and was refused, so for three weeks
/// we could not see him. The free tier (#2664) stopped new cut-offs but never gave those keys back. These tests
/// pin the give-back and, as importantly, its three limits: the reason, the date, and today's plan.
/// </summary>
public sealed class PreFreeTierKeyReinstatementTests : IDisposable
{
    private static readonly DateTime OldRuleRevocation = new(2026, 9, 2, 0, 0, 15, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private string LegacyPath => _harness.LegacyPath("devices.json");

    public void Dispose() => _harness.Dispose();

    // The payment-side table this Gateway reads. With it present, an account with no row and no running trial
    // reads as FREE, which grants hosted capacity. Without it the read FAILS (Unknown).
    private static void CreateEntitlementsTable(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS entitlements (" +
            "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
            "livemode INTEGER NULL, tier TEXT NULL)");
    }

    private static void AddPlan(GatewayDatabase db, string subject, string tier)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) " +
            "VALUES ({0}, 'active', '2030-01-01T00:00:00Z', 1, {1})", subject, tier);
    }

    private static EntitlementRegistry Entitlements(GatewayDatabase db) =>
        new(db, requireLivemode: true, trials: new TrialRegistry(db));

    private static (TenantId Tenant, string Key) Enrol(DeviceRegistry devices, TenantRegistry tenants, string subject)
    {
        var tenant = tenants.MintOrLookupBySubject(subject, $"{subject}@example.com");
        var issued = devices.RegisterForTenant(tenant, subject, $"device-{subject}", "WORKSTATION");
        return (tenant, issued.DeviceKey);
    }

    [Fact]
    public void Run_KeyCancelledByTheOldRule_OnAFreeAccount_IsGivenBackAndTheSameKeyWorks()
    {
        var db = _harness.Open();
        CreateEntitlementsTable(db);
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, key) = Enrol(devices, tenants, "sub-rylee");
        devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(key).Kind);

        var reinstated = PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now);

        Assert.Equal(1, reinstated);
        Assert.Equal(DeviceCredentialResolutionKind.Active, devices.ResolveCredential(key).Kind);
        using var ctx = db.CreateUnscopedContext();
        var row = ctx.DeviceCredentials.AsNoTracking().Single();
        Assert.Null(row.RevokedAtUtc);
        Assert.Null(row.RevokedReason);
    }

    [Fact]
    public void Run_IsIdempotent_TheSecondRunReinstatesNothing()
    {
        var db = _harness.Open();
        CreateEntitlementsTable(db);
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, _) = Enrol(devices, tenants, "sub-twice");
        devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);

        Assert.Equal(1, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
    }

    [Fact]
    public void Run_KeyCancelledAfterTheOldRuleEnded_StaysCancelled()
    {
        var db = _harness.Open();
        CreateEntitlementsTable(db);
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, key) = Enrol(devices, tenants, "sub-later");
        devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, PreFreeTierKeyReinstatement.OldRuleEndedUtc);

        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(key).Kind);
    }

    [Fact]
    public void Run_KeyCancelledForAnyOtherReason_StaysCancelled()
    {
        var db = _harness.Open();
        CreateEntitlementsTable(db);
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, key) = Enrol(devices, tenants, "sub-other-reason");
        devices.RevokeTenant(tenant, "owner_revoked", OldRuleRevocation);

        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(key).Kind);
    }

    [Fact]
    public void Run_AccountWhosePlanExcludesTheHostedGateway_StaysCancelled()
    {
        var db = _harness.Open();
        CreateEntitlementsTable(db);
        AddPlan(db, "sub-selfhost", EntitlementRegistry.TierProSelfHost);
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, key) = Enrol(devices, tenants, "sub-selfhost");
        devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);

        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(key).Kind);
    }

    [Fact]
    public void Run_EntitlementReadFails_LeavesTheKeyForTheNextStart()
    {
        // No entitlements table: the read FAILS (Unknown). Ignorance is not a yes.
        var db = _harness.Open();
        using var devices = new DeviceRegistry(db, LegacyPath, isHosted: true);
        var tenants = new TenantRegistry(db);
        var (tenant, key) = Enrol(devices, tenants, "sub-unreadable");
        devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);

        Assert.Equal(0, PreFreeTierKeyReinstatement.Run(devices, tenants, Entitlements(db), Now));
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, devices.ResolveCredential(key).Kind);
    }
}
