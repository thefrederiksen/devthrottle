using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team routes' answers (devthrottle_internal#2300), rendered exactly as a client receives them. The hosted
/// caller resolution - device key to account - is proven over real HTTP in the Gateway suite's
/// <c>HostedTeamEndpointsTests</c>; here the handlers are driven with the caller already known, plus the
/// self-hosted refusal, which needs no hosted process.
/// </summary>
public sealed class TeamEndpointsTests : IDisposable
{
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;

    public TeamEndpointsTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ListTeams_AccountInNoTeam_AnswersAnEmptyListWithACount()
    {
        var (status, body) = await RenderAsync(TeamEndpoints.ListTeams(_teams, Alice, ownAccountHasADirector: false));

        Assert.Equal(200, status);
        Assert.Equal(0, body.GetProperty("count").GetInt32());
        Assert.Equal(0, body.GetProperty("teams").GetArrayLength());
    }

    [Fact]
    public async Task ListTeams_TwoTeams_EachCarriesTheCallersRoleAndThePeopleCount()
    {
        var mine = _teams.CreateTeam(Alice, "DevThrottle").Team!;
        var pauls = _teams.CreateTeam(Bob, "Paul's project").Team!;
        _teams.AddMember(pauls.TeamId, Alice, TeamRole.Developer);

        var (_, body) = await RenderAsync(TeamEndpoints.ListTeams(_teams, Alice, ownAccountHasADirector: false));

        var teams = body.GetProperty("teams").EnumerateArray().ToList();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.Equal(mine.TeamId, teams[0].GetProperty("id").GetString());
        Assert.Equal("DevThrottle", teams[0].GetProperty("name").GetString());
        Assert.Equal("Owner", teams[0].GetProperty("role").GetString());
        Assert.Equal("1 person", teams[0].GetProperty("people").GetString());
        Assert.Equal("Paul's project", teams[1].GetProperty("name").GetString());
        Assert.Equal("Developer", teams[1].GetProperty("role").GetString());
        Assert.Equal(2, teams[1].GetProperty("memberCount").GetInt32());
        Assert.Equal("2 people", teams[1].GetProperty("people").GetString());
    }

    [Fact]
    public async Task ListTeams_ACollaboratorAndADeveloper_EachTeamCarriesThePageVerdictForTheCallersRole()
    {
        var collab = _teams.CreateTeam(Bob, "DevThrottle").Team!;
        _teams.AddMember(collab.TeamId, Alice, TeamRole.Collaborator);
        var dev = _teams.CreateTeam(Bob, "Paul's project").Team!;
        _teams.AddMember(dev.TeamId, Alice, TeamRole.Developer);

        var (_, body) = await RenderAsync(TeamEndpoints.ListTeams(_teams, Alice, ownAccountHasADirector: false));

        var byId = body.GetProperty("teams").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("app"));
        var asCollaborator = byId[collab.TeamId];
        Assert.False(asCollaborator.GetProperty("full").GetBoolean());
        Assert.Equal(new[] { "Questions|/questions", "Requests|/requests", "Reports|/reports" },
            asCollaborator.GetProperty("pages").EnumerateArray().Select(p => $"{p.GetProperty("label").GetString()}|{p.GetProperty("path").GetString()}"));
        Assert.Equal("/questions", asCollaborator.GetProperty("landing").GetString());
        Assert.Equal("This page is not available to Collaborators.", asCollaborator.GetProperty("elsewhere").GetString());

        var asDeveloper = byId[dev.TeamId];
        Assert.True(asDeveloper.GetProperty("full").GetBoolean());
        Assert.Equal(JsonValueKind.Null, asDeveloper.GetProperty("landing").ValueKind);
        Assert.Equal(JsonValueKind.Null, asDeveloper.GetProperty("elsewhere").ValueKind);
    }

    [Fact]
    public async Task CreateTeam_ValidName_Answers201WithTheCallerAsOwner()
    {
        var (status, body) = await RenderAsync(TeamEndpoints.CreateTeam(_teams, Alice, new TeamEndpoints.CreateTeamRequest("Acme")));

        Assert.Equal(201, status);
        var team = body.GetProperty("team");
        Assert.Equal("Acme", team.GetProperty("name").GetString());
        Assert.Equal("Owner", team.GetProperty("role").GetString());
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(team.GetProperty("id").GetString()!, Alice));
    }

    [Fact]
    public async Task CreateTeam_NoBodyOrNoName_Answers400WithThePlainReason()
    {
        foreach (var request in new[] { null, new TeamEndpoints.CreateTeamRequest(null), new TeamEndpoints.CreateTeamRequest("  ") })
        {
            var (status, body) = await RenderAsync(TeamEndpoints.CreateTeam(_teams, Alice, request));

            Assert.Equal(400, status);
            Assert.StartsWith("A team needs a name.", body.GetProperty("error").GetString());
        }
        Assert.Empty(_teams.ListTeamsFor(Alice));
    }

    [Fact]
    public async Task ListMembers_ForAMember_ListsRolesEmailsAndWhichOneIsTheCaller()
    {
        _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        _tenants.MintOrLookupBySubject(Bob, "bob@example.com");
        var team = _teams.CreateTeam(Alice, "Acme").Team!;
        _teams.AddMember(team.TeamId, Bob, TeamRole.Developer);
        _teams.AddMember(team.TeamId, "sub-no-email", TeamRole.Collaborator);

        var (status, body) = await RenderAsync(TeamEndpoints.ListMembers(_teams, Bob, team.TeamId));

        Assert.Equal(200, status);
        Assert.Equal("Developer", body.GetProperty("team").GetProperty("role").GetString());
        Assert.Equal(3, body.GetProperty("count").GetInt32());
        var members = body.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(new[] { "Owner", "Developer", "Collaborator" }, members.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(new[] { false, true, false }, members.Select(m => m.GetProperty("isYou").GetBoolean()));
        Assert.Equal("alice@example.com", members[0].GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, members[2].GetProperty("email").ValueKind);
        Assert.Equal("An account with no email recorded", members[2].GetProperty("name").GetString());
        // The account subject is the key and is never sent.
        Assert.DoesNotContain(Alice, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Bob, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMembers_ForANonMemberOrAnUnknownTeam_Answers404WithOneMessage()
    {
        var team = _teams.CreateTeam(Alice, "Acme").Team!;

        foreach (var teamId in new[] { team.TeamId, Guid.NewGuid().ToString(), null })
        {
            var (status, body) = await RenderAsync(TeamEndpoints.ListMembers(_teams, Bob, teamId));

            Assert.Equal(404, status);
            Assert.Equal(TeamEndpoints.NoSuchTeamRefusal, body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task ResolveCaller_SelfHostedGateway_RefusesWithTheSelfHostedReason()
    {
        // The unit test process is not hosted (CC_GATEWAY_HOSTED is unset), which is the self-hosted Gateway.
        Assert.False(GatewayHostedMode.IsHosted);
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry(_db));

        var (subject, denial) = TeamEndpoints.ResolveCaller(new DefaultHttpContext(), boundary, _tenants);

        Assert.Null(subject);
        var (status, body) = await RenderAsync(denial!);
        Assert.Equal(404, status);
        Assert.Equal(TeamEndpoints.SelfHostedRefusal, body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("GET", "/teams")]
    [InlineData("POST", "/teams")]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/members")]
    public void SessionKeyGuard_EveryTeamRoute_IsRefusedToAnAgentSessionKey(string method, string path)
    {
        // A session key resolves to its owner's personal tenant, so a team route opened to session keys would let any
        // agent create teams or read member lists as the owner. The routes are the owner's own devices' only (F4).
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path).Allowed);
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path, raised: true).Allowed);
    }

    private TeamCallerOwnership Ownership(Discovery.DirectorRegistry directors)
    {
        var devices = new DeviceRegistry(_db);
        return new TeamCallerOwnership(directors, new Streaming.PushedSessionStore(), devices, new CcDirector.Gateway.History.SessionTurnStore(_db),
            new HostedTenantBoundary(new SingleTenantContext(), devices), new SessionKeyRegistry(_db));
    }

    private TeamFleetMap NewFleetMap(out Discovery.DirectorRegistry directors)
    {
        directors = new Discovery.DirectorRegistry(Path.Combine(Path.GetTempPath(), "cc-teamep-" + Guid.NewGuid().ToString("N")));
        return new TeamFleetMap(_teams, new TeamAccess(_teams), directors, Ownership(directors),
            new Streaming.PushedSessionStore());
    }

    [Fact]
    public async Task ReadFleetMap_AMember_Answers200WithTheAllowListOnly()
    {
        _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        var team = _teams.CreateTeam(Alice, "DevThrottle").Team!;
        var map = NewFleetMap(out var directors);
        using var _ = directors;

        var (status, body) = await RenderAsync(TeamEndpoints.ReadFleetMap(map, Alice, team.TeamId));

        Assert.Equal(200, status);
        Assert.Equal("DevThrottle", body.GetProperty("teamName").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());
        Assert.Equal("everyone", body.GetProperty("scope").GetString());
        Assert.Equal(new[] { "emptyText", "layouts", "people", "role", "scope", "summary", "teamId", "teamName" },
            body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ReadFleetMap_ACollaborator_Answers403WithTheRoleTablesSentence()
    {
        var team = _teams.CreateTeam(Alice, "DevThrottle").Team!;
        Assert.True(_teams.AddMember(team.TeamId, Bob, TeamRole.Collaborator).IsDone);
        var map = NewFleetMap(out var directors);
        using var _ = directors;

        var (status, body) = await RenderAsync(TeamEndpoints.ReadFleetMap(map, Bob, team.TeamId));

        Assert.Equal(403, status);
        Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        Assert.Contains("Collaborator may not see the team's Fleet Map", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReadFleetMap_ANonMemberOrAnUnknownTeam_Answers404WithOneMessage()
    {
        var team = _teams.CreateTeam(Alice, "DevThrottle").Team!;
        var map = NewFleetMap(out var directors);
        using var _ = directors;

        foreach (var teamId in new[] { team.TeamId, Guid.NewGuid().ToString(), null })
        {
            var (status, body) = await RenderAsync(TeamEndpoints.ReadFleetMap(map, Bob, teamId));
            Assert.Equal(404, status);
            Assert.Equal(TeamEndpoints.NoSuchTeamRefusal, body.GetProperty("error").GetString());
        }
    }

    private static async Task<(int Status, JsonElement Body)> RenderAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
