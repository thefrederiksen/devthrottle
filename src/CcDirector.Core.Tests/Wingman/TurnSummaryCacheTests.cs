using System.Text;
using CcDirector.Core.Memory;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Guards the core isolation guarantee: the Wingman builds a turn summary from a
/// session's OWN terminal buffer and nothing else. Two sessions on the same repo
/// each have their own <see cref="CircularTerminalBuffer"/>, so the captured
/// transcript can never contain another session's output - the cross-contamination
/// that the JSONL-reading path could produce is structurally impossible here.
/// </summary>
public sealed class TurnSummaryCacheTests
{
    private static CircularTerminalBuffer BufferWith(string text)
    {
        var buf = new CircularTerminalBuffer(64 * 1024);
        buf.Write(Encoding.UTF8.GetBytes(text));
        return buf;
    }

    [Fact]
    public void CaptureTurnTranscript_readsOnlyItsOwnBuffer()
    {
        using var bufA = BufferWith("session A edited fileA.cs and asked: ship it?");
        using var bufB = BufferWith("session B ran the tests in another repo");

        var (transcriptA, _) = TurnSummaryCache.CaptureTurnTranscript(bufA, 0);
        var (transcriptB, _) = TurnSummaryCache.CaptureTurnTranscript(bufB, 0);

        Assert.Contains("session A", transcriptA);
        Assert.DoesNotContain("session B", transcriptA);

        Assert.Contains("session B", transcriptB);
        Assert.DoesNotContain("session A", transcriptB);
    }

    [Fact]
    public void CaptureTurnTranscript_advancesCursor_soNextTurnSeesOnlyNewOutput()
    {
        using var buf = BufferWith("first turn output");

        var (first, cursor) = TurnSummaryCache.CaptureTurnTranscript(buf, 0);

        buf.Write(Encoding.UTF8.GetBytes("\nsecond turn output"));
        var (second, _) = TurnSummaryCache.CaptureTurnTranscript(buf, cursor);

        Assert.Contains("first turn output", first);
        Assert.DoesNotContain("second turn output", first);

        Assert.Contains("second turn output", second);
        Assert.DoesNotContain("first turn output", second);
    }

    [Fact]
    public void CaptureTurnTranscript_noNewBytes_returnsEmpty()
    {
        using var buf = BufferWith("only turn output");

        var (_, cursor) = TurnSummaryCache.CaptureTurnTranscript(buf, 0);
        var (second, _) = TurnSummaryCache.CaptureTurnTranscript(buf, cursor);

        Assert.True(string.IsNullOrEmpty(second));
    }

    [Fact]
    public void CaptureTurnTranscript_nullBuffer_returnsEmpty()
    {
        var (transcript, cursor) = TurnSummaryCache.CaptureTurnTranscript(null, 0);

        Assert.Equal(string.Empty, transcript);
        Assert.Equal(0, cursor);
    }
}
