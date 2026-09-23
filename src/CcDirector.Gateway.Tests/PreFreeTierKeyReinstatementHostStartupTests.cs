using System;
using System.IO;
using System.Threading.Tasks;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The give-back of pre-free-tier device keys runs on the REAL hosted start-up path - and that path still
/// constructs without touching the database.
///
/// The first version of this change ran the give-back in the GatewayHost constructor. The hosted host defers
/// opening its database until after the listener binds (#2383, #2585), so every hosted start would have thrown
/// "the Gateway database is not open yet" before binding. The unit tests could not see it: they open the
/// database first. This test builds a hosted GatewayHost the way production does and brings it up through
/// <see cref="GatewayHost.EnsureStoresReady"/>, the step that runs right after the bind.
///
/// HOW IT FAILS ON PURPOSE: move the Run call back into the constructor and the second host's construction
/// throws; delete it from EnsureStoresReady and the key stays revoked.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class PreFreeTierKeyReinstatementHostStartupTests : IDisposable
{
    private static readonly DateTime OldRuleRevocation = new(2026, 9, 2, 0, 0, 15, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-reinstate-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _instances = Path.Combine(Path.GetTempPath(), "cc-reinstate-inst-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
    private readonly string? _priorHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);

    public PreFreeTierKeyReinstatementHostStartupTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_instances)) Directory.Delete(_instances, recursive: true); } catch { /* best effort */ }
    }

    private GatewayHost NewHostedHost() => new(
        port: GatewayHost.OperatingSystemAssignedPort,
        token: "reinstate-startup-token",
        authEnabled: true,
        instancesDirectory: _instances,
        workListsPath: Path.Combine(_root, "worklists.json"));

    [Fact]
    public async Task HostedStartup_ConstructsWithoutTheDatabase_AndGivesBackAKeyTheOldRuleCancelled()
    {
        // First life: enrol a workstation and cancel its key the way the old rule did, on 2 September.
        string key;
        await using (var first = NewHostedHost())
        {
            first.EnsureStoresReady();
            var tenant = first.TenantRegistry.MintOrLookupBySubject("sub-cut-off", "cut-off@example.com");
            key = first.Devices.RegisterForTenant(tenant, "sub-cut-off", "device-cut-off", "WORKSTATION").DeviceKey;
            first.Devices.RevokeTenant(tenant, TenantAccessRevokeReasons.EntitlementLost, OldRuleRevocation);
            Assert.Equal(DeviceCredentialResolutionKind.Revoked, first.Devices.ResolveCredential(key).Kind);
        }

        // Second life: the constructor must not touch the database (this is what the first version broke)...
        await using var second = NewHostedHost();

        // ...and the step that runs right after the bind gives the key back.
        second.EnsureStoresReady();

        Assert.Equal(1, second.ReinstatedAtStartup);
        Assert.Equal(DeviceCredentialResolutionKind.Active, second.Devices.ResolveCredential(key).Kind);
    }
}
