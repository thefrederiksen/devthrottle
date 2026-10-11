using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A Director whose person was removed from the team must be able to say so (devthrottle_internal#2311, live proof F3).
/// The 401 for a revoked key carries <c>reason: team_member_removed</c> for exactly that revoke, and for no other: every
/// other revoke - a move, a role change, a key judged revoked only by its binding - answers today's body byte for byte.
/// Over a real, throwaway, fully migrated Gateway database, the registry in hosted mode, through the real middleware.
/// </summary>
public sealed class RevokedKeyReasonTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";

    // Today's body, written out here rather than read from the middleware, so a change to it fails this test.
    private const string TodaysRevokedBody = "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\"}";
    private const string TeamRemovalBody =
        "{\"error\":\"device credential revoked\",\"code\":\"device_credential_revoked\",\"reason\":\"team_member_removed\"}";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly string _team;

    public RevokedKeyReasonTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private string TeamKey(string deviceId) =>
        _devices.RegisterForTenant(new TenantId(_team), Developer, _team + "|" + deviceId, "M-" + deviceId).DeviceKey;

    private async Task<(int Status, string Body, bool Continued)> CallAsync(Action<HttpContext> credentials)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/sessions";
        ctx.Response.Body = new MemoryStream();
        credentials(ctx);
        var continued = false;

        await AuthMiddleware.Run(
            ctx,
            new AuthMiddleware.RequireToken { Hosted = false, Token = "shared-token", Devices = _devices },
            () => { continued = true; return Task.CompletedTask; });

        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(), continued);
    }

    private static Action<HttpContext> Bearer(string key) => ctx => ctx.Request.Headers.Authorization = "Bearer " + key;

    [Fact]
    public void ResolveCredential_KeyRevokedForTeamRemoval_CarriesThatReason()
    {
        var key = TeamKey("dir-1");
        _devices.RevokeTenantMember(new TenantId(_team), Developer, TeamMemberAccessRevoker.RemovedReason);

        var resolution = _devices.ResolveCredential(key);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, resolution.Kind);
        Assert.Equal("team_member_removed", resolution.RevokedReason);
    }

    [Fact]
    public void ResolveCredential_ActiveKey_CarriesNoReason()
    {
        var resolution = _devices.ResolveCredential(TeamKey("dir-1"));

        Assert.Equal(DeviceCredentialResolutionKind.Active, resolution.Kind);
        Assert.Null(resolution.RevokedReason);
    }

    [Fact]
    public async Task Run_KeyRevokedForTeamRemoval_Answers401WithTheTeamRemovalReason()
    {
        var key = TeamKey("dir-1");
        _devices.RevokeTenantMember(new TenantId(_team), Developer, TeamMemberAccessRevoker.RemovedReason);

        var (status, body, continued) = await CallAsync(Bearer(key));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(TeamRemovalBody, body);
    }

    [Theory]
    [InlineData("director_moved_to_another_team")]
    [InlineData("team_role_cannot_run_sessions")]
    [InlineData("team_member_removed_later")]
    [InlineData("TEAM_MEMBER_REMOVED")]
    public async Task Run_KeyRevokedForAnotherReason_AnswersTodaysBodyByteForByte(string reason)
    {
        var key = TeamKey("dir-1");
        Assert.True(_devices.RevokeDevice(_team + "|dir-1", reason));

        var (status, body, _) = await CallAsync(Bearer(key));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(TodaysRevokedBody, body);
    }

    // Removed from the team with no revoker attached: the registry judges the key revoked by its binding alone, and the
    // row carries no reason. That is not proof of a removal, so the answer does not claim one.
    [Fact]
    public async Task Run_KeyJudgedRevokedOnlyByItsBinding_AnswersTodaysBody()
    {
        var key = TeamKey("dir-1");
        Assert.True(_teams.RemoveMember(_team, Developer).IsDone);

        var (status, body, _) = await CallAsync(Bearer(key));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(TodaysRevokedBody, body);
    }

    // Two revoked keys on one request, one removed from the team and one moved: the answer cannot speak for both, so it
    // claims neither.
    [Fact]
    public async Task Run_TwoRevokedKeysWithDifferentReasons_AnswersTodaysBody()
    {
        var removed = TeamKey("dir-1");
        var moved = TeamKey("dir-2");
        Assert.True(_devices.RevokeDevice(_team + "|dir-2", "director_moved_to_another_team"));
        _devices.RevokeTenantMember(new TenantId(_team), Developer, TeamMemberAccessRevoker.RemovedReason);

        var (status, body, _) = await CallAsync(ctx =>
        {
            ctx.Request.Headers.Authorization = "Bearer " + removed;
            ctx.Request.Headers.Cookie = AuthMiddleware.CookieName + "=" + moved;
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(TodaysRevokedBody, body);
    }

    [Fact]
    public async Task Run_UnknownKey_StillAnswersMissingOrInvalidToken()
    {
        var (status, body, _) = await CallAsync(Bearer("not-a-key"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal("{\"error\":\"missing or invalid token\"}", body);
    }
}
