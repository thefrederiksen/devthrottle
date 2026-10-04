using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// THE TEAM'S FLEET MAP, BY ROLE (devthrottle_internal#2312), over a real, throwaway, fully migrated Gateway database,
/// a real Director registry and a real pushed-session store. A Director on a team is seeded the way it will exist once
/// a Director is set up for a team (#2311): a device credential whose tenant is the team and whose account subject is
/// the person who enrolled it, and a Director registered under the team's tenant on that device's key.
///
/// The five tests of #2312 are the ones named Issue2312Test1..5 (Test 3 - nothing of another person's session opens -
/// is in TeamEndpointGateTests and the route-table walk, where the session routes are).
/// </summary>
public sealed class TeamFleetMapTests : IDisposable
{
    private const string Owner = "sub-map-owner";
    private const string Manager = "sub-map-manager";
    private const string Developer = "sub-map-developer";
    private const string Developer2 = "sub-map-developer-2";
    private const string Collaborator = "sub-map-collaborator";
    private const string Stranger = "sub-map-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions = new();
    private readonly TeamFleetMap _map;
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-teammap-" + Guid.NewGuid().ToString("N"));
    private readonly string _team;
    private readonly string _otherTeam;

    public TeamFleetMapTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[] { (Owner, "owner@example.com"), (Manager, "manager@example.com"),
                     (Developer, "developer@example.com"), (Developer2, "developer2@example.com"),
                     (Collaborator, "collaborator@example.com"), (Stranger, "stranger@example.com") })
            _tenants.MintOrLookupBySubject(subject, email);

        _teams = new TeamRegistry(_db, _tenants);
        _access = new TeamAccess(_teams);
        _directors = new DirectorRegistry(_instancesDir);
        _map = new TeamFleetMap(_teams, _access, _directors, _sessions, _db);

        _team = _teams.CreateTeam(Owner, "DevThrottle").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Developer2, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);

        // A second team the Developer is also on - its Directors must never reach the first team's map.
        _otherTeam = _teams.CreateTeam(Stranger, "Somewhere else").Team!.TeamId;
        Assert.True(_teams.AddMember(_otherTeam, Developer, TeamRole.Developer).IsDone);

        // The DevThrottle team's fleet: the Owner has one Director, the Developer two, the second Developer one, the
        // Manager one.
        SeedDirector(_team, "dir-owner", Owner, "Soren - DevThrottle", "SOREN_NORTH",
            Session("s-o1", "Teams - Developer - invitations", "Working"),
            Session("s-o2", "Signup page - Reviewer", "WaitingForInput"),
            Session("s-o3", "Release - Release Manager", "Idle"));
        SeedDirector(_team, "dir-dev-laptop", Developer, "Rob - laptop", "ROB-XPS",
            Session("s-d1", "Signup - Developer", "Working"),
            Session("s-d2", "Installer bug - Developer", "WaitingForPerm"));
        SeedDirector(_team, "dir-dev-desk", Developer, "Rob - desk", "ROB-DESK");
        SeedDirector(_team, "dir-dev2", Developer2, "Mike - desktop", "MIKE-PC",
            Session("s-m1", "Mike's work", "Working"));
        SeedDirector(_team, "dir-manager", Manager, "Priya - desktop", "PRIYA-PC",
            Session("s-p1", "October release", "Idle"));

        // The Developer's Director on the OTHER team.
        SeedDirector(_otherTeam, "dir-dev-elsewhere", Developer, "Rob - home lab", "ROB-HOME",
            Session("s-x1", "Elsewhere work", "Working"));
    }

    public void Dispose()
    {
        _directors.Dispose();
        _harness.Dispose();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    private static SessionDto Session(string id, string name, string state, string? assessed = null) =>
        new() { SessionId = id, Name = name, ActivityState = state, AssessedState = assessed, RepoPath = @"D:\secret\repo-" + id };

    /// <summary>A device credential bound to <paramref name="teamId"/> for <paramref name="subject"/>, and a Director that
    /// said Hello on it under the team's tenant, with these sessions pushed.</summary>
    private void SeedDirector(string teamId, string directorId, string subject, string name, string machine, params SessionDto[] sessions)
    {
        var deviceId = "device-" + directorId;
        SeedCredential(deviceId, subject, teamId);
        RegisterDirector(teamId, directorId, name, machine, "device:" + deviceId, sessions);
    }

    private void SeedCredential(string deviceId, string? subject, string tenantId, DateTime? revokedAtUtc = null)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.DeviceCredentials.Add(new DeviceCredentialEntity
        {
            DeviceId = deviceId,
            MachineName = "M-" + deviceId,
            DeviceKeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = "dt_",
            KeyLast4 = "abcd",
            IssuedAtUtc = DateTime.UtcNow,
            Status = revokedAtUtc is null ? "active" : "revoked",
            Platform = "windows",
            DeviceType = "workstation",
            AccountSubject = subject,
            TenantId = tenantId,
            RevokedAtUtc = revokedAtUtc,
        });
        ctx.SaveChanges();
    }

    private void RegisterDirector(string teamId, string directorId, string name, string machine, string? credential, params SessionDto[] sessions)
    {
        var tenant = new TenantId(teamId);
        _directors.RegisterFromStream(directorId, machine, "user", "1.0", 1234, DateTime.UtcNow, tenant, name, credential);
        var connection = "conn-" + directorId;
        _sessions.RegisterConnection(tenant, directorId, connection);
        Assert.True(_sessions.ApplySnapshot(tenant, directorId, connection, 1, sessions));
    }

    private TeamFleetMapDto MapFor(string subject)
    {
        var result = _map.Read(_team, subject);
        Assert.Equal(TeamFleetMapOutcome.Found, result.Outcome);
        return result.Map!;
    }

    private static string[] DirectorNames(TeamFleetMapDto map) =>
        map.People.SelectMany(p => p.Directors).Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] SessionNames(TeamFleetMapDto map) =>
        map.People.SelectMany(p => p.Directors).SelectMany(d => d.Sessions).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ---- The five tests of #2312 ------------------------------------------------------------------------------------

    [Fact]
    public void Issue2312Test1_Read_Developer_GetsOnlyTheirOwnDirectorsAndSessionsOnThatTeam()
    {
        var map = MapFor(Developer);

        Assert.Equal(TeamFleetMapDto.ScopeOwn, map.Scope);
        Assert.Equal("Developer", map.Role);
        var person = Assert.Single(map.People);
        Assert.True(person.IsYou);
        Assert.Equal("developer@example.com", person.Person);
        Assert.Equal(new[] { "Rob - desk", "Rob - laptop" }, DirectorNames(map));
        Assert.Equal(new[] { "Installer bug - Developer", "Signup - Developer" }, SessionNames(map));
    }

    [Theory]
    [InlineData(Owner, "Owner")]
    [InlineData(Manager, "Manager")]
    public void Issue2312Test2_Read_OwnerAndManager_GetEveryDirectorByPersonWithNameAndStatusOnly(string subject, string role)
    {
        var map = MapFor(subject);

        Assert.Equal(TeamFleetMapDto.ScopeEveryone, map.Scope);
        Assert.Equal(role, map.Role);
        Assert.Equal(new[] { "Mike - desktop", "Priya - desktop", "Rob - desk", "Rob - laptop", "Soren - DevThrottle" }, DirectorNames(map));
        Assert.Equal(
            new[] { "developer2@example.com", "developer@example.com", "manager@example.com", "owner@example.com" },
            map.People.Select(p => p.Person).OrderBy(p => p, StringComparer.Ordinal).ToArray());

        var owners = map.People.Single(p => p.Person == "owner@example.com").Directors.Single();
        Assert.Equal("SOREN_NORTH", owners.Machine);
        Assert.Equal(
            new[] { ("Release - Release Manager", "done"), ("Signup page - Reviewer", "waiting"), ("Teams - Developer - invitations", "working") },
            owners.Sessions.Select(s => (s.Name, s.Status)).ToArray());

        // No transcript, screen, input, path or id anywhere in what goes over the wire.
        var json = JsonSerializer.Serialize(map);
        foreach (var leak in new[] { "s-o1", "s-d1", "dir-owner", "device-", @"secret", "sub-map-", "transcript", "buffer", "screen", "prompt", "input" })
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Issue2312Test4_Read_Collaborator_GetsNoRoster()
    {
        var result = _map.Read(_team, Collaborator);

        Assert.Equal(TeamFleetMapOutcome.Refused, result.Outcome);
        Assert.Null(result.Map);
        Assert.Equal("In this team you are a Collaborator, and a Collaborator may not see the team's Fleet Map.", result.Refusal);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    [InlineData(Developer)]
    public void Issue2312Test5_Read_ADirectorOnAnotherTeam_NeverAppears(string subject)
    {
        var map = MapFor(subject);

        Assert.DoesNotContain("Rob - home lab", DirectorNames(map));
        Assert.DoesNotContain("Elsewhere work", SessionNames(map));
        Assert.DoesNotContain("ROB-HOME", JsonSerializer.Serialize(map));
    }

    // ---- Who may see it at all -------------------------------------------------------------------------------------

    [Fact]
    public void Read_NotAMember_IsToldThereIsNoSuchTeam()
    {
        Assert.Equal(TeamFleetMapOutcome.NoSuchTeam, _map.Read(_team, Stranger).Outcome);
    }

    [Fact]
    public void Read_NoSuchTeam_IsToldThereIsNoSuchTeam()
    {
        Assert.Equal(TeamFleetMapOutcome.NoSuchTeam, _map.Read(Guid.NewGuid().ToString(), Owner).Outcome);
        Assert.Equal(TeamFleetMapOutcome.NoSuchTeam, _map.Read("", Owner).Outcome);
    }

    [Fact]
    public void Read_NoCallerSubject_Throws()
    {
        Assert.Throws<ArgumentException>(() => _map.Read(_team, " "));
    }

    // ---- Whose a Director is ----------------------------------------------------------------------------------------

    [Fact]
    public void Read_ADirectorRegisteredWithNoDeviceKey_IsOnNobodysMap()
    {
        RegisterDirector(_team, "dir-no-key", "No key", "NOKEY-PC", credential: null, Session("s-n", "Unowned", "Working"));

        Assert.DoesNotContain("No key", DirectorNames(MapFor(Owner)));
    }

    [Fact]
    public void Read_ADirectorOnTheMachineToken_IsOnNobodysMap()
    {
        RegisterDirector(_team, "dir-token", "Token", "TOKEN-PC", "machine-token");

        Assert.DoesNotContain("Token", DirectorNames(MapFor(Owner)));
    }

    [Fact]
    public void Read_ARevokedCredential_IsOnNobodysMap()
    {
        SeedCredential("device-revoked", Developer2, _team, revokedAtUtc: DateTime.UtcNow);
        RegisterDirector(_team, "dir-revoked", "Revoked", "REVOKED-PC", "device:device-revoked");

        Assert.DoesNotContain("Revoked", DirectorNames(MapFor(Owner)));
    }

    [Fact]
    public void Read_ACredentialBoundToAnotherTenant_IsOnNobodysMap()
    {
        // The Director registered under THIS team's tenant, but the device it named is bound elsewhere: it is not shown
        // as anyone's.
        SeedCredential("device-elsewhere", Developer, _otherTeam);
        RegisterDirector(_team, "dir-bound-elsewhere", "Bound elsewhere", "ELSE-PC", "device:device-elsewhere");

        Assert.DoesNotContain("Bound elsewhere", DirectorNames(MapFor(Owner)));
        Assert.DoesNotContain("Bound elsewhere", DirectorNames(MapFor(Developer)));
    }

    [Fact]
    public void Read_ACredentialWithNoPerson_IsOnNobodysMap()
    {
        SeedCredential("device-nobody", null, _team);
        RegisterDirector(_team, "dir-nobody", "Nobody's", "NOBODY-PC", "device:device-nobody");

        Assert.DoesNotContain("Nobody's", DirectorNames(MapFor(Owner)));
    }

    [Fact]
    public void Read_APersonWhoLeftTheTeam_IsNoLongerOnTheMap()
    {
        Assert.True(_teams.RemoveMember(_team, Developer2).IsDone);

        Assert.DoesNotContain("Mike - desktop", DirectorNames(MapFor(Owner)));
    }

    [Fact]
    public void Read_ADeveloperWithNoDirectors_GetsAnEmptyMap()
    {
        var newcomer = "sub-map-newcomer";
        _tenants.MintOrLookupBySubject(newcomer, "newcomer@example.com");
        Assert.True(_teams.AddMember(_team, newcomer, TeamRole.Developer).IsDone);

        var map = MapFor(newcomer);

        Assert.Equal(TeamFleetMapDto.ScopeOwn, map.Scope);
        Assert.Empty(map.People);
    }

    [Fact]
    public void Read_ADirectorWithNoSessions_IsStillListed()
    {
        var rob = MapFor(Developer).People.Single().Directors.Single(d => d.Name == "Rob - desk");
        Assert.Empty(rob.Sessions);
    }

    [Fact]
    public void Read_AnExitedSession_IsNotOnTheMap()
    {
        SeedDirector(_team, "dir-exited", Developer, "Rob - old", "ROB-OLD", Session("s-e", "Finished long ago", "Exited"));

        Assert.DoesNotContain("Finished long ago", SessionNames(MapFor(Developer)));
    }

    [Fact]
    public void Read_ThePersonLabel_IsTheEmailNeverTheAccountSubject()
    {
        var map = MapFor(Manager);
        Assert.All(map.People, p => Assert.DoesNotContain("sub-map", p.Person, StringComparison.Ordinal));
    }

    [Fact]
    public void Read_TheCallerComesFirst()
    {
        var map = MapFor(Manager);
        Assert.True(map.People[0].IsYou);
        Assert.Equal("manager@example.com", map.People[0].Person);
        Assert.Single(map.People, p => p.IsYou);
    }

    [Fact]
    public void Read_TheGatewayDecidesTheLayoutsAndTheSentence()
    {
        var manager = MapFor(Manager);
        Assert.Equal(new[] { "by-person", "by-director" }, manager.Layouts);
        Assert.Equal(TeamFleetMapDto.SummaryEveryone, manager.Summary);

        var developer = MapFor(Developer);
        Assert.Equal(new[] { "by-director" }, developer.Layouts);
        Assert.Equal(TeamFleetMapDto.SummaryOwn, developer.Summary);
    }

    [Fact]
    public void DirectorName_NoDisplayName_IsTheMachine()
    {
        Assert.Equal("BOX", TeamFleetMap.DirectorName(new DirectorDto { DisplayName = "", MachineName = "BOX" }));
        Assert.Equal("Named", TeamFleetMap.DirectorName(new DirectorDto { DisplayName = "Named", MachineName = "BOX" }));
    }

    [Fact]
    public void SessionName_NoName_IsUnnamed()
    {
        Assert.Equal(TeamFleetMapSession.UnnamedSession, TeamFleetMap.SessionName(new SessionDto { Name = null }));
        Assert.Equal("x", TeamFleetMap.SessionName(new SessionDto { Name = "x" }));
    }

    [Fact]
    public void RegisteringCredentialOf_IsTheKeyTheDirectorSaidHelloOn_InItsOwnTenantOnly()
    {
        Assert.Equal("device:device-dir-owner", _directors.RegisteringCredentialOf(new TenantId(_team), "dir-owner"));
        Assert.Null(_directors.RegisteringCredentialOf(new TenantId(_otherTeam), "dir-owner"));
        Assert.Null(_directors.RegisteringCredentialOf(new TenantId(_team), "no-such-director"));
        Assert.Null(_directors.RegisteringCredentialOf(new TenantId(_team), ""));
    }
}

/// <summary>The one fold from a session's state to working, waiting or done.</summary>
public sealed class TeamFleetMapStatusTests
{
    [Theory]
    [InlineData("Starting", "working")]
    [InlineData("Working", "working")]
    [InlineData("WaitingForInput", "waiting")]
    [InlineData("WaitingForPerm", "waiting")]
    [InlineData("Idle", "done")]
    public void Fold_EachLiveState_IsOneOfTheThreeWords(string state, string expected)
    {
        Assert.Equal(expected, TeamFleetMapStatus.Fold(new SessionDto { ActivityState = state }));
    }

    [Fact]
    public void Fold_Exited_IsNotOnTheMap()
    {
        Assert.Null(TeamFleetMapStatus.Fold(new SessionDto { ActivityState = "Exited" }));
    }

    [Fact]
    public void Fold_TheGatewaysAssessedState_WinsOverTheDirectorsOwn()
    {
        Assert.Equal("done", TeamFleetMapStatus.Fold(new SessionDto { ActivityState = "WaitingForInput", AssessedState = "Idle" }));
    }

    [Fact]
    public void Fold_AStateItDoesNotKnow_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => TeamFleetMapStatus.Fold(new SessionDto { ActivityState = "Dreaming" }));
    }
}

/// <summary>
/// THE ALLOW-LIST, PINNED. The team Fleet Map's wire shape is exactly these fields; a field added to any of its records
/// - a session id, a transcript, a path, anything that opens a session - fails here before it can ship.
/// </summary>
public sealed class TeamFleetMapDtoTests
{
    private static string[] Fields<T>() =>
        typeof(T).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void TeamFleetMapDto_FieldSet_IsTheAllowList()
    {
        Assert.Equal(new[] { "Layouts", "People", "Role", "Scope", "Summary", "TeamId", "TeamName" }, Fields<TeamFleetMapDto>());
        Assert.Equal(new[] { "Directors", "IsYou", "Person" }, Fields<TeamFleetMapPerson>());
        Assert.Equal(new[] { "Machine", "Name", "Sessions" }, Fields<TeamFleetMapDirector>());
        Assert.Equal(new[] { "Name", "Status" }, Fields<TeamFleetMapSession>());
    }

    [Fact]
    public void TeamFleetMapDto_OnTheWire_CarriesExactlyTheAllowedKeys()
    {
        var dto = new TeamFleetMapDto("t", "Team", "Owner", TeamFleetMapDto.ScopeEveryone, TeamFleetMapDto.SummaryEveryone,
            TeamFleetMapDto.LayoutsEveryone,
            new[] { new TeamFleetMapPerson("a@example.com", true,
                new[] { new TeamFleetMapDirector("D", "M", new[] { new TeamFleetMapSession("S", "working") }) }) });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var root = doc.RootElement;
        Assert.Equal(new[] { "layouts", "people", "role", "scope", "summary", "teamId", "teamName" }, Keys(root));
        var person = root.GetProperty("people")[0];
        Assert.Equal(new[] { "directors", "isYou", "person" }, Keys(person));
        var director = person.GetProperty("directors")[0];
        Assert.Equal(new[] { "machine", "name", "sessions" }, Keys(director));
        Assert.Equal(new[] { "name", "status" }, Keys(director.GetProperty("sessions")[0]));
    }
}
