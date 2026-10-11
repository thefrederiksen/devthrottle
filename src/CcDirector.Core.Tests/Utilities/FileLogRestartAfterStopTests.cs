using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// Regression tests for the LIFETIME half of devthrottle_internal#1312.
///
/// FileLog.Stop() completes the writer's queue, which cannot be undone. FileLog.Start() then set the
/// _started flag back to 1 and started a thread on the SAME spent writer - restarting the flag and not
/// the writer. From that moment every FileLog.Write passed the "if (_started == 0) return" guard and
/// threw from Enqueue into whatever caller happened to be logging. The guard was correct code and saved
/// nothing, because _started had quietly stopped meaning "the writer can accept lines".
///
/// These tests live here rather than in the parallel Core.UnitTests assembly because they manipulate
/// FileLog's PROCESS-WIDE static state; this assembly serialises, so one test owns FileLog at a time.
/// The half of the fix that does not need static state - Enqueue never throwing - is tested in
/// Core.UnitTests, so it runs in the default gate.
/// </summary>
[Collection("ProcessWideState")]
public sealed class FileLogRestartAfterStopTests
{
    [Fact]
    public void Write_AfterStopAndStart_DoesNotThrow()
    {
        // The exact sequence observed in the failing test hosts: a production shutdown path stops the
        // log, something starts it again, and the next unrelated caller to log takes the exception.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();
        FileLog.Start();

        var ex = Record.Exception(() => FileLog.Write("a line written after a stop and a restart"));

        Assert.Null(ex);
    }

    [Fact]
    public void Write_AfterStopAndStart_IsActuallyAccepted_NotSilentlyDropped()
    {
        // DO NOT DELETE THIS AS A DUPLICATE OF THE TEST ABOVE. It is the ONLY test in this file that
        // detects a lifetime regression, and that was established by injecting the defect rather than
        // reasoned about: with the Enqueue fix in place and the Start fix removed, the three
        // "does not throw" tests here ALL PASS, because the surviving half of the fix converts the
        // crash into a silent drop. Only the dropped-line count still notices.
        //
        // That is the trap the two fixes create together: each one masks the other's symptom. Delete
        // this assertion and a future change that reverts Start to reviving a spent writer will land
        // green, with logging quietly dead for the rest of every process that stops and starts.
        //
        // "Did not throw" on its own would also pass if Start left a spent writer in place and every
        // line went to the dropped counter instead - broken logging that no longer announces itself,
        // which is worse than the crash it replaced. So assert the line was ACCEPTED.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();
        FileLog.Start();

        // Same reasoning as the trigger test below: DroppedLines is process-wide and other tests move
        // it, so the invariant is asserted directly. A spent writer still installed after Start is the
        // lifetime defect, and with Enqueue no longer throwing it is otherwise completely silent.
        Assert.False(FileLog.InstalledWriterIsSpent);

        var ex = Record.Exception(() => FileLog.Write("a line that must reach the new writer"));
        Assert.Null(ex);
    }

    [Fact]
    public void RepeatedStopStartCycles_KeepLogging()
    {
        // One cycle could be satisfied by a single replacement; the writer has to stay replaceable, so a
        // process that stops and starts more than once is not left with a spent writer on the second pass.
        using var scope = FileLog.RedirectForTests();

        var ex = Record.Exception(() =>
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                FileLog.Stop();
                FileLog.Start();
                FileLog.Write($"cycle {cycle}");
            }
        });

        Assert.Null(ex);
    }

    [Fact]
    public void DrainAndReadLines_DoesNotLeaveASpentWriterInstalledGlobally()
    {
        // THE TRIGGER, pinned. Draining stops the throwaway writer, which completes its queue - and the
        // scope used to leave that spent writer installed as the process-wide writer, with _started
        // still 1, right up until Dispose. In an assembly that runs tests in parallel, every neighbour
        // that logged in that window met a completed queue. That is what actually took down
        // AuthMiddlewareTests, GatewayInputStatsAggregatorTests, DeviceCredentialImportTests and
        // SkillStoreTests on different runs - all four in Gateway.UnitTests, which runs four at a time.
        //
        // The assertion is the dropped-line count, not "did not throw": with Enqueue no longer throwing,
        // a spent global writer is SILENT, so a throw-only test would pass while every neighbour's log
        // line was being discarded.
        using var scope = FileLog.RedirectForTests();

        FileLog.Write("a line produced inside the scope");
        var lines = scope.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains("a line produced inside the scope"));

        // The invariant, asserted directly. This first read DroppedLines instead, and that version
        // passed on its own and FAILED inside the full suite - DroppedLines is process-wide, so
        // thousands of unrelated tests had already filled the ambient writer's bounded queue and the
        // count moved for reasons that had nothing to do with this scope. An order-dependent test for
        // an order-dependent bug proves nothing; caught by running the parked suite in full, which is
        // exactly what the coverage-gap warning is for.
        Assert.False(FileLog.InstalledWriterIsSpent);

        var ex = Record.Exception(() => FileLog.Write("a neighbour logging after the drain"));
        Assert.Null(ex);
    }

    [Fact]
    public void Write_WhileStopped_IsIgnoredRatherThanThrowing()
    {
        // The gap between a stop and the next start is a legitimate state, not an error: the guard is
        // supposed to make logging a no-op there. Pinned so a future change cannot "fix" the restart by
        // removing the guard and reintroducing the throw through the other door.
        using var scope = FileLog.RedirectForTests();

        FileLog.Stop();

        var ex = Record.Exception(() => FileLog.Write("a line written while the log is stopped"));

        Assert.Null(ex);

        FileLog.Start();
    }
}
