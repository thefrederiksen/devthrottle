using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// The transcript is read once and then only what was appended. Every case compares the
/// incremental answer against a fresh whole-file computation, so "reads less" never means
/// "answers differently". Before 22 September 2026 every call read the whole file; the bytes-read
/// figure these tests assert on did not exist, because there was nothing but "all of it".
/// </summary>
public sealed class SessionTokenUsageIncrementalTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public SessionTokenUsageIncrementalTests()
    {
        SessionTokenUsage.ForgetAllCursors();
        _root = TestTempRoot.For("ccd-usage-cursor-");
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "session.jsonl");
    }

    public void Dispose()
    {
        SessionTokenUsage.ForgetAllCursors();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string UserLine(string text)
        => """{"type":"user","message":{"role":"user","content":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

    private static string AssistantLine(long input, long output, long cacheRead, long cacheCreate, string ts = "2026-06-05T03:30:22.547Z")
        => $"{{\"type\":\"assistant\",\"timestamp\":\"{ts}\",\"message\":{{\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"usage\":{{" +
           $"\"input_tokens\":{input},\"output_tokens\":{output}," +
           $"\"cache_read_input_tokens\":{cacheRead},\"cache_creation_input_tokens\":{cacheCreate}}}}}}}";

    private void Append(params string[] lines) => File.AppendAllText(_path, string.Concat(lines.Select(l => l + "\n")));

    private static void AssertSame(CcDirector.Gateway.Contracts.SessionUsageDto expected, CcDirector.Gateway.Contracts.SessionUsageDto actual)
    {
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.CacheReadTokens, actual.CacheReadTokens);
        Assert.Equal(expected.CacheCreationTokens, actual.CacheCreationTokens);
        Assert.Equal(expected.ContextTokens, actual.ContextTokens);
        Assert.Equal(expected.ContextModel, actual.ContextModel);
        Assert.Equal(expected.AssistantMessageCount, actual.AssistantMessageCount);
        Assert.Equal(expected.LastMessageUtc, actual.LastMessageUtc);
        Assert.Equal(expected.Turns.Select(t => (t.Index, t.NewTokens, t.OutputTokens, t.EndedAtUtc)),
                     actual.Turns.Select(t => (t.Index, t.NewTokens, t.OutputTokens, t.EndedAtUtc)));
    }

    private CcDirector.Gateway.Contracts.SessionUsageDto WholeFile() => SessionTokenUsage.Compute(File.ReadAllLines(_path), "sid");

    [Fact]
    public void ASecondAskWithNothingAppended_ReadsNothing_AndAnswersTheSame()
    {
        Append(UserLine("go"), AssistantLine(100, 20, 0, 50), AssistantLine(120, 30, 100, 0));

        var first = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        var second = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");

        Assert.Equal(new FileInfo(_path).Length, first.BytesRead);
        Assert.Equal(0, second.BytesRead);
        AssertSame(WholeFile(), second.Usage);
    }

    [Fact]
    public void AnAppendedTurn_ReadsOnlyTheAppendedBytes_AndAnswersAsAWholeFileReadWould()
    {
        Append(UserLine("one"), AssistantLine(100, 20, 0, 50));
        SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        var before = new FileInfo(_path).Length;

        Append(UserLine("two"), AssistantLine(200, 40, 150, 10, ts: "2026-06-05T03:31:00Z"));
        var result = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");

        Assert.Equal(new FileInfo(_path).Length - before, result.BytesRead);
        AssertSame(WholeFile(), result.Usage);
        Assert.Equal(2, result.Usage.Turns.Count);
        Assert.Equal(360, result.Usage.ContextTokens);
    }

    [Fact]
    public void ATornTailLine_IsNotCounted_UntilItIsComplete_AndThenExactlyOnce()
    {
        Append(UserLine("one"), AssistantLine(100, 20, 0, 50));
        SessionTokenUsage.ComputeFromFileTracked(_path, "sid");

        var whole = AssistantLine(200, 40, 150, 10, ts: "2026-06-05T03:31:00Z");
        File.AppendAllText(_path, whole[..(whole.Length / 2)]); // the writer is mid-line
        var torn = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        Assert.Equal(0, torn.BytesRead);
        Assert.Equal(1, torn.Usage.AssistantMessageCount);

        File.AppendAllText(_path, whole[(whole.Length / 2)..] + "\n");
        var completed = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        Assert.Equal(2, completed.Usage.AssistantMessageCount);
        AssertSame(WholeFile(), completed.Usage);

        // And nothing is counted twice on the ask after that.
        var again = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        Assert.Equal(0, again.BytesRead);
        Assert.Equal(2, again.Usage.AssistantMessageCount);
    }

    [Fact]
    public void ACompleteLastLineWithoutATrailingNewline_IsCountedNow_AndNotAgainWhenTheNewlineArrives()
    {
        Append(UserLine("one"));
        File.AppendAllText(_path, AssistantLine(100, 20, 0, 50)); // no newline yet
        var now = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        Assert.Equal(1, now.Usage.AssistantMessageCount);

        File.AppendAllText(_path, "\n");
        var later = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");
        Assert.Equal(1, later.Usage.AssistantMessageCount);
        AssertSame(WholeFile(), later.Usage);
    }

    [Fact]
    public void AFileThatShrank_IsReadFromTheStart()
    {
        Append(UserLine("one"), AssistantLine(100, 20, 0, 50), UserLine("two"), AssistantLine(200, 40, 150, 10));
        SessionTokenUsage.ComputeFromFileTracked(_path, "sid");

        File.WriteAllText(_path, UserLine("fresh") + "\n" + AssistantLine(7, 3, 0, 0) + "\n");
        var result = SessionTokenUsage.ComputeFromFileTracked(_path, "sid");

        Assert.Equal(new FileInfo(_path).Length, result.BytesRead);
        AssertSame(WholeFile(), result.Usage);
        Assert.Equal(7, result.Usage.ContextTokens);
    }

    [Fact]
    public void ADifferentSessionIdAtTheSamePath_IsReadFromTheStart()
    {
        Append(UserLine("one"), AssistantLine(100, 20, 0, 50));
        SessionTokenUsage.ComputeFromFileTracked(_path, "sid-a");

        var result = SessionTokenUsage.ComputeFromFileTracked(_path, "sid-b");

        Assert.Equal(new FileInfo(_path).Length, result.BytesRead);
        Assert.Equal("sid-b", result.Usage.SessionId);
    }

    [Fact]
    public void ASnapshot_DoesNotChangeUnderTheCaller_WhenMoreIsAppended()
    {
        Append(UserLine("one"), AssistantLine(100, 20, 0, 50));
        var held = SessionTokenUsage.ComputeFromFile(_path, "sid");

        Append(AssistantLine(300, 60, 0, 0));
        SessionTokenUsage.ComputeFromFile(_path, "sid");

        Assert.Equal(1, held.AssistantMessageCount);
        Assert.Equal(20, held.Turns.Single().OutputTokens);
    }

    [Fact]
    public void MoreTranscriptsThanCursors_DropsTheLeastRecentlyAsked_AndStillAnswersRight()
    {
        var paths = Enumerable.Range(0, SessionTokenUsage.MaxCursors + 3)
            .Select(i => Path.Combine(_root, $"t{i}.jsonl")).ToList();
        foreach (var p in paths)
            File.WriteAllText(p, UserLine("go") + "\n" + AssistantLine(10, 5, 0, 0) + "\n");
        foreach (var p in paths)
            SessionTokenUsage.ComputeFromFileTracked(p, "sid");

        // The first transcript was evicted, so it is read whole again - and correctly.
        var first = SessionTokenUsage.ComputeFromFileTracked(paths[0], "sid");
        Assert.Equal(new FileInfo(paths[0]).Length, first.BytesRead);
        Assert.Equal(10, first.Usage.ContextTokens);
    }
}
