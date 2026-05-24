using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class SessionFilterTests
{
    private static SessionInfo Make(string id, string color, string endpoint = "https://host.ts.net", string? name = null)
        => new() { SessionId = id, StatusColor = color, TailnetEndpoint = endpoint, Name = name };

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
}
