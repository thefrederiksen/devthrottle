using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ONE STOP, ONE TURN END, HOWEVER MANY FEEDS SEE IT (the Wingman-on-every-turn mission, slice E round 3).
///
/// Three feeds observe the same session: the hub's accepted delta, the host's session-state path and the watcher's own
/// reconcile sweep. The inspection's barrier probe for round 2 is kept here as the test: twenty thousand sessions seeded
/// Working, then two feeds released together by a barrier, each observing every session move to waiting. The watcher
/// that read and wrote the last state as two steps raised 30,189 turn ends for twenty thousand stops.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class TurnEndWatcherAtomicTransitionTests
{
    private const int Sessions = 20_000;

    [Fact]
    public void TwoFeedsRacingTheSameStopForTwentyThousandSessions_RaiseExactlyOneTurnEndPerSession()
    {
        var turnEnds = 0;
        var perSession = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var watcher = new TurnEndWatcher(
            onTurnEnd: signal =>
            {
                Interlocked.Increment(ref turnEnds);
                perSession.AddOrUpdate(signal.SessionId, 1, (_, n) => n + 1);
            },
            onSessionWorking: (_, _, _) => { });

        var ids = Enumerable.Range(0, Sessions).Select(i => "sid-" + i).ToArray();
        foreach (var id in ids)
            watcher.Observe(TenantId.Local, id, "Working", "dir-A");
        Assert.Equal(0, turnEnds);   // CONTROL: seeding Working raises nothing

        using var barrier = new Barrier(2);
        void Feed()
        {
            barrier.SignalAndWait();
            foreach (var id in ids)
                watcher.Observe(TenantId.Local, id, "WaitingForInput", "dir-A");
        }

        var first = new Thread(Feed);
        var second = new Thread(Feed);
        first.Start();
        second.Start();
        first.Join();
        second.Join();

        Assert.Equal(Sessions, turnEnds);
        Assert.Equal(Sessions, perSession.Count);
        Assert.DoesNotContain(perSession, kv => kv.Value != 1);
    }
}
