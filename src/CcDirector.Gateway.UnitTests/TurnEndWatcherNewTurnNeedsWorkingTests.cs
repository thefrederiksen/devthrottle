using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// TWO NEW TURNS ALWAYS HAVE A WORKING EVENT BETWEEN THEM (the Wingman-on-every-turn mission, slice I, inspection round
/// five). The verdict service relies on it: a new-turn signal arrives only after onSessionWorking has cancelled the
/// previous stop's flight, so a new turn never has to be told apart from an older stop's judgement still in flight. The
/// Architect ruled the round four overlap - two new-turn signals with no Working event between them - unreachable on
/// this reading of the watcher, and this test is what keeps that reading true.
///
/// Every state sequence below is fed to one watcher. The callbacks write to ONE ordered log on the observing thread, so
/// the assertion is about order: before every new-turn signal after the first, a Working event was already recorded.
/// </summary>
public sealed class TurnEndWatcherNewTurnNeedsWorkingTests
{
    private static readonly string[] States = { "Working", "WaitingForInput", "Idle", "Exited", "Crashed", "Unknown" };

    [Fact]
    public void ASecondNewTurnSignal_IsNeverRaisedWithoutOnSessionWorkingBetween_OverEverySequenceOfFiveStates()
    {
        var sequences = 0;
        foreach (var sequence in AllSequences(length: 5))
        {
            sequences++;
            var log = new List<string>();
            using var watcher = new TurnEndWatcher(
                onTurnEnd: signal => log.Add(signal.IsNewTurn ? "new-turn" : "catch-up"),
                onSessionWorking: (_, _, _) => log.Add("working"));

            foreach (var state in sequence)
                watcher.Observe(TenantId.Local, "sid-1", state, "dir-A");

            var workingSinceLastNewTurn = true;   // the first new turn needs nothing before it
            var seenNewTurn = false;
            foreach (var entry in log)
            {
                if (entry == "working") workingSinceLastNewTurn = true;
                if (entry != "new-turn") continue;
                Assert.True(!seenNewTurn || workingSinceLastNewTurn,
                    $"two new-turn signals with no Working event between them for [{string.Join(", ", sequence)}]: [{string.Join(", ", log)}]");
                // ...and every new turn itself comes straight after a Working event.
                Assert.True(workingSinceLastNewTurn,
                    $"a new-turn signal with no Working event before it for [{string.Join(", ", sequence)}]: [{string.Join(", ", log)}]");
                seenNewTurn = true;
                workingSinceLastNewTurn = false;
            }
        }

        Assert.Equal((int)Math.Pow(States.Length, 5), sequences);   // CONTROL: every sequence was fed
    }

    [Fact]
    public void TheOrdinaryTwoTurnSequence_RaisesWorkingBeforeEachNewTurn()
    {
        var log = new List<string>();
        using var watcher = new TurnEndWatcher(
            onTurnEnd: signal => log.Add(signal.IsNewTurn ? "new-turn" : "catch-up"),
            onSessionWorking: (_, _, _) => log.Add("working"));

        foreach (var state in new[] { "Working", "WaitingForInput", "WaitingForInput", "Working", "WaitingForInput" })
            watcher.Observe(TenantId.Local, "sid-1", state, "dir-A");

        // CONTROL that the log sees what the service sees: two new turns, each after its own Working event.
        Assert.Equal(new[] { "working", "new-turn", "working", "new-turn" }, log);
    }

    private static IEnumerable<string[]> AllSequences(int length)
    {
        var indexes = new int[length];
        while (true)
        {
            yield return indexes.Select(i => States[i]).ToArray();
            var position = length - 1;
            while (position >= 0 && ++indexes[position] == States.Length)
            {
                indexes[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }
}
