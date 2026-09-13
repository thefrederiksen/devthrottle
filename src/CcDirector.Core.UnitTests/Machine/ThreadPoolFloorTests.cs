using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The thread-pool floor's sizing rule and its refusal reporting (issue #2818). The real pool is never
/// touched: it is passed in as two delegates, so these tests state the rule without changing the
/// process they run in.
/// </summary>
public class ThreadPoolFloorTests
{
    private static ThreadPoolFloor.GetMinThreads Pool(int workers, int completionPorts) =>
        (out int w, out int c) => { w = workers; c = completionPorts; };

    [Fact]
    public void RequiredWorkerThreads_CoversEverySessionsBlockingPair_PlusSpare()
    {
        // The sizing rule stated as arithmetic a reader can check: eight processors, two blocked
        // workers for each of thirty-two sessions, sixteen spare.
        Assert.Equal(8 + (2 * 32) + 16, ThreadPoolFloor.RequiredWorkerThreads(8));
    }

    [Fact]
    public void RequiredWorkerThreads_LeavesRoomBeyondTheMachineThatFailed()
    {
        // The Director in issue #2818 was running eighteen sessions, which pin thirty-six workers. The
        // floor must leave the tunnel's keep-alive somewhere to run after those are taken.
        const int sessionsOnTheFailingMachine = 18;
        var pinned = sessionsOnTheFailingMachine * ThreadPoolFloor.BlockingWorkersPerSession;

        var spare = ThreadPoolFloor.RequiredWorkerThreads(8) - pinned;

        Assert.True(spare >= ThreadPoolFloor.Headroom,
            $"only {spare} workers would be left after {sessionsOnTheFailingMachine} sessions pin {pinned}");
    }

    [Fact]
    public void RequiredCompletionPortThreads_IsNotSizedPerSession()
    {
        // Sessions block WORKERS, not completion-port threads, so this floor is raised for the tunnel's
        // own socket completions alone and must not carry the per-session multiple.
        Assert.Equal(8 + ThreadPoolFloor.Headroom, ThreadPoolFloor.RequiredCompletionPortThreads(8));
        Assert.True(ThreadPoolFloor.RequiredCompletionPortThreads(8) < ThreadPoolFloor.RequiredWorkerThreads(8));
    }

    [Fact]
    public void Apply_RaisesBothFloors()
    {
        int askedWorkers = 0, askedCompletionPorts = 0;

        var accepted = ThreadPoolFloor.Apply(8, Pool(8, 8), (w, c) =>
        {
            askedWorkers = w;
            askedCompletionPorts = c;
            return true;
        });

        Assert.True(accepted);
        Assert.Equal(ThreadPoolFloor.RequiredWorkerThreads(8), askedWorkers);
        Assert.Equal(ThreadPoolFloor.RequiredCompletionPortThreads(8), askedCompletionPorts);
    }

    [Fact]
    public void Apply_NeverLowersAFloorSomebodyElseAlreadyRaised()
    {
        // A mixed pool: the worker floor is already far above what this would ask for, the
        // completion-port floor is below it. Raising the one must not drag the other down - that is the
        // whole reason each is a Math.Max rather than an assignment.
        int askedWorkers = 0, askedCompletionPorts = 0;

        ThreadPoolFloor.Apply(8, Pool(workers: 500, completionPorts: 8), (w, c) =>
        {
            askedWorkers = w;
            askedCompletionPorts = c;
            return true;
        });

        Assert.Equal(500, askedWorkers);
        Assert.Equal(ThreadPoolFloor.RequiredCompletionPortThreads(8), askedCompletionPorts);
    }

    [Fact]
    public void Apply_AlreadyAboveTheFloor_DoesNotTouchThePool()
    {
        var setWasCalled = false;

        var accepted = ThreadPoolFloor.Apply(8, Pool(workers: 500, completionPorts: 400), (_, _) =>
        {
            setWasCalled = true;
            return true;
        });

        Assert.True(accepted);
        Assert.False(setWasCalled);
    }

    [Fact]
    public void Apply_RefusedByTheRuntime_ReportsFailureRatherThanClaimingSuccess()
    {
        // A refused adjustment leaves the fault in place, so it must not read as a success anywhere.
        var accepted = ThreadPoolFloor.Apply(8, Pool(8, 8), (_, _) => false);

        Assert.False(accepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(24)]
    [InlineData(128)]
    public void RequiredWorkerThreads_IsAlwaysAboveTheProcessorCountDefault(int processorCount)
    {
        Assert.True(ThreadPoolFloor.RequiredWorkerThreads(processorCount) > processorCount);
    }
}
