using System.Text.Json;
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
/// Where a fresh browser starts (devthrottle_internal#2306, review finding F1): the own account if the person has ever
/// registered a Director there, else their only team, else the chooser - a verdict on GET /teams, and the device-registry
/// question it rests on.
/// </summary>
public sealed class TeamStartTests : IDisposable
{
    private const string Alice = "sub-start-alice";
    private const string Bob = "sub-start-bob";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;

    public TeamStartTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, storePath: null, isHosted: true);
        _devices.Initialize();
    }

    public void Dispose() => _harness.Dispose();

    private static TeamSummary Team(string id) => new(id, "Team " + id, TeamRole.Collaborator, 2);

    [Fact]
    public void For_NoTeams_StartsOnTheOwnAccount()
    {
        Assert.Equal(new TeamStartVerdict(TeamStartPlace.OwnAccount, null), TeamStart.For(false, Array.Empty<TeamSummary>()));
    }

    [Fact]
    public void For_ADirectorOnTheOwnAccount_StartsThereWhateverTheTeams()
    {
        Assert.Equal(TeamStartPlace.OwnAccount, TeamStart.For(true, new[] { Team("a") }).Place);
        Assert.Equal(TeamStartPlace.OwnAccount, TeamStart.For(true, new[] { Team("a"), Team("b") }).Place);
    }

    [Fact]
    public void For_NoDirectorAndOneTeam_StartsInThatTeam()
    {
        Assert.Equal(new TeamStartVerdict(TeamStartPlace.Team, "a"), TeamStart.For(false, new[] { Team("a") }));
    }

    [Fact]
    public void For_NoDirectorAndSeveralTeams_OffersTheChooser()
    {
        Assert.Equal(new TeamStartVerdict(TeamStartPlace.Choose, null), TeamStart.For(false, new[] { Team("a"), Team("b") }));
    }

    [Theory]
    [InlineData(TeamStartPlace.OwnAccount, "own-account")]
    [InlineData(TeamStartPlace.Team, "team")]
    [InlineData(TeamStartPlace.Choose, "choose")]
    public void Wire_EachPlace_HasItsWord(TeamStartPlace place, string word)
    {
        Assert.Equal(word, TeamStart.Wire(place));
    }

    [Fact]
    public void HasEverEnrolledADirector_OnlyAPhoneAndABrowser_IsFalse()
    {
        var tenant = _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        _devices.RegisterForTenant(tenant, Alice, "phone-1", "PHONE", platform: "ios", deviceType: "phone");
        _devices.RegisterForTenant(tenant, Alice, "browser-1", "BROWSER", platform: "browser", deviceType: "browser");

        Assert.False(_devices.HasEverEnrolledADirector(tenant));
    }

    [Fact]
    public void HasEverEnrolledADirector_AWorkstation_IsTrue_AndOnlyForItsOwnTenant()
    {
        var alice = _tenants.MintOrLookupBySubject(Alice, "alice@example.com");
        var bob = _tenants.MintOrLookupBySubject(Bob, "bob@example.com");
        _devices.RegisterForTenant(alice, Alice, "director-1", "DESK", platform: "windows", deviceType: "workstation");

        Assert.True(_devices.HasEverEnrolledADirector(alice));
        Assert.False(_devices.HasEverEnrolledADirector(bob));
    }

    [Fact]
    public async Task ListTeams_OneTeamAndNoDirector_SaysAFreshBrowserStartsInThatTeam()
    {
        var team = _teams.CreateTeam(Bob, "DevThrottle").Team!;
        _teams.AddMember(team.TeamId, Alice, TeamRole.Collaborator);

        var body = await RenderAsync(TeamEndpoints.ListTeams(_teams, Alice, ownAccountHasADirector: false));

        var start = body.GetProperty("start");
        Assert.Equal("team", start.GetProperty("where").GetString());
        Assert.Equal(team.TeamId, start.GetProperty("teamId").GetString());
    }

    [Fact]
    public async Task ListTeams_ADirectorOnTheOwnAccount_SaysTheOwnAccount()
    {
        var team = _teams.CreateTeam(Bob, "DevThrottle").Team!;
        _teams.AddMember(team.TeamId, Alice, TeamRole.Collaborator);

        var body = await RenderAsync(TeamEndpoints.ListTeams(_teams, Alice, ownAccountHasADirector: true));

        Assert.Equal("own-account", body.GetProperty("start").GetProperty("where").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("start").GetProperty("teamId").ValueKind);
    }

    private static async Task<JsonElement> RenderAsync(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return JsonDocument.Parse(await new StreamReader(ctx.Response.Body).ReadToEndAsync()).RootElement.Clone();
    }
}
