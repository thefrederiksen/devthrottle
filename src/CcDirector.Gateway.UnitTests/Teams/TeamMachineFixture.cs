using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A real team for the machine check of Team implementation 2.1 (review note N1): over a real database, a hosted device
/// registry and a real Director registry, the Owner's Director is enrolled into the team on <see cref="OwnerPc"/> and a
/// Developer's on <see cref="DeveloperPc"/>, each registered by its own key the way the tunnel's Hello registers it.
/// <see cref="AsOwner"/> is the check exactly as the host asks it (<c>GatewayHost.TeamMachineRefusal</c>), for a request
/// the team gate allowed for the Owner.
/// </summary>
public sealed class TeamMachineFixture : IDisposable
{
    public const string OwnerPc = "OWNER-PC";
    public const string DeveloperPc = "DEV-PC";
    public const string Owner = "sub-machine-owner";
    public const string Developer = "sub-machine-developer";

    /// <summary>When the Directors said Hello: a fixed time, because it is stored, not judged.</summary>
    private static readonly DateTime EnrolledAt = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly TeamCallerOwnership _ownership;
    private readonly DeviceCredentialIdentity _ownerKey;

    public TeamMachineFixture()
    {
        var db = _harness.Open();
        var tenants = new TenantRegistry(db);
        var teams = new TeamRegistry(db, tenants);
        _devices = new DeviceRegistry(db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false);
        _ownership = new TeamCallerOwnership(_directors, new PushedSessionStore(), _devices,
            new CcDirector.Gateway.History.SessionTurnStore(db), boundary, new SessionKeyRegistry(db, isHosted: true));

        var team = teams.CreateTeam(Owner, "Erik Laursen").Team!.TeamId;
        Tenant = new TenantId(team);
        Assert.True(teams.AddMember(team, Developer, TeamRole.Developer).IsDone);

        _ownerKey = EnrolledDirector(Owner, "director-owner", OwnerPc);
        EnrolledDirector(Developer, "director-developer", DeveloperPc);
    }

    /// <summary>The team's tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>The Owner, as the team gate allowed them: the caller the guard is asked about.</summary>
    public TeamCaller OwnerCaller => _ownership.CallerIn(Tenant, Owner, _ownerKey, null);

    /// <summary>Every Director of the team, as the host lists them for the guard.</summary>
    public IEnumerable<(string DirectorId, string? MachineName)> Directors =>
        _directors.ListDirectors(Tenant).Select(d => (d.DirectorId, (string?)d.MachineName));

    /// <summary>The check as the host asks it, for a request the gate allowed for the Owner in the team.</summary>
    public string? AsOwner(HttpContext ctx, string? machine, string what) =>
        TeamMachineGuard.Refuse(OwnerCaller, machine, Directors, what);

    /// <summary>Enrols one more Director of <paramref name="subject"/> on <paramref name="machine"/>.</summary>
    public DeviceCredentialIdentity EnrolledDirector(string subject, string directorId, string machine)
    {
        var deviceId = Tenant.Value + "|" + directorId;
        var key = _devices.RegisterForTenant(Tenant, subject, deviceId, machine).DeviceKey;
        var identity = _devices.ResolveCredential(key).Identity!;
        _directors.RegisterFromStream(directorId, machine, "u", "1.0", 1, EnrolledAt, Tenant, directorId, "device:" + deviceId);
        return identity;
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }
}
