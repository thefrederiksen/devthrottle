using System.Text.Json;
using CcDirector.Gateway.DevReports;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>Reading a send's items the way the note-taking script validates them (CONTRACT.md section 3): the
/// whole batch or nothing.</summary>
public sealed class DevReportItemParseTests
{
    private static IReadOnlyList<DevReportItem>? Parse(string itemsJson, out string error)
    {
        using var doc = JsonDocument.Parse(itemsJson);
        return DevReportItem.ParseBatch(doc.RootElement, out error);
    }

    [Fact]
    public void ParseBatch_ContractExamples_ReadEveryField()
    {
        const string json = """
            [
              { "id": "n3", "kind": "note", "text": "This number is wrong", "pending": true, "statusLabel": "x",
                "anchor": { "type": "table-cell", "selector": "#results td", "quote": "42", "rowLabel": "Gateway", "columnLabel": "Failures" } },
              { "id": "a1", "kind": "answer", "questionId": "deploy-window", "question": "When should we deploy?",
                "optionValue": "tonight", "optionLabel": "Tonight - quiet traffic", "comment": "" }
            ]
            """;

        var items = Parse(json, out var error);

        Assert.NotNull(items);
        Assert.Equal("", error);
        Assert.Equal(new DevReportItem("n3", "note", "This number is wrong",
            new DevReportAnchor("table-cell", "#results td", "42", "Gateway", "Failures", null), "", "", "", "", ""), items![0]);
        Assert.Equal(new DevReportItem("a1", "answer", "", null, "deploy-window", "When should we deploy?",
            "tonight", "Tonight - quiet traffic", ""), items[1]);
    }

    [Theory]
    [InlineData("""{ "id": "n1" }""", "must be an array")]
    [InlineData("""[]""", "empty")]
    [InlineData("""[ { "id": "", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""", "id is empty")]
    [InlineData("""[ { "id": "n1", "kind": "comment", "text": "t" } ]""", "kind")]
    [InlineData("""[ { "id": "n1", "kind": "note", "text": "t" } ]""", "anchor")]
    [InlineData("""[ { "id": "n1", "kind": "note", "text": "t", "anchor": { "type": "pixel", "selector": "s", "quote": "q" } } ]""", "anchor.type")]
    [InlineData("""[ { "id": "n1", "kind": "note", "text": 5, "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""", "text must be a string")]
    [InlineData("""[ { "id": "n1", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q", "label": null } } ]""", "label must be a string")]
    [InlineData("""[ { "id": "a1", "kind": "answer", "questionId": "", "question": "Q", "optionValue": "v", "optionLabel": "V", "comment": "" } ]""", "questionId is empty")]
    [InlineData("""[ { "id": "a1", "kind": "answer", "questionId": "q", "question": "Q", "optionValue": "v", "optionLabel": "V" } ]""", "comment must be a string")]
    public void ParseBatch_MalformedItem_RefusesTheWholeBatch(string json, string fault)
    {
        // A valid item first, so the refusal is proven to be the WHOLE batch, not just the bad item.
        if (json.StartsWith('[') && json != "[]")
            json = """[ { "id": "ok", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } }, """ + json[1..];

        var items = Parse(json, out var error);

        Assert.Null(items);
        Assert.Contains(fault, error);
    }

    [Fact]
    public void ParseBatch_StringOverTheLimit_IsRefused()
    {
        var text = new string('x', DevReportItem.MaxString + 1);
        var json = $$"""[ { "id": "n1", "kind": "note", "text": "{{text}}", "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""";

        Assert.Null(Parse(json, out var error));
        Assert.Contains("the limit is 20000", error);
    }

    [Fact]
    public void ParseBatch_IdOver128Characters_RefusesTheWholeBatch()
    {
        var id = new string('i', DevReportItem.MaxItemId + 1);
        const string ok = """{ "id": "ok", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } }""";
        var json = $$"""[ {{ok}}, { "id": "{{id}}", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""";

        Assert.Null(Parse(json, out var error));
        Assert.Equal("item 2 is not a valid note or answer: id is 129 characters; the limit is 128.", error);
    }

    [Fact]
    public void ParseBatch_IdOfExactly128Characters_IsAccepted()
    {
        var id = new string('i', DevReportItem.MaxItemId);
        var json = $$"""[ { "id": "{{id}}", "kind": "note", "text": "t", "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""";

        Assert.NotNull(Parse(json, out _));
    }

    [Fact]
    public void ParseBatch_StringAtTheLimit_IsAccepted()
    {
        var text = new string('x', DevReportItem.MaxString);
        var json = $$"""[ { "id": "n1", "kind": "note", "text": "{{text}}", "anchor": { "type": "text", "selector": "s", "quote": "q" } } ]""";

        Assert.NotNull(Parse(json, out _));
    }

    [Fact]
    public void Anchor_ToJsonAndBack_KeepsOnlyTheLabelsThePageSent()
    {
        var anchor = new DevReportAnchor("svg-part", "#p", "Queue", null, null, "Queue box");

        var json = anchor.ToJson();

        Assert.Equal("""{"type":"svg-part","selector":"#p","quote":"Queue","label":"Queue box"}""", json);
        Assert.Equal(anchor, DevReportAnchor.FromJson(json));
    }
}
