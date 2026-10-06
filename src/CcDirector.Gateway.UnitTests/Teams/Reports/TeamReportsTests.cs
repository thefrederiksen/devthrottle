using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Reports;

/// <summary>
/// A dev report sent to a member of the team (devthrottle_internal#2309), over a real database: the two pieces the
/// Collaborator's Questions will reuse (<see cref="DevReportRecipients"/> - "sent to a named member" - and
/// <see cref="DevReportPersonComments"/> - "words go to a person"), what each route answers (<see cref="TeamReports"/>),
/// who wrote a report (<see cref="DevReportAuthor"/>), whose a report is (<see cref="TeamCallerOwnership"/>), and the team
/// gate deciding the routes as production runs it, from a person's own account.
///
/// The team: the Owner, Alice and Bob (Developers, each with a Director on the team), Mike and Nina (Collaborators).
/// Alice writes the reports.
/// </summary>
public sealed class TeamReportsTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private const string Stranger = "sub-stranger";
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions = new();
    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly DevReportPersonComments _comments;
    private readonly TeamCallerOwnership _ownership;
    private readonly TeamEndpointGate _gate;
    private readonly TeamReports _reports;
    private readonly string _team;
    private readonly TenantId _tenant;
    private DateTime _now = Now;

    public TeamReportsTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "owner@example.com"), (Alice, "alice@example.com"), (Bob, "bob@example.com"),
                     (Mike, "mike@example.com"), (Nina, "nina@example.com"), (Stranger, "stranger@example.com"),
                 })
            _tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(_db, _tenants);
        _access = new TeamAccess(_teams);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _store = new DevReportStore(_db);
        _recipients = new DevReportRecipients(_db);
        _comments = new DevReportPersonComments(_db);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        _ownership = new TeamCallerOwnership(_directors, _sessions, _devices, new CcDirector.Gateway.History.SessionTurnStore(_db), boundary,
            new CcDirector.Gateway.Pairing.SessionKeyRegistry(_db, isHosted: true), reportAuthor: (tenant, id) => _store.Get(tenant, id)?.AuthorSubject);
        _gate = new TeamEndpointGate(_access, _teams, _tenants, boundary, _ownership);
        _reports = new TeamReports(_store, _recipients, _comments, _teams, _access, () => _now);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(_team);
        Assert.True(_teams.AddMember(_team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Bob, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_team, Nina, TeamRole.Collaborator).IsDone);
        TeamDirector(Alice, "director-alice");
        TeamDirector(Bob, "director-bob");
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    /// <summary>A member's Director enrolled into the team, registered on its own key the way the tunnel's Hello does.</summary>
    private void TeamDirector(string subject, string directorId)
    {
        var deviceId = _team + "|" + directorId;
        _devices.RegisterForTenant(_tenant, subject, deviceId, "M");
        _directors.RegisterFromStream(directorId, "M", "u", "1.0", 1, DateTime.UtcNow, _tenant, directorId, "device:" + deviceId);
    }

    private DevReportEntityRef Publish(string author, string title = "Signup page rewrite", string? key = null, TenantId? tenant = null)
    {
        var report = _store.Publish(tenant ?? _tenant, Guid.NewGuid().ToString("D"), key ?? @"C:\work\" + Guid.NewGuid().ToString("N") + ".html",
            "<p>bytes</p>", "waiting-on-you", title, _now, author).Report;
        return new DevReportEntityRef(report.Id.ToString("D"), report.Id);
    }

    private sealed record DevReportEntityRef(string Id, Guid Guid);

    private string MemberId(string subject) => TeamMemberIds.For(_team, subject);

    private static async Task<(int Status, JsonElement Body)> Answer(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static List<string> Ids(JsonElement body, string list = "reports") =>
        body.GetProperty(list).EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();

    // ---- DevReportRecipients: "sent to a named member" -----------------------------------------------------------

    [Fact]
    public void Send_RecordsEachRecipientOnce_AndSendingAgainIsTheSameRow()
    {
        var report = Publish(Alice);

        var first = _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike, Nina, Mike }, 1, Now);
        _now = Now.AddHours(1);
        var again = _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 1, _now);

        Assert.True(first.Sent);
        Assert.Equal(new[] { Mike, Nina }, first.Recipients.Select(r => r.RecipientSubject).OrderBy(s => s, StringComparer.Ordinal));
        Assert.True(again.Sent);
        Assert.Equal(2, again.Recipients.Count);
        Assert.All(again.Recipients, r => Assert.Equal(Now, r.SentAtUtc));
        Assert.All(again.Recipients, r => Assert.Equal(Alice, r.SentBySubject));
    }

    [Fact]
    public void Send_NoRecipientOrAnEmptySubject_Throws()
    {
        var report = Publish(Alice);
        Assert.Throws<ArgumentException>(() => _recipients.Send(_tenant, report.Guid, Alice, Array.Empty<string>(), 1, Now));
        Assert.Throws<ArgumentException>(() => _recipients.Send(_tenant, report.Guid, Alice, new[] { " " }, 1, Now));
        Assert.Throws<ArgumentException>(() => _recipients.Send(_tenant, report.Guid, "", new[] { Mike }, 1, Now));
    }

    [Fact]
    public void SentTo_IsOnlyTheRecipientsReports_NewestFirst()
    {
        var older = Publish(Alice, "Older");
        var newer = Publish(Alice, "Newer");
        var notToMike = Publish(Alice, "To Nina only");
        _recipients.Send(_tenant, older.Guid, Alice, new[] { Mike }, 1, Now);
        _recipients.Send(_tenant, newer.Guid, Alice, new[] { Mike }, 1, Now.AddMinutes(5));
        _recipients.Send(_tenant, notToMike.Guid, Alice, new[] { Nina }, 1, Now.AddMinutes(9));

        Assert.Equal(new[] { newer.Guid, older.Guid }, _recipients.SentTo(_tenant, Mike).Select(r => r.Row.ReportId));
        Assert.Equal(new[] { notToMike.Guid }, _recipients.SentTo(_tenant, Nina).Select(r => r.Row.ReportId));
        Assert.True(_recipients.IsSentTo(_tenant, older.Guid, Mike));
        Assert.False(_recipients.IsSentTo(_tenant, notToMike.Guid, Mike));
        Assert.False(_recipients.IsSentTo(_tenant, older.Guid, ""));
    }

    [Fact]
    public void MarkRead_SetsTheFirstReadOnly_AndIsFalseForAReportNotSentToThem()
    {
        var report = Publish(Alice);
        _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 1, Now);

        Assert.Equal(DevReportReadMark.Read, _recipients.MarkRead(_tenant, report.Guid, Mike, 1, Now.AddMinutes(1)));
        Assert.Equal(DevReportReadMark.Read, _recipients.MarkRead(_tenant, report.Guid, Mike, 1, Now.AddMinutes(2)));
        Assert.Equal(DevReportReadMark.NotSent, _recipients.MarkRead(_tenant, report.Guid, Nina, 1, Now.AddMinutes(3)));

        Assert.Equal(Now.AddMinutes(1), _recipients.RecipientsOf(_tenant, report.Guid).Single().ReadAtUtc);
    }

    [Fact]
    public void RecipientCounts_CountsPerReport_AndLeavesOutReportsSentToNobody()
    {
        var two = Publish(Alice);
        var none = Publish(Alice);
        _recipients.Send(_tenant, two.Guid, Alice, new[] { Mike, Nina }, 1, Now);

        var counts = _recipients.RecipientCounts(_tenant, new[] { two.Guid, none.Guid });

        Assert.Equal(2, counts[two.Guid]);
        Assert.False(counts.ContainsKey(none.Guid));
        Assert.Empty(_recipients.RecipientCounts(_tenant, Array.Empty<Guid>()));
    }

    [Fact]
    public void Recipients_AnotherTeamsTenant_SeesNoneOfThem()
    {
        var other = new TenantId(_teams.CreateTeam(Owner, "Other").Team!.TeamId);
        var report = Publish(Alice);
        _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 1, Now);

        Assert.Empty(_recipients.SentTo(other, Mike));
        Assert.False(_recipients.IsSentTo(other, report.Guid, Mike));
        Assert.Empty(_recipients.RecipientsOf(other, report.Guid));
        Assert.Equal(DevReportReadMark.NotSent, _recipients.MarkRead(other, report.Guid, Mike, 1, Now));
    }

    // ---- DevReportPersonComments: "words go to a person" ---------------------------------------------------------

    [Fact]
    public void Add_KeepsTheWordsExactly_ForThePersonTheyGoTo_AndTheWriter()
    {
        var report = Publish(Alice);
        const string words = "  The conversion number looks low.\nCan you check September?  ";

        var row = _comments.Add(_tenant, report.Guid, Mike, Alice, words, Now);

        Assert.Equal(words, row.Text);
        Assert.Equal(words, Assert.Single(_comments.To(_tenant, report.Guid, Alice)).Text);
        Assert.Equal(words, Assert.Single(_comments.From(_tenant, report.Guid, Mike)).Text);
        Assert.Empty(_comments.To(_tenant, report.Guid, Bob));
        Assert.Empty(_comments.From(_tenant, report.Guid, Nina));
        Assert.Empty(_comments.To(new TenantId(_teams.CreateTeam(Owner, "Other").Team!.TeamId), report.Guid, Alice));
    }

    [Fact]
    public void Add_EmptyOrTooLongWords_Throw()
    {
        var report = Publish(Alice);
        Assert.Throws<ArgumentException>(() => _comments.Add(_tenant, report.Guid, Mike, Alice, "   ", Now));
        Assert.Throws<ArgumentException>(() => _comments.Add(_tenant, report.Guid, Mike, Alice, new string('x', DevReportPersonComments.MaxLength + 1), Now));
        Assert.Throws<ArgumentException>(() => _comments.Add(_tenant, report.Guid, "", Alice, "hi", Now));
        _comments.Add(_tenant, report.Guid, Mike, Alice, new string('x', DevReportPersonComments.MaxLength), Now);
    }

    [Fact]
    public void CountsTo_CountsTheAuthorsCommentsPerReport()
    {
        var a = Publish(Alice);
        var b = Publish(Alice);
        _comments.Add(_tenant, a.Guid, Mike, Alice, "one", Now);
        _comments.Add(_tenant, a.Guid, Nina, Alice, "two", Now.AddMinutes(1));
        _comments.Add(_tenant, b.Guid, Mike, Bob, "to someone else", Now);

        var counts = _comments.CountsTo(_tenant, Alice, new[] { a.Guid, b.Guid });

        Assert.Equal(2, counts[a.Guid]);
        Assert.False(counts.ContainsKey(b.Guid));
    }

    // ---- TeamReports: the routes' answers ------------------------------------------------------------------------

    [Fact]
    public async Task Issue2309_AReportSentToMike_AppearsForMike_AndForNoOtherCollaborator()
    {
        var report = Publish(Alice);
        var (sentStatus, _) = await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
        Assert.Equal(200, sentStatus);

        var (_, mike) = await Answer(_reports.SentToMe(_team, Mike));
        var (_, nina) = await Answer(_reports.SentToMe(_team, Nina));

        Assert.Equal(new[] { report.Id }, Ids(mike));
        var row = mike.GetProperty("reports")[0];
        Assert.Equal("alice@example.com", row.GetProperty("from").GetString());
        Assert.Equal("New", row.GetProperty("readLabel").GetString());
        Assert.False(row.GetProperty("read").GetBoolean());
        Assert.Empty(Ids(nina));
        Assert.Equal("No reports sent to you yet.", nina.GetProperty("emptyText").GetString());
    }

    [Fact]
    public async Task SentToMe_ShowYourReports_IsTheRoleTablesAnswer()
    {
        var (_, mike) = await Answer(_reports.SentToMe(_team, Mike));
        var (_, bob) = await Answer(_reports.SentToMe(_team, Bob));

        Assert.False(mike.GetProperty("showYourReports").GetBoolean());
        Assert.True(bob.GetProperty("showYourReports").GetBoolean());
    }

    [Fact]
    public async Task Issue2309_AReportNotSentToTheCaller_IsNotFound_OnEveryRecipientRoute()
    {
        var toMike = Publish(Alice);
        var toNobody = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, toMike.Id, new[] { MemberId(Mike) }, 1));

        foreach (var id in new[] { toMike.Id, toNobody.Id, Guid.NewGuid().ToString("D"), "not-a-guid" })
        {
            Assert.Equal(404, (await Answer(_reports.SentToMeDetail(_team, Nina, id))).Status);
            Assert.Null(_reports.SentToMeReport(_team, Nina, id));
            Assert.Equal(404, (await Answer(_reports.MarkRead(_team, Nina, id, 1))).Status);
            Assert.Equal(404, (await Answer(_reports.Comment(_team, Nina, id, "let me in"))).Status);
        }
        Assert.Empty(_comments.To(_tenant, toMike.Guid, Alice));
        // POSITIVE CONTROL: the one it was sent to answers.
        Assert.Equal(200, (await Answer(_reports.SentToMeDetail(_team, Mike, toMike.Id))).Status);
        Assert.NotNull(_reports.SentToMeReport(_team, Mike, toMike.Id));
    }

    [Fact]
    public async Task MarkRead_TurnsNewIntoRead_ForThatRecipientOnly()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike), MemberId(Nina) }, 1));

        var (status, body) = await Answer(_reports.MarkRead(_team, Mike, report.Id, 1));

        Assert.Equal(200, status);
        Assert.Equal("Read", body.GetProperty("readLabel").GetString());
        Assert.Equal("Read", (await Answer(_reports.SentToMe(_team, Mike))).Body.GetProperty("reports")[0].GetProperty("readLabel").GetString());
        Assert.Equal("New", (await Answer(_reports.SentToMe(_team, Nina))).Body.GetProperty("reports")[0].GetProperty("readLabel").GetString());
        var detail = (await Answer(_reports.MineDetail(_team, Alice, report.Id))).Body;
        Assert.Equal(new[] { "Read", "Not read yet" },
            detail.GetProperty("recipients").EnumerateArray().OrderBy(r => r.GetProperty("name").GetString()).Select(r => r.GetProperty("readLabel").GetString()));
    }

    [Fact]
    public async Task Issue2309_ACollaboratorsComment_GoesToTheAuthor_AndNeverIntoTheAgentsConversation()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
        const string marker = "MARKER-2309-comment-goes-to-a-person";

        var (status, body) = await Answer(_reports.Comment(_team, Mike, report.Id, marker));

        Assert.Equal(200, status);
        Assert.Equal(marker, body.GetProperty("comment").GetProperty("text").GetString());
        // The author reads it, with who wrote it.
        var mine = (await Answer(_reports.MineDetail(_team, Alice, report.Id))).Body;
        var comment = Assert.Single(mine.GetProperty("comments").EnumerateArray());
        Assert.Equal(marker, comment.GetProperty("text").GetString());
        Assert.Equal("mike@example.com", comment.GetProperty("from").GetString());
        // The writer sees their own, and where it goes.
        var theirs = (await Answer(_reports.SentToMeDetail(_team, Mike, report.Id))).Body;
        Assert.Equal(marker, theirs.GetProperty("comments")[0].GetProperty("text").GetString());
        Assert.Equal("Your comments go to alice@example.com, who wrote this report. They are never shown to an agent.",
            theirs.GetProperty("commentsNote").GetString());
        // Nothing reached the agent's conversation: no item to deliver, no reply, nothing waiting for the session.
        Assert.Empty(_store.Items(_tenant, report.Guid));
        Assert.Empty(_store.Replies(_tenant, report.Guid));
        Assert.Empty(_store.SessionsWithOpenItems(_tenant));
    }

    [Fact]
    public async Task Comment_EmptyOrTooLong_IsRefusedAndNothingIsStored()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));

        var empty = await Answer(_reports.Comment(_team, Mike, report.Id, "  \n "));
        var tooLong = await Answer(_reports.Comment(_team, Mike, report.Id, new string('x', DevReportPersonComments.MaxLength + 1)));

        Assert.Equal(400, empty.Status);
        Assert.Equal("comment_empty", empty.Body.GetProperty("code").GetString());
        Assert.Equal(400, tooLong.Status);
        Assert.Equal("comment_too_long", tooLong.Body.GetProperty("code").GetString());
        Assert.Empty(_comments.To(_tenant, report.Guid, Alice));
    }

    [Fact]
    public async Task Send_ToSomeoneWhoIsNotAMemberOfThisTeam_IsRefused_AndNothingIsSent()
    {
        var report = Publish(Alice);
        var otherTeam = _teams.CreateTeam(Owner, "Other").Team!.TeamId;
        Assert.True(_teams.AddMember(otherTeam, Stranger, TeamRole.Collaborator).IsDone);

        foreach (var outsider in new[] { TeamMemberIds.For(_team, Stranger), TeamMemberIds.For(otherTeam, Stranger), "made-up" })
        {
            var (status, body) = await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike), outsider }, 1));
            Assert.Equal(400, status);
            Assert.Equal("not_a_member", body.GetProperty("code").GetString());
        }
        Assert.Empty(_recipients.RecipientsOf(_tenant, report.Guid));
    }

    [Fact]
    public async Task Send_ToYourself_OrToNobody_IsRefused()
    {
        var report = Publish(Alice);

        var self = await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Alice) }, 1));
        var nobody = await Answer(_reports.Send(_team, Alice, report.Id, Array.Empty<string>(), 1));

        Assert.Equal("not_to_yourself", self.Body.GetProperty("code").GetString());
        Assert.Equal("no_recipients", nobody.Body.GetProperty("code").GetString());
        Assert.Empty(_recipients.RecipientsOf(_tenant, report.Guid));
    }

    [Fact]
    public async Task Send_AReportYouDidNotWrite_IsNotFound_AndNothingIsSent()
    {
        var alices = Publish(Alice);

        var (status, _) = await Answer(_reports.Send(_team, Bob, alices.Id, new[] { MemberId(Mike) }, 1));

        Assert.Equal(404, status);
        Assert.Empty(_recipients.RecipientsOf(_tenant, alices.Guid));
        Assert.Null(_reports.OwnReport(_team, Bob, alices.Id));
        Assert.Equal(404, (await Answer(_reports.MineDetail(_team, Bob, alices.Id))).Status);
    }

    [Fact]
    public async Task Mine_ListsOnlyTheCallersOwnReports_WithWhoTheyWentToAndTheComments()
    {
        var sent = Publish(Alice, "Sent");
        Publish(Alice, "Kept");
        var bobs = Publish(Bob, "Bob's");
        await Answer(_reports.Send(_team, Alice, sent.Id, new[] { MemberId(Mike), MemberId(Nina) }, 1));
        await Answer(_reports.Comment(_team, Mike, sent.Id, "nice"));

        var (_, alice) = await Answer(_reports.Mine(_team, Alice));
        var (_, mike) = await Answer(_reports.Mine(_team, Mike));

        Assert.Equal(2, alice.GetProperty("count").GetInt32());
        Assert.DoesNotContain(bobs.Id, Ids(alice));
        var row = alice.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("id").GetString() == sent.Id);
        Assert.Equal("Sent to 2 people", row.GetProperty("sentToLabel").GetString());
        Assert.Equal("1 comment", row.GetProperty("commentsLabel").GetString());
        var kept = alice.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("title").GetString() == "Kept");
        Assert.Equal("Not sent to anyone yet", kept.GetProperty("sentToLabel").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("commentsLabel").ValueKind);
        Assert.Equal(0, mike.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task MineDetail_OffersEveryMemberWhoMayReadIt_ButNotYouNorThoseAlreadySentTo()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));

        var (_, detail) = await Answer(_reports.MineDetail(_team, Alice, report.Id));

        var choices = detail.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("memberId").GetString()).ToList();
        Assert.Equal(new[] { MemberId(Owner), MemberId(Bob), MemberId(Nina) }.OrderBy(s => s), choices.OrderBy(s => s));
        Assert.Equal(MemberId(Mike), detail.GetProperty("recipients")[0].GetProperty("memberId").GetString());
        Assert.Equal("mike@example.com", detail.GetProperty("recipients")[0].GetProperty("name").GetString());
        Assert.Equal("No comments yet. When someone you sent this report to comments on it, it appears here.",
            detail.GetProperty("commentsEmptyText").GetString());
    }

    [Fact]
    public async Task AMemberWhoLeft_IsNamedAsAFormerMember()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
        await Answer(_reports.Comment(_team, Mike, report.Id, "bye"));
        Assert.True(_teams.RemoveMember(_team, Mike).IsDone);

        var (_, detail) = await Answer(_reports.MineDetail(_team, Alice, report.Id));

        Assert.Equal("A former member of the team", detail.GetProperty("recipients")[0].GetProperty("name").GetString());
        Assert.Equal("A former member of the team", detail.GetProperty("comments")[0].GetProperty("from").GetString());
    }

    [Fact]
    public void TeamReports_NullDependencies_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamReports(null!, _recipients, _comments, _teams, _access));
        Assert.Throws<ArgumentNullException>(() => new TeamReports(_store, null!, _comments, _teams, _access));
        Assert.Throws<ArgumentNullException>(() => new TeamReports(_store, _recipients, null!, _teams, _access));
        Assert.Throws<ArgumentNullException>(() => new TeamReports(_store, _recipients, _comments, null!, _access));
        Assert.Throws<ArgumentNullException>(() => new TeamReports(_store, _recipients, _comments, _teams, null!));
        Assert.Throws<ArgumentNullException>(() => new DevReportRecipients(null!));
        Assert.Throws<ArgumentNullException>(() => new DevReportPersonComments(null!));
    }

    // ---- DevReportAuthor and the store: who wrote a report -------------------------------------------------------

    [Fact]
    public void Resolve_InATeam_IsThePersonBehindTheDirector_AndInAPersonalAccountRecordsNothing()
    {
        var author = new DevReportAuthor(_teams, _ownership);

        var alice = author.Resolve(_tenant, "director-alice");
        var unknown = author.Resolve(_tenant, "director-nobody-registered");
        var personal = author.Resolve(_tenants.LookupBySubject(Bob)!.Value, "director-bob-at-home");

        Assert.Equal(new DevReportAuthorAnswer(true, Alice), alice);
        Assert.False(alice.Refused);
        Assert.True(unknown.Refused);
        Assert.Equal(DevReportAuthorAnswer.PersonalAccount, personal);
        Assert.False(personal.Refused);
        Assert.Throws<ArgumentNullException>(() => new DevReportAuthor(null!, _ownership));
        Assert.Throws<ArgumentNullException>(() => new DevReportAuthor(_teams, null!));
    }

    [Fact]
    public void PublishAs_TheResolvedAuthor_IsTheOneRecorded_AndAPersonalAccountRecordsNone()
    {
        var author = new DevReportAuthor(_teams, _ownership);
        var bobsHome = _tenants.LookupBySubject(Bob)!.Value;

        var inTeam = DevReportAuthor.PublishAs(_store, author.Resolve(_tenant, "director-alice"), _tenant,
            Guid.NewGuid().ToString("D"), "k", "<p>1</p>", "done", "Team", Now).Report;
        var atHome = DevReportAuthor.PublishAs(_store, author.Resolve(bobsHome, "director-bob-at-home"), bobsHome,
            Guid.NewGuid().ToString("D"), "k", "<p>1</p>", "done", "Home", Now).Report;

        Assert.Equal(Alice, _store.Get(_tenant, inTeam.Id)!.AuthorSubject);
        Assert.Null(_store.Get(bobsHome, atHome.Id)!.AuthorSubject);
        Assert.Throws<InvalidOperationException>(() => DevReportAuthor.PublishAs(_store, DevReportAuthorAnswer.Unknown, _tenant,
            Guid.NewGuid().ToString("D"), "k", "<p>1</p>", "done", "Nobody", Now));
    }

    [Fact]
    public void Publish_RecordsTheAuthorOnCreation_AndALaterVersionKeepsIt()
    {
        var sid = Guid.NewGuid().ToString("D");
        var first = _store.Publish(_tenant, sid, "k", "<p>1</p>", "done", "One", Now, Alice).Report;
        var second = _store.Publish(_tenant, sid, "k", "<p>2</p>", "done", "Two", Now.AddMinutes(1), Bob).Report;
        var personal = _store.Publish(_tenants.LookupBySubject(Bob)!.Value, sid, "k", "<p>1</p>", "done", "Home", Now).Report;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Alice, _store.Get(_tenant, first.Id)!.AuthorSubject);
        Assert.Null(personal.AuthorSubject);
    }

    // ---- whose a report is, and the gate deciding the routes from a person's own account -------------------------

    private static Func<string, string?> Values(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Whose_AReportTheCallerWrote_IsTheirs_AnotherMembersIsSomeoneElses_AndAnUnknownOneIsUnknown()
    {
        var alices = Publish(Alice);
        var noAuthor = Publish(author: null!);
        const string pattern = TeamReportEndpoints.MineReportPattern + "/recipients";

        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, pattern, Values(("reportId", alices.Id))));
        Assert.Equal(TeamOwnership.SomeoneElses, _ownership.Whose(_tenant, Bob, pattern, Values(("reportId", alices.Id))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, pattern, Values(("reportId", noAuthor.Id))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, pattern, Values(("reportId", Guid.NewGuid().ToString("D")))));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, pattern, Values(("reportId", "not-a-guid"))));
        var noReader = new TeamCallerOwnership(_directors, _sessions, _devices, new CcDirector.Gateway.History.SessionTurnStore(_db),
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices), new CcDirector.Gateway.Pairing.SessionKeyRegistry(_db, isHosted: true));
        Assert.Equal(TeamOwnership.Unknown, noReader.Whose(_tenant, Alice, pattern, Values(("reportId", alices.Id))));
    }

    private DeviceCredentialIdentity OwnAccountKey(string subject)
    {
        var tenant = _tenants.LookupBySubject(subject)!.Value;
        var key = _devices.RegisterForTenant(tenant, subject, "browser-" + subject, "Browser").DeviceKey;
        return _devices.ResolveCredential(key).Identity!;
    }

    private static DefaultHttpContext Request(string method, string pattern, DeviceCredentialIdentity device,
        params (string Name, string Value)[] routeValues)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, pattern));
        foreach (var (name, value) in routeValues)
            ctx.Request.RouteValues[name] = value;
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = device;
        return ctx;
    }

    private async Task<(bool Reached, int Status)> Run(DefaultHttpContext ctx)
    {
        var reached = false;
        await _gate.RunAsync(ctx, () => { reached = true; return Task.CompletedTask; });
        return (reached, reached ? 200 : ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Gate_FromTheirOwnAccount_TheAuthorReachesTheirReport_AndAnotherDeveloperIsRefusedAsWatchingIt()
    {
        var alices = Publish(Alice);
        const string send = TeamReportEndpoints.MineReportPattern + "/recipients";

        var alice = await Run(Request("POST", send, OwnAccountKey(Alice), ("teamId", _team), ("reportId", alices.Id)));
        var bob = await Run(Request("POST", send, OwnAccountKey(Bob), ("teamId", _team), ("reportId", alices.Id)));
        var bobReads = await Run(Request("GET", TeamReportEndpoints.MineReportPattern, OwnAccountKey(Bob), ("teamId", _team), ("reportId", alices.Id)));
        var unknown = await Run(Request("GET", TeamReportEndpoints.MineReportPattern, OwnAccountKey(Alice), ("teamId", _team), ("reportId", Guid.NewGuid().ToString("D"))));

        Assert.Equal((true, 200), alice);
        Assert.Equal((false, 403), bob);
        Assert.Equal((false, 403), bobReads);
        Assert.Equal((false, 403), unknown);
    }

    [Fact]
    public async Task Gate_ACollaborator_ReachesWhatWasSentToThem_ButHasNoReportsOfTheirOwn()
    {
        var alices = Publish(Alice);
        var mike = OwnAccountKey(Mike);

        Assert.Equal((true, 200), await Run(Request("GET", TeamReportEndpoints.SentToMePattern, mike, ("teamId", _team))));
        Assert.Equal((true, 200), await Run(Request("POST", TeamReportEndpoints.SentToMePattern + "/{reportId}/comments", mike,
            ("teamId", _team), ("reportId", alices.Id))));
        Assert.Equal((false, 403), await Run(Request("GET", TeamReportEndpoints.MinePattern, mike, ("teamId", _team))));
        Assert.Equal((false, 403), await Run(Request("GET", TeamReportEndpoints.MineReportPattern, mike, ("teamId", _team), ("reportId", alices.Id))));
    }

    [Fact]
    public async Task Gate_SomeoneWhoIsNotAMember_IsToldThereIsNoSuchTeam()
    {
        var alices = Publish(Alice);
        var stranger = OwnAccountKey(Stranger);

        Assert.Equal((false, 404), await Run(Request("GET", TeamReportEndpoints.SentToMePattern, stranger, ("teamId", _team))));
        Assert.Equal((false, 404), await Run(Request("GET", TeamReportEndpoints.MineReportPattern, stranger, ("teamId", _team), ("reportId", alices.Id))));
    }

    [Theory]
    [InlineData("GET", TeamReportEndpoints.SentToMePattern, TeamAction.AnswerQuestionsSendRequestsReadReports, TeamTarget.Team)]
    [InlineData("GET", TeamReportEndpoints.SentToMePattern + "/{reportId}/html", TeamAction.AnswerQuestionsSendRequestsReadReports, TeamTarget.Team)]
    [InlineData("POST", TeamReportEndpoints.SentToMePattern + "/{reportId}/comments", TeamAction.AnswerQuestionsSendRequestsReadReports, TeamTarget.Team)]
    [InlineData("GET", TeamReportEndpoints.MinePattern, TeamAction.RunSessionsOnOwnComputers, TeamTarget.TeamNarrowedToCaller)]
    [InlineData("GET", TeamReportEndpoints.MineReportPattern + "/html", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn)]
    [InlineData("POST", TeamReportEndpoints.MineReportPattern + "/recipients", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn)]
    public void Rules_EachReportRoute_StatesItsAction(string method, string pattern, TeamAction action, TeamTarget target)
    {
        var rule = TeamEndpointRules.Find(method, pattern);

        Assert.NotNull(rule);
        Assert.Equal(action, rule!.Action);
        Assert.Equal(target, rule.Target);
        Assert.Equal(TeamFrom.RouteTeamId, rule.TeamFrom);
        if (target == TeamTarget.CallersOwn)
            Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, rule.OthersAction);
    }

    [Theory]
    [InlineData("POST", TeamReportEndpoints.MinePattern)]
    [InlineData("GET", "/dev-reports")]
    [InlineData("GET", "/dev-reports/{reportId}")]
    [InlineData("GET", "/dev-reports/{reportId}/html")]
    [InlineData("POST", "/dev-reports/{reportId}/send")]
    public void Rules_TheOwnersDevReportRoutes_AndAWriteToTheMineList_StateNoAction(string method, string pattern)
    {
        // Undeclared, so inside a team's tenant the gate refuses them: a member never reads the tenant's reports there.
        Assert.Null(TeamEndpointRules.Find(method, pattern));
    }

    // ---- review findings F1, F2, F6, F7 (the Tech Lead's review of #3557) -----------------------------------------

    /// <summary>A report with a second version waiting to be published: the same session, the same key, two publishes.</summary>
    private (string Id, Guid Guid, Action PublishVersion2) TwoVersionReport(string author)
    {
        var sid = Guid.NewGuid().ToString("D");
        var key = @"C:\work\" + Guid.NewGuid().ToString("N") + ".html";
        var report = _store.Publish(_tenant, sid, key, "<p>version one</p>", "waiting-on-you", "First title", _now, author).Report;
        return (report.Id.ToString("D"), report.Id,
            () => _store.Publish(_tenant, sid, key, "<p>version two</p>", "done", "Second title", _now.AddMinutes(30), author));
    }

    private async Task<(int Status, string Body, string? Version)> Html(string caller, string reportId, int? version = null)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        if (version is { } v) ctx.Request.QueryString = new QueryString("?version=" + v);
        var result = _reports.SentToMeHtml(_team, caller, reportId, ctx);
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var served = ctx.Response.Headers["X-Dev-Report-Version"].ToString();
        return (ctx.Response.StatusCode, text, served.Length > 0 ? served : null);
    }

    [Fact]
    public void Send_TheNewestVersion_MovesTheRowForwardAndMarksItUnread_AndAnOlderOneIsRefusedAndMovesNothing()
    {
        var report = TwoVersionReport(Alice);
        Assert.True(_recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 1, Now).Sent);
        _recipients.MarkRead(_tenant, report.Guid, Mike, 1, Now.AddMinutes(1));
        report.PublishVersion2();

        // Version 1 is no longer the newest: refused, and nothing changes for Mike.
        var stale = _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike, Nina }, 1, Now.AddMinutes(2));
        Assert.False(stale.Sent);
        var held = _recipients.RowFor(_tenant, report.Guid, Mike)!;
        Assert.Equal(1, held.SentVersion);
        Assert.Equal(Now, held.SentAtUtc);
        Assert.NotNull(held.ReadAtUtc);
        Assert.Null(_recipients.RowFor(_tenant, report.Guid, Nina));

        // The newest version moves him forward, unread again.
        Assert.True(_recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 2, Now.AddMinutes(3)).Sent);
        held = _recipients.RowFor(_tenant, report.Guid, Mike)!;
        Assert.Equal(2, held.SentVersion);
        Assert.Equal(Now.AddMinutes(3), held.SentAtUtc);
        Assert.Null(held.ReadAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => _recipients.Send(_tenant, report.Guid, Alice, new[] { Mike }, 0, Now));
    }

    [Fact]
    public async Task Issue2309_R2_APublishLandingBetweenTheRoutesCheckAndTheWrite_IsCaughtByTheWrite_AndNothingIsSent()
    {
        // Round-3 review R2, deterministically: the route checks version 1 is the newest (it is), and THEN - before the
        // send's write - the session publishes version 2, as the other Gateway process can during a deploy. The write
        // itself must refuse; a check read earlier cannot.
        var report = TwoVersionReport(Alice);
        _recipients.BeforeSendWriteForTests = report.PublishVersion2;
        try
        {
            var (status, body) = await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
            Assert.Equal(409, status);
            Assert.Equal("version_not_newest", body.GetProperty("code").GetString());
            Assert.Equal(TeamReportEndpoints.NotTheNewestVersion, body.GetProperty("error").GetString());
        }
        finally
        {
            _recipients.BeforeSendWriteForTests = null;
        }
        // Nothing was sent: Mike holds nothing, and the report is at version 2.
        Assert.Null(_recipients.RowFor(_tenant, report.Guid, Mike));
        Assert.Equal(2, _store.Get(_tenant, report.Guid)!.Version);

        // POSITIVE CONTROL: the same send, of the version now newest, goes through.
        Assert.Equal(200, (await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 2))).Status);
        Assert.Equal(2, _recipients.RowFor(_tenant, report.Guid, Mike)!.SentVersion);
    }

    [Fact]
    public async Task Issue2309_F1_TheRecipientReadsTheVersionSent_NeverAnEarlierOne_AndALaterOneOnlyWhenSentAgain()
    {
        var report = TwoVersionReport(Alice);
        Assert.Equal(200, (await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1))).Status);
        report.PublishVersion2();

        // A later version is published: Mike still holds version 1 - its bytes, its title - and cannot ask for version 2.
        Assert.Equal((200, "<p>version one</p>", "1"), await Html(Mike, report.Id));
        Assert.Equal(404, (await Html(Mike, report.Id, 2)).Status);
        var (_, detail) = await Answer(_reports.SentToMeDetail(_team, Mike, report.Id));
        Assert.Equal(1, detail.GetProperty("report").GetProperty("version").GetInt32());
        Assert.Equal("First title", detail.GetProperty("report").GetProperty("title").GetString());
        var (_, list) = await Answer(_reports.SentToMe(_team, Mike));
        Assert.Equal("First title", list.GetProperty("reports")[0].GetProperty("title").GetString());

        // The author sees Mike holds an older version and may send the newer one; sending it moves him forward.
        var (_, mine) = await Answer(_reports.MineDetail(_team, Alice, report.Id));
        Assert.Equal("Has version 1 of 2", mine.GetProperty("recipients")[0].GetProperty("versionLabel").GetString());
        Assert.Contains(mine.GetProperty("choices").EnumerateArray(), c => c.GetProperty("memberId").GetString() == MemberId(Mike));
        Assert.Equal(200, (await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 2))).Status);

        Assert.Equal((200, "<p>version two</p>", "2"), await Html(Mike, report.Id));
        // Never an earlier one, once they hold a later one.
        var earlier = await Html(Mike, report.Id, 1);
        Assert.Equal(404, earlier.Status);
        Assert.Contains(TeamReportEndpoints.VersionNotSentToYou, earlier.Body);
        // Not sent to Nina at all: nothing, whatever version is asked for.
        Assert.Equal(404, (await Html(Nina, report.Id)).Status);
        Assert.Equal(404, (await Html(Nina, report.Id, 1)).Status);
    }

    [Fact]
    public async Task Issue2309_D2_ASendOfAVersionThatIsNoLongerTheNewest_IsRefusedWithTheGatewaysSentence_AndSendsNothing()
    {
        var report = TwoVersionReport(Alice);
        Assert.Equal(200, (await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1))).Status);
        // The session publishes version 2 while Alice's page still shows version 1.
        report.PublishVersion2();

        var (status, body) = await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike), MemberId(Nina) }, 1));
        Assert.Equal(409, status);
        Assert.Equal("version_not_newest", body.GetProperty("code").GetString());
        Assert.Equal(TeamReportEndpoints.NotTheNewestVersion, body.GetProperty("error").GetString());
        // Nothing was sent: Mike still holds version 1, Nina holds nothing.
        Assert.Equal(1, _recipients.RowFor(_tenant, report.Guid, Mike)!.SentVersion);
        Assert.Null(_recipients.RowFor(_tenant, report.Guid, Nina));

        // The version her page now shows is sent, and it is exactly that version.
        Assert.Equal(200, (await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike), MemberId(Nina) }, 2))).Status);
        Assert.Equal(2, _recipients.RowFor(_tenant, report.Guid, Mike)!.SentVersion);
        Assert.Equal(2, _recipients.RowFor(_tenant, report.Guid, Nina)!.SentVersion);
    }

    [Fact]
    public async Task Issue2309_D5_AReadNamesTheVersion_AndOnlyTheVersionHeldIsMarked_SoAVersionSentWhileOpenIsReadWhenSeen()
    {
        var report = TwoVersionReport(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
        Assert.Equal(200, (await Answer(_reports.MarkRead(_team, Mike, report.Id, 1))).Status);
        Assert.NotNull(_recipients.RowFor(_tenant, report.Guid, Mike)!.ReadAtUtc);

        // Alice sends version 2 while Mike has the report open on version 1: he holds version 2, unread.
        report.PublishVersion2();
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 2));
        Assert.Null(_recipients.RowFor(_tenant, report.Guid, Mike)!.ReadAtUtc);

        // A read posted for version 1 lands after the move: refused, nothing marked - version 2 has not been seen.
        var (status, body) = await Answer(_reports.MarkRead(_team, Mike, report.Id, 1));
        Assert.Equal(409, status);
        Assert.Equal("version_not_held", body.GetProperty("code").GetString());
        Assert.Equal(TeamReportEndpoints.ReadVersionNotHeld, body.GetProperty("error").GetString());
        Assert.Null(_recipients.RowFor(_tenant, report.Guid, Mike)!.ReadAtUtc);

        // The page shows version 2 and marks it: now it is read.
        Assert.Equal(200, (await Answer(_reports.MarkRead(_team, Mike, report.Id, 2))).Status);
        Assert.NotNull(_recipients.RowFor(_tenant, report.Guid, Mike)!.ReadAtUtc);
        var (_, list) = await Answer(_reports.SentToMe(_team, Mike));
        Assert.True(list.GetProperty("reports")[0].GetProperty("read").GetBoolean());
    }

    [Fact]
    public async Task Issue2309_F2_BothReadersAreToldByTheGateway_ThatTheReportsNotesAreOff()
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));

        Assert.False((await Answer(_reports.SentToMeDetail(_team, Mike, report.Id))).Body.GetProperty("notesOpen").GetBoolean());
        Assert.False((await Answer(_reports.MineDetail(_team, Alice, report.Id))).Body.GetProperty("notesOpen").GetBoolean());
        // Round-3 review R1: the report's own answer controls are off too, by the Gateway's flag.
        Assert.False((await Answer(_reports.SentToMeDetail(_team, Mike, report.Id))).Body.GetProperty("answersOpen").GetBoolean());
        Assert.False((await Answer(_reports.MineDetail(_team, Alice, report.Id))).Body.GetProperty("answersOpen").GetBoolean());
    }

    [Theory]
    [InlineData("left the team")]
    [InlineData("made a Collaborator")]
    public async Task Issue2309_F6_AnAuthorWhoCanNoLongerReadComments_ClosesTheComments_AndAWrittenOneIsRefusedAndNotKept(string what)
    {
        var report = Publish(Alice);
        await Answer(_reports.Send(_team, Alice, report.Id, new[] { MemberId(Mike) }, 1));
        var (_, open) = await Answer(_reports.SentToMeDetail(_team, Mike, report.Id));
        Assert.True(open.GetProperty("canComment").GetBoolean());

        Assert.True((what == "left the team" ? _teams.RemoveMember(_team, Alice) : _teams.ChangeRole(_team, Alice, TeamRole.Collaborator)).IsDone);

        var (_, closed) = await Answer(_reports.SentToMeDetail(_team, Mike, report.Id));
        Assert.False(closed.GetProperty("canComment").GetBoolean());
        Assert.Equal(TeamReportEndpoints.AuthorCannotReceive, closed.GetProperty("commentsNote").GetString());
        var (status, body) = await Answer(_reports.Comment(_team, Mike, report.Id, "Hello?"));
        Assert.Equal(409, status);
        Assert.Equal("author_cannot_receive", body.GetProperty("code").GetString());
        Assert.Equal(TeamReportEndpoints.AuthorCannotReceive, body.GetProperty("error").GetString());
        Assert.Empty(_comments.To(_tenant, report.Guid, Alice));
        Assert.Empty(_comments.From(_tenant, report.Guid, Mike));
    }

    [Fact]
    public void ListByAuthor_IsOnlyThatPersonsReports_NewestUpdateFirst_InThatTenant()
    {
        var first = Publish(Alice, "First");
        _now = Now.AddMinutes(5);
        var second = Publish(Alice, "Second");
        Publish(Bob, "Bob");

        Assert.Equal(new[] { second.Guid, first.Guid }, _store.ListByAuthor(_tenant, Alice).Select(r => r.Id));
        Assert.Empty(_store.ListByAuthor(_tenants.LookupBySubject(Alice)!.Value, Alice));
        Assert.Throws<ArgumentException>(() => _store.ListByAuthor(_tenant, " "));
    }
}
