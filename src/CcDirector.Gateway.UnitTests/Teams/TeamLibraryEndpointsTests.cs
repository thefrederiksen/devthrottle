using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The team library routes' own admission (devthrottle_internal#2304): a route enters the team its path names ONLY
/// when the team gate recorded that it allowed this very request in that very team. The end-to-end behaviour - each
/// role over real HTTP, the three tests of the issue - is HostedTeamLibraryTests in the Gateway suite.
/// </summary>
public sealed class TeamLibraryEndpointsTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;

    public TeamLibraryEndpointsTests()
    {
        _db = _harness.Open();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private static DefaultHttpContext Request(string? routeTeam, string? allowedTeam)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        if (routeTeam is not null)
            ctx.Request.RouteValues["teamId"] = routeTeam;
        if (allowedTeam is not null)
            ctx.Items[TeamEndpointGate.AllowedTeamItemKey] = allowedTeam;
        return ctx;
    }

    [Fact]
    public void TeamToEnter_TheGateAllowedThisTeam_ReturnsIt()
    {
        Assert.Equal("team-a", TeamLibraryEndpoints.TeamToEnter(Request("team-a", "team-a")));
    }

    [Fact]
    public void TeamToEnter_TheGateNeverRan_ReturnsNull()
    {
        Assert.Null(TeamLibraryEndpoints.TeamToEnter(Request("team-a", null)));
    }

    [Fact]
    public void TeamToEnter_TheGateAllowedADifferentTeam_ReturnsNull()
    {
        Assert.Null(TeamLibraryEndpoints.TeamToEnter(Request("team-a", "team-b")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TeamToEnter_NoTeamInTheRoute_ReturnsNull(string? routeTeam)
    {
        Assert.Null(TeamLibraryEndpoints.TeamToEnter(Request(routeTeam, routeTeam)));
    }

    [Fact]
    public void TeamToEnter_NoContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TeamLibraryEndpoints.TeamToEnter(null!));
    }

    [Fact]
    public async Task AdmitIntoTeam_SelfHostedGateway_RefusesWithTheSelfHostedReason_EvenWhenTheGateAllowed()
    {
        Assert.False(GatewayHostedMode.IsHosted);
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), _devices);

        var (team, subject, denial) = TeamLibraryEndpoints.AdmitIntoTeam(Request("team-a", "team-a"), boundary, _tenants);

        Assert.Null(team);
        Assert.Null(subject);
        var (status, body) = await RenderAsync(denial!);
        Assert.Equal(404, status);
        Assert.Equal(TeamEndpoints.SelfHostedRefusal, body.GetProperty("error").GetString());
    }

    [Fact]
    public void MemberReference_SamePersonSameTeam_IsStable_AndCarriesNoEmailOrSubject()
    {
        var first = TeamLibraryEndpoints.MemberReference("team-a", "sub-priya@example.com");
        var second = TeamLibraryEndpoints.MemberReference("team-a", " sub-priya@example.com ");

        Assert.Equal(first, second);
        Assert.StartsWith(TeamLibraryEndpoints.MemberReferencePrefix, first);
        Assert.DoesNotContain("@", first);
        Assert.DoesNotContain("sub-priya", first);
    }

    [Fact]
    public void MemberReference_SamePersonTwoTeams_Differs()
    {
        Assert.NotEqual(TeamLibraryEndpoints.MemberReference("team-a", "sub-priya"), TeamLibraryEndpoints.MemberReference("team-b", "sub-priya"));
    }

    [Theory]
    [InlineData(null, "sub")]
    [InlineData(" ", "sub")]
    [InlineData("team-a", null)]
    [InlineData("team-a", "")]
    public void MemberReference_Blank_Throws(string? teamId, string? subject)
    {
        Assert.ThrowsAny<ArgumentException>(() => TeamLibraryEndpoints.MemberReference(teamId!, subject!));
    }

    [Fact]
    public void ChangedBy_AReferenceToAMember_IsTheirName()
    {
        var reference = TeamLibraryEndpoints.MemberReference("team-a", "sub-priya");
        var names = new Dictionary<string, string> { [reference] = "Priya" };
        Assert.Equal("Priya", TeamLibraryEndpoints.ChangedBy(reference, names));
    }

    [Fact]
    public void ChangedBy_AReferenceToSomeoneNoLongerInTheTeam_IsAFormerMember()
    {
        var reference = TeamLibraryEndpoints.MemberReference("team-a", "sub-gone");
        Assert.Equal(TeamLibraryEndpoints.FormerMember, TeamLibraryEndpoints.ChangedBy(reference, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("priya@example.com")]
    [InlineData("an agent")]
    public void ChangedBy_AnythingTheServerDidNotStamp_IsNotRecorded_NeverTheTypedText(string? authoredBy)
    {
        Assert.Equal(TeamLibraryEndpoints.NotRecorded, TeamLibraryEndpoints.ChangedBy(authoredBy, new Dictionary<string, string>()));
    }

    [Fact]
    public void ServerStampedAuthor_Stamped_WinsOverTheClaim()
    {
        var ctx = new DefaultHttpContext();
        ServerStampedAuthor.Set(ctx, "team-member:abc");
        Assert.Equal("team-member:abc", ServerStampedAuthor.Resolve(ctx, "typed by the client"));
    }

    [Theory]
    [InlineData("typed by the client")]
    [InlineData(null)]
    public void ServerStampedAuthor_NotStamped_IsTheClaimUnchanged(string? claimed)
    {
        Assert.Equal(claimed, ServerStampedAuthor.Resolve(new DefaultHttpContext(), claimed));
    }

    [Fact]
    public void ServerStampedAuthor_BlankStampOrNoContext_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => ServerStampedAuthor.Set(new DefaultHttpContext(), " "));
        Assert.Throws<ArgumentNullException>(() => ServerStampedAuthor.Resolve(null!, "x"));
    }

    [Fact]
    public void MemberName_IsTheEmail_OrSaysNoneIsRecorded()
    {
        Assert.Equal("a@example.com", TeamEndpoints.MemberName(new TeamMember("s", "a@example.com", TeamRole.Developer, DateTime.UtcNow)));
        Assert.Equal("An account with no email recorded", TeamEndpoints.MemberName(new TeamMember("s", null, TeamRole.Developer, DateTime.UtcNow)));
    }

    [Fact]
    public void Map_NullArguments_Throw()
    {
        var app = WebApplication.CreateBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => TeamLibraryEndpoints.Map(app, null!, null!, null!, null!, null!, null!));
    }

    [Theory]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/skills")]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/library")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/workflows")]
    public void SessionKeyGuard_TheTeamLibraryRoutes_AreRefusedToAnAgentSessionKey(string method, string path)
    {
        // Managed from a person's own account only. A session reaches the team's library through /gateway/skills with
        // its own key, which is bound to the team's tenant.
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path).Allowed);
        Assert.False(CcDirector.Gateway.Util.SessionKeyGuard.Check(method, path, raised: true).Allowed);
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
