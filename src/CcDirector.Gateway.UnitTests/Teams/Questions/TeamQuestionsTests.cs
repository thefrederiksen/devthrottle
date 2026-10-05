using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Questions;

/// <summary>
/// The questions waiting on a member of a team (devthrottle_internal#2307), over a real database and the REAL answer
/// delivery (<see cref="DevReportDelivery"/>): the only stand-ins are the session's reach (the roster) and the Director at
/// the other end of the tunnel, which records EVERY command it is handed, whatever its verb. "Never placed in a session's
/// input" is asserted on that record - the one seam that can type into a session - and on what the session's own report
/// read serves (<see cref="DevReportStore.Items"/>).
///
/// The team: the Owner, Alice (a Developer, who writes the reports), Mike and Nina (Collaborators).
/// </summary>
public sealed class TeamQuestionsTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Mike = "sub-mike";
    private const string Nina = "sub-nina";
    private const string DirectorId = "director-alice";
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly DevReportPersonComments _comments;
    private readonly DevReportDelivery _delivery;
    private readonly TeamQuestions _questions;
    private readonly TeamReports _reports;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _team;
    private readonly TenantId _tenant;
    private readonly ConcurrentDictionary<string, DevReportSessionReach> _reach = new(StringComparer.Ordinal);

    /// <summary>Every command toward the Director, whole: its verb, the session it names, and its payload.</summary>
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly ConcurrentQueue<PromptRequest> _prompts = new();

    public TeamQuestionsTests()
    {
        var db = _harness.Open();
        var tenants = new TenantRegistry(db);
        foreach (var (subject, email) in new[]
                 {
                     (Owner, "owner@example.com"), (Alice, "alice@example.com"), (Mike, "mike@example.com"), (Nina, "nina@example.com"),
                 })
            tenants.MintOrLookupBySubject(subject, email);
        _teams = new TeamRegistry(db, tenants);
        _access = new TeamAccess(_teams);
        _store = new DevReportStore(db);
        _recipients = new DevReportRecipients(db);
        _comments = new DevReportPersonComments(db);
        _delivery = new DevReportDelivery(_store,
            (tenant, sid) =>
            {
                var reach = _reach.TryGetValue(sid, out var r) ? r : DevReportSessionReach.Busy;
                return new DevReportSessionLiveness(reach, reach == DevReportSessionReach.Ended ? null : DirectorId, "test roster");
            },
            (tenant, directorId) => new SessionVerbClient(new DirectorDto { DirectorId = directorId, MachineName = "TEST" }, SendAsync),
            _lifetime.Token,
            () => Now);
        _questions = new TeamQuestions(_store, _recipients, _comments, _teams, _access, _delivery, () => Now);
        _reports = new TeamReports(_store, _recipients, _comments, _teams, _access, () => Now, _questions);

        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(_team);
        Assert.True(_teams.AddMember(_team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Mike, TeamRole.Collaborator).IsDone);
        Assert.True(_teams.AddMember(_team, Nina, TeamRole.Collaborator).IsDone);
    }

    public void Dispose()
    {
        _lifetime.Dispose();
        _harness.Dispose();
    }

    private Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
    {
        _commands.Enqueue(command.Verb + " " + command.SessionId + " " + command.PayloadJson);
        if (command.Verb == "prompt")
            _prompts.Enqueue(JsonSerializer.Deserialize<PromptRequest>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(
            JsonSerializer.Serialize(new PromptResponse { Accepted = true, ActivityState = "Working" })));
    }

    internal const string Question = "Should the trial be 14 days or 30?";

    /// <summary>A report with one question - the mockup's (S8) - as an agent writes it.</summary>
    internal static string Html(string questionId = "trial-length", string question = Question) =>
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Pricing</h1></header>" +
        "<section data-dev-report=\"summary\"><p>The signup page says 14 today.</p></section>" +
        "<section data-dev-report=\"questions\">" +
        $"<div data-dev-report-question=\"{questionId}\" data-dev-report-question-text=\"{question}\">" +
        $"<label><input type=\"radio\" name=\"{questionId}\" value=\"14\" data-recommended> 14 days</label>" +
        $"<label><input type=\"radio\" name=\"{questionId}\" value=\"30\"> 30 days</label>" +
        "<textarea data-dev-report-comment placeholder=\"Anything to add (optional)\"></textarea></div>" +
        "</section><section data-dev-report=\"detail\"><p>The detail.</p></section>";

    /// <summary>A report written in the team by a session of Alice's, sent to <paramref name="to"/>.</summary>
    private (Guid Id, string SessionId) Report(params string[] to)
    {
        var sid = Guid.NewGuid().ToString("D");
        var report = _store.Publish(_tenant, sid, @"C:\work\" + Guid.NewGuid().ToString("N") + ".html", Html(), "waiting-on-you", "Pricing", Now, Alice).Report;
        if (to.Length > 0) Assert.True(_recipients.Send(_tenant, report.Id, Alice, to, 1, Now).Sent);
        return (report.Id, sid);
    }

    private Task<(int Status, JsonElement Body)> AnswerAsync(string who, Guid report, string option, string comment = "",
        int version = 1, string questionId = "trial-length")
        => Run(_questions.AnswerAsync(_team, who, report.ToString("D"), questionId, version, option, comment, "device", CancellationToken.None));

    private static async Task<(int Status, JsonElement Body)> Run(Task<IResult> pending) => await Run(await pending);

    private static async Task<(int Status, JsonElement Body)> Run(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<JsonElement> ListFor(string who)
    {
        var (status, body) = await Run(_questions.List(_team, who));
        Assert.Equal(200, status);
        return body;
    }

    private static List<string> QuestionIds(JsonElement body, string list) =>
        body.GetProperty(list).EnumerateArray().Select(q => q.GetProperty("reportId").GetString() + "/" + q.GetProperty("questionId").GetString()).ToList();

    private async Task WaitForPrompts(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_prompts.Count < count)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Expected {count} prompt(s); the Director received {_prompts.Count}.");
            await Task.Delay(20);
        }
    }

    // ---- the issue's three tests ------------------------------------------------------------------------------------

    [Fact]
    public async Task Issue2307_AnAnswerFromACollaborator_DeliversTheChosenOption_ToTheSessionThatAsked()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Idle;

        var (status, body) = await AnswerAsync(Mike, report, "30");

        Assert.Equal(200, status);
        var prompt = Assert.Single(_prompts);
        // The question's words and the option's label came from the version Mike was sent, not from any page.
        Assert.Contains($"to \"{Question}\": \"30 days\" (value \"30\")", prompt.Text);
        Assert.Contains("A person this report was sent to answered your dev report \"Pricing\"", prompt.Text);
        Assert.DoesNotContain("The owner", prompt.Text);
        Assert.False(prompt.AgentDriven);
        Assert.Equal(SubmissionRoutes.GatewayDevReport, prompt.Provenance!.Route);
        // The session that asked is the one the report came from.
        Assert.Contains(sid, Assert.Single(_commands));
        Assert.Equal("Delivered to the session", body.GetProperty("question").GetProperty("answer").GetProperty("statusLabel").GetString());
        var stored = Assert.Single(_store.Items(_tenant, report));
        Assert.Equal((Mike, "30", ""), (stored.AnswererSubject, stored.OptionValue, stored.Comment));
    }

    [Fact]
    public async Task Issue2307_ABusySession_HoldsTheAnswer_AndTheTurnEndDeliversItOnce()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Busy;

        var (status, body) = await AnswerAsync(Mike, report, "14");
        Assert.Equal(200, status);
        Assert.Equal("Delivered when the agent finishes its turn", body.GetProperty("question").GetProperty("answer").GetProperty("statusLabel").GetString());
        Assert.Empty(_commands);

        _reach[sid] = DevReportSessionReach.Idle;
        new DevReportTurnEndLauncher(_delivery).OnTurnEnd(_tenant, sid, isNewTurn: true);
        await WaitForPrompts(1);
        Assert.Equal(0, await _delivery.SettleAsync(_tenant, sid, CancellationToken.None));
        Assert.Contains("\"14 days\" (value \"14\")", Assert.Single(_prompts).Text);
    }

    [Fact]
    public async Task Issue2307_TheCollaboratorsComment_GoesToThePersonWhoAsked_AndIsNeverPlacedInAnySessionsInput()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Busy;
        var marker = "MARKER-2307-" + Guid.NewGuid().ToString("N");

        var (status, _) = await AnswerAsync(Mike, report, "30", "We trialled 14 days last year. " + marker);
        Assert.Equal(200, status);

        // The settle pass while the session works, then the turn end, then another settle: every way an item goes out.
        await _delivery.SettleAsync(_tenant, sid, CancellationToken.None);
        _reach[sid] = DevReportSessionReach.Idle;
        new DevReportTurnEndLauncher(_delivery).OnTurnEnd(_tenant, sid, isNewTurn: true);
        await WaitForPrompts(1);
        await Task.Delay(200);
        await _delivery.SettleAsync(_tenant, sid, CancellationToken.None);

        // POSITIVE CONTROL: the choice IS in the prompt the session received.
        Assert.Contains("\"30 days\" (value \"30\")", Assert.Single(_prompts).Text);
        // The words are in nothing sent toward any session, whatever its verb.
        Assert.NotEmpty(_commands);
        Assert.All(_commands, c => Assert.DoesNotContain(marker, c));
        // Nor in what the session's own report read serves: the agent's conversation is these items.
        Assert.All(_store.Items(_tenant, report), i =>
        {
            Assert.DoesNotContain(marker, i.Comment);
            Assert.DoesNotContain(marker, i.Text);
        });
        Assert.All(_store.Replies(_tenant, report), r => Assert.DoesNotContain(marker, r.Text));
        // They ARE with the person who asked: the comment row to Alice, and her own report's page.
        var comment = Assert.Single(_comments.To(_tenant, report, Alice));
        Assert.Contains(marker, comment.Text);
        Assert.Equal("trial-length", comment.QuestionId);
        var (mineStatus, mine) = await Run(_reports.MineDetail(_team, Alice, report.ToString("D")));
        Assert.Equal(200, mineStatus);
        var onPage = Assert.Single(mine.GetProperty("comments").EnumerateArray());
        Assert.Contains(marker, onPage.GetProperty("text").GetString());
        Assert.Equal($"About \"{Question}\" - chose \"30 days\"", onPage.GetProperty("aboutLabel").GetString());
    }

    [Fact]
    public async Task Issue2307_AQuestionAddressedToANamedCollaborator_AppearsOnlyForThem_AndAnyoneElseIsRefused()
    {
        var (report, sid) = Report(Mike);
        var (other, _) = Report(); // sent to nobody

        var mike = await ListFor(Mike);
        Assert.Equal(new[] { report.ToString("D") + "/trial-length" }, QuestionIds(mike, "waiting"));
        Assert.Equal(1, mike.GetProperty("count").GetInt32());
        var card = mike.GetProperty("waiting")[0];
        Assert.Equal(Question, card.GetProperty("question").GetString());
        Assert.Equal("alice@example.com", card.GetProperty("askedBy").GetString());
        Assert.Equal("Your words go to alice@example.com. Only your choice reaches the agent.", card.GetProperty("commentNote").GetString());
        Assert.Equal(new[] { ("14", "14 days", true), ("30", "30 days", false) },
            card.GetProperty("options").EnumerateArray().Select(o => (o.GetProperty("value").GetString()!, o.GetProperty("label").GetString()!, o.GetProperty("recommended").GetBoolean())));

        var nina = await ListFor(Nina);
        Assert.Empty(QuestionIds(nina, "waiting"));
        Assert.Equal(TeamQuestions.NothingWaiting, nina.GetProperty("subtitle").GetString());
        Assert.Empty(QuestionIds(await ListFor(Alice), "waiting"));

        // Nina answering Mike's question, or a report sent to nobody, gets the one not-found - and nothing is stored or sent.
        foreach (var (who, id) in new[] { (Nina, report), (Mike, other), (Nina, Guid.NewGuid()) })
        {
            var (status, body) = await AnswerAsync(who, id, "14", "words");
            Assert.Equal(404, status);
            Assert.Equal(TeamQuestions.NoSuchQuestion, body.GetProperty("error").GetString());
        }
        Assert.Empty(_store.Items(_tenant, report));
        Assert.Empty(_comments.To(_tenant, report, Alice));
        Assert.Empty(_commands);
        _ = sid;
    }

    // ---- what an answer must name ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnswerAsync_AVersionTheMemberNoLongerHolds_IsRefused_AndNothingIsStored()
    {
        var (report, sid) = Report(Mike);
        _store.Publish(_tenant, sid, _store.Get(_tenant, report)!.Key, Html("trial-length", "Fourteen or thirty?"), "waiting-on-you", "Pricing", Now, Alice);
        Assert.True(_recipients.Send(_tenant, report, Alice, [Mike], 2, Now).Sent);

        var (status, body) = await AnswerAsync(Mike, report, "14", version: 1);

        Assert.Equal(409, status);
        Assert.Equal("version_not_held", body.GetProperty("code").GetString());
        Assert.Empty(_store.Items(_tenant, report));
        // The page reads again and is shown version 2's question.
        Assert.Equal("Fourteen or thirty?", (await ListFor(Mike)).GetProperty("waiting")[0].GetProperty("question").GetString());
    }

    [Theory]
    [InlineData("no-such-question", "14", 404)]
    [InlineData("trial-length", "21", 400)]
    public async Task AnswerAsync_AnUnknownQuestionOrOption_IsRefused_AndNothingIsStored(string questionId, string option, int expected)
    {
        var (report, _) = Report(Mike);

        var (status, _) = await AnswerAsync(Mike, report, option, "words", questionId: questionId);

        Assert.Equal(expected, status);
        Assert.Empty(_store.Items(_tenant, report));
        Assert.Empty(_comments.To(_tenant, report, Alice));
    }

    [Fact]
    public async Task AnswerAsync_ASecondAnswerFromTheSamePerson_IsRefused_AndNotSentAgain()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Idle;
        Assert.Equal(200, (await AnswerAsync(Mike, report, "14")).Status);

        var (status, body) = await AnswerAsync(Mike, report, "30", "changed my mind");

        Assert.Equal(409, status);
        Assert.Equal(TeamQuestions.AlreadyAnswered, body.GetProperty("error").GetString());
        Assert.Single(_store.Items(_tenant, report));
        Assert.Single(_prompts);
        Assert.Empty(_comments.To(_tenant, report, Alice));
    }

    [Fact]
    public async Task AnswerAsync_TheSessionHasEnded_IsRefused_NothingIsStored_AndTheQuestionNoLongerWaits()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Ended;

        var (status, body) = await AnswerAsync(Mike, report, "14", "words");

        Assert.Equal(409, status);
        Assert.Equal(TeamQuestions.SessionEnded, body.GetProperty("error").GetString());
        Assert.Empty(_store.Items(_tenant, report));
        Assert.Empty(_comments.To(_tenant, report, Alice));
        Assert.Empty(QuestionIds(await ListFor(Mike), "waiting"));
    }

    [Fact]
    public async Task AnswerAsync_WhenTheAskerCanNoLongerReadComments_AWordedAnswerIsRefused_AndAChoiceAloneStillGoes()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Idle;
        Assert.True(_teams.ChangeRole(_team, Alice, TeamRole.Collaborator).IsDone);

        var card = (await ListFor(Mike)).GetProperty("waiting")[0];
        Assert.False(card.GetProperty("canComment").GetBoolean());
        Assert.Equal(TeamQuestions.CommentClosed, card.GetProperty("commentNote").GetString());

        var (refused, body) = await AnswerAsync(Mike, report, "14", "words for nobody");
        Assert.Equal(409, refused);
        Assert.Equal("author_cannot_receive", body.GetProperty("code").GetString());
        Assert.Empty(_store.Items(_tenant, report));
        Assert.Empty(_prompts);

        Assert.Equal(200, (await AnswerAsync(Mike, report, "14")).Status);
        Assert.Single(_prompts);
        Assert.Empty(_comments.To(_tenant, report, Alice));
    }

    [Fact]
    public async Task AnswerAsync_ACommentOverTheLimit_IsRefused_AndOnlyWhitespaceIsNoComment()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Idle;

        Assert.Equal(400, (await AnswerAsync(Mike, report, "14", new string('x', DevReportPersonComments.MaxLength + 1))).Status);
        Assert.Empty(_store.Items(_tenant, report));

        Assert.Equal(200, (await AnswerAsync(Mike, report, "14", "  \n ")).Status);
        Assert.Empty(_comments.To(_tenant, report, Alice));
    }

    [Fact]
    public async Task AnswerAsync_TwoPeopleAnswerTheSameQuestionWhileTheSessionWorks_NeitherReplacesTheOther_AndBothReachIt()
    {
        var (report, sid) = Report(Mike, Nina);
        _reach[sid] = DevReportSessionReach.Busy;

        Assert.Equal(200, (await AnswerAsync(Mike, report, "14")).Status);
        Assert.Equal(200, (await AnswerAsync(Nina, report, "30")).Status);
        _reach[sid] = DevReportSessionReach.Idle;
        await _delivery.SettleAsync(_tenant, sid, CancellationToken.None);

        var prompt = Assert.Single(_prompts).Text;
        Assert.Contains("\"14 days\" (value \"14\")", prompt);
        Assert.Contains("\"30 days\" (value \"30\")", prompt);
        Assert.All(_store.Items(_tenant, report), i => Assert.Equal(DevReportItemStates.Delivered, i.Status));
    }

    // ---- what the page reads -----------------------------------------------------------------------------------------

    [Fact]
    public async Task List_AnAnsweredQuestion_MovesToAnswered_WithTheChoiceItsStateAndTheWordsReadBackToTheirWriterOnly()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Busy;
        Assert.Equal(200, (await AnswerAsync(Mike, report, "30", "my words")).Status);

        var list = await ListFor(Mike);

        Assert.Empty(QuestionIds(list, "waiting"));
        Assert.Equal(TeamQuestions.NothingWaiting, list.GetProperty("subtitle").GetString());
        var answer = list.GetProperty("answered")[0].GetProperty("answer");
        Assert.Equal("You chose \"30 days\"", answer.GetProperty("chosenLabel").GetString());
        Assert.Equal("Delivered when the agent finishes its turn", answer.GetProperty("statusLabel").GetString());
        Assert.Equal("my words", answer.GetProperty("yourComment").GetString());
        Assert.Equal("Your words went to alice@example.com, not to the agent", answer.GetProperty("yourCommentLabel").GetString());
        Assert.False(list.GetProperty("answered")[0].GetProperty("canAnswer").GetBoolean());
    }

    [Fact]
    public async Task SentToMe_ReportsSayHowManyQuestionsWaitOnTheReader_AndStopSayingSoOnceAnswered_WithAnswersStillOffInTheViewer()
    {
        var (report, sid) = Report(Mike);
        _reach[sid] = DevReportSessionReach.Busy;

        var (_, list) = await Run(_reports.SentToMe(_team, Mike));
        Assert.Equal("1 question waiting on you - answer it on Questions", list.GetProperty("reports")[0].GetProperty("questionsLabel").GetString());
        var (_, detail) = await Run(_reports.SentToMeDetail(_team, Mike, report.ToString("D")));
        Assert.Equal("1 question waiting on you - answer it on Questions", detail.GetProperty("report").GetProperty("questionsLabel").GetString());
        // The viewer's answer controls stay off: the answer is given on the Questions page, never in the frame.
        Assert.False(detail.GetProperty("answersOpen").GetBoolean());

        Assert.Equal(200, (await AnswerAsync(Mike, report, "14")).Status);
        (_, list) = await Run(_reports.SentToMe(_team, Mike));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("reports")[0].GetProperty("questionsLabel").ValueKind);
    }

    [Fact]
    public async Task AboutLabelFor_IsNullForAPlainCommentOnTheReport()
    {
        var (report, _) = Report(Mike);
        var row = _comments.Add(_tenant, report, Mike, Alice, "on the whole report", Now);
        Assert.Null(_questions.AboutLabelFor(_tenant, row));
        var (_, mine) = await Run(_reports.MineDetail(_team, Alice, report.ToString("D")));
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("comments")[0].GetProperty("aboutLabel").ValueKind);
    }

    [Theory]
    [InlineData(0, null, "No questions waiting on you.")]
    [InlineData(1, "1 question waiting on you - answer it on Questions", "One question is waiting on you.")]
    [InlineData(3, "3 questions waiting on you - answer them on Questions", "3 questions are waiting on you.")]
    public void WaitingLabelAndSubtitle_SayHowManyInWords(int waiting, string? label, string subtitle)
    {
        Assert.Equal(label, TeamQuestions.WaitingLabel(waiting));
        Assert.Equal(subtitle, TeamQuestions.Subtitle(waiting));
    }

    [Fact]
    public void ChoiceItem_CarriesTheChoiceAndNoWordsOfThePersonsOwn()
    {
        var q = Assert.Single(DevReportQuestions.Read(Html()));
        var item = TeamQuestions.ChoiceItem(q, q.Option("30")!);
        Assert.Equal((DevReportItem.Answer, "", "", "trial-length", Question, "30", "30 days"),
            (item.Kind, item.Text, item.Comment, item.QuestionId, item.Question, item.OptionValue, item.OptionLabel));
        Assert.Matches("^a-[0-9a-f]{32}$", item.Id);
    }
}
