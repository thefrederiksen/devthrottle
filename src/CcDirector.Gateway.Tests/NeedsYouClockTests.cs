using System;
using System.Threading;
using CcDirector.Gateway.Briefing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="NeedsYouClock"/> - the Gateway-owned per-session clock (issue #218)
/// that records when a session entered the red / NEEDS-YOU state: set on first red, held while
/// red, cleared when it leaves red, re-stamped strictly later on a re-entry.
/// </summary>
public sealed class NeedsYouClockTests
{
    [Fact]
    public void Stamp_NotRed_ReturnsNull()
    {
        var clock = new NeedsYouClock();

        var result = clock.Stamp("s1", isRed: false);

        Assert.Null(result);
    }

    [Fact]
    public void Stamp_FirstRed_ReturnsAStampNearNow()
    {
        var clock = new NeedsYouClock();
        var before = DateTime.UtcNow;

        var result = clock.Stamp("s1", isRed: true);

        Assert.NotNull(result);
        var after = DateTime.UtcNow;
        Assert.InRange(result.Value, before, after);
    }

    [Fact]
    public void Stamp_StaysRedAcrossPolls_HoldsSameValue()
    {
        var clock = new NeedsYouClock();

        var first = clock.Stamp("s1", isRed: true);
        Thread.Sleep(20); // a later poll cycle
        var second = clock.Stamp("s1", isRed: true);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The value must not advance while the session stays red (AC: byte-identical).
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void Stamp_LeavesRed_ClearsToNull()
    {
        var clock = new NeedsYouClock();
        clock.Stamp("s1", isRed: true);

        var afterLeaving = clock.Stamp("s1", isRed: false);

        Assert.Null(afterLeaving);
    }

    [Fact]
    public void Stamp_ReEntersRed_SecondStampIsStrictlyLater()
    {
        var clock = new NeedsYouClock();

        var first = clock.Stamp("s1", isRed: true);
        clock.Stamp("s1", isRed: false); // leaves red - episode ends, value goes null
        Thread.Sleep(20);
        var second = clock.Stamp("s1", isRed: true); // returns to red - new episode

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.Value > first.Value,
            $"re-entry stamp {second.Value:o} should be strictly later than first {first.Value:o}");
    }

    [Fact]
    public void Stamp_TracksSessionsIndependently()
    {
        var clock = new NeedsYouClock();

        var a = clock.Stamp("a", isRed: true);
        Thread.Sleep(20);
        var b = clock.Stamp("b", isRed: true);
        // re-poll a while still red: it keeps its (earlier) value, independent of b.
        var aAgain = clock.Stamp("a", isRed: true);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(aAgain);
        Assert.Equal(a.Value, aAgain.Value);
        Assert.True(b.Value > a.Value);
    }

    [Fact]
    public void Stamp_EmptySessionId_Throws()
    {
        var clock = new NeedsYouClock();

        Assert.Throws<ArgumentException>(() => clock.Stamp("", isRed: true));
    }
}
