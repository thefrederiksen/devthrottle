using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A STOP THAT ARRIVES RIGHT AFTER A CANCELLED JUDGEMENT IS JUDGED, NOT LOST (the turn pipeline mission, issue #3399).
///
/// <see cref="TurnVerdictService.OnSessionWorking"/> cancels the running judgement, and that judgement keeps the session's
/// gate until it has written its rows. Its joined list used to stay open, so a turn end arriving in that window JOINED
/// the judgement the Working edge had just cancelled: it came back "already judging" and was never read, judged or
/// narrated. In production that lost the colour and the voice for a short turn ending while the previous stop was still
/// in its settle wait or its model call, and it kept VoiceServingLoopIsolationTests red on some main runs. A cancelled
/// judgement is now closed to joins, so the later stop queues and is handed the gate when the cancelled judgement leaves.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class ACancelledJudgementTakesNoJoinsTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-cancelled-join";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string Spoken = "The branch is pushed and the pull request is open.";
    private static readonly DateTime ObservedAt = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static TurnEndSignal Signal(int minute) => new(Sid, "dir-1", Tenant, ObservedAt.AddMinutes(minute), IsNewTurn: true);

    /// <summary>An environment whose FIRST judge call is held until released, and ignores cancellation while held - so
    /// the judgement it belongs to is certain to still hold the gate when the next stop arrives.</summary>
    private static (FakeTurnVerdictEnvironment Env, TaskCompletionSource Entered, TaskCompletionSource Release) BlockedFirstJudge()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => Screen(Sid, ReplyText, "> "),
            Conversation = _ => Reply("push it", ReplyText),
            Narrator = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.DefaultNarration()),
        };
        env.Judge = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
            return FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken);
        };
        return (env, entered, release);
    }

    [Fact]
    public async Task StartTurnEnd_AStopArrivingAfterTheWorkingEdgeCancelledTheRunningJudgement_IsJudgedAndNarratedOnce()
    {
        var (env, entered, release) = BlockedFirstJudge();
        using var service = new TurnVerdictService(env);

        var first = service.StartTurnEnd(Signal(0));
        await entered.Task.WaitAsync(Wait);
        service.OnSessionWorking(Tenant, Sid);
        var second = service.StartTurnEnd(Signal(1));

        // The cancelled judgement still holds the gate: its judge call is held. The second stop must NOT have been
        // answered "already judging" - that is the stop being lost.
        if (second.IsCompleted)
            Assert.False((await second).SkipCause == ActivityCauses.AlreadyJudging,
                "the stop after the Working edge joined the judgement that edge had just cancelled, and will never be judged");

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, (await first.WaitAsync(Wait)).Kind);
        var outcome = await second.WaitAsync(Wait);
        Assert.True(await service.WaitForFlightsAsync(Wait));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        // Two judge calls: the cancelled judgement's own, and exactly one for the later stop.
        Assert.Equal(2, env.JudgeCalls);
        // Narrated exactly once - the later stop. The cancelled judgement never reached its narration call.
        Assert.Equal(1, env.NarratorCalls);
        var stored = env.Latest(Tenant, Sid);
        Assert.NotNull(stored);
        Assert.False(stored!.Failed);
        Assert.Equal(ObservedAt.AddMinutes(1), stored.TurnEndObservedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(stored.Narration));
        // The later stop's own row says judged, once; nothing records it as joined.
        var rows = env.Traces.Where(t => t.Trigger == "turn-end" && t.TurnEndObservedAtUtc == ObservedAt.AddMinutes(1)).ToList();
        var row = Assert.Single(rows);
        Assert.NotEqual(TurnVerdictTraceOutcomes.Joined, row.Outcome);
    }

    [Fact]
    public async Task VerdictForCurrentScreenAsync_AfterTheWorkingEdgeAndTheNextStop_WaitsForTheNextStopsVerdict_NotTheCancelledOne()
    {
        // The voice path's shape: a stop after the Working edge, then a request for the current screen's words. It is
        // handed the verdict of the judgement that covers the new stop, not the cancellation of the one before it.
        var (env, entered, release) = BlockedFirstJudge();
        using var service = new TurnVerdictService(env);

        var first = service.StartTurnEnd(Signal(0));
        await entered.Task.WaitAsync(Wait);
        service.OnSessionWorking(Tenant, Sid);
        var second = service.StartTurnEnd(Signal(1));
        var voice = service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        release.SetResult();
        await first.WaitAsync(Wait);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await second.WaitAsync(Wait)).Kind);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await voice.WaitAsync(Wait)).Kind);
        Assert.True(await service.WaitForFlightsAsync(Wait));
        // One model call for the later stop, shared by the voice request.
        Assert.Equal(2, env.JudgeCalls);
    }

    [Fact]
    public async Task StartTurnEnd_AStopDuringARunningJudgementThatWasNotCancelled_StillJoinsIt_AndIsNotJudgedTwice()
    {
        // CONTROL: closing a judgement to joins is only for a cancelled one. With no Working edge, a second stop during a
        // running judgement joins it and asks nothing of its own - one model call per stop.
        var (env, entered, release) = BlockedFirstJudge();
        using var service = new TurnVerdictService(env);

        var first = service.StartTurnEnd(Signal(0));
        await entered.Task.WaitAsync(Wait);
        var second = await service.StartTurnEnd(Signal(1)).WaitAsync(Wait);

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, second.Kind);
        Assert.Equal(ActivityCauses.AlreadyJudging, second.SkipCause);

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await first.WaitAsync(Wait)).Kind);
        Assert.True(await service.WaitForFlightsAsync(Wait));
        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(1, env.NarratorCalls);
        Assert.Single(env.Traces, t => t.Trigger == "turn-end" && t.TurnEndObservedAtUtc == ObservedAt.AddMinutes(1)
                                       && t.Outcome == TurnVerdictTraceOutcomes.Joined);
    }
}
