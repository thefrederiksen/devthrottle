using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director set up for a team (devthrottle_internal#2311), through the hosted enrollment endpoint's own functions -
/// <see cref="HostedEnrollmentEndpoint.Enroll"/>, <see cref="HostedEnrollmentEndpoint.ListTeams"/> and
/// <see cref="HostedEnrollmentEndpoint.Move"/> - with real ES256-signed account tokens, a hosted device registry and a
/// real team of one member per role, over a real, throwaway, fully migrated database. The last tests map the routes on
/// a real web host to show what is and is not there with Teams released or dark.
/// </summary>
public sealed class HostedTeamEnrollmentTests : IDisposable
{
    private const string Audience = "authenticated";
    private const string Issuer = "https://test.example.supabase.co/auth/v1";
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TestEs256Key _key = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly JwtAccessTokenValidator _validator;
    private readonly DirectorConnectionRegistry _connections = new();
    private readonly Dictionary<(string Tenant, string Director), int> _sessions = new();
    private readonly string _team;
    private readonly string _secondTeam;
    private readonly string _collaboratorTeam;

    public HostedTeamEnrollmentTests()
    {
        _db = OpenWithEntitlements();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _access = new TeamAccess(_teams);
        _validator = new JwtAccessTokenValidator(
            "test-signing-secret", timeProvider: null, publicKeySetJson: _key.PublicKeySetJson(),
            expectedAudience: Audience, expectedIssuer: Issuer, allowSymmetricHs256: false);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
        _secondTeam = _teams.CreateTeam(Manager, "Beta").Team!.TeamId;
        Assert.True(_teams.AddMember(_secondTeam, Developer, TeamRole.Developer).IsDone);
        _collaboratorTeam = _teams.CreateTeam(Owner, "Gamma").Team!.TeamId;
        Assert.True(_teams.AddMember(_collaboratorTeam, Developer, TeamRole.Collaborator).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
        _key.Dispose();
    }

    /// <summary>The payment-side table the Gateway only reads, with one row: the Developer's own account pays for a
    /// self-host plan, which does NOT include hosted capacity - so the PERSONAL gate refuses them with a 402.</summary>
    private GatewayDatabase OpenWithEntitlements()
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
            Developer, "active", null, true, EntitlementRegistry.TierProSelfHost);
        return db;
    }

    private EntitlementRegistry Entitlements() => new(_db, requireLivemode: false, trials: new TrialRegistry(_db));

    private string Token(string subject) => _key.Token(subject, subject + "@example.com", Audience, Issuer);

    private static EnrollSignedInRequest Req(string deviceId, string? teamId) => new()
    {
        DeviceId = deviceId,
        MachineName = "Laptop",
        Platform = "windows",
        DeviceType = "workstation",
        TeamId = teamId,
    };

    private HostedEnrollmentEndpoint.EnrollResult Enroll(string subject, string deviceId, string? teamId, bool released = true) =>
        HostedEnrollmentEndpoint.Enroll(Token(subject), Req(deviceId, teamId), _devices, _tenants, _validator,
            Entitlements(), DateTime.UtcNow, new TrialRegistry(_db), released ? _access : null);

    private HostedEnrollmentEndpoint.TeamEnrollment TeamEnrollment() =>
        new(_teams, _access, (tenant, director) => _sessions.TryGetValue((tenant.Value, director), out var n) ? n : 0, _connections);

    private HostedEnrollmentEndpoint.EnrollResult Move(string subject, string deviceKey, string? teamId) =>
        HostedEnrollmentEndpoint.Move(Token(subject), new MoveDirectorRequest { DeviceKey = deviceKey, TeamId = teamId },
            _devices, _tenants, _validator, TeamEnrollment(), Entitlements(), DateTime.UtcNow, new TrialRegistry(_db));

    private DeviceCredentialIdentity Active(string key)
    {
        var resolution = _devices.ResolveCredential(key);
        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
        return resolution.Identity!;
    }

    // ---- Enrollment into a chosen team ----------------------------------------------------------------------------

    [Fact]
    public void Enroll_IntoATeamWhereThePersonMayRunSessions_BindsTheKeyToTheTeamsTenant_ForThatPerson()
    {
        foreach (var subject in new[] { Owner, Manager, Developer })
        {
            var result = Enroll(subject, "dir-" + subject, _team);

            Assert.Equal(200, result.Status);
            var identity = Active(result.Response!.DeviceKey);
            Assert.Equal(_team, identity.TenantId);
            Assert.Equal(subject, identity.AccountSubject);
            Assert.EndsWith("|dir-" + subject, identity.DeviceId);
        }
    }

    [Fact]
    public void Enroll_IntoATeam_AsACollaborator_IsRefusedInPlainWords_AndNoKeyIsMinted()
    {
        var before = _devices.Count;
        var result = Enroll(Collaborator, "dir-c", _team);

        Assert.Equal(403, result.Status);
        Assert.Null(result.Response);
        Assert.StartsWith(HostedEnrollmentEndpoint.CannotRunSessionsInTeamLead, result.Error);
        Assert.Contains("Collaborator may not run sessions on their own computers", result.Error);
        Assert.Equal(before, _devices.Count);
    }

    [Fact]
    public void Enroll_IntoATeam_NotAMember_IsRefused_WithTheSameAnswerWhetherOrNotTheTeamExists()
    {
        var before = _devices.Count;
        var real = Enroll(Stranger, "dir-s", _team);
        var invented = Enroll(Stranger, "dir-s", Guid.NewGuid().ToString());

        Assert.Equal(403, real.Status);
        Assert.Equal(real.Status, invented.Status);
        Assert.Equal(real.Error, invented.Error);
        Assert.Contains(TeamAccessDecision.NotAMemberRefusal, real.Error);
        Assert.Equal(before, _devices.Count);
    }

    [Fact]
    public void Enroll_IntoATeam_NeverReachesThePersonalPaidGate_AndMintsNoPersonalTenant()
    {
        // The Developer's own account pays for a plan without hosted capacity, so their PERSONAL enrollment is refused...
        var personal = Enroll(Developer, "dir-p", null);
        Assert.Equal(402, personal.Status);
        Assert.Null(_tenants.LookupBySubject(Developer));

        // ...and their enrollment into the team is not: a team has no trial and is never refused for a bill here.
        var team = Enroll(Developer, "dir-t", _team);
        Assert.Equal(200, team.Status);
        Assert.Null(_tenants.LookupBySubject(Developer));
    }

    [Fact]
    public void Enroll_ATeamIdWhereTeamsIsNotReleased_IsRefused_AndNoKeyIsMinted()
    {
        var before = _devices.Count;
        var result = Enroll(Owner, "dir-o", _team, released: false);

        Assert.Equal(400, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.TeamsNotReleasedRefusal, result.Error);
        Assert.Equal(before, _devices.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enroll_NoTeam_IsThePersonalEnrollment_Unchanged_ReleasedOrNot(string? teamId)
    {
        foreach (var released in new[] { true, false })
        {
            var result = Enroll(Manager, "dir-m-" + released, teamId, released);

            Assert.Equal(200, result.Status);
            var personal = _tenants.LookupBySubject(Manager);
            Assert.NotNull(personal);
            var identity = Active(result.Response!.DeviceKey);
            Assert.Equal(personal!.Value.Value, identity.TenantId);
            // The personal namespace is exactly the one that existed before teams: the hash of the tenant.
            Assert.Equal(Sha256Hex(personal.Value.Value) + "|dir-m-" + released, identity.DeviceId);
        }
    }

    [Fact]
    public void Enroll_TwoMembersPresentingTheSameDeviceId_GetTwoRows_NeitherTakesTheOthersOver()
    {
        var owner = Enroll(Owner, "same-id", _team);
        var developer = Enroll(Developer, "same-id", _team);

        Assert.Equal(200, owner.Status);
        Assert.Equal(200, developer.Status);
        Assert.NotEqual(Active(owner.Response!.DeviceKey).DeviceId, Active(developer.Response!.DeviceKey).DeviceId);
        Assert.Equal(Owner, Active(owner.Response.DeviceKey).AccountSubject);
    }

    [Fact]
    public void Enroll_OnePersonsTwoDirectors_OnTwoTeams_HoldTwoKeysInTwoTenants()
    {
        var a = Enroll(Developer, "director-a", _team);
        var b = Enroll(Developer, "director-b", _secondTeam);

        Assert.Equal(_team, Active(a.Response!.DeviceKey).TenantId);
        Assert.Equal(_secondTeam, Active(b.Response!.DeviceKey).TenantId);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ---- The teams a person may set a Director up for -------------------------------------------------------------

    [Fact]
    public void ListTeams_OffersEveryTeamWhereThePersonMayRunSessions_AndNeverACollaboratorsTeam()
    {
        var result = HostedEnrollmentEndpoint.ListTeams(Token(Developer), _validator, _teams, _access);

        Assert.Equal(200, result.Status);
        var offered = result.Response!.Teams;
        Assert.Equal(new[] { "Acme", "Beta" }, offered.Select(t => t.Name).ToArray());
        Assert.DoesNotContain(offered, t => t.TeamId == _collaboratorTeam);
        var acme = offered.Single(t => t.Name == "Acme");
        Assert.Equal(_team, acme.TeamId);
        Assert.Equal("developer", acme.Role);
        Assert.Equal(4, acme.MemberCount);
    }

    [Fact]
    public void ListTeams_GivesEachRoleInItsStoredWords()
    {
        Assert.Equal("owner", HostedEnrollmentEndpoint.ListTeams(Token(Owner), _validator, _teams, _access)
            .Response!.Teams.Single(t => t.TeamId == _team).Role);
        Assert.Equal("manager", HostedEnrollmentEndpoint.ListTeams(Token(Manager), _validator, _teams, _access)
            .Response!.Teams.Single(t => t.TeamId == _team).Role);
    }

    [Fact]
    public void ListTeams_APersonWithNoTeam_AndAPersonWhoIsOnlyACollaborator_GetAnEmptyList()
    {
        var none = HostedEnrollmentEndpoint.ListTeams(Token(Stranger), _validator, _teams, _access);
        var onlyCollaborator = HostedEnrollmentEndpoint.ListTeams(Token(Collaborator), _validator, _teams, _access);

        Assert.Equal(200, none.Status);
        Assert.Empty(none.Response!.Teams);
        Assert.Equal(200, onlyCollaborator.Status);
        Assert.Empty(onlyCollaborator.Response!.Teams);
    }

    [Fact]
    public void ListTeams_NoTokenOrABadToken_Is401()
    {
        Assert.Equal(401, HostedEnrollmentEndpoint.ListTeams(null, _validator, _teams, _access).Status);
        Assert.Equal(401, HostedEnrollmentEndpoint.ListTeams("not-a-token", _validator, _teams, _access).Status);
        Assert.Equal(401, HostedEnrollmentEndpoint.ListTeams(_key.Token(Owner, "o@x.com", "wrong-audience", Issuer), _validator, _teams, _access).Status);
    }

    [Fact]
    public void ListTeams_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => HostedEnrollmentEndpoint.ListTeams(Token(Owner), null!, _teams, _access));
        Assert.Throws<ArgumentNullException>(() => HostedEnrollmentEndpoint.ListTeams(Token(Owner), _validator, null!, _access));
        Assert.Throws<ArgumentNullException>(() => HostedEnrollmentEndpoint.ListTeams(Token(Owner), _validator, _teams, null!));
    }

    // ---- Moving a Director to another team ------------------------------------------------------------------------

    [Fact]
    public void Move_WithASessionRegistered_IsRefused_AndTheOldKeyStillWorks()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;
        _sessions[(_team, "director-a")] = 1;

        var result = Move(Developer, key, _secondTeam);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveWithSessionsRefusal, result.Error);
        Assert.Equal(_team, Active(key).TenantId);
    }

    [Fact]
    public void Move_WithNoSession_PutsTheDirectorInTheNewTeam_RevokesTheOldKey_AndCutsItsOldTunnel()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;
        var oldTunnelCut = false;
        var siblingCut = false;
        _connections.Register(new TenantId(_team), "conn-old", () => oldTunnelCut = true, Developer, "director-a");
        _connections.Register(new TenantId(_team), "conn-sibling", () => siblingCut = true, Developer, "director-other");

        var result = Move(Developer, key, _secondTeam);

        Assert.Equal(200, result.Status);
        var moved = Active(result.Response!.DeviceKey);
        Assert.Equal(_secondTeam, moved.TenantId);
        Assert.Equal(Developer, moved.AccountSubject);
        Assert.EndsWith("|director-a", moved.DeviceId);
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(key).Kind);
        Assert.True(oldTunnelCut);
        Assert.False(siblingCut);
        // The machine name and platform travel with the Director.
        Assert.Equal(new DeviceDisplay("Laptop", "windows", "workstation"), _devices.DisplayOfDevice(moved.DeviceId));
    }

    [Fact]
    public void Move_BackToThePersonsOwnAccount_RunsThePersonalGate()
    {
        var ownerKey = Enroll(Owner, "director-o", _team).Response!.DeviceKey;
        var home = Move(Owner, ownerKey, null);
        Assert.Equal(200, home.Status);
        Assert.Equal(_tenants.LookupBySubject(Owner)!.Value.Value, Active(home.Response!.DeviceKey).TenantId);

        // The Developer's own plan does not include hosted capacity: moving home is refused exactly as enrolling
        // home is, and the team key keeps working.
        var developerKey = Enroll(Developer, "director-d", _team).Response!.DeviceKey;
        var refused = Move(Developer, developerKey, null);
        Assert.Equal(402, refused.Status);
        Assert.Equal(_team, Active(developerKey).TenantId);
    }

    [Fact]
    public void Move_IntoATeamWhereThePersonIsOnlyACollaborator_OrNotAMember_IsRefused_AndNothingChanges()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;

        var collaborator = Move(Developer, key, _collaboratorTeam);
        Assert.Equal(403, collaborator.Status);
        Assert.StartsWith(HostedEnrollmentEndpoint.CannotRunSessionsInTeamLead, collaborator.Error);

        var strangerTeam = _teams.CreateTeam(Stranger, "Delta").Team!.TeamId;
        var notAMember = Move(Developer, key, strangerTeam);
        Assert.Equal(403, notAMember.Status);

        Assert.Equal(_team, Active(key).TenantId);
    }

    [Fact]
    public void Move_ToTheTeamItIsAlreadyIn_IsRefused()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;
        var result = Move(Developer, key, _team);

        Assert.Equal(409, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveToSameTeamRefusal, result.Error);
        Assert.Equal(_team, Active(key).TenantId);
    }

    [Fact]
    public void Move_SomeoneElsesKey_IsRefused()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;
        var result = Move(Manager, key, _secondTeam);

        Assert.Equal(403, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveSomeoneElsesKeyRefusal, result.Error);
        Assert.Equal(_team, Active(key).TenantId);
    }

    [Fact]
    public void Move_ARevokedOrUnknownKey_IsRefused()
    {
        var key = Enroll(Developer, "director-a", _team).Response!.DeviceKey;
        Assert.Equal(200, Move(Developer, key, _secondTeam).Status);

        Assert.Equal(401, Move(Developer, key, _team).Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveKeyNotActiveRefusal, Move(Developer, "no-such-key", _team).Error);
    }

    [Fact]
    public void Move_AKeyHostedSetupDidNotIssue_IsRefused()
    {
        var tenant = _tenants.MintOrLookupBySubject(Owner, null);
        var key = _devices.RegisterForTenant(tenant, Owner, "bare-device-id", "M").DeviceKey;

        var result = Move(Owner, key, _team);

        Assert.Equal(400, result.Status);
        Assert.Equal(HostedEnrollmentEndpoint.MoveKeyNotFromSetupRefusal, result.Error);
    }

    [Fact]
    public void Move_BadRequestOrToken_IsRefusedBeforeAnythingIsRead()
    {
        Assert.Equal(400, HostedEnrollmentEndpoint.Move(Token(Owner), null, _devices, _tenants, _validator, TeamEnrollment()).Status);
        Assert.Equal(400, HostedEnrollmentEndpoint.Move(Token(Owner), new MoveDirectorRequest(), _devices, _tenants, _validator, TeamEnrollment()).Status);
        Assert.Equal(401, HostedEnrollmentEndpoint.Move(null, new MoveDirectorRequest { DeviceKey = "k" }, _devices, _tenants, _validator, TeamEnrollment()).Status);
        Assert.Equal(401, HostedEnrollmentEndpoint.Move("bad", new MoveDirectorRequest { DeviceKey = "k" }, _devices, _tenants, _validator, TeamEnrollment()).Status);
        Assert.Throws<ArgumentNullException>(() => HostedEnrollmentEndpoint.Move(Token(Owner), null, _devices, _tenants, _validator, null!));
    }

    [Fact]
    public void TeamScopedDeviceId_DependsOnTheTeamAndThePerson()
    {
        var a = HostedEnrollmentEndpoint.TeamScopedDeviceId("t1", "s1", "d");
        Assert.Equal(a, HostedEnrollmentEndpoint.TeamScopedDeviceId("t1", "s1", "d"));
        Assert.NotEqual(a, HostedEnrollmentEndpoint.TeamScopedDeviceId("t2", "s1", "d"));
        Assert.NotEqual(a, HostedEnrollmentEndpoint.TeamScopedDeviceId("t1", "s2", "d"));
        Assert.EndsWith("|d", a);
        Assert.DoesNotContain("s1", a);
    }

    // ---- The routes on a real web host ----------------------------------------------------------------------------

    [Fact]
    public async Task Map_TeamsReleased_ServesTheTeamsListAndTheMoveOverHttp()
    {
        await using var app = await Host(TeamEnrollment());
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var list = new HttpRequestMessage(HttpMethod.Get, HostedEnrollmentEndpoint.TeamsPath);
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Developer));
        var listed = await (await http.SendAsync(list)).Content.ReadFromJsonAsync<EnrollHostedTeamsResponse>();
        Assert.Equal(new[] { "Acme", "Beta" }, listed!.Teams.Select(t => t.Name).ToArray());

        using var enroll = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
        {
            Content = JsonContent.Create(new { deviceId = "director-w", machineName = "M", platform = "linux", deviceType = "workstation", teamId = _team }),
        };
        enroll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Developer));
        var enrolled = await (await http.SendAsync(enroll)).Content.ReadFromJsonAsync<DeviceRegistrationResponse>();
        Assert.Equal(_team, Active(enrolled!.DeviceKey).TenantId);

        using var move = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.MovePath)
        {
            Content = JsonContent.Create(new { deviceKey = enrolled.DeviceKey, teamId = _secondTeam }),
        };
        move.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Developer));
        var moveResponse = await http.SendAsync(move);
        Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);
        var moved = await moveResponse.Content.ReadFromJsonAsync<DeviceRegistrationResponse>();
        Assert.Equal(_secondTeam, Active(moved!.DeviceKey).TenantId);

        using var collaborator = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
        {
            Content = JsonContent.Create(new { deviceId = "director-x", machineName = "M", teamId = _team }),
        };
        collaborator.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Collaborator));
        var refused = await http.SendAsync(collaborator);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Collaborator may not run sessions", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Map_TeamsDark_TheTwoTeamRoutesAreAbsent_ATeamIdIsRefused_AndPersonalEnrollmentWorks()
    {
        await using var app = await Host(null);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var list = new HttpRequestMessage(HttpMethod.Get, HostedEnrollmentEndpoint.TeamsPath);
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Owner));
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(list)).StatusCode);

        using var move = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.MovePath)
        {
            Content = JsonContent.Create(new { deviceKey = "k" }),
        };
        move.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Owner));
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(move)).StatusCode);

        using var team = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
        {
            Content = JsonContent.Create(new { deviceId = "d-1", machineName = "M", teamId = _team }),
        };
        team.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Owner));
        var refused = await http.SendAsync(team);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("does not offer teams yet", await refused.Content.ReadAsStringAsync());

        using var personal = new HttpRequestMessage(HttpMethod.Post, HostedEnrollmentEndpoint.Path)
        {
            Content = JsonContent.Create(new { deviceId = "d-2", machineName = "M" }),
        };
        personal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(Owner));
        var ok = await http.SendAsync(personal);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var key = (await ok.Content.ReadFromJsonAsync<DeviceRegistrationResponse>())!.DeviceKey;
        Assert.Equal(_tenants.LookupBySubject(Owner)!.Value.Value, Active(key).TenantId);
    }

    private async Task<WebApplication> Host(HostedEnrollmentEndpoint.TeamEnrollment? teams)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        HostedEnrollmentEndpoint.Map(app, _devices, _tenants, _validator, Entitlements(), new TrialRegistry(_db), teams);
        await app.StartAsync();
        return app;
    }
}
