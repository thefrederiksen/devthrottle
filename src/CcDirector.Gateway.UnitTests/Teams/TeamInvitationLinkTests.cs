using System.Text.Json;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
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
/// THE OWNER CAN COPY AN INVITATION LINK (Teams v1). The invitation email cannot be sent until the website's sender is
/// live (devthrottle_internal#2316), so the create and resend answers carry the accept link, once, to the person who
/// sent it. Over a real, fully migrated Gateway database:
///  - the link is in the create and resend answers, it is <c>{base}/invite/{token}</c>, and that token accepts;
///  - resend makes a new link and the old one no longer opens anything;
///  - the token is in NO other answer (the list, the options, cancel, a refusal), in NO log line, and in NO stored
///    column - the row keeps only its hash;
///  - a Developer still cannot create an invitation, and gets no link;
///  - with no public address there is no link, and the answer says so.
/// In the FileLog capture collection because it reads the log lines the routes wrote.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class TeamInvitationLinkTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";
    private const string Newcomer = "sub-newcomer";
    private const string Base = "https://gateway.example";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly RecordingMailer _mailer = new();
    private readonly string _team;

    public TeamInvitationLinkTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        tenants.MintOrLookupBySubject(Newcomer, "anna@example.com");
        _teams = new TeamRegistry(_db, tenants, readTeamBill: _ => new TeamBill(true, true, "active", 1));
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teams.AddMember(_team, Developer, TeamRole.Developer);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Create_TheAnswerCarriesTheAcceptLink_ForTheSender_AndThatLinkAccepts()
    {
        var (status, body) = await Create(Owner, "anna@example.com", "Developer");

        Assert.Equal(201, status);
        var token = Assert.Single(_mailer.Sent).Token;
        var link = body.GetProperty("link");
        Assert.Equal($"{Base}/invite/{token}", link.GetProperty("url").GetString());
        var note = link.GetProperty("note").GetString()!;
        Assert.Contains("anna@example.com", note);
        Assert.Contains("as Developer", note);
        Assert.Contains("Resend", note);

        // The token in the link is the one that opens and accepts the invitation.
        var accepted = _teams.AcceptInvitation(TokenOf(link), Newcomer);
        Assert.Equal(TeamInvitationOutcome.Done, accepted.Outcome);
    }

    [Fact]
    public async Task Resend_TheAnswerCarriesANewLink_ThatAccepts_AndTheOldLinkNoLongerOpens()
    {
        var (_, created) = await Create(Owner, "anna@example.com", "Collaborator");
        var oldToken = TokenOf(created.GetProperty("link"));
        var id = created.GetProperty("invitation").GetProperty("id").GetString()!;

        var (status, resent) = await Render(await TeamInvitationEndpoints.ResendAsync(_teams, _mailer, () => Base, Owner, _team, id, CancellationToken.None));

        Assert.Equal(200, status);
        var newToken = TokenOf(resent.GetProperty("link"));
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(_mailer.Sent[^1].Token, newToken);
        Assert.Equal(TeamInvitationOutcome.NotFound, _teams.OpenInvitation(oldToken, Newcomer).Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(newToken, Newcomer).Outcome);
    }

    [Fact]
    public async Task TheToken_IsInNoOtherAnswer_NoLogLine_AndNoStoredColumn()
    {
        string token;
        string id;
        IReadOnlyList<string> lines;
        var others = new List<string>();
        using (var log = FileLog.RedirectForTests())
        {
            var (_, created) = await Create(Owner, "anna@example.com", "Developer");
            token = TokenOf(created.GetProperty("link"));
            id = created.GetProperty("invitation").GetProperty("id").GetString()!;

            others.Add((await Render(TeamInvitationEndpoints.List(_teams, Owner, _team))).Body.GetRawText());
            others.Add((await Render(TeamInvitationEndpoints.Options(_teams, Owner, _team))).Body.GetRawText());
            others.Add((await Render(TeamInvitationEndpoints.Answer(_teams.OpenInvitation(token, Newcomer), "open"))).Body.GetRawText());
            // A second invitation to the same address is refused; the refusal carries no link either.
            others.Add((await Create(Owner, "anna@example.com", "Developer")).Body.GetRawText());
            others.Add((await Render(TeamInvitationEndpoints.Answer(_teams.CancelInvitation(_team, id, Owner), "cancel"))).Body.GetRawText());
            lines = log.DrainAndReadLines();
        }

        Assert.All(others, answer => Assert.DoesNotContain(token, answer));
        Assert.All(others, answer => Assert.DoesNotContain("/invite/", answer));
        // The routes did log - the proof is not an empty log - and no line carries the secret or the link.
        Assert.Contains(lines, l => l.Contains("[TeamInvitationEndpoints] POST invitations: stored", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("link shown=True", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("/invite/", StringComparison.Ordinal));

        // Every stored column of the invitation row: the hash is there, the token is not.
        using var ctx = _db.CreateUnscopedContext();
        var row = ctx.TeamInvitations.AsNoTracking().Single(i => i.Id == id);
        var stored = typeof(CcDirector.Gateway.Data.Entities.TeamInvitationEntity).GetProperties()
            .Select(p => p.GetValue(row)?.ToString() ?? "")
            .ToList();
        Assert.Contains(TeamInvitationRules.HashAcceptToken(token), stored);
        Assert.DoesNotContain(stored, value => value.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeveloper_StillCannotCreateAnInvitation_AndGetsNoLink()
    {
        var (status, body) = await Create(Developer, "anna@example.com", "Collaborator");

        Assert.Equal(403, status);
        Assert.False(body.TryGetProperty("link", out _));
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public async Task NoPublicAddress_TheInvitationIsSaved_AndTheAnswerSaysTheLinkCannotBeShown()
    {
        var (status, body) = await Render(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, () => null, Owner, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest("anna@example.com", "Developer"), CancellationToken.None));

        Assert.Equal(201, status);
        var link = body.GetProperty("link");
        Assert.Equal(JsonValueKind.Null, link.GetProperty("url").ValueKind);
        Assert.Contains("has no public address", link.GetProperty("note").GetString());
        Assert.Single(_mailer.Sent);
    }

    private async Task<(int Status, JsonElement Body)> Create(string caller, string email, string role) =>
        await Render(await TeamInvitationEndpoints.CreateAsync(_teams, _mailer, () => Base, caller, _team,
            new TeamInvitationEndpoints.CreateInvitationRequest(email, role), CancellationToken.None));

    private static string TokenOf(JsonElement link)
    {
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith(Base + "/invite/", url);
        return Uri.UnescapeDataString(url[(Base + "/invite/").Length..]);
    }

    private static async Task<(int Status, JsonElement Body)> Render(IResult result)
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

        public Task<TeamInvitationMailResult> SendAsync(string invitationId, string teamId, string acceptToken, CancellationToken ct = default)
        {
            Sent.Add((invitationId, acceptToken));
            return Task.FromResult(new TeamInvitationMailResult(true, null, 200, null));
        }
    }
}
