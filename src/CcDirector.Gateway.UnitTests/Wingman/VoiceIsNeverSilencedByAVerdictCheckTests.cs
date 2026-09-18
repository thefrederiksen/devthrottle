using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Slice I of the Wingman-on-every-turn mission (owner ruling, 16 September 2026): a verdict check never silences
/// voice, and a stop somebody is listening to gets one re-attempt when the judge gave no words at all.
///
/// Of the last 100 stops on 16 September, 29 had no narration because a field check refused the whole answer, and
/// 24 of those carried a readable spoken field that was thrown away.
///
/// CONTRACT v3 REMOVED THE CAUSE RATHER THAN THE SYMPTOM (owner ruling, 18 September 2026). The receipt check
/// that refused those 24 answers is gone, and so is the "spoken" field they carried: the judge answers no prose
/// at all now, and the words come from the narration call inside the same reading. So the tests that proved a
/// REFUSED answer's words were still played have become tests that the same answer is no longer refused - the
/// stronger claim, and the one the measured 40 percent failure rate asked for. What is unchanged is the other
/// half: an answer this contract still refuses carries no words, stamps the row refused, and is silent.
///
/// The turn-end sequence is the host's own, in the host's order, exactly as <see cref="TurnVerdictVoiceMergeTests"/>
/// drives it: the verdict seat first, then the voice refresh.
/// </summary>
public sealed class VoiceIsNeverSilencedByAVerdictCheckTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-voice";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string Spoken = "The branch is pushed and the pull request is open, so the review can start.";
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vnsv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingSpeech : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9, 8, 7 }) });
        }
    }

    /// <summary>The production row stamp's inputs, read off the same fake store the seat wrote to.</summary>
    private sealed class RowsOver : ITurnVerdictRowSource
    {
        private readonly FakeTurnVerdictEnvironment _env;
        private readonly TurnVerdictService _verdicts;
        public RowsOver(FakeTurnVerdictEnvironment env, TurnVerdictService verdicts) { _env = env; _verdicts = verdicts; }
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _env.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => _verdicts.IsReading(tenant, sessionId);
    }

    private sealed record Rig(WingmanVoiceService Voice, TurnVerdictService Verdicts, FakeTurnVerdictEnvironment Env, CountingSpeech Speech);

    private Rig Build()
    {
        Directory.CreateDirectory(_dir);
        var vault = new KeyVault(Path.Combine(_dir, "keys.vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => Screen(Sid, ReplyText, "> "),
            Conversation = _ => Reply("push it", ReplyText),
        };
        var verdicts = new TurnVerdictService(env);
        var speech = new CountingSpeech();
        var translator = new CountingBrain(() => "a translation nobody should have asked for");
        var voice = new WingmanVoiceService((_, _, _) => Task.FromResult<IAgentBrain>(translator), vault, settings,
            Path.Combine(_dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(speech), turnVerdicts: verdicts);
        env.VoiceSession = sid => voice.IsVoiceSession(Tenant, sid);
        return new Rig(voice, verdicts, env, speech);
    }

    private static async Task HostTurnEndAsync(Rig rig, SessionVerbClient route)
    {
        var signal = new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);
        var judging = rig.Verdicts.StartTurnEnd(signal);
        if (rig.Voice.IsVoiceSession(signal.Tenant, signal.SessionId) && !rig.Verdicts.IsHeld(signal.Tenant, signal.SessionId))
            await rig.Voice.GenerateAsync(signal.Tenant, signal.SessionId, route, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
        await judging;
    }

    /// <summary>The answer the OLD contract refused: a finished stop whose quote appears nowhere in the reply.
    /// Contract v3 asks for no quote, so this is now an ordinary accepted answer - which is the point.</summary>
    private static string OnceReceiptRefusedAnswer()
        => FakeTurnVerdictEnvironment.Finished("I rewrote the whole deploy pipeline from scratch.", Spoken);

    /// <summary>An answer contract v3 DOES refuse: a state word that is not one of the seven. It carries no prose
    /// at all, because a v3 refusal carries none - there is no field left for a judge to put words in.</summary>
    private static string RefusedAnswer()
        => """
           {
             "state": "everything-is-fine",
             "label": "Pushed the branch",
             "agentRecommends": null,
             "menu": null,
             "options": []
           }
           """;

    // ================================================================= the refusal that v3 deleted

    /// <summary>
    /// THE 24 THROWN-AWAY ANSWERS, FROM THE OTHER SIDE. This is the same answer slice I salvaged words out of, and
    /// contract v3 does not refuse it at all: it is a reading, the row is accepted, and the listener hears the
    /// narration call's words rather than a rescued fragment of a rejected answer.
    /// </summary>
    [Fact]
    public async Task AnAnswerTheReceiptCheckOnceRefused_OnAVoiceSession_IsNowAnAcceptedReading_AndIsNarrated()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(OnceReceiptRefusedAnswer());
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);
        Assert.Equal(TurnVerdictStates.FinishedReport, stored.State);
        Assert.Equal(Spoken, stored.Spoken);

        // Narrated once, with the reading's own words, tied to its id.
        Assert.Equal(1, rig.Speech.Calls);
        var ready = rig.Voice.Get(Tenant, Sid);
        Assert.NotNull(ready);
        Assert.Equal(Spoken, ready!.Spoken);
        Assert.Equal(stored.VerdictId, ready.SourceIdentity);
        // An answer that was accepted is never re-attempted: one judge call.
        Assert.Equal(1, rig.Env.JudgeCalls);
    }

    [Fact]
    public async Task AVoiceRefreshThatArrivesAfterAJudgementEnded_ReusesItsWords_AndDoesNotAskTheJudgeAgain()
    {
        // The ordering the host's turn-end sequence only sometimes produces, forced: the judgement has finished and
        // stored its record BEFORE the voice refresh looks. Joining the flight is not available, so only the reuse
        // rule stands between this stop and a second model call.
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(OnceReceiptRefusedAnswer());
        rig.Voice.Mark(Tenant, Sid);

        var judged = await rig.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);      // CONTROL: the stop really was read
        Assert.Equal(1, rig.Env.JudgeCalls);

        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: true);

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(Spoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(1, rig.Speech.Calls);
    }

    [Fact]
    public async Task AnAnswerTheReceiptCheckOnceRefused_OnExplain_IsNarrated_NotReportedAsAnError()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(OnceReceiptRefusedAnswer());

        var narration = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);

        Assert.Null(narration.Error);
        Assert.Equal(Spoken, narration.Spoken);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(1, rig.Speech.Calls);
    }

    /// <summary>An answer contract v3 still refuses stamps the row refused and plays nothing - there are no words
    /// in it to play, because a v3 refusal carries no prose at all. This is the half of slice I that survives the
    /// contract change unaltered: a row a reading could not be formed for never reads as calm.</summary>
    [Fact]
    public async Task AStopV3StillRefuses_StampsTheRowRefused_AndIsSilent()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(RefusedAnswer());
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.True(stored.Failed);
        Assert.Equal("", stored.Spoken);
        Assert.Equal(0, rig.Speech.Calls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));

        var row = new SessionDto { SessionId = Sid, ActivityState = "WaitingForInput" };
        TurnVerdictRowStamp.Stamp(new[] { row }, new RowsOver(rig.Env, rig.Verdicts), Tenant);

        Assert.Equal(VerdictStates.Failed, row.VerdictState);
        Assert.Null(row.VerdictLabel);
        Assert.True(row.TurnVerdict!.Failed);
    }

    // ================================================================= a stop with no words stays silent

    [Fact]
    public async Task AJudgeThatTimesOut_OnAVoiceSession_IsReattemptedOnce_AndWhenThatTimesOutToo_NothingIsNarrated()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => throw new TimeoutException("The wingman model call did not answer within 30 seconds.");
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.True(stored.Failed);
        Assert.Equal("", stored.Spoken);
        Assert.Equal(0, rig.Speech.Calls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, rig.Env.JudgeCalls);
    }

    // ================================================================= the one re-attempt, and who gets it

    [Fact]
    public async Task AVoiceSessionWhoseJudgeTimesOut_GetsOneReattemptWithTheSixtySecondDeadline_AndIsNarrated()
    {
        var rig = Build();
        var calls = 0;
        rig.Env.Judge = (_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new TimeoutException("The wingman model call did not answer within 30 seconds.")
            : Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(2, rig.Env.JudgeCalls);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(TurnVerdictSettings.DefaultJudgeTimeoutSeconds), TimeSpan.FromSeconds(60) },
            rig.Env.JudgeTimeouts.ToArray());
        Assert.False(rig.Env.Latest(Tenant, Sid)!.Failed);
        Assert.Equal(Spoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task AVoiceSessionWhoseJudgeAnswersSomethingThatIsNotJson_GetsOneReattempt()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => true;
        var calls = 0;
        env.Judge = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? "The session finished and nothing is needed."
            : FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));

        var outcome = await new TurnVerdictService(env).StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        Assert.Equal(2, env.JudgeCalls);
    }

    [Fact]
    public async Task APersonPressingExplain_WhoseJudgeTimesOut_GetsOneReattempt_EvenOnASessionNotInVoiceMode()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        env.Judge = (_, _) => throw new TimeoutException("no answer");

        var outcome = await new TurnVerdictService(env).VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);

        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(TurnVerdictFailureKind.DidNotAnswer, outcome.Failure);
        Assert.Equal(2, env.JudgeCalls);
    }

    [Fact]
    public async Task AStopNobodyIsListeningTo_WhoseJudgeTimesOut_KeepsTheOneCallRule()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        env.Judge = (_, _) => throw new TimeoutException("no answer");

        var outcome = await new TurnVerdictService(env).StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));

        Assert.Equal(TurnVerdictOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(TurnVerdictSettings.DefaultJudgeTimeoutSeconds) }, env.JudgeTimeouts.ToArray());
    }

    /// <summary>
    /// THE "READABLE REFUSAL" CASE IS GONE, and it is gone because it became the "accepted" case beside it.
    /// Contract v3 does not refuse an answer over a quote, so the answer that case fed is now a reading - the
    /// same input, two rows apart, proving the same thing twice. What still has to hold is the rule: a first call
    /// that produced a reading, or that named its own wait, is never asked a second time.
    /// </summary>
    [Theory]
    [InlineData("rate limit")]
    [InlineData("accepted")]
    public async Task AVoiceSession_IsNotReattempted_WhenTheFirstCallCarriedWordsOrNamedItsOwnWait(string firstAnswer)
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => true;
        env.Judge = firstAnswer switch
        {
            "rate limit" => (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40)),
            _ => (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken)),
        };

        await new TurnVerdictService(env).StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));

        Assert.Equal(1, env.JudgeCalls);
    }

    // ================================================================= a later request about the same failed stop
    // The slice I inspection's two findings, each as the inspector described the scenario.

    [Fact]
    public async Task ARateLimitedTurnEnd_ThenAVoiceRequestForTheUnchangedScreen_AsksTheJudgeOnce()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => true;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        var verdicts = new TurnVerdictService(env);

        var turnEnd = await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictFailureKind.RateLimited, turnEnd.Failure);    // CONTROL: the stop really was rate limited
        var voice = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(TurnVerdictOutcomeKind.Failed, voice.Kind);
        Assert.Equal(TurnVerdictFailureKind.RateLimited, voice.Failure);
        Assert.NotNull(voice.RetryAfter);
    }

    [Fact]
    public async Task AnExplainThatJoinsAHeldTurnEndOnASessionNotInVoiceMode_WhoseCallTimesOut_GetsTheReattempt()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        env.Judge = (_, _) => Interlocked.Increment(ref calls) == 1
            ? held.Task
            : Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));
        var verdicts = new TurnVerdictService(env);

        var turnEnd = verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.True(await WaitUntil(() => env.JudgeCalls == 1));
        var explain = verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.True(await WaitUntil(() => verdicts.FlightAskedOnDemand(Tenant, Sid)));
        held.SetException(new TimeoutException("The wingman model call did not answer within 30 seconds."));

        var explained = await explain;
        await turnEnd;

        Assert.Equal(2, env.JudgeCalls);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, explained.Kind);
    }

    [Fact]
    public async Task ARateLimitedTurnEndOnAnUnreadableScreen_ThenAVoiceRequest_AsksTheJudgeOnce()
    {
        // Round two of the inspection: the rate limit's wait is checked before the screen, so a Director whose screen
        // read returns no rows cannot buy a second call inside the wait.
        var env = ServiceEnv();
        env.Screen = () => null;
        env.VoiceSession = _ => true;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        var verdicts = new TurnVerdictService(env);

        var turnEnd = await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictFailureKind.RateLimited, turnEnd.Failure);    // CONTROL: the stop really was rate limited
        var voice = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(TurnVerdictFailureKind.RateLimited, voice.Failure);
    }

    [Fact]
    public async Task AnExplainThatJoinsAfterTheFlightDecidedItsAttempts_RunsItsOwnRequest_AndAsksAgain()
    {
        // Round two of the inspection, in the inspector's ordering: a non-voice turn end times out, decides on no
        // re-attempt, and is held at its trace write; explain joins there, and the flight is released. The explain is
        // not served by the flight - it asks through its own request once the flight has ended.
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        var calls = 0;
        env.Judge = (_, _) => Interlocked.Increment(ref calls) == 1
            ? throw new TimeoutException("The wingman model call did not answer within 30 seconds.")
            : Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));
        using var atTrace = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holds = 0;
        env.BeforeRecordTrace = _ =>
        {
            if (Interlocked.Increment(ref holds) != 1) return;
            atTrace.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var verdicts = new TurnVerdictService(env);

        var turnEnd = verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.True(atTrace.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, env.JudgeCalls);
        var explain = verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.False(verdicts.FlightAskedOnDemand(Tenant, Sid));    // joined, and not served: the attempts were decided
        release.Set();

        await turnEnd;
        var explained = await explain;

        Assert.Equal(2, env.JudgeCalls);
        Assert.Equal(TurnVerdictOutcomeKind.Judged, explained.Kind);
    }

    [Fact]
    public async Task ASecondNewTurnWithTheSameReplyOnAnUnreadableScreen_IsNotHeldByThePreviousStopsRateLimit()
    {
        // Round three of the inspection: no Working event was observed between the two stops, the screen is
        // unreadable and the reply text is the same. The new-turn signal itself ends the previous stop's wait.
        var env = ServiceEnv();
        env.Screen = () => null;
        env.VoiceSession = _ => false;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        var verdicts = new TurnVerdictService(env);

        var first = await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictFailureKind.RateLimited, first.Failure);    // CONTROL: the first stop was rate limited
        await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt.AddSeconds(1), IsNewTurn: true));

        Assert.Equal(2, env.JudgeCalls);
    }

    [Fact]
    public async Task AFlightThatIsEndingWhenItsHoldIsCleared_DoesNotWriteItsWaitBack()
    {
        // The hold generation on its own: the rate-limited flight has stored its record and is held at its trace write
        // when a Working event clears the hold. Released, it must not write the old stop's wait back.
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        using var atTrace = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holds = 0;
        env.BeforeRecordTrace = _ =>
        {
            if (Interlocked.Increment(ref holds) != 1) return;
            atTrace.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var verdicts = new TurnVerdictService(env);

        var first = verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.True(atTrace.Wait(TimeSpan.FromSeconds(5)));
        verdicts.OnSessionWorking(Tenant, Sid);
        release.Set();
        await first;

        Assert.Equal(1, env.JudgeCalls);
        Assert.False(verdicts.HasRateLimitHold(Tenant, Sid));
    }

    [Fact]
    public async Task AnExplainThatWasRateLimited_MayAskAgainOnceTheNamedWaitHasPassed()
    {
        // Round three of the inspection: the explain's own flight decided against a re-attempt (a rate limit), so it is
        // not a failure explain already asked past, and after the wait a second explain asks.
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        env.Clock = () => now;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        var verdicts = new TurnVerdictService(env);

        var first = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.Equal(TurnVerdictFailureKind.RateLimited, first.Failure);
        Assert.Equal(1, env.JudgeCalls);

        now = now.AddSeconds(41);
        await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);

        Assert.Equal(2, env.JudgeCalls);
    }

    [Fact]
    public async Task ATimedOutStopOnAVoiceSession_IsNotAskedAgainByALaterVoiceRefresh()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => true;
        env.Judge = (_, _) => throw new TimeoutException("no answer");
        var verdicts = new TurnVerdictService(env);

        await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, env.JudgeCalls);
        var voice = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.Voice);

        Assert.Equal(TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, env.JudgeCalls);
        Assert.Equal(TurnVerdictOutcomeKind.Failed, voice.Kind);
    }

    [Fact]
    public async Task AnExplainAfterAFailedTurnEnd_AsksAgainOnce_AndASecondExplainReusesTheFailureItAskedFor()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        env.Judge = (_, _) => throw new TimeoutException("no answer");
        var verdicts = new TurnVerdictService(env);

        await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(1, env.JudgeCalls);                                             // nobody listening: one call

        await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.Equal(1 + TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, env.JudgeCalls);   // the person's own ask

        var second = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);
        Assert.Equal(1 + TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, env.JudgeCalls);
        Assert.Equal(TurnVerdictOutcomeKind.Failed, second.Kind);
    }

    [Fact]
    public async Task AnExplainInsideARateLimitsNamedWait_DoesNotAskAgain_AndAfterTheWaitItMay()
    {
        var env = ServiceEnv();
        env.VoiceSession = _ => false;
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        env.Clock = () => now;
        env.Judge = (_, _) => throw new WingmanModelRateLimitedException("429", TimeSpan.FromSeconds(40));
        var verdicts = new TurnVerdictService(env);

        await verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        var inside = await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);

        Assert.Equal(1, env.JudgeCalls);
        Assert.Equal(TurnVerdictFailureKind.RateLimited, inside.Failure);

        now = now.AddSeconds(41);
        await verdicts.VerdictForCurrentScreenAsync(Tenant, "dir-1", Sid, TurnVerdictTrigger.OnDemand);

        Assert.Equal(2, env.JudgeCalls);                                             // CONTROL: the wait was what stopped it
    }

    private static FakeTurnVerdictEnvironment ServiceEnv() => new()
    {
        Screen = () => Screen(Sid, ReplyText, "> "),
        Conversation = _ => Reply("push it", ReplyText),
    };
}
