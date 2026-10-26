using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// A session reads the owner's stored answers to a report, and marks them acted on (Factories Run Alone, the
/// read-answers command). Over the real store and the real migrated schema; the owner's answers go in through the real
/// delivery, to a session that has ENDED - the factory run that published the report and closed. The only fakes are the
/// roster's verdict and who the calling session's Director belongs to.
/// </summary>
public sealed class DevReportAnswersTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-report-answers");
    private static readonly TenantId Other = new("tenant-report-answers-other");
    private static readonly DateTime Now = new(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
    private const string Key = @"d:\factory\outreach\report.html";
    private const string DirectorId = "director-answers";

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly DevReportStore _store;
    private readonly string _runOne = Guid.NewGuid().ToString("D");
    private readonly string _runTwo = Guid.NewGuid().ToString("D");
    private readonly string _reader = Guid.NewGuid().ToString("D");
    private DateTime _now = Now;

    /// <summary>Who the caller's Director belongs to: a personal account unless a test says otherwise.</summary>
    private DevReportAuthorAnswer _author = DevReportAuthorAnswer.PersonalAccount;

    public DevReportAnswersTests()
    {
        _db = _h.Open();
        _store = new DevReportStore(_db);
    }

    public void Dispose() => _h.Dispose();

    private DevReportAnswers Answers() => new(_store, (_, _) => _author, () => _now);

    /// <summary>The owner's send through the real delivery, to a session the roster says has ENDED.</summary>
    private Task<IReadOnlyList<DevReportItemUpdate>> OwnerSends(DevReportEntity report, params DevReportItem[] items)
        => new DevReportDelivery(_store,
                (_, _) => new DevReportSessionLiveness(DevReportSessionReach.Ended, null, "the run closed"),
                (_, _) => throw new InvalidOperationException("nothing may be typed into an ended session"),
                CancellationToken.None, () => _now)
            .SendAsync(Tenant, report, items, "device", default);

    private const string Html =
        "<header data-dev-report=\"header\" data-dev-report-status=\"waiting-on-you\"><h1>Outreach run</h1></header>" +
        "<section data-dev-report=\"summary\"><p>S</p></section>" +
        "<section data-dev-report=\"questions\">" +
        "<div data-dev-report-question=\"deploy\" data-dev-report-question-text=\"When should we send?\">" +
        "<label><input type=\"radio\" name=\"deploy\" value=\"tonight\" data-recommended> Tonight</label>" +
        "<label><input type=\"radio\" name=\"deploy\" value=\"monday\"> Monday</label></div>" +
        "<div data-dev-report-question=\"scope\"><h3>Which list?</h3>" +
        "<label><input type=\"radio\" name=\"scope\" value=\"all\" data-recommended> All</label>" +
        "<label><input type=\"radio\" name=\"scope\" value=\"warm\"> Warm only</label></div>" +
        "</section>" +
        "<section data-dev-report=\"detail\"><p>D</p></section>";

    private DevReportEntity Publish(string sessionId, TenantId? tenant = null, string? author = null, string key = Key)
        => _store.Publish(tenant ?? Tenant, sessionId, key, Html, "waiting-on-you", "Outreach run", _now, author).Report;

    private static DevReportItem Answer(string id, string questionId, string value, string comment = "")
        => new(id, DevReportItem.Answer, "", null, questionId, "When should we send?", value, value == "monday" ? "Monday" : "Tonight", comment);

    private static DevReportItem Note(string id, string text)
        => new(id, DevReportItem.Note, text, new DevReportAnchor(DevReportAnchor.Text, "p", "the quoted words", null, null, null),
            "", "", "", "", "");

    private static async Task<(int Status, JsonElement Body)> Run(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Question(JsonElement report, string id)
        => report.GetProperty("questions").EnumerateArray().Single(q => q.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Read_ByReportId_FromADifferentSessionOfTheAccount_ShowsTheAnswerAndTheOpenQuestion()
    {
        var report = Publish(_runOne);
        _now = Now.AddHours(2);
        var sent = await OwnerSends(report, Answer("a1", "deploy", "monday", "after the holiday"), Note("n1", "Drop the second paragraph"));
        Assert.All(sent, u => Assert.Equal(DevReportItemStates.Saved, u.Status));

        var (status, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        Assert.Equal(200, status);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        var view = body.GetProperty("reports")[0];
        Assert.Equal(report.Id.ToString("D"), view.GetProperty("id").GetString());
        Assert.Equal(_runOne, view.GetProperty("sessionId").GetString());
        var deploy = Question(view, "deploy");
        Assert.True(deploy.GetProperty("answered").GetBoolean());
        Assert.Equal("When should we send?", deploy.GetProperty("question").GetString());
        Assert.Equal("monday", deploy.GetProperty("optionValue").GetString());
        Assert.Equal("Monday", deploy.GetProperty("optionLabel").GetString());
        Assert.Equal("after the holiday", deploy.GetProperty("comment").GetString());
        Assert.Equal(Now.AddHours(2), deploy.GetProperty("answeredAtUtc").GetDateTime());
        Assert.Equal(JsonValueKind.Null, deploy.GetProperty("actedOnAtUtc").ValueKind);
        var scope = Question(view, "scope");
        Assert.False(scope.GetProperty("answered").GetBoolean());
        Assert.Equal("Which list?", scope.GetProperty("question").GetString());
        var note = Assert.Single(view.GetProperty("notes").EnumerateArray());
        Assert.Equal("Drop the second paragraph", note.GetProperty("text").GetString());
        Assert.Equal("the quoted words", note.GetProperty("quote").GetString());
        Assert.Equal(1, view.GetProperty("answered").GetInt32());
        Assert.Equal(1, view.GetProperty("notAnswered").GetInt32());
        Assert.Equal(2, view.GetProperty("notActedOn").GetInt32());
    }

    [Fact]
    public async Task Read_NothingAnswered_SaysEveryQuestionIsNotAnswered()
    {
        var report = Publish(_runOne);

        var (_, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        var view = body.GetProperty("reports")[0];
        Assert.All(view.GetProperty("questions").EnumerateArray(), q => Assert.False(q.GetProperty("answered").GetBoolean()));
        Assert.Equal(2, view.GetProperty("notAnswered").GetInt32());
        Assert.Equal(0, view.GetProperty("notActedOn").GetInt32());
    }

    [Fact]
    public async Task Read_TheLatestAnswerToAQuestionWins()
    {
        var report = Publish(_runOne);
        await OwnerSends(report, Answer("a1", "deploy", "tonight"));
        _now = Now.AddMinutes(5);
        await OwnerSends(report, Answer("a2", "deploy", "monday"));

        var (_, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        var deploy = Question(body.GetProperty("reports")[0], "deploy");
        Assert.Equal("a2", deploy.GetProperty("itemId").GetString());
        Assert.Equal("monday", deploy.GetProperty("optionValue").GetString());
    }

    [Fact]
    public async Task Read_AnotherAccountsReport_IsNotFound_AndDoesNotLeak()
    {
        var theirs = Publish(_runOne, Other);

        var (status, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, theirs.Id.ToString("D"), null));
        var (keyStatus, keyBody) = await Run(Answers().Read(Tenant, _reader, DirectorId, null, Key));

        Assert.Equal(404, status);
        Assert.Equal("report_not_found", body.GetProperty("code").GetString());
        Assert.Equal(200, keyStatus);
        Assert.Equal(0, keyBody.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Read_InATeam_ATeamMatesReportIsNotFound_AndTheirOwnIsRead()
    {
        var mine = Publish(_runOne, author: "sub-alice");
        var hers = Publish(_runTwo, author: "sub-bob");
        _author = new DevReportAuthorAnswer(true, "sub-alice");

        var (bobStatus, _) = await Run(Answers().Read(Tenant, _reader, DirectorId, hers.Id.ToString("D"), null));
        var (aliceStatus, _) = await Run(Answers().Read(Tenant, _reader, DirectorId, mine.Id.ToString("D"), null));
        var (_, byKey) = await Run(Answers().Read(Tenant, _reader, DirectorId, null, Key));

        Assert.Equal(404, bobStatus);
        Assert.Equal(200, aliceStatus);
        Assert.Equal(mine.Id.ToString("D"), Assert.Single(byKey.GetProperty("reports").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Read_InATeam_ASessionNobodyCanBeNamedBehind_IsRefused()
    {
        var report = Publish(_runOne, author: "sub-alice");
        _author = DevReportAuthorAnswer.Unknown;

        var (status, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));
        var (ackStatus, _) = await Run(Answers().Ack(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        Assert.Equal(403, status);
        Assert.Equal("report_author_unknown", body.GetProperty("code").GetString());
        Assert.Equal(403, ackStatus);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    [InlineData("11111111-1111-1111-1111-111111111111", Key)]
    public async Task Read_NotExactlyOneOfReportAndKey_IsABadQuery(string? reportId, string? key)
    {
        var (status, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, reportId, key));

        Assert.Equal(400, status);
        Assert.Equal("bad_query", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Read_ByKey_FindsTheSeatsEarlierReportAcrossSessions_UntilItIsActedOn()
    {
        // Run one publishes and closes; the owner answers it. Run two publishes the same file again - a NEW report, because
        // a report is keyed on (session, file). The answer is on run one's report, and a key read must not hide it.
        var first = Publish(_runOne);
        await OwnerSends(first, Answer("a1", "deploy", "monday"));
        _now = Now.AddDays(1);
        var second = Publish(_runTwo);

        var (_, before) = await Run(Answers().Read(Tenant, _reader, DirectorId, null, Key));

        Assert.Equal(new[] { second.Id.ToString("D"), first.Id.ToString("D") },
            before.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("id").GetString()));

        await Run(Answers().Ack(Tenant, _reader, DirectorId, first.Id.ToString("D"), null));
        var (_, after) = await Run(Answers().Read(Tenant, _reader, DirectorId, null, Key));

        Assert.Equal(second.Id.ToString("D"), Assert.Single(after.GetProperty("reports").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Ack_MarksEveryItemActedOnByTheCaller_AndASecondAckMarksNothing()
    {
        var report = Publish(_runOne);
        await OwnerSends(report, Answer("a1", "deploy", "monday"), Note("n1", "words"));
        _now = Now.AddHours(3);

        var (status, body) = await Run(Answers().Ack(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        Assert.Equal(200, status);
        Assert.Equal(new[] { "a1", "n1" }, body.GetProperty("marked").EnumerateArray().Select(m => m.GetProperty("itemId").GetString()));
        var view = body.GetProperty("reports")[0];
        Assert.Equal(0, view.GetProperty("notActedOn").GetInt32());
        var deploy = Question(view, "deploy");
        Assert.Equal(Now.AddHours(3), deploy.GetProperty("actedOnAtUtc").GetDateTime());
        Assert.Equal(_reader, deploy.GetProperty("actedOnSessionId").GetString());

        _now = Now.AddHours(4);
        var (_, again) = await Run(Answers().Ack(Tenant, Guid.NewGuid().ToString("D"), DirectorId, report.Id.ToString("D"), null));

        Assert.Empty(again.GetProperty("marked").EnumerateArray());
        Assert.Equal(_reader, Question(again.GetProperty("reports")[0], "deploy").GetProperty("actedOnSessionId").GetString());
    }

    [Fact]
    public async Task Ack_NamedItems_MarksOnlyThose()
    {
        var report = Publish(_runOne);
        await OwnerSends(report, Answer("a1", "deploy", "monday"), Note("n1", "words"));

        var (_, body) = await Run(Answers().Ack(Tenant, _reader, DirectorId, report.Id.ToString("D"), ["a1", "no-such-item"]));

        Assert.Equal("a1", Assert.Single(body.GetProperty("marked").EnumerateArray()).GetProperty("itemId").GetString());
        Assert.Equal(1, body.GetProperty("reports")[0].GetProperty("notActedOn").GetInt32());
    }

    [Fact]
    public async Task Ack_ThenTheOwnerChangesHisAnswer_TheNewAnswerIsNotActedOn()
    {
        var report = Publish(_runOne);
        await OwnerSends(report, Answer("a1", "deploy", "monday"));
        await Run(Answers().Ack(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));
        _now = Now.AddHours(1);
        await OwnerSends(report, Answer("a2", "deploy", "tonight"));

        var (_, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        var deploy = Question(body.GetProperty("reports")[0], "deploy");
        Assert.Equal("a2", deploy.GetProperty("itemId").GetString());
        Assert.Equal(JsonValueKind.Null, deploy.GetProperty("actedOnAtUtc").ValueKind);
    }

    [Fact]
    public async Task Ack_AnotherAccountsReport_IsNotFound_AndMarksNothing()
    {
        var theirs = Publish(_runOne, Other);
        _store.AddItems(Other, theirs, [Answer("a1", "deploy", "monday")], DevReportItemStates.SavedState, "device", Now);

        var (status, _) = await Run(Answers().Ack(Tenant, _reader, DirectorId, theirs.Id.ToString("D"), null));

        Assert.Equal(404, status);
        Assert.Null(Assert.Single(_store.Items(Other, theirs.Id)).ActedOnAtUtc);
    }

    [Fact]
    public async Task Read_ATeamMembersAnswerIsNeverShown()
    {
        var report = Publish(_runOne);
        _store.AddItems(Tenant, report, [Answer("a1", "deploy", "monday")], DevReportItemStates.SavedState, "device", Now);
        using (var ctx = _db.CreateContext(Tenant))
        {
            var row = ctx.DevReportItems.Single(i => i.ClientItemId == "a1");
            row.AnswererSubject = "sub-member";
            ctx.SaveChanges();
        }

        var (_, body) = await Run(Answers().Read(Tenant, _reader, DirectorId, report.Id.ToString("D"), null));

        Assert.False(Question(body.GetProperty("reports")[0], "deploy").GetProperty("answered").GetBoolean());
    }
}
