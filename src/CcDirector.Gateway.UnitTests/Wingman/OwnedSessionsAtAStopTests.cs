using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// KNOW THE CHILDREN (the simpler session colours build, step 1; owner ruling, 2026-09-28). At a stop the Gateway works
/// out how many sessions are under the stopped one, at every level, and how many of them are still working - from the
/// SAME roster snapshot, and the same stamping pass, the held check answers from. One reading of ownership, not two.
///
/// Every session id below is written from scratch.
/// </summary>
public sealed class OwnedSessionsAtAStopTests
{
    private static SessionDto Row(string id, string activity, string? owner = null) => new()
    {
        SessionId = id,
        ActivityState = activity,
        IsControlled = owner is not null,
        ControllerSessionId = owner,
    };

    private static TurnVerdictSessionState Resolve(string sessionId, params SessionDto[] roster)
        => TurnVerdictHeldCheck.Resolve(roster.Select(s => ("dir-1", s)).ToList(), sessionId);

    [Fact]
    public void Resolve_ASessionOwningNothing_CarriesAZeroCount_NotNull()
    {
        var state = Resolve("alone", Row("alone", "WaitingForInput"), Row("other", "Working"));

        Assert.NotNull(state.Owned);
        Assert.Equal(0, state.Owned!.Total);
    }

    [Fact]
    public void Resolve_ASessionNotInTheRoster_HasNoOwnedAnswer()
    {
        var state = Resolve("gone", Row("other", "Working"));

        Assert.Null(state.Facts);
        Assert.Null(state.Owned);
    }

    [Fact]
    public void Resolve_CountsEveryLevel_AndWorkingIsTheBlueDefinition()
    {
        // A manager under the architect, two workers under the manager: one working, one stopped. An exited child
        // is under it too - it counts as stopped, never as working, although the crew line's bucket would call it so.
        var state = Resolve("architect",
            Row("architect", "WaitingForInput"),
            Row("manager", "WaitingForInput", owner: "architect"),
            Row("worker-a", "Working", owner: "manager"),
            Row("worker-b", "WaitingForInput", owner: "manager"),
            Row("finished", "Exited", owner: "architect"),
            Row("stranger", "Working"));

        Assert.Equal(new OwnedSessionCounts(Working: 1, Stopped: 3, NeedYou: 0), state.Owned);
        Assert.Equal(4, state.Owned!.Total);
    }

    [Fact]
    public void Resolve_EverythingUnderItStopped_HasNoWorkingChild()
    {
        var state = Resolve("lead",
            Row("lead", "WaitingForInput"),
            Row("w1", "WaitingForInput", owner: "lead"),
            Row("w2", "Exited", owner: "lead"));

        Assert.Equal(0, state.Owned!.Working);
        Assert.Equal(2, state.Owned.Total);
    }

    [Fact]
    public void Resolve_TheHeldAnswerAndTheChildrenComeFromOneStampedRoster()
    {
        // The manager is held by the live architect AND owns a working worker: both answers in one snapshot.
        var state = Resolve("manager",
            Row("architect", "WaitingForInput"),
            Row("manager", "WaitingForInput", owner: "architect"),
            Row("worker", "Starting", owner: "manager"));

        Assert.True(state.Held);
        Assert.Equal(1, state.Owned!.Working);
    }

    [Fact]
    public async Task TheStopPackage_CarriesTheOwnedSessionsOfTheLateSnapshot_NotTheFirst()
    {
        // The seat reads the roster once before the settle wait and again after it, immediately before the screen read.
        // The package must carry the LATE answer: the one whose held and facts answers licensed the read. Each read
        // here answers differently, so a package built from the first snapshot is caught.
        var reads = 0;
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => Screen("sid-owned", "  The sweep is written.", "", "> "),
            Judge = (_, _) => Task.FromResult("done"),
            Owned = _ => new OwnedSessionCounts(Working: Interlocked.Increment(ref reads), Stopped: 0, NeedYou: 0),
        };

        await new TurnVerdictService(env).StartTurnEnd(
            new TurnEndSignal("sid-owned", "dir-1", TenantId.Local, DateTime.UtcNow, IsNewTurn: true));

        // The late snapshot is the last roster read before the screen read.
        var steps = env.Steps.ToList();
        var lateRead = steps.Take(steps.IndexOf("screen")).Count(step => step == "state");
        Assert.True(lateRead >= 2, $"the seat read the roster {lateRead} time(s) before the screen; this test needs the late read");
        var trace = Assert.Single(env.Traces);
        Assert.Equal(new OwnedSessionCounts(Working: lateRead, Stopped: 0, NeedYou: 0), trace.Package!.OwnedSessions);
    }
}
