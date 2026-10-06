using System.Net;
using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The invitation routes' answers (devthrottle_internal#2301), rendered exactly as a client receives them, with the
/// caller already known. The device-key-to-caller step and the release switch are proven over real HTTP in the Gateway
/// suite's <c>HostedTeamInvitationEndpointsTests</c>.
/// </summary>
public sealed class TeamInvitationEndpointsTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly RecordingMailer _mailer = new();
    private readonly string _team;

    public TeamInvitationEndpointsTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        _teams = new TeamRegistry(_db, tenants, readTeamBill: _ => new TeamBill(true, true, "active", 1));
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teams.AddMember(_team, Developer, TeamRole.Developer);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task CreateAsync_Stored_IsCreatedAndAsksTheMailerForThatInvitationOnly()
    {
        var (status, body) = await RenderAsync(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, Owner, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest("Anna@Example.com", "developer"), CancellationToken.None));

        Assert.Equal(201, status);
        var invitation = body.GetProperty("invitation");
        Assert.Equal("anna@example.com", invitation.GetProperty("email").GetString());
        Assert.Equal("Developer", invitation.GetProperty("role").GetString());
        Assert.Equal("sent", invitation.GetProperty("state").GetString());
        Assert.False(invitation.TryGetProperty("acceptToken", out _));
        var sent = Assert.Single(_mailer.Sent);
        Assert.Equal(invitation.GetProperty("id").GetString(), sent.Id);
        // The link's secret went to the mailer and nowhere into the answer.
        Assert.False(string.IsNullOrEmpty(sent.Token));
        Assert.DoesNotContain(sent.Token, body.GetRawText());
        Assert.True(body.GetProperty("email").GetProperty("sent").GetBoolean());
    }

    [Fact]
    public async Task CreateAsync_EmailNotSent_StillCreated_AndSaysWhyAndToResend()
    {
        _mailer.Answer = new TeamInvitationMailResult(false, "The email service is down.", 503, null);

        var (status, body) = await RenderAsync(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, Owner, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest("anna@example.com", "Collaborator"), CancellationToken.None));

        Assert.Equal(201, status);
        var email = body.GetProperty("email");
        Assert.False(email.GetProperty("sent").GetBoolean());
        Assert.Equal("The invitation is saved, but its email was not sent: The email service is down. Resend it from the team's invitations.",
            email.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Boss")]
    public async Task CreateAsync_NoRecognisableRole_IsABadRequestAndSendsNothing(string? role)
    {
        var (status, body) = await RenderAsync(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, Owner, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest("anna@example.com", role), CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Contains("Manager, Developer or Collaborator", body.GetProperty("error").GetString());
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task CreateAsync_DeveloperInviting_IsForbiddenAndSendsNothing()
    {
        var (status, body) = await RenderAsync(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, Developer, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest("anna@example.com", "Collaborator"), CancellationToken.None));

        Assert.Equal(403, status);
        Assert.Equal(TeamInvitationRefusals.NotAllowedToInvite, body.GetProperty("error").GetString());
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task ResendAsync_Renewed_SendsTheEmailAgain()
    {
        var id = _teams.CreateInvitation(_team, Owner, "anna@example.com", TeamRole.Developer).Invitation!.Id;

        var (status, _) = await RenderAsync(await TeamInvitationEndpoints.ResendAsync(_teams, _mailer, Owner, _team, id, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Equal(id, Assert.Single(_mailer.Sent).Id);
    }

    [Fact]
    public async Task Options_ForTheOwner_ListsTheThreeRolesWithTheirSentences()
    {
        var (status, body) = await RenderAsync(TeamInvitationEndpoints.Options(_teams, Owner, _team));

        Assert.Equal(200, status);
        Assert.Equal("Acme", body.GetProperty("teamName").GetString());
        Assert.Equal(new[] { "Manager", "Developer", "Collaborator" },
            body.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("role").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("blocked").ValueKind);
        Assert.Equal("The invitation expires in 7 days.", body.GetProperty("expiryNote").GetString());
    }

    [Fact]
    public async Task OptionsAndList_NotAMember_AreNotFound()
    {
        Assert.Equal(404, (await RenderAsync(TeamInvitationEndpoints.Options(_teams, "sub-outsider", _team))).Status);
        Assert.Equal(404, (await RenderAsync(TeamInvitationEndpoints.List(_teams, "sub-outsider", _team))).Status);
    }

    [Theory]
    [InlineData(TeamInvitationOutcome.NotFound, 404)]
    [InlineData(TeamInvitationOutcome.Forbidden, 403)]
    [InlineData(TeamInvitationOutcome.Refused, 409)]
    [InlineData(TeamInvitationOutcome.Unavailable, 503)]
    public async Task Answer_EachRefusal_HasItsStatusAndCarriesTheReason(TeamInvitationOutcome outcome, int expected)
    {
        var (status, body) = await RenderAsync(TeamInvitationEndpoints.Answer(new TeamInvitationResult(outcome, "Because.", null), "test"));

        Assert.Equal(expected, status);
        Assert.Equal("Because.", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/invite/AbC_-123xyz", "/invite/[redacted]")]
    [InlineData("?next=%2Finvite%2FAbC_-123xyz", "?next=%2Finvite%2F[redacted]")]
    [InlineData("?next=%2Finvite%2FAbC_-123xyz&x=1", "?next=%2Finvite%2F[redacted]&x=1")]
    [InlineData("/teams/abc/invitations", "/teams/abc/invitations")]
    [InlineData("/sessions", "/sessions")]
    [InlineData("", "")]
    public void RedactForLog_TheAcceptPagesSecret_IsCutOut_AndNothingElseChanges(string input, string expected)
    {
        Assert.Equal(expected, TeamInvitationEndpoints.RedactForLog(input));
    }

    [Theory]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/invitations")]
    [InlineData("GET", "/teams/3f1d2c9e-0000-4000-8000-000000000001/invitations/options")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/invitations")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/invitations/abc/resend")]
    [InlineData("POST", "/teams/3f1d2c9e-0000-4000-8000-000000000001/invitations/abc/cancel")]
    [InlineData("POST", "/team-invitations/open")]
    [InlineData("POST", "/team-invitations/accept")]
    [InlineData("POST", "/team-invitations/decline")]
    public void SessionKeyGuard_EveryInvitationRoute_IsRefusedToAnAgentSessionKey(string method, string path)
    {
        // An agent's session key resolves to its owner's personal tenant; an invitation route open to it would let any
        // agent invite people onto the owner's bill or accept an invitation as the owner.
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

    private sealed class RecordingMailer : ITeamInvitationMailer
    {
        public List<(string Id, string Token)> Sent { get; } = new();
        public TeamInvitationMailResult Answer { get; set; } = new(true, null, 200, null);

        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default)
        {
            Sent.Add((invitationId, acceptToken));
            return Task.FromResult(Answer);
        }
    }
}
