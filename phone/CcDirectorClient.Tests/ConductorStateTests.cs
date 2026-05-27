using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class ConductorStateTests
{
    private static SessionInfo Red(string id, string name)
        => new() { SessionId = id, Name = name, StatusColor = "red", TailnetEndpoint = "https://host.ts.net" };

    private static SessionInfo Green(string id, string name)
        => new() { SessionId = id, Name = name, StatusColor = "green", TailnetEndpoint = "https://host.ts.net" };

    [Fact]
    public void Update_EmptyRoster_NoWork()
    {
        var c = new ConductorState();
        Assert.Null(c.Update(new List<SessionInfo>()));
        Assert.False(c.HasWork);
        Assert.Equal(0, c.Count);
        Assert.Null(c.Current);
    }

    [Fact]
    public void Update_OnlyRedSessionsEnterTheQueue()
    {
        var c = new ConductorState();
        c.Update(new List<SessionInfo> { Red("1", "alpha"), Green("2", "beta"), Red("3", "gamma") });

        Assert.Equal(2, c.Count);
        Assert.Equal("alpha", c.Current!.DisplayName);
    }

    [Fact]
    public void Advance_WrapsRoundRobin_OnlyOnExplicitCall()
    {
        var c = new ConductorState();
        c.Update(new List<SessionInfo> { Red("1", "alpha"), Red("2", "beta") });

        Assert.Equal("alpha", c.Current!.DisplayName);
        Assert.Equal("beta", c.Advance()!.DisplayName);
        Assert.Equal("alpha", c.Advance()!.DisplayName); // wraps
    }

    [Fact]
    public void Update_KeepsCursorOnSameSessionAcrossRefresh()
    {
        var c = new ConductorState();
        c.Update(new List<SessionInfo> { Red("1", "alpha"), Red("2", "beta") });
        c.Advance(); // now on beta
        Assert.Equal("beta", c.Current!.DisplayName);

        // A refresh that reorders the roster must not yank the user off beta.
        c.Update(new List<SessionInfo> { Red("3", "newcomer"), Red("2", "beta"), Red("1", "alpha") });
        Assert.Equal("beta", c.Current!.DisplayName);
    }

    [Fact]
    public void Update_WhenCurrentResolved_ClampsToValidEntry()
    {
        var c = new ConductorState();
        c.Update(new List<SessionInfo> { Red("1", "alpha"), Red("2", "beta") });
        c.Advance(); // on beta (index 1)

        // beta resolved (no longer red); only alpha remains. Cursor must point at a real entry.
        c.Update(new List<SessionInfo> { Red("1", "alpha"), Green("2", "beta") });
        Assert.Equal(1, c.Count);
        Assert.Equal("alpha", c.Current!.DisplayName);
    }

    [Fact]
    public void Advance_EmptyQueue_ReturnsNull()
    {
        var c = new ConductorState();
        Assert.Null(c.Advance());
    }

    [Fact]
    public void ExcludeHeld_DropsHeldSessionsFromTheFifoQueue()
    {
        var held = new SessionInfo
        {
            SessionId = "2", Name = "beta", StatusColor = "red",
            TailnetEndpoint = "https://host.ts.net", OnHold = true,
        };

        // FIFO mode: the held red session never enters the queue.
        var fifo = new ConductorState(excludeHeld: true);
        fifo.Update(new List<SessionInfo> { Red("1", "alpha"), held, Red("3", "gamma") });
        Assert.Equal(2, fifo.Count);
        Assert.DoesNotContain(fifo.Queue, s => s.SessionId == "2");

        // The default conductor is unchanged: it still rotates through the held session.
        var conductor = new ConductorState();
        conductor.Update(new List<SessionInfo> { Red("1", "alpha"), held, Red("3", "gamma") });
        Assert.Equal(3, conductor.Count);
    }
}
