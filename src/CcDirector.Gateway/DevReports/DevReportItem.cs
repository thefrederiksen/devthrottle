using System.Text.Json;

namespace CcDirector.Gateway.DevReports;

/// <summary>A note's anchor, exactly the contract's shape. <see cref="RowLabel"/>, <see cref="ColumnLabel"/> and
/// <see cref="Label"/> are null when the page did not send them.</summary>
internal sealed record DevReportAnchor(
    string Type, string Selector, string Quote, string? RowLabel, string? ColumnLabel, string? Label)
{
    public const string TableCell = "table-cell";
    public const string SvgPart = "svg-part";
    public const string Text = "text";
    public const string Element = "element";

    public static readonly IReadOnlyList<string> Types = [TableCell, SvgPart, Text, Element];

    /// <summary>The anchor as the contract's JSON object - the form it is stored and served in.</summary>
    public string ToJson()
    {
        var map = new Dictionary<string, string>
        {
            ["type"] = Type,
            ["selector"] = Selector,
            ["quote"] = Quote,
        };
        if (RowLabel is not null) map["rowLabel"] = RowLabel;
        if (ColumnLabel is not null) map["columnLabel"] = ColumnLabel;
        if (Label is not null) map["label"] = Label;
        return JsonSerializer.Serialize(map);
    }

    /// <summary>Reads an anchor stored by <see cref="ToJson"/>.</summary>
    public static DevReportAnchor FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        string? Opt(string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
        return new DevReportAnchor(
            e.GetProperty("type").GetString()!, e.GetProperty("selector").GetString()!, e.GetProperty("quote").GetString()!,
            Opt("rowLabel"), Opt("columnLabel"), Opt("label"));
    }
}

/// <summary>
/// One item the owner sent - a note or an answer - in the contract's shape
/// (<c>packages/client-core/src/devreports/CONTRACT.md</c> section 3). A note carries <see cref="Text"/> and
/// <see cref="Anchor"/>; an answer carries the question and option fields and <see cref="Comment"/>. Every
/// string is the owner's page's, verbatim.
/// </summary>
internal sealed record DevReportItem(
    string Id,
    string Kind,
    string Text,
    DevReportAnchor? Anchor,
    string QuestionId,
    string Question,
    string OptionValue,
    string OptionLabel,
    string Comment)
{
    public const string Note = "note";
    public const string Answer = "answer";

    /// <summary>The contract's limit on every string, in UTF-16 code units - what the script's
    /// <c>string.length</c> counts.</summary>
    public const int MaxString = 20000;

    /// <summary>The longest client item id, in UTF-16 code units. The id is part of a unique PostgreSQL B-tree index,
    /// whose entries are limited to about a third of a page (phase 2 review Medium 1); CONTRACT.md section 3.</summary>
    public const int MaxItemId = 128;

    /// <summary>The most items one send may carry.</summary>
    public const int MaxItemsPerSend = 500;

    /// <summary>
    /// Reads a send's <c>items</c> array the way the note-taking script validates it (<c>validItem</c>): the
    /// whole batch or nothing. Properties the contract does not name - <c>pending</c>, <c>statusLabel</c> on a
    /// queued item - are ignored. Returns null and a sentence naming the first fault when the batch is not valid.
    /// </summary>
    public static IReadOnlyList<DevReportItem>? ParseBatch(JsonElement items, out string error)
    {
        error = "";
        if (items.ValueKind != JsonValueKind.Array)
        {
            error = "items must be an array of notes and answers.";
            return null;
        }
        var count = items.GetArrayLength();
        if (count == 0)
        {
            error = "items is empty; send at least one note or answer.";
            return null;
        }
        if (count > MaxItemsPerSend)
        {
            error = $"a send carries at most {MaxItemsPerSend} items, and this one has {count}.";
            return null;
        }

        var parsed = new List<DevReportItem>(count);
        var index = 0;
        foreach (var element in items.EnumerateArray())
        {
            var item = Parse(element, out var fault);
            if (item is null)
            {
                error = $"item {index + 1} is not a valid note or answer: {fault}";
                return null;
            }
            parsed.Add(item);
            index++;
        }
        return parsed;
    }

    private static DevReportItem? Parse(JsonElement e, out string fault)
    {
        fault = "";
        if (e.ValueKind != JsonValueKind.Object) { fault = "it is not an object."; return null; }
        if (!Str(e, "id", out var id, ref fault) ) return null;
        if (id.Length == 0) { fault = "id is empty."; return null; }
        if (id.Length > MaxItemId) { fault = $"id is {id.Length} characters; the limit is {MaxItemId}."; return null; }
        if (!Str(e, "kind", out var kind, ref fault)) return null;

        if (kind == Note)
        {
            if (!Str(e, "text", out var text, ref fault)) return null;
            if (!e.TryGetProperty("anchor", out var a) || a.ValueKind != JsonValueKind.Object)
            {
                fault = "a note must carry an anchor object.";
                return null;
            }
            if (!Str(a, "type", out var type, ref fault)) return null;
            if (!DevReportAnchor.Types.Contains(type))
            {
                fault = $"anchor.type \"{type}\" is not one of: {string.Join(", ", DevReportAnchor.Types)}.";
                return null;
            }
            if (!Str(a, "selector", out var selector, ref fault)) return null;
            if (!Str(a, "quote", out var quote, ref fault)) return null;
            if (!OptStr(a, "rowLabel", out var rowLabel, ref fault)) return null;
            if (!OptStr(a, "columnLabel", out var columnLabel, ref fault)) return null;
            if (!OptStr(a, "label", out var label, ref fault)) return null;
            return new DevReportItem(id, Note, text,
                new DevReportAnchor(type, selector, quote, rowLabel, columnLabel, label), "", "", "", "", "");
        }

        if (kind == Answer)
        {
            if (!Str(e, "questionId", out var questionId, ref fault)) return null;
            if (questionId.Length == 0) { fault = "questionId is empty."; return null; }
            if (!Str(e, "question", out var question, ref fault)) return null;
            if (!Str(e, "optionValue", out var optionValue, ref fault)) return null;
            if (!Str(e, "optionLabel", out var optionLabel, ref fault)) return null;
            if (!Str(e, "comment", out var comment, ref fault)) return null;
            return new DevReportItem(id, Answer, "", null, questionId, question, optionValue, optionLabel, comment);
        }

        fault = $"kind \"{kind}\" is not note or answer.";
        return null;
    }

    private static bool Str(JsonElement e, string name, out string value, ref string fault)
    {
        value = "";
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            fault = $"{name} must be a string.";
            return false;
        }
        value = v.GetString()!;
        if (value.Length > MaxString)
        {
            fault = $"{name} is {value.Length} characters; the limit is {MaxString}.";
            return false;
        }
        return true;
    }

    private static bool OptStr(JsonElement e, string name, out string? value, ref string fault)
    {
        value = null;
        if (!e.TryGetProperty(name, out _)) return true;
        if (!Str(e, name, out var present, ref fault)) return false;
        value = present;
        return true;
    }
}
