using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Issue #2905, round 3: EVERY STOP THE SEAT OBSERVES LEAVES EXACTLY ONE TRACE OR ONE COUNTED LOSS, through one door.
/// A trace the writer cannot keep is logged and counted by the writer; a trace the seat cannot even offer - no judgement
/// for the session read its settings - is counted by the writer too, never only logged by the seat.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictEveryStopIsAccountedForTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-accounted";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string Spoken = "The retention sweep. The branch is pushed and nothing is waiting on you.";
    private static readonly string FinishedAnswer = FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken);
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static TurnEndSignal Signal(int minute = 0) => new(Sid, "dir-1", Tenant, ObservedAt.AddMinutes(minute), IsNewTurn: true);

    private static FakeTurnVerdictEnvironment Env() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
        Judge = (_, _) => Task.FromResult(FinishedAnswer),
    };

    /// <summary>How a stop left the seat: its turn-end rows and its counted losses.</summary>
    private static int Accounted(FakeTurnVerdictEnvironment env)
        => env.Traces.Count(t => t.Trigger == "turn-end") + env.NotKept.Count;

    /// <summary>The observed time of every turn-end row and every counted loss, as minutes after <see cref="ObservedAt"/>,
    /// sorted - one entry per stop the seat accounted for, named by the stop it is ABOUT.</summary>
    private static int[] AccountedStops(FakeTurnVerdictEnvironment env)
        => env.Traces.Where(t => t.Trigger == "turn-end").Select(t => t.TurnEndObservedAtUtc)
            .Concat(env.NotKept.Select(l => l.Trace.TurnEndObservedAtUtc))
            .Select(at => (int)(at - ObservedAt).TotalMinutes)
            .OrderBy(minute => minute)
            .ToArray();

    /// <summary>One exit, and the stops it admitted, as minutes after <see cref="ObservedAt"/>.</summary>
    private sealed record Exit(string Name, int[] Stops, FakeTurnVerdictEnvironment Env, TurnVerdictService Service);

    [Fact]
    public async Task EveryExitOfAnObservedStop_LeavesExactlyOneRowOrOneCountedLoss_PerStop()
    {
        var exits = new List<Exit>();

        // Judged.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            Assert.Equal(TurnVerdictOutcomeKind.Judged, (await service.StartTurnEnd(Signal())).Kind);
            exits.Add(new("judged", new[] { 0 }, env, service));
        }
        // Refused by the contract.
        {
            var env = Env(); env.Judge = (_, _) => Task.FromResult("not a verdict at all");
            var service = new TurnVerdictService(env);
            Assert.Equal(TurnVerdictOutcomeKind.Failed, (await service.StartTurnEnd(Signal())).Kind);
            exits.Add(new("refused", new[] { 0 }, env, service));
        }
        // Reused: a second stop on the same screen.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            await service.StartTurnEnd(Signal());
            Assert.Equal(TurnVerdictOutcomeKind.Reused, (await service.StartTurnEnd(Signal(1))).Kind);
            exits.Add(new("reused", new[] { 0, 1 }, env, service));
        }
        // Skipped: held.
        {
            var env = Env(); env.Held = _ => true;
            var service = new TurnVerdictService(env);
            Assert.Equal(ActivityCauses.Held, (await service.StartTurnEnd(Signal())).SkipCause);
            exits.Add(new("skipped", new[] { 0 }, env, service));
        }
        // Cancelled: the session worked while its verdict was being formed.
        {
            var env = Env(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            env.Judge = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return FinishedAnswer; };
            var service = new TurnVerdictService(env);
            var pending = service.StartTurnEnd(Signal());
            await entered.Task.WaitAsync(Wait);
            service.OnSessionWorking(Tenant, Sid);
            Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await pending.WaitAsync(Wait)).Kind);
            exits.Add(new("cancelled", new[] { 0 }, env, service));
        }
        // Failed at the boundary, with settings: the store threw.
        {
            var env = Env(); env.NextStoreThrows = new InvalidOperationException("the database went away");
            var service = new TurnVerdictService(env);
            Assert.Equal(TurnVerdictOutcomeKind.Failed, (await service.StartTurnEnd(Signal())).Kind);
            exits.Add(new("failed-with-settings", new[] { 0 }, env, service));
        }
        // Failed at the boundary before any settings were read.
        {
            var env = Env(); env.SettingsOverride = () => throw new InvalidOperationException("settings unreadable");
            var service = new TurnVerdictService(env);
            Assert.Equal(TurnVerdictOutcomeKind.Failed, (await service.StartTurnEnd(Signal())).Kind);
            exits.Add(new("failed-without-settings", new[] { 0 }, env, service));
        }
        // Joined a running judgement.
        {
            var env = Env(); var (entered, release) = BlockFirstJudge(env);
            var service = new TurnVerdictService(env);
            var first = service.StartTurnEnd(Signal());
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(1))).SkipCause);
            release.SetResult();
            await first.WaitAsync(Wait);
            exits.Add(new("joined", new[] { 0, 1 }, env, service));
        }
        // Joined a judgement whose settings read threw.
        {
            var env = Env();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim();
            var reads = 0;
            env.SettingsOverride = () =>
            {
                // The first read is the caller-thread free check; the second is the flight's own, and it is held, then throws.
                if (Interlocked.Increment(ref reads) == 2) { entered.TrySetResult(); release.Wait(Wait); }
                throw new InvalidOperationException("settings unreadable");
            };
            var service = new TurnVerdictService(env);
            var first = service.StartTurnEnd(Signal());
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(1))).SkipCause);
            release.Set();
            Assert.Equal(TurnVerdictOutcomeKind.Failed, (await first.WaitAsync(Wait)).Kind);
            exits.Add(new("joined-no-settings", new[] { 0, 1 }, env, service));
        }
        // Queued behind an ending judgement, then handed the gate - and a third stop joining that successor.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            Task<TurnVerdictOutcome>? queuedHead = null, queuedJoiner = null;
            service.OnJoinedListClosedForTests = _ =>
            {
                if (queuedHead is not null) return;
                queuedHead = service.StartTurnEnd(Signal(1));
                queuedJoiner = service.StartTurnEnd(Signal(2));
            };
            await service.StartTurnEnd(Signal()).WaitAsync(Wait);
            await queuedHead!.WaitAsync(Wait);
            Assert.Equal(ActivityCauses.AlreadyJudging, (await queuedJoiner!.WaitAsync(Wait)).SkipCause);
            exits.Add(new("queued-handed-over", new[] { 0, 1, 2 }, env, service));
        }
        // Queued behind an ending judgement, then shutdown.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            Task<TurnVerdictOutcome>? queued = null;
            service.OnJoinedListClosedForTests = _ =>
            {
                if (queued is not null) return;
                queued = service.StartTurnEnd(Signal(1));
                service.Dispose();
            };
            await service.StartTurnEnd(Signal()).WaitAsync(Wait);
            Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await queued!.WaitAsync(Wait)).Kind);
            exits.Add(new("queued-cancelled-at-shutdown", new[] { 0, 1 }, env, service));
        }
        // Queued behind a judgement that never read its settings, then shutdown.
        {
            var (env, service, queued) = QueuedBehindAJudgementWithNoSettingsAtShutdown();
            await service.WaitForFlightsAsync(Wait);
            Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await queued.WaitAsync(Wait)).Kind);
            exits.Add(new("queued-no-settings-at-shutdown", new[] { 0, 1 }, env, service));
        }
        // Registered, then disposal before its flight starts: stood down after disposal.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            var disposing = Task.CompletedTask;
            service.OnAdmissionCheckedForTests = _ => disposing = Task.Run(service.Dispose);
            var stop = service.StartTurnEnd(Signal());
            await disposing.WaitAsync(Wait);
            await stop.WaitAsync(Wait);
            exits.Add(new("registered-then-disposed", new[] { 0 }, env, service));
        }
        // Refused: arrived after shutdown began.
        {
            var env = Env(); var service = new TurnVerdictService(env);
            service.Dispose();
            await service.StartTurnEnd(Signal()).WaitAsync(Wait);
            exits.Add(new("refused-after-shutdown", new[] { 0 }, env, service));
        }

        var unaccounted = new List<string>();
        foreach (var exit in exits)
        {
            Assert.True(await exit.Service.WaitForFlightsAsync(Wait), $"{exit.Name}: the drain timed out");
            // BY IDENTITY, NOT BY TOTAL (round 3 inspection): a row naming the wrong stop beside a stop with no row adds up
            // to the right count, so each admitted stop must be named exactly once by a row or a counted loss.
            var accounted = AccountedStops(exit.Env);
            if (!accounted.SequenceEqual(exit.Stops))
                unaccounted.Add($"{exit.Name}: stops admitted at minute(s) [{string.Join(",", exit.Stops)}], " +
                                $"but rows and counted losses name minute(s) [{string.Join(",", accounted)}]");
        }
        Assert.True(unaccounted.Count == 0, string.Join("; ", unaccounted));
    }

    [Fact]
    public async Task DisposalLandingBetweenTheAdmissionCheckAndRegistration_StillAccountsForTheStop_BeforeTheDrainReturns()
    {
        // Round 2, finding 2. A stop passed the disposed check, paused, and registered after Dispose had found no flight and
        // the drain had returned; it then stood down with no row and no counted loss.
        //
        // REACHED AT THE EXACT POINT: the seam runs after the stop has found the service not disposed and before it is
        // registered, and shutdown begins there - Dispose, then the drain - on another thread. On the old code Dispose and the
        // drain both finished inside the seam. Admission and disposal are now one step each under one lock, so Dispose waits
        // for the registration; the seam gives it half a second to prove it cannot finish first.
        var env = Env();
        var service = new TurnVerdictService(env);
        Task? shutdown = null;
        var shutdownFinishedInsideTheGap = false;
        var accountedWhenTheDrainReturned = -1;
        service.OnAdmissionCheckedForTests = _ =>
        {
            if (shutdown is not null) return;
            shutdown = Task.Run(async () =>
            {
                service.Dispose();
                Assert.True(await service.WaitForFlightsAsync(Wait));
                accountedWhenTheDrainReturned = Accounted(env);
            });
            shutdownFinishedInsideTheGap = shutdown.Wait(TimeSpan.FromMilliseconds(500));
        };

        var stop = service.StartTurnEnd(Signal());
        await shutdown!.WaitAsync(Wait);
        await stop.WaitAsync(Wait);

        Assert.False(shutdownFinishedInsideTheGap, "shutdown finished while the stop was between its admission check and its registration");
        Assert.Equal(1, accountedWhenTheDrainReturned);
        Assert.Equal(1, Accounted(env));
    }

    [Fact]
    public async Task AStopQueuedBehindAJudgementThatNeverReadItsSettings_AtShutdown_IsACountedLoss_NotOnlyALogLine()
    {
        // Round 2, finding 3. The predecessor's settings read threw, a stop queued behind it, and shutdown began before the
        // hand-over. The queued stop was cancelled with null settings and only logged: no row, and no writer counter moved.
        using var writer = new TurnVerdictTraceWriter((_, _) => { });
        var (env, service, queued) = QueuedBehindAJudgementWithNoSettingsAtShutdown(writer);

        Assert.True(await service.WaitForFlightsAsync(Wait));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await queued.WaitAsync(Wait)).Kind);

        var loss = Assert.Single(env.NotKept, l => l.Trace.TurnEndObservedAtUtc == ObservedAt.AddMinutes(1));
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, loss.Trace.Outcome);
        Assert.Equal(TurnVerdictService.SettingsNeverReadCause, loss.Cause);
        // The predecessor's own stop is a counted loss too, and the writer counted both.
        Assert.Single(env.NotKept, l => l.Trace.TurnEndObservedAtUtc == ObservedAt);
        Assert.Equal(2, writer.Lost);
    }

    [Fact]
    public void TheServiceHasOneTraceDoor_AndLogsNoLossOfItsOwn()
    {
        // The search the round-3 ruling asks for, kept as a guard: the writer is the only place a trace is logged as not kept.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Wingman", "TurnVerdictService.cs"));
        Assert.DoesNotContain("NOT KEPT", source);
        Assert.Equal(1, Occurrences(source, "_env.RecordTrace("));
        Assert.Equal(2, Occurrences(source, "_env.TraceNotKept("));
        Assert.Equal(1, Occurrences(source, "private void TraceStop("));
    }

    /// <summary>A judgement whose settings read throws, with a stop queued behind it at the point its list closes, and
    /// shutdown beginning there too - before the hand-over.</summary>
    private static (FakeTurnVerdictEnvironment Env, TurnVerdictService Service, Task<TurnVerdictOutcome> Queued)
        QueuedBehindAJudgementWithNoSettingsAtShutdown(TurnVerdictTraceWriter? writer = null)
    {
        var env = Env();
        env.TraceWriter = writer;
        env.SettingsOverride = () => throw new InvalidOperationException("settings unreadable");
        var service = new TurnVerdictService(env);
        Task<TurnVerdictOutcome>? queued = null;
        var queuedWhenShutdownBegan = false;
        service.OnJoinedListClosedForTests = _ =>
        {
            if (queued is not null) return;
            queued = service.StartTurnEnd(Signal(1));
            queuedWhenShutdownBegan = !queued.IsCompleted;
            service.Dispose();
        };
        var first = service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, first.WaitAsync(Wait).GetAwaiter().GetResult().Kind);
        Assert.NotNull(queued);
        Assert.True(queuedWhenShutdownBegan, "the stop did not queue: it had already been answered when shutdown began");
        return (env, service, queued!);
    }

    private static (TaskCompletionSource Entered, TaskCompletionSource Release) BlockFirstJudge(FakeTurnVerdictEnvironment env)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Judge = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
            return FinishedAnswer;
        };
        return (entered, release);
    }

    private static int Occurrences(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
