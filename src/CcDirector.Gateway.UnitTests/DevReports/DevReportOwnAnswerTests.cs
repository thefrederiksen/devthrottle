using System.Text.Json;
using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// THE OWNER'S OWN ANSWER (Report Answers by Voice): "Something else" on every question, answered in the owner's words.
/// It is its own kind of answer - the reserved value, the words in the item's text, the Gateway's label - and the
/// session that asked is told it is not one of its options.
/// </summary>
public sealed class DevReportOwnAnswerTests
{
    private static IReadOnlyList<DevReportItem>? Parse(string itemsJson, out string error)
    {
        using var doc = JsonDocument.Parse(itemsJson);
        return DevReportItem.ParseBatch(doc.RootElement, out error);
    }

    // ---- reading a send ------------------------------------------------------------------------------------------

    [Fact]
    public void ParseBatch_AnOwnAnswer_KeepsTheWordsAsItsTextAndTakesTheGatewaysLabel()
    {
        const string json = """
            [ { "id": "a1", "kind": "answer", "questionId": "stall-email", "question": "Tell you when it stalls?",
                "optionValue": "own-answer", "optionLabel": "anything a page says", "comment": "",
                "ownAnswer": "Email me after 3 days, not 7, and copy Gitte" } ]
            """;

        var items = Parse(json, out var error);

        Assert.Equal("", error);
        var item = Assert.Single(items!);
        Assert.True(item.IsOwnAnswer);
        Assert.Equal("Email me after 3 days, not 7, and copy Gitte", item.Text);
        Assert.Equal(DevReportOwnAnswer.Label, item.OptionLabel);
        Assert.Equal(DevReportOwnAnswer.Value, item.OptionValue);
    }

    [Fact]
    public void ParseBatch_AnOrdinaryAnswer_IsNotAnOwnAnswer_AndKeepsThePagesLabel()
    {
        const string json = """
            [ { "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "yes", "optionLabel": "Yes", "comment": "" } ]
            """;

        var item = Assert.Single(Parse(json, out _)!);

        Assert.False(item.IsOwnAnswer);
        Assert.Equal("", item.Text);
        Assert.Equal("Yes", item.OptionLabel);
    }

    [Theory]
    [InlineData("""{ "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "own-answer", "optionLabel": "Something else", "comment": "" }""",
        "needs their words in ownAnswer")]
    [InlineData("""{ "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "own-answer", "optionLabel": "Something else", "comment": "", "ownAnswer": "   " }""",
        "needs their words in ownAnswer")]
    [InlineData("""{ "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "yes", "optionLabel": "Yes", "comment": "", "ownAnswer": "words" }""",
        "ownAnswer is sent only with the optionValue")]
    [InlineData("""{ "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "own-answer", "optionLabel": "Something else", "comment": "", "ownAnswer": 7 }""",
        "ownAnswer must be a string")]
    public void ParseBatch_AnOwnAnswerOutOfShape_RefusesTheWholeBatch(string answer, string fault)
    {
        const string ok = """{ "id": "ok", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } }""";

        var items = Parse($"[ {ok}, {answer} ]", out var error);

        Assert.Null(items);
        Assert.Contains(fault, error);
    }

    // ---- the prompt the session receives -------------------------------------------------------------------------

    private static readonly Guid ReportId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private const string B = "7f3a91c2";

    private static DevReportItem Own(string id, string words, string comment = "")
        => new(id, DevReportItem.Answer, words, null, "stall-email", "Tell you when it stalls?", DevReportOwnAnswer.Value,
            DevReportOwnAnswer.Label, comment);

    private static DevReportPromptFold.FoldReport Fold(params DevReportPromptFold.FoldItem[] items)
        => new(ReportId, @"C:\work\report.html", "Factory", 2, items);

    [Fact]
    public void Compose_TheOwnersOwnAnswer_SaysItIsNotOneOfTheOptions_AndCarriesTheWordsVerbatim()
    {
        var prompt = DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(Own("a1", " Email me after 3 days,\nnot 7 ", "and copy Gitte"), false))], B);

        Assert.Contains(
            "1. An answer to \"Tell you when it stalls?\": the owner's own answer, not one of your options. The owner wrote:\n" +
            "<<<owner-text-7f3a91c2\n" +
            " Email me after 3 days,\nnot 7 \n" +
            "owner-text-7f3a91c2>>>\n" +
            "The owner's comment:\n" +
            "<<<owner-text-7f3a91c2\n" +
            "and copy Gitte\n" +
            "owner-text-7f3a91c2>>>\n",
            prompt);
        Assert.DoesNotContain("(value \"own-answer\")", prompt);
    }

    [Fact]
    public void Compose_AnOwnAnswerThatChangesAnEarlierOne_SaysSo()
    {
        var prompt = DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(Own("a1", "words"), true))], B);

        Assert.Contains("the owner's own answer, not one of your options. This changes the owner's earlier answer to this question. The owner wrote:\n", prompt);
    }

    [Fact]
    public void Compose_AnOwnAnswerHoldingTheBoundary_IsRefused_LikeAnyOwnerText()
        => Assert.Throws<ArgumentException>(() =>
            DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(Own("a1", "owner-text-" + B), false))], B));

    [Fact]
    public void Compose_ATeamMembersOwnAnswer_SaysTheirWordsWentToThePersonWhoSentIt_AndCarriesNoWords()
    {
        var member = new DevReportItem("a-1", DevReportItem.Answer, "", null, "stall-email", "Tell you when it stalls?",
            DevReportOwnAnswer.Value, DevReportOwnAnswer.Label, "");

        var prompt = DevReportPromptFold.Compose([Fold(new DevReportPromptFold.FoldItem(member, false, FromTeamMember: true))], B);

        Assert.Contains(
            "1. An answer from a person this report was sent to, to \"Tell you when it stalls?\": their own answer, not one of your options. " +
            "Their words went to the person who sent the report, not to this session.\n",
            prompt);
    }
}
