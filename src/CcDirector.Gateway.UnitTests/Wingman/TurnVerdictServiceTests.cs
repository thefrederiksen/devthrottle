using CcDirector.Core.Tenancy;
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
/// The turn-verdict seat (the Wingman-on-every-turn mission, slice C): what it reads, what it asks, what it
/// stores, and above all what it refuses to touch.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
///
/// Every screen and conversation below is written from scratch. Not a byte of a real session is here.
/// </summary>
public sealed class TurnVerdictServiceTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-1";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private static readonly DateTime ObservedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static TurnEndSignal Signal(string sid = Sid, DateTime? at = null)
        => new(sid, "dir-1", Tenant, at ?? ObservedAt, IsNewTurn: true);

    private static FakeTurnVerdictEnvironment Env() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
        Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, "The retention sweep. The branch is pushed and nothing is waiting on you.")),
    };

    // ================================================================= the held check

    [Fact]
    public async Task HeldSession_ResolvedThroughTheRealPushStoreIngest_IsSkippedAsHeld_WithNoScreenReadAndNoModelCall_ThenJudgedOnceItsOwnerExits()
    {
        // Built through the REAL ingest, with the production environment over it - the one test in this file
        // that must not use the fake, because the defect it guards is in the path from the push store to the
        // answer. The pushed row arrives claiming it is held; the ingest discards that claim.
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 1, new[] { Owner("WaitingForInput"), Child() }));

        // CONTROL: the stored row itself says "not held". A check that read the row would read this worker.
        Assert.False(pushed.TryLocate(Tenant, "child-1", Stale)!.Value.Session.HasLiveSupervisor);

        var screenReads = 0;
        var brain = new CountingBrain(() => FakeTurnVerdictEnvironment.CannotTell("The child session stopped."));
        var records = new List<TurnVerdictRecord>();
        var env = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, SettleMs = 0 },
            pushedSessions: pushed,
            streamStale: Stale,
            route: (_, directorId) => RouteServing(directorId, () => Screen("child-1", "Working on the migration.", "> "),
                onRead: () => Interlocked.Increment(ref screenReads)),
            conversation: (_, _) => null,
            judgeBrain: (_, _) => brain,
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(_harness.Open()),
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => false);
        var service = new TurnVerdictService(env);

        var held = await service.StartTurnEnd(Signal("child-1"));

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, held.Kind);
        Assert.Equal(ActivityCauses.Held, held.SkipCause);
        Assert.Equal(0, screenReads);
        Assert.Equal(0, brain.Asks);
        Assert.Null(env.Latest(Tenant, "child-1"));

        // Its owner exits. Nothing about the child changed - only the liveness of the session that owned it.
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 2, new[] { Owner("Exited"), Child() }));

        var judged = await service.StartTurnEnd(Signal("child-1", ObservedAt.AddMinutes(1)));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);
        Assert.Equal(1, screenReads);
        Assert.Equal(1, brain.Asks);
        Assert.NotNull(env.Latest(Tenant, "child-1"));
    }

    private static SessionDto Owner(string state) => new()
    {
        SessionId = "owner-1",
        Name = "the owning session",
        Agent = "ClaudeCode",
        ActivityState = state,
    };

    private static SessionDto Child() => new()
    {
        SessionId = "child-1",
        Name = "the owned session",
        Agent = "ClaudeCode",
        ActivityState = "WaitingForInput",
        IsControlled = true,
        ControllerSessionId = "owner-1",
        HasLiveSupervisor = true,   // an inbound claim the ingest must discard
    };

    // ================================================================= the other free checks

    [Fact]
    public async Task BrandNewSession_IsSkipped_WithNoReadAndNoCall()
    {
        var env = Env();
        env.Facts = sid => new SessionDto { SessionId = sid, ActivityState = "WaitingForInput", IsBrandNew = true };
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        AssertSkippedBeforeAnything(env, outcome, ActivityCauses.BrandNew);
    }

    [Theory]
    [InlineData("Exited", false)]
    [InlineData("WaitingForInput", true)]
    public async Task ExitedOrCrashedSession_IsSkipped_WithNoReadAndNoCall(string state, bool crashed)
    {
        var env = Env();
        env.Facts = sid => new SessionDto { SessionId = sid, ActivityState = state, Crashed = crashed };
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        AssertSkippedBeforeAnything(env, outcome, ActivityCauses.SessionExit);
    }

    [Fact]
    public async Task JudgeSwitchOff_SkipsAStopNobodyIsListeningTo()
    {
        var env = Env();
        env.Knobs = env.Knobs with { JudgeEnabled = false };
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        AssertSkippedBeforeAnything(env, outcome, ActivityCauses.JudgeSwitchOff);
    }

    [Fact]
    public async Task JudgeSwitchOff_StillJudgesAVoiceSession_BecauseItsNarrationIsTheVerdict()
    {
        // INFERRED RULING, pinned so it can be reversed on purpose rather than by accident: the judge switch
        // defaults off, and a voice session's only narration is the verdict's spoken section. Honouring the
        // switch here would silence voice for every account that has not opted into judging.
        var env = Env();
        env.Knobs = env.Knobs with { JudgeEnabled = false };
        env.VoiceSession = _ => true;
        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        Assert.Equal(1, env.JudgeCalls);
    }

    private static void AssertSkippedBeforeAnything(FakeTurnVerdictEnvironment env, TurnVerdictOutcome outcome, string cause)
    {
        Assert.Equal(TurnVerdictOutcomeKind.Skipped, outcome.Kind);
        Assert.Equal(cause, outcome.SkipCause);
        Assert.Equal(0, env.ScreenReads);
        Assert.Equal(0, env.JudgeCalls);
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictSkipped && r.Cause == cause);
    }

    // ================================================================= reuse on an unchanged screen

    [Fact]
    public async Task UnchangedScreen_ReusesTheStoredVerdict_WithNoModelCall_AndAChangedScreenIsJudgedAgain()
    {
        var env = Env();
        var service = new TurnVerdictService(env);

        var first = await service.StartTurnEnd(Signal());
        Assert.Equal(TurnVerdictOutcomeKind.Judged, first.Kind);
        Assert.Equal(1, env.JudgeCalls);

        var later = ObservedAt.AddMinutes(2);
        var second = await service.StartTurnEnd(Signal(at: later));
        Assert.Equal(TurnVerdictOutcomeKind.Reused, second.Kind);
        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(first.Verdict!.VerdictId, second.Verdict!.VerdictId);
        // The join key moves to the stop that reused it, so that stop's turn-log record still pairs with a row.
        Assert.Equal(later, env.Latest(Tenant, Sid)!.TurnEndObservedAtUtc);

        env.Screen = () => Screen(Sid, "Something new happened on the screen.", "> ");
        var third = await service.StartTurnEnd(Signal(at: later.AddMinutes(1)));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, third.Kind);
        Assert.Equal(2, env.ScreenReads + 0 - 1);   // three reads in all, the third on the changed screen
        Assert.Equal(2, env.JudgeCalls);
    }

    // ================================================================= the ceiling

    [Theory]
    [InlineData(2)]
    [InlineData(null)]   // the shipped default, carried from the settings into the service untouched
    public async Task InFlightCeilingReached_TheNextStopIsSkippedAndCounted(int? ceiling)
    {
        var env = Env();
        if (ceiling is { } value) env.Knobs = env.Knobs with { MaxInFlight = value };
        var expected = env.Knobs.MaxInFlight;
        // A ceiling of one would be satisfied by a service that ignored the setting and stopped at one.
        Assert.NotEqual(1, expected);
        if (ceiling is null) Assert.Equal(TurnVerdictSettings.Defaults.MaxInFlight, expected);

        var release = new TaskCompletionSource();
        env.Judge = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeTurnVerdictEnvironment.CannotTell("The session stopped.");
        };
        var service = new TurnVerdictService(env);

        var inFlight = Enumerable.Range(0, expected).Select(i => service.StartTurnEnd(Signal($"sid-{i}"))).ToList();
        Assert.True(await WaitUntil(() => env.JudgeCalls == expected), $"only {env.JudgeCalls} of {expected} judgements reached the judge");

        var over = await service.StartTurnEnd(Signal("sid-over"));

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, over.Kind);
        Assert.Equal(ActivityCauses.InFlightCap, over.SkipCause);
        Assert.Equal(1, service.CapSkips);
        Assert.Equal(expected, env.JudgeCalls);

        release.SetResult();
        foreach (var judgement in inFlight)
            Assert.Equal(TurnVerdictOutcomeKind.Judged, (await judgement).Kind);
    }

    // ================================================================= the settle wait and the checks around it

    [Fact]
    public async Task TheSettleWait_HappensBeforeTheScreenRead_AndTheSessionIsResolvedAgainBetweenThem()
    {
        var env = Env();
        env.Knobs = env.Knobs with { SettleMs = 1500 };

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        // The wait carries the settings' value, it comes before the one read, and the roster is read again
        // after it and before the screen.
        Assert.Equal(new[] { "state", "settle:1500", "state", "screen" }, env.Steps.ToArray());
    }

    /// <summary>
    /// The inspector's probe for finding 2, kept as the test. An automatic request for a session whose facts say
    /// Working was judged: one screen read and one judge call about a turn still in progress.
    /// </summary>
    [Theory]
    [InlineData(TurnVerdictTrigger.Voice)]
    [InlineData(TurnVerdictTrigger.Sweep)]
    [InlineData(TurnVerdictTrigger.TurnEnd)]
    public async Task AnAutomaticRequest_ForASessionThatIsWorking_IsSkipped_WithNoReadAndNoCall(TurnVerdictTrigger trigger)
    {
        var env = Env();
        env.Facts = sid => new SessionDto { SessionId = sid, Agent = "ClaudeCode", ActivityState = "Working" };
        var service = new TurnVerdictService(env);

        var outcome = trigger == TurnVerdictTrigger.TurnEnd
            ? await service.StartTurnEnd(Signal())
            : await service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, trigger);

        AssertSkippedBeforeAnything(env, outcome, ActivityCauses.WorkingObservation);
    }

    /// <summary>
    /// The inspector's probe for finding 3, kept as the test. The session was not held when it was first checked
    /// and was held by the time its screen was read; the turn end was judged anyway, one read and one call.
    /// Run with no settle wait (the probe's own shape: held changes straight after the first answer) and with one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task ASessionThatBecomesHeldAfterTheFirstCheck_IsSkippedAsHeld_BeforeItsScreenIsRead(int settleMs)
    {
        var env = Env();
        env.Knobs = env.Knobs with { SettleMs = settleMs };
        env.Held = _ => env.StateReads > 1;   // "not held" to the first snapshot, "held" to every later one

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal());

        AssertSkippedBeforeAnything(env, outcome, ActivityCauses.Held);
        // CONTROL: the first check really did answer "not held" - the request went on to a second snapshot.
        Assert.Equal(2, env.StateReads);
        Assert.Equal(settleMs > 0 ? new[] { "state", $"settle:{settleMs}", "state" } : new[] { "state", "state" },
            env.Steps.ToArray());
    }

    /// <summary>
    /// The inspector's probe for finding 4, kept as the test. A Working edge landing just after the stored verdict
    /// was read used to be reused anyway, and the refreshed copy stored under the NEW epoch - an old verdict
    /// restored over a session that had gone back to work.
    /// </summary>
    [Fact]
    public async Task WorkingLandingJustAfterTheStoredVerdictIsRead_CancelsTheReuse_AndRestoresNothing()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await service.StartTurnEnd(Signal())).Kind);
        Assert.NotNull(env.Latest(Tenant, Sid));   // CONTROL: there is a verdict about this unchanged screen

        env.AfterNextLatest = () => service.OnSessionWorking(Tenant, Sid);
        var outcome = await service.StartTurnEnd(Signal(at: ObservedAt.AddMinutes(2)));

        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);
        Assert.Null(outcome.Verdict);
        Assert.Equal(0, env.StoredCount(Tenant, Sid));
        Assert.Equal(1, env.JudgeCalls);
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictCancelled);
        Assert.DoesNotContain(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictReused);
    }

    // ================================================================= an exception nobody expected

    [Fact]
    public async Task AnExceptionFromTheStore_LeavesAFailedRecordNamingItsType_AndALedgerEvent()
    {
        var env = Env();
        env.NextStoreThrows = new IOException("the database file is locked");
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(Signal());

        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(TurnVerdictFailureKind.Unavailable, outcome.Failure);
        Assert.False(outcome.HasAcceptedVerdict);
        Assert.Equal(1, env.JudgeCalls);   // the judge answered; it was the store that threw

        var stored = env.Latest(Tenant, Sid);
        Assert.NotNull(stored);
        Assert.True(stored!.Failed);
        Assert.Equal("", stored.Verdict);
        Assert.Equal("the verdict could not be formed: System.IO.IOException", stored.FailureReason);
        Assert.Equal(stored.VerdictId, outcome.Verdict!.VerdictId);
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictFailed
                                          && r.Cause == ActivityCauses.JudgeUnavailable
                                          && r.Detail.Contains("exception=IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictJudged);
        Assert.False(service.IsReading(Tenant, Sid));
    }

    // ================================================================= failures never move a row

    [Fact]
    public Task JudgeUnreachable_StoresAFailedRecord_AndNoVerdict()
        => AssertFailed(
            (_, _) => throw new HttpRequestException("No connection could be made."),
            TurnVerdictFailureKind.DidNotAnswer, "the judge did not answer");

    [Fact]
    public Task JudgeTimesOut_StoresAFailedRecord_AndNoVerdict()
        => AssertFailed(
            (_, _) => throw new TimeoutException("The wingman model call did not answer within 30 seconds."),
            TurnVerdictFailureKind.DidNotAnswer, "within 30 seconds");

    [Fact]
    public Task JudgeRateLimited_StoresAFailedRecord_AndCarriesTheProvidersWait()
        => AssertFailed(
            (_, _) => throw new WingmanModelRateLimitedException("The wingman model call failed: 429 TooManyRequests.", TimeSpan.FromSeconds(40)),
            TurnVerdictFailureKind.RateLimited, "rate limited", expectedRetryAfter: TimeSpan.FromSeconds(40));

    [Fact]
    public Task UnparseableAnswer_StoresAFailedRecord_AndNoVerdict()
        => AssertFailed(
            (_, _) => Task.FromResult("Sure! The session finished."),
            TurnVerdictFailureKind.Refused, "not valid JSON");

    [Fact]
    public Task ReceiptNotOnTheReplyOrTheScreen_StoresAFailedRecord_AndNoVerdict()
        => AssertFailed(
            (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished("I have pushed the branch and merged it.", "Done.")),
            TurnVerdictFailureKind.Refused, "not found verbatim");

    [Fact]
    public Task UnknownRiskWord_StoresAFailedRecord_AndNoVerdict()
        => AssertFailed(
            (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, "Done.", risk: "catastrophic")),
            TurnVerdictFailureKind.Refused, "unknown risk word");

    private static async Task AssertFailed(
        Func<string, CancellationToken, Task<string>> judge,
        TurnVerdictFailureKind expected,
        string reasonFragment,
        TimeSpan? expectedRetryAfter = null)
    {
        var env = Env();
        env.Judge = judge;
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(Signal());

        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(expected, outcome.Failure);
        Assert.False(outcome.HasAcceptedVerdict);
        Assert.Equal(expectedRetryAfter, outcome.RetryAfter);
        Assert.Equal(1, env.JudgeCalls);

        // A failed record IS stored - silence is never a decision - and it carries no verdict word, so the row
        // stays exactly as the detector left it.
        var stored = env.Latest(Tenant, Sid);
        Assert.NotNull(stored);
        Assert.True(stored!.Failed);
        Assert.Equal("", stored.Verdict);
        Assert.Contains(reasonFragment, stored.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FakeTurnVerdictEnvironment.Model, stored.Model);
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictFailed);
        Assert.False(service.IsReading(Tenant, Sid));
    }

    // ================================================================= a Working edge ends the verdict

    [Fact]
    public async Task WorkingDuringTheRead_CancelsTheJudgement_ClearsReading_AndInvalidatesTheStoredVerdict()
    {
        var env = Env();
        var service = new TurnVerdictService(env);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await service.StartTurnEnd(Signal())).Kind);
        Assert.NotNull(env.Latest(Tenant, Sid));   // CONTROL: there is a verdict to invalidate

        env.Screen = () => Screen(Sid, "A later stop on a different screen.", "> ");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };

        var pending = service.StartTurnEnd(Signal(at: ObservedAt.AddMinutes(3)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsReading(Tenant, Sid));   // CONTROL: it really is mid-read

        service.OnSessionWorking(Tenant, Sid);

        Assert.False(service.IsReading(Tenant, Sid));
        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);
        Assert.Null(env.Latest(Tenant, Sid));
        Assert.Equal(0, env.StoredCount(Tenant, Sid));
        Assert.Contains(env.Records, r => r.EventType == ActivityEventTypes.TurnVerdictCancelled);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheSessionWorked_IsNotStored()
    {
        // The judge here ignores cancellation and answers anyway, the way a slow provider does.
        var env = Env();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Judge = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return FakeTurnVerdictEnvironment.Finished(ReplyText, "Done.");
        };
        var service = new TurnVerdictService(env);

        var pending = service.StartTurnEnd(Signal());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.OnSessionWorking(Tenant, Sid);
        release.SetResult();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(0, env.StoredCount(Tenant, Sid));
    }

    // ================================================================= one stop, one read

    [Fact]
    public async Task TwoTurnEndsRacingOnOneSession_ReadTheScreenOnce()
    {
        var env = Env();
        var release = new TaskCompletionSource();
        env.Judge = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeTurnVerdictEnvironment.Finished(ReplyText, "Done.");
        };
        var service = new TurnVerdictService(env);

        var first = service.StartTurnEnd(Signal());
        Assert.True(await WaitUntil(() => env.JudgeCalls == 1));
        var second = await service.StartTurnEnd(Signal(at: ObservedAt.AddSeconds(15)));

        Assert.Equal(TurnVerdictOutcomeKind.Skipped, second.Kind);
        Assert.Equal(ActivityCauses.AlreadyJudging, second.SkipCause);

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await first).Kind);
        Assert.Equal(1, env.ScreenReads);
        Assert.Equal(1, env.JudgeCalls);
    }

    [Fact]
    public async Task AVerdictRequestDuringATurnEndJudgement_JoinsIt_AndAsksNothingOfItsOwn()
    {
        var env = Env();
        var release = new TaskCompletionSource();
        env.Judge = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeTurnVerdictEnvironment.Finished(ReplyText, "Done.");
        };
        var service = new TurnVerdictService(env);

        var turnEnd = service.StartTurnEnd(Signal());
        var joined = service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice,
            _ => throw new InvalidOperationException("a joined request must not read the screen itself"));
        release.SetResult();

        var a = await turnEnd;
        var b = await joined;
        Assert.Equal(a.Verdict!.VerdictId, b.Verdict!.VerdictId);
        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(1, env.ScreenReads);
    }

    [Fact]
    public async Task ATurnEndObservedWhileAVerdictIsInFlight_StampsItsObservedMomentOnThatVerdict()
    {
        // Found on the rig: the idle sweep began judging a stop before the detector's reconcile tick observed it.
        // The tick's turn end was dropped by the gate - rightly, one call per stop - and the stored verdict kept
        // the previous stop's moment, so the join key into the turn log pointed at the wrong stop.
        var env = Env();
        var release = new TaskCompletionSource();
        env.Judge = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeTurnVerdictEnvironment.Finished(ReplyText, "Done.");
        };
        var service = new TurnVerdictService(env);

        var sweep = service.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Sweep);
        Assert.True(await WaitUntil(() => env.JudgeCalls == 1));
        var observed = DateTime.UtcNow.AddMinutes(1);
        var dropped = await service.StartTurnEnd(Signal(at: observed));
        Assert.Equal(ActivityCauses.AlreadyJudging, dropped.SkipCause);   // control: one call for the stop

        release.SetResult();
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await sweep).Kind);
        Assert.Equal(observed, env.Latest(Tenant, Sid)!.TurnEndObservedAtUtc);
        Assert.Equal(1, env.JudgeCalls);
    }

    // ================================================================= what the verdict feeds

    [Fact]
    public async Task AMenuVerdict_FeedsTheScreenCache_UnderTheFullGridHash()
    {
        const string sid = "sid-menu-cache";
        string[] rows = { "Do you want to proceed?", "> 1. Yes", "  2. No" };
        var env = Env();
        env.Screen = () => Screen(sid, rows);
        env.Conversation = _ => null;
        env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu(
            "Proceed with the change?", "Do you want to proceed?", "The migration session is asking whether to proceed."));

        var outcome = await new TurnVerdictService(env).StartTurnEnd(Signal(sid));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        Assert.True(WingmanScreenVerdictCache.TryGet($"{Tenant}/{sid}", WingmanScreenVerdictCache.HashRows(rows), out var needs));
        Assert.Equal("menu", needs);
    }

    [Fact]
    public async Task ThePromptTheJudgeIsAsked_CarriesTheAccountsLanguage()
    {
        var env = Env();
        env.LanguageValue = SpokenLanguages.French;
        await new TurnVerdictService(env).StartTurnEnd(Signal());

        var prompt = Assert.Single(env.Prompts);
        Assert.Contains(SpeechContract.SpeakInLanguageRule(SpokenLanguages.French), prompt);
        Assert.DoesNotContain(SpeechContract.SpeakInLanguageRule(SpokenLanguages.English), prompt);
    }
}
