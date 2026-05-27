using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class SessionFilterTests
{
    private static SessionInfo Make(string id, string color, string endpoint = "https://host.ts.net", string? name = null, bool onHold = false)
        => new() { SessionId = id, StatusColor = color, TailnetEndpoint = endpoint, Name = name, OnHold = onHold };

    [Theory]
    [InlineData("red", true)]
    [InlineData("RED", true)]
    [InlineData("green", false)]
    [InlineData("blue", false)]
    [InlineData("yellow", false)]
    [InlineData("unknown", false)]
    public void NeedsAttention_OnlyRedNeedsTheUser(string color, bool expected)
    {
        Assert.Equal(expected, SessionFilter.NeedsAttention(Make("a", color)));
    }

    [Fact]
    public void NeedsAttention_RedButNoEndpoint_IsFalse()
    {
        // Cannot talk to a session with no Director endpoint, so it is not actionable.
        Assert.False(SessionFilter.NeedsAttention(Make("a", "red", endpoint: "")));
    }

    [Fact]
    public void AttentionQueue_KeepsOnlyRed_InStableNameOrder()
    {
        var roster = new List<SessionInfo>
        {
            Make("1", "blue",  name: "working-one"),
            Make("2", "red",   name: "zeta"),
            Make("3", "green", name: "done"),
            Make("4", "red",   name: "alpha"),
        };

        var queue = SessionFilter.AttentionQueue(roster);

        Assert.Equal(2, queue.Count);
        Assert.Equal("alpha", queue[0].DisplayName);
        Assert.Equal("zeta", queue[1].DisplayName);
    }

    [Fact]
    public void AttentionQueue_Null_ReturnsEmpty()
    {
        Assert.Empty(SessionFilter.AttentionQueue(null!));
    }

    [Fact]
    public void NeedsAttention_HeldRed_ExcludedOnlyWhenExcludeHeld()
    {
        var held = Make("a", "red", onHold: true);
        // The all-sessions conductor (excludeHeld=false) still sees a held red session.
        Assert.True(SessionFilter.NeedsAttention(held, excludeHeld: false));
        // FIFO mode (excludeHeld=true) skips it - hold means "leave me out of the rotation".
        Assert.False(SessionFilter.NeedsAttention(held, excludeHeld: true));
    }

    [Fact]
    public void AttentionQueue_ExcludeHeld_DropsHeldRedSessions()
    {
        var roster = new List<SessionInfo>
        {
            Make("1", "red", name: "alpha"),
            Make("2", "red", name: "beta", onHold: true),
            Make("3", "red", name: "gamma"),
        };

        var all = SessionFilter.AttentionQueue(roster, excludeHeld: false);
        Assert.Equal(3, all.Count); // conductor unchanged: holds still appear

        var fifo = SessionFilter.AttentionQueue(roster, excludeHeld: true);
        Assert.Equal(2, fifo.Count);
        Assert.Equal("alpha", fifo[0].DisplayName);
        Assert.Equal("gamma", fifo[1].DisplayName);
    }
}
