using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

/// <summary>
/// In-process election tests. Two MutexLeaderElection instances on different
/// background threads (within the same test process) contend for the same
/// named mutex; Windows kernel mutex ownership is per-thread, so only one
/// instance's election thread can win at a time. This mirrors the two-process
/// behaviour we need without spawning subprocesses.
///
/// Uses Local\ rather than Global\ to keep the test isolated from a real
/// running Director on the same machine.
/// </summary>
public class MutexLeaderElectionTests
{
    private static string UniqueName() =>
        @"Local\cc-director-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SingleInstance_BecomesLeader()
    {
        var name = UniqueName();
        using var election = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));

        election.Start();

        Assert.True(WaitFor(() => election.IsLeader, TimeSpan.FromSeconds(2)),
            "Lone election should become leader within 2s");
    }

    [Fact]
    public void TwoInstances_OnlyOneIsLeader()
    {
        var name = UniqueName();
        using var first = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));
        using var second = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));

        first.Start();
        Assert.True(WaitFor(() => first.IsLeader, TimeSpan.FromSeconds(2)),
            "First election should become leader");

        second.Start();
        // Give the second election a chance to poll and confirm follower state.
        Thread.Sleep(300);

        Assert.True(first.IsLeader);
        Assert.False(second.IsLeader);
    }

    [Fact]
    public void Follower_PromotesAfterLeaderStops()
    {
        var name = UniqueName();
        using var first = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));
        using var second = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));

        first.Start();
        Assert.True(WaitFor(() => first.IsLeader, TimeSpan.FromSeconds(2)));

        second.Start();
        Thread.Sleep(300);
        Assert.False(second.IsLeader);

        first.Stop();

        Assert.True(WaitFor(() => second.IsLeader, TimeSpan.FromSeconds(3)),
            "Follower should promote to leader within 3s of previous leader stopping");
    }

    [Fact]
    public void LeadershipChanged_FiresOnAcquisitionAndRelease()
    {
        var name = UniqueName();
        using var election = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));

        var changes = new List<bool>();
        var gate = new object();
        election.LeadershipChanged += b => { lock (gate) changes.Add(b); };

        election.Start();
        WaitFor(() => election.IsLeader, TimeSpan.FromSeconds(2));
        election.Stop();

        lock (gate)
        {
            Assert.Equal(new[] { true, false }, changes);
        }
    }

    [Fact]
    public void Stop_OnFollower_DoesNotThrow()
    {
        var name = UniqueName();
        using var leader = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));
        using var follower = new MutexLeaderElection(name, TimeSpan.FromMilliseconds(50));

        leader.Start();
        WaitFor(() => leader.IsLeader, TimeSpan.FromSeconds(2));

        follower.Start();
        Thread.Sleep(200);
        Assert.False(follower.IsLeader);

        // Stopping a follower should not attempt ReleaseMutex (it never owned it).
        var ex = Record.Exception(() => follower.Stop());
        Assert.Null(ex);
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
