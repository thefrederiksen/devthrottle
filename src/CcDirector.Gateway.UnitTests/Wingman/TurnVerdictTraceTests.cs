using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// What the turn-verdict seat writes for the Wingman inspector (devthrottle_internal#2029). The rule, in one line: a
/// trace for every STOP whatever happened to it, and for every JUDGE CALL whatever asked for it - carrying the
/// package the judge was given, the exact prompt, the answer as received and how long it took, or, for a stop that
/// stood down, its cause and no content.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
///
/// Every screen and conversation below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class TurnVerdictTraceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-1";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private static readonly DateTime ObservedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
    private const string Spoken = "The retention sweep. The branch is pushed and nothing is waiting on you.";
    private static readonly string FinishedAnswer = FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static TurnEndSignal Signal(DateTime? at = null) => new(Sid, "dir-1", Tenant, at ?? ObservedAt, IsNewTurn: true);

    private static FakeTurnVerdictEnvironment Env() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
        Judge = (_, _) => Task.FromResult(FinishedAnswer),
    };

    // ================================================================= a judge call

    [Fact]
    public async Task AJudgedStop_LeavesOneTrace_WithTheExactPromptTheRawReplyTheReplyTimeAndThePackage()
    {
        var env = Env();
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Judged, trace.Outcome);
        Assert.Equal("turn-end", trace.Trigger);
        Assert.Equal(Sid, trace.SessionId);
        Assert.Equal("dir-1", trace.DirectorId);
        Assert.Equal(ObservedAt, trace.TurnEndObservedAtUtc);
        Assert.Equal(outcome.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(outcome.Verdict.VerdictId, trace.Verdict!.VerdictId);
        // The prompt the judge was ACTUALLY asked, not a rebuild of it.
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
        Assert.Equal(0.2, trace.ReplySeconds);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
        Assert.Equal(ReplyText, trace.Package.LatestReply);
        Assert.False(trace.ColourEnabled);
    }

    [Fact]
    public async Task ARefusedAnswer_LeavesATrace_WithTheRawReplyThatFailed_AndTheReason()
    {
        var env = Env();
        // A state word that is not one of the seven: the contract refuses it. (The receipt that used to refuse
        // an answer here was cut in contract v3 - see AnAnswerTheOldReceiptAndRiskRulesRefused_IsNowARealReading.)
        const string invented = """
            {
              "state": "everything-is-fine",
              "label": "Deployed the Gateway",
              "agentRecommends": null,
              "menu": null,
              "options": []
            }
            """;
        env.Judge = (_, _) => Task.FromResult(invented);

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Refused, trace.Outcome);
        Assert.Equal(invented, trace.RawReply);
        Assert.True(trace.Verdict!.Failed);
        Assert.False(string.IsNullOrWhiteSpace(trace.Verdict.FailureReason));
        Assert.NotNull(trace.Prompt);
        Assert.NotNull(trace.Package);
        // A REFUSED READING MAKES NO NARRATION CALL, so the trace shows that half as never made rather than as
        // failed. The debug view has to be able to tell "we did not ask" from "we asked and it went wrong".
        Assert.Null(trace.NarrationPrompt);
        Assert.Null(trace.NarrationRawReply);
        Assert.Null(trace.NarrationSeconds);
    }

    /// <summary>
    /// BOTH HALVES OF ONE READING ARE ON ONE TRACE (owner ruling, 2026-09-18). The judge call was traced from the
    /// start; the narration call was not traced at all, so the debug view could show what the Wingman decided and
    /// never what it then said or why it said it. A reading is one thing now, and its record is one row.
    ///
    /// THERE IS NO SECOND PACKAGE, deliberately: the narration call is given the judge's package, so storing it
    /// twice would be two copies of one screen that could drift apart and be read as two different screens.
    /// </summary>
    [Fact]
    public async Task AnAcceptedReading_LeavesOneTrace_CarryingBothModelCalls()
    {
        var env = Env();
        // SAID EXPLICITLY, because the assertion below is on the narrated WORDS. The double's default narrator
        // echoes a value the canned judge builders share across the whole test process, so a sibling class judging
        // a different stop can decide what this one hears - which is a flake when it disagrees and, worse, a pass
        // when it happens to agree.
        env.Narrator = (_, _) => Task.FromResult(Spoken);

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.NotNull(trace.Prompt);
        Assert.NotNull(trace.RawReply);
        Assert.NotNull(trace.Package);

        Assert.NotNull(trace.NarrationPrompt);
        Assert.NotNull(trace.NarrationRawReply);
        Assert.NotNull(trace.NarrationSeconds);
        Assert.Null(trace.NarrationFailureDetail);
        // The two prompts are different questions about the same stop, not the same text stored twice.
        Assert.NotEqual(trace.Prompt, trace.NarrationPrompt);
        Assert.Contains(Spoken, trace.NarrationRawReply!);
    }

    [Fact]
    public async Task AJudgeThatDoesNotAnswer_LeavesATrace_WithThePromptAndNoReply()
    {
        var env = Env();
        env.Judge = (_, _) => throw new TimeoutException("the judge took longer than thirty seconds");

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.DidNotAnswer, trace.Outcome);
        Assert.NotNull(trace.Prompt);
        Assert.Null(trace.RawReply);
        Assert.Null(trace.ReplySeconds);
    }

    [Fact]
    public async Task WhenTheVerdictStoreThrowsAfterTheJudgeAnswered_TheTraceStillKeepsThePromptAndTheWholeAnswer()
    {
        // Found in review: the evidence lived in locals the flight's exception boundary could not see, so this trace
        // carried only the exception type - exactly the case where what the judge said is the thing that was lost.
        var env = Env();
        env.NextStoreThrows = new IOException("the database file is locked");

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Unavailable, trace.Outcome);
        Assert.Equal(nameof(IOException), trace.Cause);
        Assert.Equal(outcome.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
        Assert.Equal(0.2, trace.ReplySeconds);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSessionWorked_LeavesACancelledTrace_WithThePromptAndTheAnswer()
    {
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.OnSessionWorking(Tenant, Sid);
        release.SetResult();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.Equal(ActivityCauses.WorkingObservation, trace.Cause);
        Assert.Null(trace.VerdictId);
        Assert.Null(trace.Verdict);
        Assert.Equal(Assert.Single(env.Prompts), trace.Prompt);
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }

    [Fact]
    public async Task ACancelledTrace_CarriesItsOwnStopsMoment_AndALaterStopObservedMeanwhileIsJudgedUnderItsOwn()
    {
        // Found in review: the cancellation looked up "the latest observed stop" again, after the session had moved on,
        // and stamped the old stop's screen and answer with the new stop's moment.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Judge = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.OnSessionWorking(Tenant, Sid);
        var later = ObservedAt.AddMinutes(3);
        // The later stop does NOT join the cancelled judgement (issue #3399: it used to, and was never judged). It queues
        // behind it and is handed the gate when the cancelled judgement leaves.
        var second = service.StartTurnEnd(Signal(later));
        release.SetResult();

        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Kind);

        // Two stops, two traces, each under its own moment: the earlier one cancelled with its evidence, and the later
        // one judged with its own.
        Assert.True(await WaitUntil(() => env.Traces.Count == 2), $"expected two traces, saw {env.Traces.Count}");
        var cancelled = Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Cancelled);
        Assert.Equal(ObservedAt, cancelled.TurnEndObservedAtUtc);
        Assert.NotNull(cancelled.Prompt);
        var judged = Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Judged);
        Assert.Equal(later, judged.TurnEndObservedAtUtc);
        Assert.NotNull(judged.Prompt);
    }

    [Fact]
    public async Task AJoinedStop_IsRecordedByTheJudgementItJoined_WithTheSettingsThatJudgementAlreadyRead()
    {
        // Found in review twice: this stop first vanished, and then became a background task nobody tracked - which
        // shutdown could drop, which could record two stops out of order, and which read the settings on the turn-end
        // handler. The judgement it joins records it, with the settings that judgement already holds.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Unreachable from here on: the joined stop must need no settings read of its own.
        env.SettingsOverride = () => throw new InvalidOperationException("the settings store is unreachable");

        var later = ObservedAt.AddMinutes(3);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var joined = service.StartTurnEnd(Signal(later));
        clock.Stop();

        Assert.True(joined.IsCompletedSuccessfully);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await joined).SkipCause);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"the turn end waited {clock.Elapsed}");

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Kind);

        // Two rows from the one judgement: its own, and the stop that joined it, under that stop's own moment.
        Assert.Equal(2, env.Traces.Count);
        var judged = Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Judged);
        var joinedTrace = Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Joined);
        Assert.Equal(later, joinedTrace.TurnEndObservedAtUtc);
        Assert.Equal(ActivityCauses.AlreadyJudging, joinedTrace.Cause);
        Assert.Null(joinedTrace.Prompt);
        Assert.NotNull(judged.Prompt);
    }

    [Fact]
    public async Task AStopArrivingWhileTheJudgementItWouldJoinIsEnding_TakesTheGateItself_AndIsJudged()
    {
        // Found in review: the ending judgement closed its joined list while it still held the gate, so a stop arriving
        // in that window could neither join it nor take the gate, and was lost.
        //
        // THE PROBE IS INSIDE THE WINDOW, not racing it: the stop is started from within the write of the joined row,
        // which is the exact moment the ending judgement is writing what joined it. If the gate were still held then,
        // this stop would find a flight it cannot join and come back skipped.
        var env = Env();
        var firstJudge = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var judgeCalls = 0;
        env.Judge = async (_, _) =>
        {
            if (Interlocked.Increment(ref judgeCalls) == 1)
            {
                entered.TrySetResult();
                await firstJudge.Task;
            }
            return FinishedAnswer;
        };
        TurnVerdictService service = null!;
        Task<TurnVerdictOutcome>? arrivingTask = null;
        env.BeforeRecordTrace = trace =>
        {
            if (trace.Outcome != TurnVerdictTraceOutcomes.Joined || arrivingTask is not null) return;
            // A different screen, so this stop is judged rather than served the stored verdict - what is being proved
            // is that it gets the gate at all.
            env.Screen = () => Screen(Sid, "A different screen, so this stop is judged rather than reusing the last verdict.", "> ");
            arrivingTask = service.StartTurnEnd(Signal(ObservedAt.AddMinutes(2)));
        };
        service = new TurnVerdictService(env);

        var first = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(ObservedAt.AddMinutes(1)))).SkipCause);

        firstJudge.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(arrivingTask);
        var arriving = await arrivingTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, arriving.Kind);
        Assert.Equal(2, judgeCalls);
        Assert.True(await WaitUntil(() => env.Traces.Count == 3), $"expected three traces, saw {env.Traces.Count}");
        Assert.Equal(2, env.Traces.Count(t => t.Outcome == TurnVerdictTraceOutcomes.Judged));
        Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Joined);
    }

    [Fact]
    public async Task AtShutdown_AJoinedStopIsRecorded_BecauseTheJudgementItJoinedIsWaitedFor()
    {
        // The joined stop is part of the judgement it joined, so the shutdown wait covers it; as a background task it
        // could be left unfinished when the writer closed and the database went.
        var env = Env();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var later = ObservedAt.AddMinutes(2);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(later))).SkipCause);

        service.Dispose();
        Assert.True(await service.WaitForFlightsAsync(TimeSpan.FromSeconds(5)));

        // Both rows are in hand the moment the wait returns - nothing is still being written somewhere else.
        Assert.Equal(2, env.Traces.Count);
        Assert.Contains(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Cancelled);
        var joined2 = Assert.Single(env.Traces, t => t.Outcome == TurnVerdictTraceOutcomes.Joined);
        Assert.Equal(later, joined2.TurnEndObservedAtUtc);
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await pending).Kind);
    }

    /// <summary>A judgement blocked inside its judge until <paramref name="release"/> completes, with a trace writer over
    /// the real store and a clock that moves one second per read, so the store's newest-first order is the order the
    /// rows were handed in.</summary>
    private (FakeTurnVerdictEnvironment Env, TurnVerdictTraceStore Store, TurnVerdictTraceWriter Writer) StoreBacked(
        TaskCompletionSource release, TaskCompletionSource entered)
    {
        var store = new TurnVerdictTraceStore(_harness.Open());
        var writer = new TurnVerdictTraceWriter(store.Append);
        var env = Env();
        env.TraceWriter = writer;
        var start = DateTime.UtcNow;
        var ticks = 0;
        env.Clock = () => start.AddSeconds(Interlocked.Increment(ref ticks));
        var judgeCalls = 0;
        env.Judge = async (_, _) =>
        {
            if (Interlocked.Increment(ref judgeCalls) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return FinishedAnswer;
        };
        return (env, store, writer);
    }

    [Fact]
    public async Task AShutdownBeginningAsTheEndingJudgementClosesItsJoinedList_StillWaitsForIt_AndTheJoinedRowIsWritten()
    {
        // Issue #2905, window one. The judgement used to leave the gate BEFORE writing the stops that joined it, so a
        // shutdown starting between the two found no judgement to wait for, closed the writer, and the joined row was
        // refused - logged and counted, never written.
        //
        // THE SHUTDOWN BEGINS AT THE EXACT POINT, not near it: from the seam that runs once the ending judgement has closed
        // its joined list and before it writes a row - where the old ordering had already left the gate. It runs the
        // host's own sequence there (cancel, wait for the flights, close the writer). When the wait finds nothing it
        // returns at once and the writer closes before the row is handed in; that is the old defect, reached every run.
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (env, store, writer) = StoreBacked(release, entered);
        using var writerScope = writer;
        var service = new TurnVerdictService(env);

        Task<bool>? flightsFinished = null;
        Task? writerClosed = null;
        service.OnJoinedListClosedForTests = closedSid =>
        {
            if (flightsFinished is not null) return;
            service.Dispose();
            flightsFinished = service.WaitForFlightsAsync(TimeSpan.FromSeconds(5));
            writerClosed = flightsFinished.IsCompleted
                ? writer.CompleteAsync()
                : flightsFinished.ContinueWith(done => writer.CompleteAsync(), TaskScheduler.Default).Unwrap();
        };

        var first = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var later = ObservedAt.AddMinutes(1);
        Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(later))).SkipCause);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(flightsFinished);
        Assert.True(await flightsFinished!.WaitAsync(TimeSpan.FromSeconds(5)), "the shutdown wait timed out");
        await writerClosed!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, writer.Dropped);
        var history = store.History(Tenant, Sid);
        var joined = Assert.Single(history, t => t.Outcome == TurnVerdictTraceOutcomes.Joined);
        Assert.Equal(later, joined.TurnEndObservedAtUtc);
        Assert.Single(history, t => t.Outcome == TurnVerdictTraceOutcomes.Judged);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task ANewJudgementForTheSameSession_CannotHandInItsRowBeforeTheEndingJudgementsJoinedRow_AsStoredOrderShows()
    {
        // Issue #2905, window two. Once the gate was released and before the joined rows were written, a new judgement for
        // the same session could start and finish - a held session skips with no model call at all - and hand in its row
        // first. RecordedAtUtc is stamped at hand-over and the history reads newest first, so the older stop read newer.
        //
        // THE NEW STOP ARRIVES AT THE EXACT POINT: from the seam that runs once the ending judgement has closed its joined
        // list and before it stamps a row. If that stop takes the gate there - its roster read happens on this thread when
        // it does, which is how the seam tells - the seam waits for it to finish, so its row is handed in first, every run.
        // If it cannot take the gate, it must wait for the ending judgement, and the seam does not wait for it.
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (env, store, writer) = StoreBacked(release, entered);
        using var writerScope = writer;
        var service = new TurnVerdictService(env);

        var joinedAt = ObservedAt.AddMinutes(1);
        var heldAt = ObservedAt.AddMinutes(2);
        Task<TurnVerdictOutcome>? arriving = null;
        var tookTheGateInTheWindow = false;
        service.OnJoinedListClosedForTests = closedSid =>
        {
            if (arriving is not null) return;
            env.Held = heldSid => true;
            var readsBefore = env.StateReads;
            arriving = service.StartTurnEnd(Signal(heldAt));
            tookTheGateInTheWindow = env.StateReads != readsBefore;
            if (tookTheGateInTheWindow)
                arriving.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };

        var first = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(joinedAt))).SkipCause);

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.NotNull(arriving);
        var held = await arriving!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivityCauses.Held, held.SkipCause);
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Newest first, from the store: the held stop, then the stop that joined, then the judgement it joined.
        var history = store.History(Tenant, Sid);
        Assert.Equal(
            new[] { TurnVerdictTraceOutcomes.Skipped, TurnVerdictTraceOutcomes.Joined, TurnVerdictTraceOutcomes.Judged },
            history.Select(t => t.Outcome).ToArray());
        Assert.Equal(heldAt, history[0].TurnEndObservedAtUtc);
        Assert.Equal(joinedAt, history[1].TurnEndObservedAtUtc);
        Assert.False(tookTheGateInTheWindow, "the new stop took the gate while the ending judgement still owed its joined row");
    }

    [Fact]
    public async Task AStopWaitingBehindAnEndingJudgement_WhenShutdownBegins_LeavesACancelledRow_AndTheDrainWaitsForIt()
    {
        // Issue #2905, round 2, finding 1. A stop that arrived after the ending judgement closed its joined list used to
        // wait on that judgement's completion OUTSIDE the gate, where the shutdown drain could not see it. The drain waited
        // for the judgement alone, the writer closed, and the stop - observed before shutdown - then stood down with no row
        // and no per-stop line.
        //
        // BOTH EVENTS HAPPEN AT THE EXACT POINT: from the seam that runs once the ending judgement has closed its joined
        // list and before it writes a row. The stop arrives there, so it can neither join nor take the gate; then, with it
        // still waiting, the host's own shutdown sequence begins there (cancel, wait for the flights, close the writer).
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (env, store, writer) = StoreBacked(release, entered);
        using var writerScope = writer;
        var service = new TurnVerdictService(env);

        var waitingAt = ObservedAt.AddMinutes(1);
        Task<TurnVerdictOutcome>? waiting = null;
        var waitingWhenShutdownBegan = false;
        Task<bool>? flightsFinished = null;
        Task? writerClosed = null;
        var tracesWhenTheDrainReturned = -1;
        service.OnJoinedListClosedForTests = closedSid =>
        {
            if (waiting is not null) return;
            waiting = service.StartTurnEnd(Signal(waitingAt));
            waitingWhenShutdownBegan = !waiting.IsCompleted;
            service.Dispose();
            flightsFinished = service.WaitForFlightsAsync(TimeSpan.FromSeconds(5));
            writerClosed = flightsFinished.ContinueWith(done =>
            {
                tracesWhenTheDrainReturned = env.Traces.Count;
                return writer.CompleteAsync();
            }, TaskScheduler.Default).Unwrap();
        };

        var first = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.NotNull(waiting);
        Assert.True(waitingWhenShutdownBegan, "the stop did not reach the point it was sent to: it had already been answered when shutdown began");
        Assert.True(await flightsFinished!.WaitAsync(TimeSpan.FromSeconds(5)), "the shutdown wait timed out");
        await writerClosed!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await waiting!.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        // The drain returned only once the waiting stop's row had been handed in, and the writer took it.
        Assert.Equal(2, tracesWhenTheDrainReturned);
        Assert.Equal(0, writer.Dropped);
        var history = store.History(Tenant, Sid);
        Assert.Equal(
            new[] { TurnVerdictTraceOutcomes.Cancelled, TurnVerdictTraceOutcomes.Judged },
            history.Select(t => t.Outcome).ToArray());
        Assert.Equal(waitingAt, history[0].TurnEndObservedAtUtc);
        Assert.Equal(ActivityCauses.Shutdown, history[0].Cause);
    }

    [Fact]
    public async Task AStopWaitingBehindAnEndingJudgement_IsNotOvertakenByALaterStop_ArrivingBeforeThatJudgementCompletes()
    {
        // Issue #2905, round 2, finding 2. The waiting stop used to take the gate only when the ending judgement COMPLETED,
        // which is after it left the gate. A later stop arriving in between took the gate synchronously and handed in its
        // row first, so the store read the earlier stop as the newer one.
        //
        // EACH STOP ARRIVES AT ITS EXACT POINT. The waiting stop arrives from the seam that runs once the ending judgement
        // has closed its joined list. The later stop arrives from the seam that runs once that judgement has left the gate
        // and before it completes - the gap the finding names. The waiting stop's own row is held until the later stop has
        // arrived, so the later stop always arrives while the waiting stop is still being answered; and if the later stop
        // takes the gate there - its roster read happens on this thread when it does - the seam waits for it to finish, so
        // an overtaking row is handed in first, every run.
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (env, store, writer) = StoreBacked(release, entered);
        using var writerScope = writer;
        var service = new TurnVerdictService(env);

        var joinedAt = ObservedAt.AddMinutes(1);
        var waitingAt = ObservedAt.AddMinutes(2);
        var laterAt = ObservedAt.AddMinutes(3);
        var laterArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.BeforeRecordTrace = trace =>
        {
            if (trace.TurnEndObservedAtUtc == waitingAt && trace.Outcome == TurnVerdictTraceOutcomes.Skipped)
                laterArrived.Task.Wait(TimeSpan.FromSeconds(5));
        };

        Task<TurnVerdictOutcome>? waiting = null;
        Task<TurnVerdictOutcome>? later = null;
        var laterTookTheGate = false;
        var gapSeamFired = 0;
        service.OnJoinedListClosedForTests = closedSid =>
        {
            if (waiting is not null) return;
            // Held from here on, so neither stop asks the judge - what is under test is only the order they are answered in.
            env.Held = heldSid => true;
            waiting = service.StartTurnEnd(Signal(waitingAt));
        };
        service.OnLeftGateForTests = leftSid =>
        {
            if (waiting is null || Interlocked.Exchange(ref gapSeamFired, 1) == 1) return;
            var readsBefore = env.StateReads;
            later = service.StartTurnEnd(Signal(laterAt));
            laterTookTheGate = env.StateReads != readsBefore;
            laterArrived.TrySetResult();
            if (laterTookTheGate)
                later.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };

        var first = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivityCauses.AlreadyJudging, (await service.StartTurnEnd(Signal(joinedAt))).SkipCause);

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.NotNull(waiting);
        Assert.Equal(ActivityCauses.Held, (await waiting!.WaitAsync(TimeSpan.FromSeconds(5))).SkipCause);
        Assert.NotNull(later);
        await later!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await service.WaitForFlightsAsync(TimeSpan.FromSeconds(5)));
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Newest first, from the store: the later stop, the waiting stop, the stop that joined, the judgement it joined. (The
        // judgement's own row carries the latest stop it saw observed - the joined one - so it is named by its outcome.)
        var history = store.History(Tenant, Sid);
        Assert.Equal(4, history.Count);
        Assert.Equal(
            new[] { laterAt, waitingAt, joinedAt },
            history.Take(3).Select(t => t.TurnEndObservedAtUtc).ToArray());
        Assert.Equal(TurnVerdictTraceOutcomes.Joined, history[2].Outcome);
        Assert.Equal(TurnVerdictTraceOutcomes.Judged, history[3].Outcome);
        Assert.False(laterTookTheGate, "the later stop took the gate while an earlier stop was still waiting for it");
    }

    [Fact]
    public async Task ACancellation_TracesUnderTheSettingsItsFlightStartedWith_AndCompletes_EvenWhenASettingsReadWouldNowThrow()
    {
        // Found in review: the cancellation read the settings again; a throw there escaped the flight's boundary and
        // left every joined caller waiting forever, and a switch flipped mid-flight changed what was recorded.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        env.SettingsOverride = () => throw new InvalidOperationException("the settings store is unreachable");
        service.OnSessionWorking(Tenant, Sid);
        release.SetResult();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);
        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }

    [Fact]
    public async Task AtShutdown_WaitingForTheFlights_LetsACancelledJudgementHandInItsTrace()
    {
        // Found in review: shutdown closed the writer without waiting for the judgements it had just cancelled, so
        // their cancelled traces arrived at a closed writer.
        var env = Env();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FinishedAnswer;
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();

        Assert.True(await service.WaitForFlightsAsync(TimeSpan.FromSeconds(5)));
        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Cancelled, trace.Outcome);
        Assert.NotNull(trace.Prompt);
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await pending).Kind);
    }

    // ================================================================= a stop that stood down

    [Fact]
    public async Task AHeldStop_LeavesASkippedTrace_WithItsCause_AndNothingFromItsScreen()
    {
        var env = Env();
        env.Held = _ => true;

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Skipped, trace.Outcome);
        Assert.Equal(ActivityCauses.Held, trace.Cause);
        Assert.Equal(ObservedAt, trace.TurnEndObservedAtUtc);
        Assert.Null(trace.VerdictId);
        Assert.Null(trace.Package);
        Assert.Null(trace.Prompt);
        Assert.Null(trace.RawReply);
        Assert.Equal(0, env.ScreenReads);
    }

    [Fact]
    public async Task TheVoicePathSkippingAHeldSession_LeavesNoTrace_BecauseItIsNotAStop()
    {
        var env = Env();
        env.Held = _ => true;

        var outcome = await new TurnVerdictService(env).VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);
        Assert.Empty(env.Traces);
    }

    [Fact]
    public async Task AnAccountWithItsJudgeSwitchOff_LeavesNoTrace_ForASkippedStop_OrForAJudgedVoiceSession()
    {
        var env = Env();
        env.Knobs = env.Knobs with { JudgeEnabled = false };
        var service = new TurnVerdictService(env);

        var skipped = await service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Skipped, skipped.Kind);

        // A voice session is still judged with the switch off, because its narration IS the verdict.
        env.VoiceSession = _ => true;
        var judged = await service.StartTurnEnd(Signal(ObservedAt.AddMinutes(1)));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);

        Assert.Empty(env.Traces);
    }

    [Fact]
    public async Task ANewStopOnAnUnchangedScreen_LeavesAReusedTrace_WithNoPromptBecauseNobodyWasAsked()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        var first = await service.StartTurnEnd(Signal());

        var later = ObservedAt.AddMinutes(2);
        var second = await service.StartTurnEnd(Signal(later));
        Assert.Equal(TurnVerdictOutcomeKind.Reused, second.Kind);

        Assert.Equal(2, env.Traces.Count);
        var reused = env.Traces.Last();
        Assert.Equal(TurnVerdictTraceOutcomes.Reused, reused.Outcome);
        Assert.Equal(first.Verdict!.VerdictId, reused.VerdictId);
        Assert.Equal(later, reused.TurnEndObservedAtUtc);
        Assert.Null(reused.Prompt);
        Assert.Null(reused.RawReply);
    }

    [Fact]
    public async Task TheVoicePathComingPastAnUnchangedScreen_LeavesNoFurtherTrace()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        await service.StartTurnEnd(Signal());

        var again = await service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(TurnVerdictOutcomeKind.Reused, again.Kind);
        Assert.Single(env.Traces);
    }

    // ================================================================= the carrying-on clock

    [Fact]
    public void ACarryingOnClockThatRunsOut_LeavesAnExpiredTrace_NamingTheVerdictItReplaced()
    {
        var now = ObservedAt;
        var env = new FakeTurnVerdictEnvironment
        {
            Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            Clock = () => now,
        };
        env.Store(Tenant, Sid, new TurnVerdictDto
        {
            VerdictId = "carry-1",
            JudgedAtUtc = ObservedAt,
            TurnEndObservedAtUtc = ObservedAt.AddSeconds(-30),
            ScreenHash = "hash-1",
            Model = FakeTurnVerdictEnvironment.Model,
            ContractVersion = TurnVerdictContract.Version,
            PackageKind = "agent-reply",
            Verdict = TurnVerdictVocabulary.ContinuesAlone,
            Confidence = "high",
            Evidence = "I will keep watching the nightly build.",
            Label = "Watching the nightly build",
            Summary = "It is watching the nightly build by itself.",
            AnswerVia = "reply",
            Risk = "none",
            Spoken = "The nightly build. It is watching the build.",
        });
        var service = new TurnVerdictService(env);

        now = ObservedAt.AddMinutes(10);
        Assert.Equal(1, service.ExpireCarryingOn(Tenant));

        var trace = Assert.Single(env.Traces);
        Assert.Equal(TurnVerdictTraceOutcomes.Expired, trace.Outcome);
        Assert.Equal("clock", trace.Trigger);
        Assert.Equal("carry-1", trace.ReplacedVerdictId);
        Assert.Equal(env.Latest(Tenant, Sid)!.VerdictId, trace.VerdictId);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, trace.Verdict!.Verdict);
        Assert.Equal(ObservedAt.AddSeconds(-30), trace.TurnEndObservedAtUtc);
        Assert.True(trace.ColourEnabled);
    }

    // ================================================================= through the production environment

    [Fact]
    public async Task ThroughTheProductionEnvironment_AWorkingEdgeClearsTheVerdict_ButTheTraceStays()
    {
        // The real environment, the real writer and the real stores, so the claim is about what the Gateway writes
        // and keeps: the verdict row goes when the session works, and the inspector's record does not.
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 1, new[]
        {
            new SessionDto { SessionId = Sid, Name = "the pushing session", Agent = "ClaudeCode", ActivityState = "WaitingForInput" },
        }));

        var db = _harness.Open();
        var traces = new TurnVerdictTraceStore(db);
        using var writer = new TurnVerdictTraceWriter(traces.Append);
        var env = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, SettleMs = 0 },
            pushedSessions: pushed,
            streamStale: TimeSpan.FromMinutes(5),
            route: (_, directorId) => RouteServing(directorId, () => Screen(Sid, ReplyText, "> ")),
            conversation: (_, _) => Reply("push it", ReplyText),
            judgeBrain: (_, _, _) => new CountingBrain(() => FinishedAnswer),
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(db),
            traces: writer,
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => false,
            narrationPlan: _ => NarrationPlan.Allowed);
        var service = new TurnVerdictService(env);

        var judged = await service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);
        Assert.NotNull(env.Latest(Tenant, Sid));

        service.OnSessionWorking(Tenant, Sid);
        await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(env.Latest(Tenant, Sid));
        Assert.Equal(0, writer.Failed);
        var trace = Assert.Single(traces.History(Tenant, Sid));
        Assert.Equal(judged.Verdict!.VerdictId, trace.VerdictId);
        Assert.Equal(TurnVerdictTraceOutcomes.Judged, trace.Outcome);
        Assert.Contains(ReplyText, trace.Package!.ScreenRows);
        Assert.False(string.IsNullOrWhiteSpace(trace.Prompt));
        Assert.Equal(FinishedAnswer, trace.RawReply);
    }
}
