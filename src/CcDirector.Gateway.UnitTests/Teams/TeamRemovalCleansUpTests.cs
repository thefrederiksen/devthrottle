using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Tests.Teams.Questions;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Team implementation piece 1.5 (devthrottle_internal#2789): removing someone from a team clears what was SENT to them
/// and keeps what they WROTE (security review M4); every membership change leaves one durable record of whose, which,
/// by whom and when (Risk 12); and a removed member's very next request is refused, because the gate asks the member
/// table on every request (Risk 9).
///
/// The team: the Owner (who writes the reports here), Alice the Manager, Mike and Nina the contributors. Over a real
/// database and an injected clock.
/// </summary>
public sealed class TeamRemovalCleansUpTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private static readonly DateTime Start = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly TeamQuestions _questions;
    private readonly TeamReports _reports;
    private readonly TeamEndpointGate _gate;
    private readonly string _team;
    private readonly TenantId _tenant;
    private DateTime _now = Start;

    public TeamRemovalCleansUpTests()
    {
        _db = _harness.Open();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "owner@example.com"), (Alice, "alice@example.com"), (Mike, "mike@example.com"), (Nina, "nina@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants, () => _now);
        var access = new TeamAccess(_teams);
        _store = new DevReportStore(_db);
        _recipients = new DevReportRecipients(_db);
        var comments = new DevReportPersonComments(_db);
        // No session is live here, so an answer is held and stored - which is all these tests need of it.
        var delivery = new DevReportDelivery(_store,
            (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Busy, null, "no session in these tests"),
            (_, _) => null, CancellationToken.None, () => _now);
        _questions = new TeamQuestions(_store, _recipients, comments, _teams, access, delivery, () => _now);
        _reports = new TeamReports(_store, _recipients, comments, _teams, access, _questions, new ContributorNotices(_db, _teams, () => _now), () => _now);
        _gate = new TeamEndpointGate(access, _teams, _tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false));

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(_team);
        Assert.True(_teams.AddMember(_team, Alice, TeamRole.Manager, Owner).IsDone);
        Assert.True(_teams.AddMember(_team, Mike, TeamRole.Collaborator, Owner).IsDone);
        Assert.True(_teams.AddMember(_team, Nina, TeamRole.Collaborator, Owner).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private string MemberId(string subject) => TeamMemberIds.For(_team, subject);

    /// <summary>A report with one question marked for contributors, written by the Owner and sent by them to
    /// <paramref name="to"/> through the send route.</summary>
    private async Task<Guid> SentReport(params string[] to)
    {
        var report = _store.Publish(_tenant, Guid.NewGuid().ToString("D"), @"C:\work\" + Guid.NewGuid().ToString("N") + ".html",
            TeamQuestionsTests.Html(), "waiting-on-you", "Pricing", _now, Owner).Report;
        var (status, _) = await Run(_reports.Send(_team, Owner, report.Id.ToString("D"), to.Select(MemberId).ToList(), 1));
        Assert.Equal(200, status);
        return report.Id;
    }

    private static async Task<(int Status, JsonElement Body)> Run(Task<IResult> pending) => await Run(await pending);

    private static async Task<(int Status, JsonElement Body)> Run(IResult result)
    {
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private List<TeamMembershipEventEntity> Records()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamMembershipEvents.Where(e => e.TeamId == _team).ToList().OrderBy(e => e.AtUtc).ToList();
    }

    private List<TeamMembershipEventEntity> RecordsOf(string subject) =>
        Records().Where(e => e.AccountSubject == subject).ToList();

    private TeamGateVerdict SentToMeRequest(string subject) =>
        _gate.Check("GET", TeamReportEndpoints.SentToMePattern, name => name == "teamId" ? _team : null,
            _tenants.MintOrLookupBySubject(subject, null), () => subject, _ => TeamOwnership.Unknown);

    // ---- M4: removal clears what was sent, keeps what was written -----------------------------------------------------

    [Fact]
    public async Task RemoveTeamMember_AContributorWhoWasSentReports_AddedBack_HasEmptyReportsAndQuestionsPages_AndTheOldReportIsNotReadableById()
    {
        var report = await SentReport(Mike);
        var id = report.ToString("D");
        Assert.Equal(1, (await Run(_reports.SentToMe(_team, Mike))).Body.GetProperty("count").GetInt32());

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Owner, MemberId(Mike)).Outcome);
        Assert.True(_teams.AddMember(_team, Mike, TeamRole.Collaborator, Owner).IsDone);

        var (pageStatus, page) = await Run(_reports.SentToMe(_team, Mike));
        Assert.Equal(200, pageStatus);
        Assert.Equal(0, page.GetProperty("count").GetInt32());
        Assert.Equal(0, page.GetProperty("reports").GetArrayLength());
        var (detailStatus, detail) = await Run(_reports.SentToMeDetail(_team, Mike, id));
        Assert.Equal(404, detailStatus);
        Assert.Equal(TeamReportEndpoints.NotSentToYou, detail.GetProperty("error").GetString());
        var html = NewHttpContext();
        await _reports.SentToMeHtml(_team, Mike, id, html).ExecuteAsync(html);
        Assert.Equal(404, html.Response.StatusCode);
        Assert.Equal(404, (await Run(_reports.MarkRead(_team, Mike, id, 1))).Status);
        Assert.Equal(404, (await Run(_reports.Comment(_team, Mike, id, "still here?"))).Status);
        var (_, questions) = await Run(_questions.List(_team, Mike));
        Assert.Equal(0, questions.GetProperty("count").GetInt32());
        Assert.Null(_recipients.RowFor(_tenant, report, Mike));
    }

    [Fact]
    public async Task RemoveTeamMember_AContributor_LeavesWhatWasSentToOtherMembersAlone()
    {
        var report = await SentReport(Mike, Nina);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Owner, MemberId(Mike)).Outcome);

        Assert.NotNull(_recipients.RowFor(_tenant, report, Nina));
        Assert.Equal(1, (await Run(_reports.SentToMe(_team, Nina))).Body.GetProperty("count").GetInt32());
        Assert.Equal(200, (await Run(_reports.SentToMeDetail(_team, Nina, report.ToString("D")))).Status);
    }

    [Fact]
    public async Task RemoveTeamMember_AContributorWhoCommentedAndAnswered_TheirWordsStayOnTheOwnersView()
    {
        var report = await SentReport(Mike);
        var id = report.ToString("D");
        Assert.Equal(200, (await Run(_reports.Comment(_team, Mike, id, "Looks right to me."))).Status);
        Assert.Equal(200, (await Run(_questions.AnswerAsync(_team, Mike, id, "trial-length", 1, "30", "thirty please", "device",
            CancellationToken.None))).Status);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Owner, MemberId(Mike)).Outcome);

        var (status, detail) = await Run(_reports.MineDetail(_team, Owner, id));
        Assert.Equal(200, status);
        var comments = detail.GetProperty("comments").EnumerateArray().ToList();
        Assert.Equal(2, comments.Count);
        Assert.All(comments, c => Assert.Equal(TeamReportEndpoints.FormerMember, c.GetProperty("from").GetString()));
        Assert.Contains(comments, c => c.GetProperty("text").GetString() == "Looks right to me.");
        // The words written with the answer still say which question and what was chosen - the answer itself survived.
        Assert.Contains(comments, c => c.GetProperty("text").GetString() == "thirty please"
                                       && c.GetProperty("aboutLabel").GetString() == TeamQuestions.AboutLabel(TeamQuestionsTests.Question, "30 days"));
        var answer = Assert.Single(_store.Items(_tenant, report));
        Assert.Equal((Mike, "30"), (answer.AnswererSubject, answer.OptionValue));
        // What was sent to them is gone from the Owner's list of who holds the report.
        Assert.Equal(0, detail.GetProperty("recipients").GetArrayLength());
    }

    // ---- Risk 12: one durable record per membership change ------------------------------------------------------------

    [Fact]
    public void CreateTeam_WritesOneCreatedRecord_ByTheCreator_AtTheClock()
    {
        var record = Assert.Single(RecordsOf(Owner));

        Assert.Equal(TeamMembershipEvents.Created, record.Event);
        Assert.Null(record.FromRole);
        Assert.Equal(TeamRole.Owner, record.ToRole);
        Assert.Equal(Owner, record.BySubject);
        Assert.Equal(Start, record.AtUtc);
        Assert.Equal(4, Records().Count);
    }

    [Fact]
    public void ChangeMemberRole_WritesOneRecord_WithBothRoles_TheOwnerAsActor_AndTheTime()
    {
        _now = Start.AddHours(1);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.ChangeMemberRole(_team, Owner, MemberId(Alice), TeamRole.Developer).Outcome);

        var record = RecordsOf(Alice).Last();
        Assert.Equal(2, RecordsOf(Alice).Count);
        Assert.Equal(TeamMembershipEvents.RoleChanged, record.Event);
        Assert.Equal(TeamRole.Manager, record.FromRole);
        Assert.Equal(TeamRole.Developer, record.ToRole);
        Assert.Equal(Owner, record.BySubject);
        Assert.Equal(Start.AddHours(1), record.AtUtc);
    }

    [Fact]
    public void RemoveTeamMember_WritesOneRemovedRecord_TheManagerAsActor_AndTheRecordSurvivesTheRemovalAndARejoin()
    {
        _now = Start.AddHours(2);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Alice, MemberId(Mike)).Outcome);

        Assert.Null(_teams.RoleOf(_team, Mike));
        var removed = RecordsOf(Mike).Last();
        Assert.Equal(TeamMembershipEvents.Removed, removed.Event);
        Assert.Equal(TeamRole.Collaborator, removed.FromRole);
        Assert.Null(removed.ToRole);
        Assert.Equal(Alice, removed.BySubject);
        Assert.Equal(Start.AddHours(2), removed.AtUtc);

        _now = Start.AddHours(3);
        Assert.True(_teams.AddMember(_team, Mike, TeamRole.Collaborator, Owner).IsDone);

        Assert.Equal(new[] { TeamMembershipEvents.Added, TeamMembershipEvents.Removed, TeamMembershipEvents.Added },
            RecordsOf(Mike).Select(r => r.Event));
    }

    [Fact]
    public void RemoveMember_ThePersonThemselves_IsRecordedAsLeft()
    {
        _now = Start.AddHours(1);

        Assert.True(_teams.RemoveMember(_team, Nina, Nina).IsDone);

        var record = RecordsOf(Nina).Last();
        Assert.Equal(TeamMembershipEvents.Left, record.Event);
        Assert.Equal(Nina, record.BySubject);
    }

    [Fact]
    public void Invitations_AddedAtOnceAndAccepted_EachWriteOneRecord_NamingWhoActed()
    {
        _tenants.MintOrLookupBySubject("sub-rosa", "rosa@example.com");
        _now = Start.AddHours(4);

        var atOnce = _teams.CreateInvitation(_team, Alice, "rosa@example.com", TeamRole.Collaborator);
        var waiting = _teams.CreateInvitation(_team, Alice, "sam@example.com", TeamRole.Collaborator);
        Assert.True(atOnce.AddedAtOnce);
        _tenants.MintOrLookupBySubject("sub-sam", "sam@example.com");
        _now = Start.AddHours(5);
        Assert.Equal(TeamInvitationOutcome.Done, _teams.AcceptInvitation(waiting.AcceptToken!, "sub-sam").Outcome);

        var rosa = Assert.Single(RecordsOf("sub-rosa"));
        Assert.Equal((TeamMembershipEvents.AddedAtOnce, (TeamRole?)TeamRole.Collaborator, Alice, Start.AddHours(4)),
            (rosa.Event, rosa.ToRole, rosa.BySubject, rosa.AtUtc));
        var sam = Assert.Single(RecordsOf("sub-sam"));
        Assert.Equal((TeamMembershipEvents.JoinedByInvitation, (TeamRole?)TeamRole.Collaborator, "sub-sam", Start.AddHours(5)),
            (sam.Event, sam.ToRole, sam.BySubject, sam.AtUtc));
    }

    [Fact]
    public void RefusedChanges_WriteNoRecord()
    {
        var before = Records().Count;

        Assert.Equal(TeamMemberChangeOutcome.Refused, _teams.RemoveTeamMember(_team, Owner, MemberId(Owner)).Outcome);
        Assert.Equal(TeamMemberChangeOutcome.Forbidden, _teams.RemoveTeamMember(_team, Mike, MemberId(Nina)).Outcome);
        Assert.Equal(TeamMemberChangeOutcome.Forbidden, _teams.ChangeMemberRole(_team, Alice, MemberId(Mike), TeamRole.Developer).Outcome);
        Assert.False(_teams.AddMember(_team, Mike, TeamRole.Developer, Owner).IsDone);
        Assert.False(_teams.ChangeRole(_team, Owner, TeamRole.Manager, Owner).IsDone);

        Assert.Equal(before, Records().Count);
    }

    // ---- Risk 9: the member table is asked on every request -----------------------------------------------------------

    [Fact]
    public void Check_ARemovedMembersVeryNextRequest_IsRefused()
    {
        Assert.Equal(TeamGateOutcome.Allowed, SentToMeRequest(Mike).Outcome);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Owner, MemberId(Mike)).Outcome);

        Assert.Equal(TeamGateOutcome.NoSuchTeam, SentToMeRequest(Mike).Outcome);
    }
}
