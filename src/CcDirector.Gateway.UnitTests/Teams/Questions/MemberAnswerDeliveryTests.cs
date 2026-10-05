using CcDirector.Core.Tenancy;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Questions;

/// <summary>
/// A team member's answer in the two places the agent's conversation is written (devthrottle_internal#2307): the prompt
/// (<see cref="DevReportPromptFold"/>) and the store (<see cref="DevReportStore"/>). Both refuse an item from a member
/// that carries any words of the member's own, and both keep two people's answers apart.
/// </summary>
public sealed class MemberAnswerDeliveryTests : IDisposable
{
    private static readonly TenantId Team = new("team-questions");
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ReportId = Guid.Parse("7a000000-0000-4000-8000-000000000001");
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static DevReportItem Choice(string id, string option = "30", string comment = "", string text = "", string kind = DevReportItem.Answer)
        => new(id, kind, text, null, "trial-length", "14 days or 30?", option, option + " days", comment);

    private static DevReportPromptFold.FoldReport Fold(params DevReportPromptFold.FoldItem[] items)
        => new(ReportId, @"C:\work\pricing.html", "Pricing", 1, items);

    // ---- the prompt ---------------------------------------------------------------------------------------------

    [Fact]
    public void Compose_AMembersAnswer_IsTheChoiceFromAPersonTheReportWasSentTo_WithNoOwnersWordsLine()
    {
        var text = DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(Choice("a-1"), false, FromTeamMember: true))], "b0b0b0b0");

        Assert.Equal(
            "A person this report was sent to answered your dev report \"Pricing\" (version 1, file \"C:\\\\work\\\\pricing.html\").\n\n" +
            "1. An answer from a person this report was sent to, to \"14 days or 30?\": \"30 days\" (value \"30\").\n\n" +
            "Reply in the report with: cc-dev-reports reply --report 7a000000-0000-4000-8000-000000000001 \"<your reply>\"\n" +
            "Then update the report file and publish it again with: cc-dev-reports open \"C:\\\\work\\\\pricing.html\"\n",
            text);
    }

    [Fact]
    public void Compose_AMembersAnswerBesideTheOwnersItems_KeepsTheOwnersWordsLine_AndSaysWhoGaveEach()
    {
        var text = DevReportPromptFold.Compose([Fold(
            new DevReportPromptFold.FoldItem(Choice("a-1", "14", comment: "owner words"), false),
            new DevReportPromptFold.FoldItem(Choice("a-2"), true, FromTeamMember: true))], "b0b0b0b0");

        Assert.StartsWith("The owner's own words below sit between a line <<<owner-text-b0b0b0b0", text);
        Assert.Contains("The owner answered your dev report \"Pricing\"", text);
        Assert.Contains("1. An answer to \"14 days or 30?\": \"14 days\" (value \"14\").\nThe owner's comment:", text);
        Assert.Contains("2. An answer from a person this report was sent to, to \"14 days or 30?\": \"30 days\" (value \"30\"). " +
                        "This changes that person's earlier answer to this question.", text);
    }

    [Theory]
    [InlineData("their words", "", DevReportItem.Answer)]
    [InlineData("", "their words", DevReportItem.Answer)]
    [InlineData("", "their words", DevReportItem.Note)]
    public void Compose_AMembersItemCarryingWordsOfTheirOwn_IsRefused(string comment, string text, string kind)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(Choice("a-1", comment: comment, text: text, kind: kind), false, FromTeamMember: true))], "b0b0b0b0"));
        Assert.Contains("never into a session", ex.Message);
    }

    // ---- the store ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("their words", "", DevReportItem.Answer)]
    [InlineData("", "their words", DevReportItem.Note)]
    public void AddItems_AMembersItemCarryingWordsOfTheirOwn_IsRefused_AndNothingIsStored(string comment, string text, string kind)
    {
        var store = new DevReportStore(_harness.Open());
        var report = store.Publish(Team, "s1", @"C:\r.html", "<p>x</p>", "waiting-on-you", "R", Now, "sub-alice").Report;

        Assert.Throws<ArgumentException>(() => store.AddItems(Team, report,
            [Choice("a-1", comment: comment, text: text, kind: kind)], DevReportItemStates.HeldState, "device", Now, "sub-mike"));
        Assert.Empty(store.Items(Team, report.Id));
    }

    [Fact]
    public void AddItems_ALaterAnswerReplacesOnlyTheSamePersonsWaitingOne_AndADeliveredOneCountsPerPerson()
    {
        var store = new DevReportStore(_harness.Open());
        var report = store.Publish(Team, "s1", @"C:\r.html", "<p>x</p>", "waiting-on-you", "R", Now, "sub-alice").Report;

        store.AddItems(Team, report, [Choice("a-mike", "14")], DevReportItemStates.HeldState, "device", Now, "sub-mike");
        store.AddItems(Team, report, [Choice("a-nina", "30")], DevReportItemStates.HeldState, "device", Now, "sub-nina");
        store.AddItems(Team, report, [Choice("a-owner", "14")], DevReportItemStates.HeldState, "device", Now);
        Assert.All(store.Items(Team, report.Id), i => Assert.Equal(DevReportItemStates.Held, i.Status));

        store.AddItems(Team, report, [Choice("a-mike-2", "30")], DevReportItemStates.HeldState, "device", Now, "sub-mike");
        var byId = store.Items(Team, report.Id).ToDictionary(i => i.ClientItemId);
        Assert.Equal(DevReportItemStates.Replaced, byId["a-mike"].Status);
        Assert.Equal(DevReportItemStates.Held, byId["a-nina"].Status);
        Assert.Equal(DevReportItemStates.Held, byId["a-owner"].Status);

        Assert.Equal(new[] { "a-mike-2" }, store.AnswersBy(Team, "sub-mike", [report.Id]).Values.Select(i => i.ClientItemId));
        Assert.False(store.HasDeliveredAnswer(Team, report.Id, "trial-length", "sub-nina"));
    }

    [Fact]
    public void AnswersBy_LeavesOutARefusedAnswer_SoTheQuestionStillWaits_AndHasDeliveredAnswerIsPerPerson()
    {
        var store = new DevReportStore(_harness.Open());
        var report = store.Publish(Team, "s1", @"C:\r.html", "<p>x</p>", "waiting-on-you", "R", Now, "sub-alice").Report;
        store.AddItems(Team, report, [Choice("a-mike")], DevReportItemStates.HeldState, "device", Now, "sub-mike");
        Assert.Equal(1, store.RefuseWaiting(Team, "s1", DevReportItemStates.SessionEndedState));

        Assert.Empty(store.AnswersBy(Team, "sub-mike", [report.Id]));

        store.AddItems(Team, report, [Choice("a-nina")], DevReportItemStates.DeliveredState, "device", Now, "sub-nina");
        Assert.True(store.HasDeliveredAnswer(Team, report.Id, "trial-length", "sub-nina"));
        Assert.False(store.HasDeliveredAnswer(Team, report.Id, "trial-length", "sub-mike"));
        Assert.False(store.HasDeliveredAnswer(Team, report.Id, "trial-length"));
    }

    [Fact]
    public void PersonComments_Add_KeepsTheQuestionTheCommentIsAbout()
    {
        var comments = new DevReportPersonComments(_harness.Open());
        comments.Add(Team, ReportId, "sub-mike", "sub-alice", "about the trial", Now, "trial-length");
        comments.Add(Team, ReportId, "sub-mike", "sub-alice", "about the whole report", Now.AddMinutes(1));

        Assert.Equal(new[] { "trial-length", null }, comments.To(Team, ReportId, "sub-alice").Select(c => c.QuestionId));
    }
}
