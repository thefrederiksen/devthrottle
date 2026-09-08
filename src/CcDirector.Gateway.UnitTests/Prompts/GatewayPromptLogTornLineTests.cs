using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Prompts;
using Xunit;

namespace CcDirector.Gateway.Tests.Prompts;

/// <summary>
/// The torn-line recovery of <see cref="GatewayPromptLog"/> (devthrottle #2635, the Architect's ruling of
/// 2026-09-01, carried to the Gateway by ruling R13 of the Mentor on the Gateway mission).
///
/// THE DEFECT. The share swallows a partial append and glues the next record onto it, so one physical line
/// holds a dead head and a whole record. The reader used to SKIP such a line, which silently dropped the
/// glued-on record - fifteen of them on the owner's 2026-W36 alone. The reference reader
/// (tools/mentor/metrics.py read_prompt_log_file in the internal repository) splits the line at the LAST
/// occurrence of the text {"ts":" and parses the tail: the tail is a recovered record, the dead head is one
/// lost record, and a tail that still does not parse is a second lost record. A marker at position zero is no
/// split (the whole line IS the unparseable record): one lost record, nothing recovered.
///
/// Watched red before the fix: the first test returned ONE record where the reference returns TWO.
/// </summary>
public sealed class GatewayPromptLogTornLineTests : IDisposable
{
    private readonly string _dir;
    private readonly GatewayPromptLog _log;
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    public GatewayPromptLogTornLineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gw-promptlog-torn-" + Guid.NewGuid().ToString("N"));
        _log = new GatewayPromptLog(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PromptRecord Rec(int minute, string text) => new()
    {
        TsUtc = Day.AddMinutes(minute),
        Machine = "SOREN_NORTH",
        SessionId = "session-1",
        ContextId = "ctx-1",
        Agent = "ClaudeCode",
        Role = "user",
        Modality = "typed",
        Surface = "desktop",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        Text = text,
    };

    private static string Line(PromptRecord record) =>
        JsonSerializer.Serialize(record, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    /// <summary>Write the tenant's daily file for <see cref="Day"/> with exactly these physical lines.</summary>
    private void WriteDay(params string[] lines)
    {
        var path = _log.FileFor(TenantId.Local, Day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    [Fact]
    public void A_torn_line_gives_back_the_record_glued_onto_its_tail()
    {
        var first = Line(Rec(1, "first prompt"));
        var whole = Line(Rec(2, "second prompt"));
        var torn = whole.Substring(0, whole.Length / 2) + whole;   // a dead head glued to a whole record
        WriteDay(first, torn);

        var records = _log.Read(TenantId.Local, Day, Day);

        Assert.Equal(new[] { "first prompt", "second prompt" }, records.Select(r => r.Text));
    }

    [Fact]
    public void ReadDetailed_names_the_torn_line_and_counts_recovered_and_lost()
    {
        var first = Line(Rec(1, "first prompt"));
        var whole = Line(Rec(2, "second prompt"));
        var torn = whole.Substring(0, whole.Length / 2) + whole;
        WriteDay(first, "", torn);                                  // the blank line still counts as line 2

        var result = _log.ReadDetailed(TenantId.Local, Day, Day);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(("conversation-20260901.jsonl", 1), (result.Records[0].FileName, result.Records[0].LineNumber));
        Assert.Equal(("conversation-20260901.jsonl", 3), (result.Records[1].FileName, result.Records[1].LineNumber));
        var only = Assert.Single(result.TornLines);
        Assert.Equal(("conversation-20260901.jsonl", 3), (only.FileName, only.LineNumber));
        Assert.Equal(1, result.Recovered);
        Assert.Equal(1, result.Lost);
    }

    [Fact]
    public void A_tail_that_does_not_parse_is_a_second_lost_record()
    {
        var whole = Line(Rec(2, "second prompt"));
        var torn = whole.Substring(0, whole.Length / 2) + whole.Substring(0, whole.Length - 5);
        WriteDay(torn);

        var result = _log.ReadDetailed(TenantId.Local, Day, Day);

        Assert.Empty(result.Records);
        Assert.Single(result.TornLines);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(2, result.Lost);
    }

    [Fact]
    public void A_marker_at_position_zero_is_no_split()
    {
        // The line starts with the marker and is unparseable: there is no earlier head to cut off, so the
        // reference does not split (cut > 0 is required) - one lost record, nothing recovered.
        var whole = Line(Rec(2, "second prompt"));
        WriteDay(whole.Substring(0, whole.Length - 5));

        var result = _log.ReadDetailed(TenantId.Local, Day, Day);

        Assert.Empty(result.Records);
        Assert.Single(result.TornLines);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(1, result.Lost);
    }

    [Fact]
    public void A_clean_file_has_no_torn_lines()
    {
        WriteDay(Line(Rec(1, "first prompt")), Line(Rec(2, "second prompt")));

        var result = _log.ReadDetailed(TenantId.Local, Day, Day);

        Assert.Equal(2, result.Records.Count);
        Assert.Empty(result.TornLines);
        Assert.Equal((0, 0), (result.Recovered, result.Lost));
        Assert.Equal(2, _log.ReadAll(TenantId.Local).Count);
    }
}
