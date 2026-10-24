using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A device key judged active is remembered for at most a minute (issue #4409). These prove both halves: the memory is
/// real (a revoke written OUTSIDE this process - a raw connection, as the other Gateway in a deploy would - is not seen
/// until the key ages out), and every write made INSIDE this process is seen at once: a revoke, a bulk revoke of an
/// account, a removal from a team, and a write inside a transaction once it commits.
///
/// Serial: the number is process-wide, and another test registering a device in parallel would make a remembered key
/// here be read again - correct, and fatal to the "still remembered" facts.
/// </summary>
[Collection(SerialMetricCollection.Name)]
public sealed class DeviceKeyRememberingTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private long _clockMs = 5_000_000;

    public void Dispose() => _harness.Dispose();

    private (DeviceRegistry Registry, GatewayDatabase Db, TenantId Tenant, string Key) HostedKey(string subject)
    {
        var db = _harness.Open();
        var registry = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true) { NowMs = () => _clockMs };
        var tenant = new TenantRegistry(db).MintOrLookupBySubject(subject, subject + "@example.com");
        var key = registry.RegisterForTenant(tenant, subject, "device-" + subject, "WORKSTATION").DeviceKey;
        return (registry, db, tenant, key);
    }

    /// <summary>Revoke every device row through a connection EF never sees: the one kind of write the memory cannot
    /// notice, and so the probe for whether an answer came from memory.</summary>
    private void RevokeFromAnotherProcess()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _harness.DbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE device_credentials SET \"Status\" = 'revoked', \"RevokedAtUtc\" = '2026-10-24 00:00:00'";
        Assert.True(command.ExecuteNonQuery() >= 1);
    }

    [Fact]
    public void ActiveKey_RevokedByAnotherProcess_IsRememberedUntilMaxAgeThenRefused()
    {
        var (registry, _, _, key) = HostedKey("sub-remember");
        using var _ = registry;
        Assert.Equal(DeviceCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);

        RevokeFromAnotherProcess();

        _clockMs += (long)DeviceRegistry.RememberedKeyMaxAge.TotalMilliseconds - 1;
        Assert.Equal(DeviceCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);
        _clockMs += 1;
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, registry.ResolveCredential(key).Kind);
    }

    [Fact]
    public void ActiveKey_RevokedThroughTheRegistry_IsRefusedAtOnce()
    {
        var (registry, _, _, key) = HostedKey("sub-revoke");
        using var _ = registry;
        var deviceId = registry.ResolveCredential(key).Identity!.DeviceId;

        Assert.True(registry.RevokeDevice(deviceId, "test"));

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, registry.ResolveCredential(key).Kind);
    }

    [Fact]
    public void ActiveKey_AccountRevokedInBulk_IsRefusedAtOnceByAnotherRegistry()
    {
        var (first, db, tenant, key) = HostedKey("sub-bulk");
        using var _ = first;
        using var second = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true) { NowMs = () => _clockMs };
        Assert.Equal(DeviceCredentialResolutionKind.Active, second.ResolveCredential(key).Kind);

        Assert.Equal(1, first.RevokeTenant(tenant, "test"));

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, second.ResolveCredential(key).Kind);
    }

    [Fact]
    public void TeamKey_PersonRemovedFromTheTeam_IsRefusedAtOnce()
    {
        var db = _harness.Open();
        using var registry = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true) { NowMs = () => _clockMs };
        var tenants = new TenantRegistry(db);
        var teams = new TeamRegistry(db, tenants);
        tenants.MintOrLookupBySubject("sub-owner", "owner@example.com");
        tenants.MintOrLookupBySubject("sub-dev", "dev@example.com");
        var team = teams.CreateTeam("sub-owner", "Acme").Team!.TeamId;
        Assert.True(teams.AddMember(team, "sub-dev", TeamRole.Developer).IsDone);
        var key = registry.RegisterForTenant(new TenantId(team), "sub-dev", "dev-device", "WORKSTATION").DeviceKey;
        var active = registry.ResolveCredential(key);
        Assert.Equal(DeviceCredentialResolutionKind.Active, active.Kind);
        Assert.True(active.Identity!.IsTeamKey);

        Assert.True(teams.RemoveMember(team, "sub-dev").IsDone);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, registry.ResolveCredential(key).Kind);
    }

    [Fact]
    public void ActiveKey_ReadBetweenARevokeAndItsCommit_IsRefusedAfterTheCommit()
    {
        // A read inside the window sees the old committed row under the NEW number; only the commit moving the number
        // again makes that answer forgotten.
        var (registry, db, tenant, key) = HostedKey("sub-tx");
        using var _ = registry;
        Assert.Equal(DeviceCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);

        using (var ctx = db.CreateUnscopedContext())
        using (var tx = ctx.Database.BeginTransaction())
        {
            ctx.Database.ExecuteSqlRaw(
                "UPDATE device_credentials SET \"Status\" = 'revoked' WHERE \"TenantId\" = {0}", tenant.Value);

            Assert.Equal(DeviceCredentialResolutionKind.Active, registry.ResolveCredential(key).Kind);

            tx.Commit();
        }

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, registry.ResolveCredential(key).Kind);
    }

    [Fact]
    public void UnknownKey_IsNeverRemembered_SoANewKeyWorksAtOnce()
    {
        var (registry, _, tenant, _) = HostedKey("sub-new");
        using var _ = registry;
        Assert.Equal(DeviceCredentialResolutionKind.Unknown, registry.ResolveCredential("dt_not_a_key").Kind);

        var fresh = registry.RegisterForTenant(tenant, "sub-new", "second-device", "WORKSTATION").DeviceKey;

        Assert.Equal(DeviceCredentialResolutionKind.Active, registry.ResolveCredential(fresh).Kind);
    }

    [Theory]
    [InlineData("UPDATE gateway.device_credentials AS d\nSET \"Status\" = @p0", true)]
    [InlineData("INSERT INTO gateway.tenants (\"Id\", \"AccountSubject\") VALUES (@p0, @p1) RETURNING \"CreatedAtUtc\"", true)]
    [InlineData("DELETE FROM gateway.team_members AS t WHERE t.\"TeamId\" = @p0", true)]
    [InlineData("DELETE FROM \"teams\" WHERE \"TeamId\" = @p0 RETURNING 1;", true)]
    [InlineData("UPDATE\ndevice_credentials SET \"Status\" = 'revoked'", true)]
    [InlineData("SELECT d.\"DeviceId\", d.\"UpdatedAtUtc\" FROM gateway.device_credentials AS d WHERE d.\"DeviceKeyHash\" = @p0", false)]
    [InlineData("SELECT EXISTS (SELECT 1 FROM gateway.tenants AS t WHERE t.\"Id\" = @p0)", false)]
    [InlineData("UPDATE gateway.tenant_settings SET \"Value\" = @p0", false)]
    [InlineData("UPDATE gateway.session_keys SET \"RevokedAtUtc\" = @p0", false)]
    public void WritesACredentialTable_MatchesWritesToTheTablesAKeyCheckReads(string sql, bool expected)
    {
        Assert.Equal(expected, CredentialTableWriteInterceptor.WritesACredentialTable(sql));
    }
}
