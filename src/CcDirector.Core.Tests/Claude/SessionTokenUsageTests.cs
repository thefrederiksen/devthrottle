using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

// =====================================================================================
// SessionTokenUsage - mechanical JSONL usage walk: totals, context size, turn grouping.
// Line shapes mirror real Claude Code transcripts (verified against a live session file).
// =====================================================================================
public sealed class SessionTokenUsageTests
{
    private static string UserLine(string text)
        => """{"type":"user","message":{"role":"user","content":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

    private static string MetaUserLine()
        => """{"type":"user","isMeta":true,"message":{"role":"user","content":"injected"}}""";

    private static string ToolResultLine()
        => """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    private static string AssistantLine(long input, long output, long cacheRead, long cacheCreate, string ts = "2026-06-05T03:30:22.547Z")
        => $"{{\"type\":\"assistant\",\"timestamp\":\"{ts}\",\"message\":{{\"role\":\"assistant\",\"usage\":{{" +
           $"\"input_tokens\":{input},\"output_tokens\":{output}," +
           $"\"cache_read_input_tokens\":{cacheRead},\"cache_creation_input_tokens\":{cacheCreate}}}}}}}";

    [Fact]
    public void Compute_SumsTotals_AndContextIsLatestLineInput()
    {
        var lines = new[]
        {
            UserLine("build it"),
            AssistantLine(input: 100, output: 50, cacheRead: 1000, cacheCreate: 200),
            AssistantLine(input: 10, output: 40, cacheRead: 1500, cacheCreate: 30),
        };

        var u = SessionTokenUsage.Compute(lines, "sid");

        Assert.Equal(110, u.InputTokens);
        Assert.Equal(90, u.OutputTokens);
        Assert.Equal(2500, u.CacheReadTokens);
        Assert.Equal(230, u.CacheCreationTokens);
        Assert.Equal(10 + 1500 + 30, u.ContextTokens); // the LATEST line only
        Assert.Equal(2, u.AssistantMessageCount);
        Assert.NotNull(u.LastMessageUtc);
    }

    [Fact]
    public void Compute_GroupsByRealUserPrompts_ToolResultsAndMetaDoNotStartTurns()
    {
        var lines = new[]
        {
            UserLine("turn one"),
            AssistantLine(10, 20, 0, 0, ts: "2026-06-05T01:00:00Z"),
            ToolResultLine(),                                   // same turn
            AssistantLine(5, 30, 0, 7, ts: "2026-06-05T01:01:00Z"),
            MetaUserLine(),                                     // same turn
            UserLine("turn two"),
            AssistantLine(3, 40, 0, 0, ts: "2026-06-05T01:02:00Z"),
        };

        var u = SessionTokenUsage.Compute(lines, "sid");

        Assert.Equal(2, u.Turns.Count);
        Assert.Equal(1, u.Turns[0].Index);
        Assert.Equal(50, u.Turns[0].OutputTokens);
        Assert.Equal(20 + 10 + 30 + 5 + 7, u.Turns[0].NewTokens); // output + input + cacheCreate
        Assert.Equal(new DateTime(2026, 6, 5, 1, 1, 0, DateTimeKind.Utc), u.Turns[0].EndedAtUtc);
        Assert.Equal(40, u.Turns[1].OutputTokens);
    }

    [Fact]
    public void Compute_AssistantBeforeFirstPrompt_CountsInTotalsNotTurns()
    {
        var lines = new[]
        {
            AssistantLine(10, 20, 0, 0),
            UserLine("first real prompt"),
            AssistantLine(5, 30, 0, 0),
        };

        var u = SessionTokenUsage.Compute(lines, "sid");

        Assert.Equal(50, u.OutputTokens);
        Assert.Single(u.Turns);
        Assert.Equal(30, u.Turns[0].OutputTokens);
    }

    [Fact]
    public void Compute_TolerantOfGarbageAndTornLines()
    {
        var lines = new[]
        {
            "not json at all",
            """{"type":"assistant","message":{"role":"assistant"}}""",   // no usage block
            """{"type":"assistant","message":{"role":"assist""",        // torn tail line
            UserLine("go"),
            AssistantLine(1, 2, 3, 4),
        };

        var u = SessionTokenUsage.Compute(lines, "sid");

        Assert.Equal(2, u.OutputTokens);
        Assert.Equal(1, u.AssistantMessageCount);
        Assert.Single(u.Turns);
    }

    [Fact]
    public void Compute_PromptOnlyTurn_Dropped()
    {
        var lines = new[]
        {
            UserLine("turn one"),
            AssistantLine(1, 2, 0, 0),
            UserLine("just submitted, no reply yet"),
        };

        var u = SessionTokenUsage.Compute(lines, "sid");
        Assert.Single(u.Turns);
    }

    [Fact]
    public void Compute_CapsReturnedTurns_TotalsStillComplete()
    {
        var lines = new List<string>();
        for (var i = 0; i < SessionTokenUsage.MaxTurnsReturned + 10; i++)
        {
            lines.Add(UserLine($"turn {i}"));
            lines.Add(AssistantLine(1, 10, 0, 0));
        }

        var u = SessionTokenUsage.Compute(lines, "sid");

        Assert.Equal(SessionTokenUsage.MaxTurnsReturned, u.Turns.Count);
        Assert.Equal(10L * (SessionTokenUsage.MaxTurnsReturned + 10), u.OutputTokens);
        // The kept entries are the NEWEST ones.
        Assert.Equal(SessionTokenUsage.MaxTurnsReturned + 10, u.Turns[^1].Index);
    }

    [Fact]
    public void ComputeFromFile_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() =>
            SessionTokenUsage.ComputeFromFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".jsonl"), "sid"));
    }
}
