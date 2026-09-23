using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.FakeTurnVerdictEnvironment;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// A FAILED READING IS ASKED AGAIN, ON ONE SCHEDULE, AND THE CARD NEVER CLAIMS AN ATTEMPT THAT IS NOT BOOKED
/// (mission "Wingman error and retry", 2026-09-19).
///
/// The root cause these pin: a failed reading of an unchanged screen was REUSED by every automatic path, so a
/// session whose model call failed once stayed failed until the agent wrote something new - while the card said
/// an attempt was coming. The schedule is one minute, one, one, five, five, five, thirty, thirty, written on the
/// stored reading itself and carried by the idle sweep through <see cref="TurnVerdictService.StartDueRetriesAsync"/>.
///
/// Every test drives the real service through the sweep's own entry point with a clock the test moves, so what is
/// proved is the caller's behaviour and not a hand-built record.
/// </summary>
public sealed class AFailedReadingIsAskedAgainOnAScheduleTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-retry";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string Spoken = "The branch is pushed and the pull request is open, so the review can start.";
    private static readonly DateTime Start = new(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public DateTime Now = Start;
        public bool JudgeFails = true;
        public readonly FakeTurnVerdictEnvironment Env;
        public readonly TurnVerdictService Verdicts;

        public Rig()
        {
            Env = new FakeTurnVerdictEnvironment
            {
                Screen = () => Screen(Sid, ReplyText, "> "),
                Conversation = _ => Reply("push it", ReplyText),
            };
            Env.Clock = () => Now;
            Env.Judge = (_, _) => JudgeFails
                ? throw new TimeoutException("The wingman model call did not answer within 30 seconds.")
                : Task.FromResult(Finished(ReplyText, Spoken));
            Verdicts = new TurnVerdictService(Env);
        }

        public Task<TurnVerdictOutcome> TurnEnds()
            => Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, Now, IsNewTurn: true));

        public TurnVerdictDto Latest() => Verdicts.Latest(Tenant, Sid)!;

        /// <summary>One pass of the idle sweep's retry question, waited to its stored result.</summary>
        public async Task<int> SweepAsync()
        {
            var before = Latest().VerdictId;
            var started = await Verdicts.StartDueRetriesAsync(Tenant);
            if (started > 0)
                Assert.True(await WaitUntil(() => Verdicts.Latest(Tenant, Sid) is { } l && l.VerdictId != before),
                    "the retry the sweep started never stored a record");
            return started;
        }
    }

    [Fact]
    public async Task AFailedReadingOnAnUnchangedScreen_IsAskedAgainWhenDue_AndNotBefore()
    {
        var rig = new Rig();
        var first = await rig.TurnEnds();
        Assert.Equal(TurnVerdictOutcomeKind.Failed, first.Kind);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Latest().RetriesMade);
        Assert.Equal(Start.AddMinutes(1), rig.Latest().NextRetryAtUtc);

        // NOT BEFORE: fifty-nine seconds on, the sweep passes and nothing is asked.
        rig.Now = Start.AddSeconds(59);
        Assert.Equal(0, await rig.SweepAsync());
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        Assert.Equal(TurnVerdictService.HostRecoveryProbePrompt, Assert.Single(rig.Env.RecoveryProbePrompts));
        Assert.Equal(TurnVerdictService.HostRecoveryProbeTimeout, Assert.Single(rig.Env.RecoveryProbeTimeouts));

        // WHEN DUE: the same unchanged screen is asked about again. This is the line that was missing.
        rig.Now = Start.AddSeconds(61);
        Assert.Equal(1, await rig.SweepAsync());
        Assert.Equal(2, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Latest().RetriesMade);
        Assert.Equal(rig.Now.AddMinutes(1), rig.Latest().NextRetryAtUtc);
    }

    [Fact]
    public async Task ATimeout_PausesTheBookedRetryUntilAProbeAnswers_WithoutLosingTheReading()
    {
        var rig = new Rig();
        rig.Env.RecoveryProbe = (_, _) => throw new TimeoutException("the small recovery call did not answer");
        await rig.TurnEnds();
        var booked = rig.Latest();

        Assert.NotNull(booked.NextRetryAtUtc);
        rig.Now = booked.NextRetryAtUtc.Value.AddSeconds(1);
        Assert.Equal(0, await rig.SweepAsync());

        // The failed probe spent no retry. The same reading, count and booking remain present and due.
        var paused = rig.Latest();
        Assert.Equal(booked.VerdictId, paused.VerdictId);
        Assert.Equal(booked.RetriesMade, paused.RetriesMade);
        Assert.Equal(booked.NextRetryAtUtc, paused.NextRetryAtUtc);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);

        // Another pass inside the probe interval spends nothing at all.
        Assert.Equal(0, await rig.SweepAsync());
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);

        // The next probe answers, so this same due reading is retried and succeeds.
        rig.Now = rig.Now.Add(TurnVerdictService.HostRecoveryProbeInterval).AddSeconds(1);
        rig.Env.RecoveryProbe = (_, _) => Task.FromResult("OK");
        rig.JudgeFails = false;
        Assert.Equal(1, await rig.SweepAsync());
        Assert.Equal(2, rig.Env.RecoveryProbeCalls);
        Assert.Equal(2, rig.Env.JudgeCalls);
        Assert.False(rig.Latest().Failed);
        Assert.Null(rig.Latest().NextRetryAtUtc);
    }

    [Fact]
    public async Task ConcurrentSweepPasses_ShareOneRecoveryProbe()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(30); // the retry is not due; only recovery is being tested
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.RecoveryProbe = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return "OK";
        };

        var first = rig.Verdicts.StartDueRetriesAsync(Tenant);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = rig.Verdicts.StartDueRetriesAsync(Tenant);

        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        release.TrySetResult();
        Assert.Equal(0, await first);
        Assert.Equal(0, await second);
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
    }

    [Fact]
    public async Task ANewerTimeout_IsNotClearedByAnOlderProbeAnswer()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(30);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.RecoveryProbe = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return "OK";
        };

        var sweep = rig.Verdicts.StartDueRetriesAsync(Tenant);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Current work is allowed through while retries are paused. Its later timeout is newer evidence than
        // this in-flight probe, so the older answer must not release the retry gate.
        var current = await rig.Verdicts.VerdictForCurrentScreenAsync(
            Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.Equal(TurnVerdictOutcomeKind.Failed, current.Kind);
        release.TrySetResult();
        Assert.Equal(0, await sweep);

        var judgeCallsAfterCurrentReading = rig.Env.JudgeCalls;
        rig.Env.RecoveryProbe = (_, _) => Task.FromResult("OK");
        Assert.Equal(0, await rig.Verdicts.StartDueRetriesAsync(Tenant));
        Assert.Equal(2, rig.Env.RecoveryProbeCalls);
        Assert.Equal(judgeCallsAfterCurrentReading, rig.Env.JudgeCalls);
    }

    [Fact]
    public async Task Shutdown_CancelsTheRecoveryProbe_AndTheDrainFinishes()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(30);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.RecoveryProbe = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        };

        var sweep = rig.Verdicts.StartDueRetriesAsync(Tenant);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rig.Verdicts.Dispose();

        Assert.Equal(0, await sweep.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await rig.Verdicts.WaitForFlightsAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
    }

    [Fact]
    public async Task ATransportFailure_DoesNotPauseRetries()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Env.Judge = (_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("the connection failed before the host could answer")
            : Task.FromResult(Finished(ReplyText, Spoken));

        await rig.TurnEnds();
        var booked = rig.Latest();
        Assert.NotNull(booked.NextRetryAtUtc);
        rig.Now = booked.NextRetryAtUtc.Value.AddSeconds(1);

        Assert.Equal(1, await rig.SweepAsync());
        Assert.Equal(0, rig.Env.RecoveryProbeCalls);
        Assert.False(rig.Latest().Failed);
    }

    [Fact]
    public async Task TheSchedule_IsOneOneOneFiveFiveFiveThirtyThirty_AndThenItStops()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        var expectedMinutes = new[] { 1, 1, 1, 5, 5, 5, 30, 30 };

        for (var retry = 0; retry < expectedMinutes.Length; retry++)
        {
            var booked = rig.Latest().NextRetryAtUtc;
            Assert.Equal(rig.Now.AddMinutes(expectedMinutes[retry]), booked);
            Assert.Equal(retry, rig.Latest().RetriesMade);

            // One second short of the booked time nothing runs; one second past it exactly one retry does.
            rig.Now = booked!.Value.AddSeconds(-1);
            Assert.Equal(0, await rig.SweepAsync());
            rig.Now = booked.Value.AddSeconds(1);
            Assert.Equal(1, await rig.SweepAsync());
        }

        // All eight are spent: nothing is booked, and no later sweep asks again, however long it waits.
        Assert.Equal(8, rig.Latest().RetriesMade);
        Assert.Null(rig.Latest().NextRetryAtUtc);
        Assert.Equal(9, rig.Env.JudgeCalls);
        rig.Now = rig.Now.AddHours(6);
        Assert.Equal(0, await rig.SweepAsync());
        Assert.Equal(9, rig.Env.JudgeCalls);

        // And the card says so - only now.
        var card = WingmanErrorFold.For(rig.Latest(), agentWorking: false)!;
        Assert.True(card.Exhausted);
        Assert.Equal(WingmanErrorFold.ExhaustedLine, card.ExhaustedText);
    }

    [Fact]
    public async Task TheFirstSuccess_EndsTheSchedule_AndASuccessfulReadingIsNeverAskedTwice()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(61);
        rig.JudgeFails = false;
        Assert.Equal(1, await rig.SweepAsync());

        var good = rig.Latest();
        Assert.False(good.Failed);
        Assert.Null(good.NextRetryAtUtc);
        Assert.Equal(0, good.RetriesMade);
        Assert.Null(WingmanErrorFold.For(good, agentWorking: false));
        Assert.Equal(2, rig.Env.JudgeCalls);

        // THE RULE THAT STAYS: a successful reading of an unchanged screen is never paid for twice - not by the
        // sweep's retry question, not by the Retry trigger asked directly, not by any other automatic trigger.
        rig.Now = rig.Now.AddHours(2);
        Assert.Equal(0, await rig.SweepAsync());
        foreach (var trigger in new[] { TurnVerdictTrigger.Retry, TurnVerdictTrigger.Sweep, TurnVerdictTrigger.Voice })
            await rig.Verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, trigger);
        Assert.Equal(2, rig.Env.JudgeCalls);
        Assert.Equal(good.VerdictId, rig.Latest().VerdictId);
    }

    [Fact]
    public async Task ANewTurn_EndsTheOldSchedule_AndAFailureOnItStartsAtTheBeginning()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(61);
        await rig.SweepAsync();
        rig.Now = rig.Now.AddSeconds(61);
        await rig.SweepAsync();
        Assert.Equal(2, rig.Latest().RetriesMade);

        // The session goes back to work: the failed record is superseded, so nothing is booked for anybody.
        rig.Verdicts.OnSessionWorking(Tenant, Sid);
        Assert.Null(rig.Verdicts.Latest(Tenant, Sid));
        rig.Now = rig.Now.AddMinutes(10);
        var callsBefore = rig.Env.JudgeCalls;
        Assert.Equal(0, await rig.Verdicts.StartDueRetriesAsync(Tenant));
        Assert.Equal(callsBefore, rig.Env.JudgeCalls);

        // The new turn ends and fails too: its schedule starts from the first minute, not where the old one was.
        await rig.TurnEnds();
        Assert.Equal(0, rig.Latest().RetriesMade);
        Assert.Equal(rig.Now.AddMinutes(1), rig.Latest().NextRetryAtUtc);
    }

    [Fact]
    public async Task APersonAsking_MakesOneAttemptNow_AndLeavesTheScheduleAsItWas()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        rig.Now = Start.AddSeconds(61);
        await rig.SweepAsync();
        var before = rig.Latest();
        Assert.Equal(1, before.RetriesMade);

        // Twenty seconds later, long before the booked retry, a person presses the button. One attempt is made at once.
        rig.Now = rig.Now.AddSeconds(20);
        var pressed = await rig.Verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.Equal(TurnVerdictOutcomeKind.Failed, pressed.Kind);
        // ONE attempt is one reading. A reading a person asked for may try the model a second time inside itself
        // (thirty seconds, then sixty) - that stays, and it is not the schedule.
        Assert.Equal(2 + TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, rig.Env.JudgeCalls);

        // It neither consumed a retry nor reset the schedule: the same count, the same booked time.
        var after = rig.Latest();
        Assert.NotEqual(before.VerdictId, after.VerdictId);
        Assert.Equal(before.RetriesMade, after.RetriesMade);
        Assert.Equal(before.NextRetryAtUtc, after.NextRetryAtUtc);

        // And a press that works ends the schedule like any success.
        rig.JudgeFails = false;
        await rig.Verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.False(rig.Latest().Failed);
        Assert.Null(rig.Latest().NextRetryAtUtc);
    }

    [Fact]
    public async Task AUsedUpSchedule_StillAnswersAPress_AndStaysUsedUp()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        while (rig.Latest().NextRetryAtUtc is { } booked)
        {
            rig.Now = booked.AddSeconds(1);
            await rig.SweepAsync();
        }
        var calls = rig.Env.JudgeCalls;

        await rig.Verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);

        Assert.Equal(calls + TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, rig.Env.JudgeCalls);
        Assert.Equal(8, rig.Latest().RetriesMade);
        Assert.Null(rig.Latest().NextRetryAtUtc);   // a press books nothing, so the card keeps saying nothing is scheduled
    }

    [Fact]
    public async Task ARateLimitsNamedWait_IsHonoured_TheNextRetryIsTheLaterOfTheTwo()
    {
        var rig = new Rig();
        rig.Env.Judge = (_, _) => throw new WingmanModelRateLimitedException("The wingman model call failed: 429 TooManyRequests.", TimeSpan.FromMinutes(4));
        await rig.TurnEnds();
        // The schedule says one minute, the provider said four: four it is.
        Assert.Equal(Start.AddMinutes(4), rig.Latest().NextRetryAtUtc);
        Assert.Equal(WingmanFailureKinds.RateLimited, rig.Latest().FailureKind);

        rig.Now = Start.AddMinutes(2);
        Assert.Equal(0, await rig.SweepAsync());

        // A named wait SHORTER than the schedule's step does not pull the retry earlier.
        var shortWait = new Rig();
        shortWait.Env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(10));
        await shortWait.TurnEnds();
        Assert.Equal(Start.AddMinutes(1), shortWait.Latest().NextRetryAtUtc);
    }

    [Fact]
    public async Task ARefusalThatIsNotAboutTheButtons_StillFails_AndIsStillRetried()
    {
        // An unknown state word is not a button rule: the whole answer is refused, the card shows the error, and the
        // schedule asks again. Only a bad BUTTON LIST is forgiven (see the contract tests).
        var rig = new Rig();
        var answers = new Queue<string>(new[] { RefusedButReadableMenu("Do you want to proceed?"), "Sure! The session finished." });
        rig.Env.Judge = (_, _) => Task.FromResult(answers.Count > 0 ? answers.Dequeue() : Finished(ReplyText, Spoken));

        await rig.TurnEnds();
        Assert.True(rig.Latest().Failed);
        Assert.Equal(WingmanFailureKinds.Refused, rig.Latest().FailureKind);
        Assert.NotNull(rig.Latest().NextRetryAtUtc);

        rig.Now = rig.Latest().NextRetryAtUtc!.Value.AddSeconds(1);
        Assert.Equal(1, await rig.SweepAsync());          // the unreadable reply: fails again, booked again
        Assert.True(rig.Latest().Failed);
        Assert.Equal(1, rig.Latest().RetriesMade);

        rig.Now = rig.Latest().NextRetryAtUtc!.Value.AddSeconds(1);
        Assert.Equal(1, await rig.SweepAsync());          // and the third answer is good
        Assert.False(rig.Latest().Failed);
    }

    [Fact]
    public async Task ABadButtonList_IsNotAFailure_TheReadingIsKeptAndNarrated_AndNothingIsRetried()
    {
        // The model answered with ONE option, which breaks a button rule. The buttons go; the reading stays.
        var rig = new Rig();
        rig.Env.VoiceSession = _ => true;
        rig.Env.Judge = (_, _) => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            state = "needs-you",
            label = "Choose whether to proceed",
            agentRecommends = (string?)null,
            menu = new { question = "Do you want to proceed?", selectionMode = "single", submit = "" },
            options = new object[] { new { key = "Proceed", send = "1", recommended = true, note = "Carries on." } },
        }));
        rig.Env.Narrator = (_, _) => Task.FromResult(NarratedAnswer(Spoken));

        var outcome = await rig.TurnEnds();

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        var kept = rig.Latest();
        Assert.False(kept.Failed);
        Assert.Equal(TurnVerdictVocabulary.NeededYou, kept.Verdict);
        Assert.Empty(kept.Options);
        Assert.Null(kept.Menu);
        Assert.False(string.IsNullOrEmpty(kept.OptionsDroppedReason));   // recorded, for the debug view
        Assert.Null(kept.NextRetryAtUtc);
        Assert.Null(WingmanErrorFold.For(kept, agentWorking: false));    // and no error on the card
        Assert.Equal(1, rig.Env.JudgeCalls);
        rig.Now = rig.Now.AddHours(1);
        Assert.Equal(0, await rig.SweepAsync());
    }

    [Fact]
    public async Task AReadingWhoseWriteUpFailed_ShowsTheSameError_AndIsReadAgainOnTheSameSchedule()
    {
        // The judge answered; the second call, which writes the words, did not. The owner is owed words and has
        // none, so it is the same tag and the same schedule - not a second kind of failure with its own rules.
        var rig = new Rig { JudgeFails = false };
        rig.Env.VoiceSession = _ => true;
        var narratorFails = true;
        rig.Env.Narrator = (_, _) => narratorFails
            ? throw new TimeoutException("The narration call did not answer.")
            : Task.FromResult(NarratedAnswer(Spoken));

        await rig.TurnEnds();

        var first = rig.Latest();
        Assert.False(first.Failed);
        Assert.NotNull(first.NarrationFailureReason);
        Assert.Equal(Start.AddMinutes(1), first.NextRetryAtUtc);
        Assert.Equal("Wingman error", WingmanErrorFold.For(first, agentWorking: false)!.Tag);

        narratorFails = false;
        rig.Now = Start.AddSeconds(61);
        Assert.Equal(1, await rig.SweepAsync());
        Assert.Equal(1, rig.Env.RecoveryProbeCalls);
        Assert.Null(rig.Latest().NarrationFailureReason);
        Assert.Null(rig.Latest().NextRetryAtUtc);
        Assert.Null(WingmanErrorFold.For(rig.Latest(), agentWorking: false));
    }

    [Fact]
    public async Task ABookedRetryThatCanNeverRun_IsWithdrawn_SoTheCardStopsPromisingIt()
    {
        var rig = new Rig();
        await rig.TurnEnds();
        Assert.NotNull(rig.Latest().NextRetryAtUtc);

        // The account turns the judge off and nobody is listening: no retry will ever be allowed to run.
        rig.Env.Knobs = rig.Env.Knobs with { JudgeEnabled = false };
        rig.Env.RecoveryProbe = (_, _) => throw new TimeoutException("the model host is still stalled");
        rig.Now = Start.AddMinutes(2);
        Assert.Equal(0, await rig.Verdicts.StartDueRetriesAsync(Tenant));

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.RecoveryProbeCalls);
        Assert.Null(rig.Latest().NextRetryAtUtc);
        Assert.True(WingmanErrorFold.For(rig.Latest(), agentWorking: false)!.Exhausted);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void TheCard_SaysNothingMoreIsScheduled_ExactlyWhenNothingIsBooked(int retriesMade, bool booked)
    {
        var reading = new TurnVerdictDto
        {
            VerdictId = "v1",
            Failed = true,
            FailureKind = WingmanFailureKinds.DidNotAnswer,
            FailureReason = "the judge did not answer",
            RetriesMade = retriesMade,
            NextRetryAtUtc = booked ? Start.AddMinutes(1) : null,
        };

        var card = WingmanErrorFold.For(reading, agentWorking: false)!;

        Assert.Equal("Wingman error", card.Tag);
        Assert.Equal("Ask again", card.AskAgainLabel);
        Assert.Equal(!booked, card.Exhausted);
        Assert.Equal(reading.NextRetryAtUtc, card.NextRetryAtUtc);
        if (booked)
        {
            Assert.Equal("", card.ExhaustedText);
            Assert.Equal($"retry {retriesMade + 1} of 8", card.RetryLabel);
            Assert.Equal(8 - retriesMade, card.RetriesRemaining);
        }
        else
        {
            Assert.Equal("The Wingman could not read this stop. Nothing more is scheduled.", card.ExhaustedText);
            Assert.Equal("", card.RetryLabel);
            Assert.Equal(0, card.RetriesRemaining);
        }
        // A plain reason that never names a provider or a model.
        Assert.False(string.IsNullOrWhiteSpace(card.Reason));

        // While the agent works there is no stop to read, so there is no error to show.
        Assert.Null(WingmanErrorFold.For(reading, agentWorking: true));
    }
}
