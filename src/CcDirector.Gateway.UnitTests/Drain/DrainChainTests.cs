using CcDirector.ControlApi.Drain;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The reporting chain: who gets the drain message, and what may be closed when.
///
/// This is the behaviour that turned seventeen sessions into seven messages in the first real drain, and
/// the behaviour that stops an Architect being closed while two of its Workers are still writing.
/// </summary>
public class DrainChainTests
{
    private static WorkspaceSeat Seat(string id, string name, string? reportsTo = null, string? parent = null, int order = 0)
        => new()
        {
            SessionId = id,
            Name = name,
            Agent = "ClaudeCode",
            RepoPath = "D:/repo",
            ReportsTo = reportsTo,
            ParentSessionId = parent,
            SortOrder = order,
        };

    [Fact]
    public void Build_ControllerNamesTheSenior_SeatIsNotAHead()
    {
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect"),
            Seat("m", "Manager", reportsTo: "a"),
        });

        Assert.Equal(new[] { "a" }, chain.Heads);
        Assert.Equal("a", chain.Node("m")!.ReportsTo);
        Assert.Equal(1, chain.Node("m")!.Depth);
    }

    [Fact]
    public void Build_NoController_FallsBackToTheSpawningParent()
    {
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect"),
            Seat("w", "Worker", parent: "a"),
        });

        Assert.Equal(new[] { "a" }, chain.Heads);
        Assert.Equal("a", chain.Node("w")!.ReportsTo);
    }

    [Fact]
    public void Build_ControllerWins_WhenBothAreSetAndDisagree()
    {
        // Parentage is history; control is the live relationship. A Worker spawned by an Architect but
        // handed to a Manager reports to the Manager, and the drain message must go to the Manager's head.
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect"),
            Seat("m", "Manager", reportsTo: "a"),
            Seat("w", "Worker", reportsTo: "m", parent: "a"),
        });

        Assert.Equal("m", chain.Node("w")!.ReportsTo);
        Assert.Equal(2, chain.Node("w")!.Depth);
    }

    [Fact]
    public void Build_SeniorOnAnotherDirector_IsNotASenior_SoTheSeatIsAHead()
    {
        // The seat's controller lives somewhere this drain cannot reach. It has nobody HERE to report
        // through, so it must be messaged itself - the difference between a seat that is covered and a
        // seat that is silently never asked for anything.
        var chain = DrainChain.Build(new[]
        {
            Seat("w", "Worker", reportsTo: "somebody-on-another-director"),
        });

        Assert.Equal(new[] { "w" }, chain.Heads);
        Assert.Null(chain.Node("w")!.ReportsTo);
    }

    [Fact]
    public void Build_SeatReportingToItself_IsAHead_AndIsNotACycle()
    {
        var chain = DrainChain.Build(new[] { Seat("a", "A", reportsTo: "a") });

        Assert.Equal(new[] { "a" }, chain.Heads);
        Assert.Empty(chain.SeatsInCycles);
    }

    [Fact]
    public void Build_CycleInTheChain_IsBrokenAndNamed()
    {
        // A loop would make the close walk never terminate and would hide the whole ring from the message
        // step. It is broken so the drain still reaches them, and NAMED so somebody sees the fault.
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "A", reportsTo: "b"),
            Seat("b", "B", reportsTo: "a"),
        });

        Assert.NotEmpty(chain.SeatsInCycles);
        Assert.NotEmpty(chain.Heads);
        Assert.Equal(2, chain.Nodes.Count);
    }

    [Fact]
    public void CloseOrder_IsDeepestFirst()
    {
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect", order: 0),
            Seat("m", "Manager", reportsTo: "a", order: 1),
            Seat("w", "Worker", reportsTo: "m", order: 2),
        });

        Assert.Equal(new[] { "w", "m", "a" }, chain.CloseOrder);
    }

    [Fact]
    public void CanClose_RefusesASeniorWhileASubordinateIsStillOpen()
    {
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect"),
            Seat("w1", "Worker 1", reportsTo: "a"),
            Seat("w2", "Worker 2", reportsTo: "a"),
        });

        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "w1" };
        Assert.False(chain.CanClose("a", closed));

        closed.Add("w2");
        Assert.True(chain.CanClose("a", closed));
    }

    [Fact]
    public void CanClose_IsTrueForALeaf_WithNothingClosed()
    {
        var chain = DrainChain.Build(new[] { Seat("w", "Worker") });
        Assert.True(chain.CanClose("w", new HashSet<string>()));
    }

    [Fact]
    public void Descendants_ReachesEveryDepth()
    {
        var chain = DrainChain.Build(new[]
        {
            Seat("a", "Architect"),
            Seat("m", "Manager", reportsTo: "a"),
            Seat("w", "Worker", reportsTo: "m"),
        });

        Assert.Equal(new[] { "m", "w" }, chain.Descendants("a").OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "w" }, chain.Descendants("m"));
        Assert.Empty(chain.Descendants("w"));
    }

    [Fact]
    public void Build_SeatWithNoSessionId_IsLeftOutEntirely()
    {
        // An authored seat has never been a session: it cannot be messaged, reported to, or closed, and
        // giving it an empty key would collide every such seat onto one node.
        var chain = DrainChain.Build(new[]
        {
            new WorkspaceSeat { Name = "authored", Agent = "ClaudeCode", RepoPath = "D:/repo" },
            Seat("a", "Architect"),
        });

        Assert.Single(chain.Nodes);
        Assert.Equal(new[] { "a" }, chain.Heads);
    }

    [Fact]
    public void Build_TheHandWrittenShape_ProducesTheSameHeadsAndKeepsEveryone()
    {
        // The shape of the first real drain: two missions with their own trees, plus standalones. What is
        // asserted is the PROPERTY the run depended on - one message per head, and every seat reachable
        // from some head - rather than a number that would say nothing if the shape changed.
        var seats = new[]
        {
            Seat("arch1", "Linux Support - Architect"),
            Seat("mgr1", "Linux Support - Manager", reportsTo: "arch1"),
            Seat("w1", "Linux Support - VM Worker", reportsTo: "mgr1"),
            Seat("w2", "Linux Support - Packaging Worker", reportsTo: "mgr1"),
            Seat("arch2", "New Studio Cube - Architect 2"),
            Seat("mgr2", "New Studio Cube - Manager R-D", reportsTo: "arch2"),
            Seat("insp", "New Studio Cube - Inspector R-C", reportsTo: "arch2"),
            Seat("rd_a", "Cube R-D Worker A", reportsTo: "mgr2"),
            Seat("rd_b", "Cube R-D Worker B", reportsTo: "mgr2"),
            Seat("solo1", "Email Spine - Architect"),
            Seat("solo2", "Motivation videos"),
        };

        var chain = DrainChain.Build(seats);

        Assert.Equal(new[] { "arch1", "arch2", "solo1", "solo2" }, chain.Heads);

        var reached = new HashSet<string>(chain.Heads, StringComparer.OrdinalIgnoreCase);
        foreach (var head in chain.Heads)
            foreach (var d in chain.Descendants(head))
                reached.Add(d);
        Assert.Equal(seats.Length, reached.Count);
    }
}
