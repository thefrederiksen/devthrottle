using CcDirector.Core.Tenancy;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Questions;

/// <summary>
/// A team member's answer in the two places the agent's conversation is written (devthrottle_internal#2307): the prompt
/// (<see cref="DevReportPromptFold"/>) and the store (<see cref="DevReportStore"/>). Both refuse an item from a member
/// that carries any words of the member's own, and both keep two people's answers apart. The store takes a member's
/// answer in one transaction that decides, in the database, the version held and that it is their only answer.
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

    // ---- the store: a member's answer, taken in one transaction (review F2, F3, F4) ----------------------------------

    /// <summary>A report Alice wrote, sent at version 1 to Mike and Nina - the recipient rows an answer is checked against.</summary>
    private (DevReportStore Store, DevReportRecipients Recipients, CcDirector.Gateway.Data.Entities.DevReportEntity Report) Sent()
    {
        var db = _harness.Open();
        var store = new DevReportStore(db);
        var recipients = new DevReportRecipients(db);
        var report = store.Publish(Team, "s1", @"C:\r.html", "<p>x</p>", "waiting-on-you", "R", Now, "sub-alice").Report;
        Assert.True(recipients.Send(Team, report.Id, "sub-alice", ["sub-mike", "sub-nina"], 1, Now).Sent);
        return (store, recipients, report);
    }

    private static MemberAnswerOutcome Answer(DevReportStore store, CcDirector.Gateway.Data.Entities.DevReportEntity report, string who, string id,
        string option = "30", string words = "", int version = 1)
        => store.AddMemberAnswer(Team, report, version, Choice(id, option), who, words, "sub-alice", "device", Now);

    [Theory]
    [InlineData("their words", "", DevReportItem.Answer)]
    [InlineData("", "their words", DevReportItem.Note)]
    public void AddMemberAnswer_AnItemCarryingWordsOfTheirOwn_IsRefused_AndNothingIsStored(string comment, string text, string kind)
    {
        var (store, _, report) = Sent();

        Assert.Throws<ArgumentException>(() => store.AddMemberAnswer(Team, report, 1,
            Choice("a-1", comment: comment, text: text, kind: kind), "sub-mike", "", "sub-alice", "device", Now));
        Assert.Empty(store.Items(Team, report.Id));
    }

    [Fact]
    public void AddMemberAnswer_TheChoiceIsHeld_WithTheVersionItWasGivenOn_AndTheWordsGoToThePersonInTheSameWrite()
    {
        var (store, _, report) = Sent();

        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-mike", words: "We trialled 14 days."));

        var item = Assert.Single(store.Items(Team, report.Id));
        Assert.Equal((DevReportItemStates.Held, "sub-mike", (int?)1, "", ""), (item.Status, item.AnswererSubject, item.SourceVersion, item.Comment, item.Text));
        var comment = Assert.Single(new DevReportPersonComments(_harness.Open()).To(Team, report.Id, "sub-alice"));
        Assert.Equal(("sub-mike", "We trialled 14 days.", "trial-length"), (comment.FromSubject, comment.Text, comment.QuestionId));
    }

    [Fact]
    public void AddMemberAnswer_TwoPeople_EachHaveTheirOwn_AndTheOwnersAnswerIsUntouched()
    {
        var (store, _, report) = Sent();
        store.AddItems(Team, report, [Choice("a-owner", "14")], DevReportItemStates.HeldState, "device", Now);

        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-mike", "14"));
        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-nina", "a-nina", "30"));

        Assert.All(store.Items(Team, report.Id), i => Assert.Equal(DevReportItemStates.Held, i.Status));
        Assert.Equal(new[] { "a-mike" }, store.AnswersBy(Team, "sub-mike", [report.Id]).Values.Select(i => i.ClientItemId));
        Assert.False(store.HasDeliveredAnswer(Team, report.Id, "trial-length", "sub-nina"));
    }

    [Fact]
    public void AddMemberAnswer_ASecondAnswerFromTheSamePerson_IsNotTaken_AndItsWordsAreNotStored()
    {
        var (store, _, report) = Sent();
        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-1", "14", "first words"));

        Assert.Equal(MemberAnswerOutcome.AlreadyAnswered, Answer(store, report, "sub-mike", "a-2", "30", "second words"));

        Assert.Equal("a-1", Assert.Single(store.Items(Team, report.Id)).ClientItemId);
        Assert.Equal("first words", Assert.Single(new DevReportPersonComments(_harness.Open()).To(Team, report.Id, "sub-alice")).Text);
    }

    [Fact]
    public void AddMemberAnswer_AfterTheirAnswerWasRefused_ThePersonMayAnswerAgain()
    {
        var (store, _, report) = Sent();
        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-1"));
        Assert.Equal(1, store.RefuseWaiting(Team, "s1", DevReportItemStates.SessionEndedState));
        Assert.Empty(store.AnswersBy(Team, "sub-mike", [report.Id]));

        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-2"));
        Assert.Equal("a-2", Assert.Single(store.AnswersBy(Team, "sub-mike", [report.Id]).Values).ClientItemId);
    }

    [Fact]
    public void AddMemberAnswer_AVersionThePersonDoesNotHold_IsNotTaken_AndNoWordsAreStored()
    {
        var (store, _, report) = Sent();

        Assert.Equal(MemberAnswerOutcome.VersionNotHeld, Answer(store, report, "sub-mike", "a-1", words: "words", version: 2));
        Assert.Equal(MemberAnswerOutcome.VersionNotHeld, Answer(store, report, "sub-stranger", "a-2", words: "words"));

        Assert.Empty(store.Items(Team, report.Id));
        Assert.Empty(new DevReportPersonComments(_harness.Open()).To(Team, report.Id, "sub-alice"));
    }

    /// <summary>REVIEW F3, THE INTERLEAVING. The route has read that Mike holds version 1; before the answer's transaction
    /// begins, Alice sends version 2 to him (another request, or the other process during a deploy). The answer to version
    /// 1 is not taken: the database, not the earlier read, decides which version he holds.</summary>
    [Fact]
    public void AddMemberAnswer_ANewerVersionSentBetweenTheReadAndTheWrite_IsNotTaken()
    {
        var (store, recipients, report) = Sent();
        var publisher = new DevReportStore(_harness.Open());
        publisher.Publish(Team, "s1", @"C:\r.html", "<p>y</p>", "waiting-on-you", "R2", Now, "sub-alice");
        store.BeforeMemberAnswerWriteForTests = () =>
            Assert.True(new DevReportRecipients(_harness.Open()).Send(Team, report.Id, "sub-alice", ["sub-mike"], 2, Now).Sent);

        Assert.Equal(MemberAnswerOutcome.VersionNotHeld, Answer(store, report, "sub-mike", "a-1", words: "words"));

        Assert.Empty(store.Items(Team, report.Id));
        Assert.Empty(new DevReportPersonComments(_harness.Open()).To(Team, report.Id, "sub-alice"));
        Assert.Equal(2, recipients.RowFor(Team, report.Id, "sub-mike")!.SentVersion);
    }

    /// <summary>REVIEW F2, THE INTERLEAVING. Two Gateway processes take the same person's answer at once: both checked
    /// that nothing was answered, and the other one's answer commits between this one's check and its write. Only one is
    /// taken, with only its words - no in-memory lock spans the two, so the database decides.</summary>
    [Fact]
    public void AddMemberAnswer_AnotherProcessTakesTheSamePersonsAnswerBetweenTheReadAndTheWrite_OnlyOneIsTaken()
    {
        var (store, _, report) = Sent();
        var otherProcess = new DevReportStore(_harness.Open());
        store.BeforeMemberAnswerWriteForTests = () =>
            Assert.Equal(MemberAnswerOutcome.Stored, Answer(otherProcess, report, "sub-mike", "a-other", "14", "the other process's words"));

        Assert.Equal(MemberAnswerOutcome.AlreadyAnswered, Answer(store, report, "sub-mike", "a-this", "30", "this process's words"));

        Assert.Equal("a-other", Assert.Single(store.Items(Team, report.Id)).ClientItemId);
        Assert.Equal("the other process's words", Assert.Single(new DevReportPersonComments(_harness.Open()).To(Team, report.Id, "sub-alice")).Text);
    }

    /// <summary>The database's own guarantee, beneath the transaction's check: a second not-refused answer by one person to
    /// one question cannot be written at all, while a refused one does not stand in the way.</summary>
    [Fact]
    public void TheMemberAnswerIndex_RefusesASecondNotRefusedAnswerByOnePerson_AndIgnoresARefusedOne()
    {
        var (store, _, report) = Sent();
        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-1"));

        CcDirector.Gateway.Data.Entities.DevReportItemEntity Row(string id, string status) => new()
        {
            TenantId = Team.Value, ReportId = report.Id, SessionId = "s1", ClientItemId = id, Kind = DevReportItem.Answer,
            QuestionId = "trial-length", Status = status, StatusLabel = status, SenderKind = "device", SentAtUtc = Now,
            AnswererSubject = "sub-mike", SourceVersion = 1,
        };
        using (var ctx = _harness.Open().CreateContext(Team))
        {
            ctx.DevReportItems.Add(Row("a-dup", DevReportItemStates.Held));
            var ex = Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() => ctx.SaveChanges());
            Assert.Contains("UNIQUE", ex.InnerException?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        }
        using (var ctx = _harness.Open().CreateContext(Team))
        {
            ctx.DevReportItems.Add(Row("a-refused", DevReportItemStates.Refused));
            ctx.SaveChanges();
        }
        Assert.Equal(2, store.Items(Team, report.Id).Count);
    }

    [Fact]
    public void AnswersBy_LeavesOutARefusedAnswer_SoTheQuestionStillWaits_AndHasDeliveredAnswerIsPerPerson()
    {
        var (store, _, report) = Sent();
        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-mike", "a-mike"));
        Assert.Equal(1, store.RefuseWaiting(Team, "s1", DevReportItemStates.SessionEndedState));
        Assert.Empty(store.AnswersBy(Team, "sub-mike", [report.Id]));

        Assert.Equal(MemberAnswerOutcome.Stored, Answer(store, report, "sub-nina", "a-nina"));
        var claim = Guid.NewGuid();
        Assert.Single(store.ClaimWaiting(Team, "s1", claim, Now));
        store.FinishClaim(Team, claim, DevReportItemStates.DeliveredState, Now);
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
