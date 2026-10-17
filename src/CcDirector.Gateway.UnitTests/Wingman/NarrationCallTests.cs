using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Slice J of the Wingman-on-every-turn mission: a stop somebody is listening to gets a narration call of its own,
/// with the version 10 fidelity prompt, beside its verdict. Slice I measured the judge's spoken field as too thin to
/// be the whole narration (6 of 20 as faithful as the old translator), so the judge's words play first and the
/// narration call's words replace them when it answers.
///
/// What these hold: WHEN the call is made (a voice session's stop, or explain - and never twice for one verdict id),
/// WHAT it is given (the judge's decision, so a menu is narrated as a menu), and what a listener hears around it (the
/// menu sentence once, never twice, and the judge's words when the call gives nothing).
///
/// The turn-end sequence is the host's own, in the host's order, as <see cref="VoiceIsNeverSilencedByAVerdictCheckTests"/>
/// drives it: the verdict seat first, then the voice refresh.
/// </summary>
public sealed class NarrationCallTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-narration";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string JudgeSpoken = "The branch is pushed and the pull request is open.";
    private const string Narrated = "The branch is pushed and a pull request is open, so the review can start whenever you are ready.";
    private const string MenuQuestion = "Proceed with the change?";
    private static readonly string[] MenuRows = { "Do you want to proceed?", "> 1. Proceed", "  2. Stop" };
    private static readonly DateTime ObservedAt = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingSpeech : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        /// <summary>When set, the synthesis with this 1-based call number waits on <see cref="Release"/> before it answers.</summary>
        public int HoldCall { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == HoldCall) await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        }
    }

    private sealed record Rig(WingmanVoiceService Voice, TurnVerdictService Verdicts, FakeTurnVerdictEnvironment Env, CountingSpeech Speech);

    private Rig Build(bool menu = false)
    {
        Directory.CreateDirectory(_dir);
        var vault = new KeyVault(Path.Combine(_dir, "keys.vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        var env = new FakeTurnVerdictEnvironment();
        if (menu)
        {
            env.Screen = () => Screen(Sid, MenuRows);
            env.Conversation = _ => null;
            env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu(MenuQuestion, "Do you want to proceed?", JudgeSpoken));
        }
        else
        {
            env.Screen = () => Screen(Sid, ReplyText, "> ");
            env.Conversation = _ => Reply("push it", ReplyText);
            env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, JudgeSpoken));
        }
        env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        var verdicts = new TurnVerdictService(env);
        var speech = new CountingSpeech();
        var translator = new CountingBrain(() => "a translation nobody should have asked for");
        var voice = new WingmanVoiceService((_, _, _, _) => Task.FromResult<IAgentBrain>(translator), vault, settings, false, CcDirector.Gateway.Tenancy.TenantAiGate.SelfHost,
            Path.Combine(_dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(speech), turnVerdicts: verdicts);
        env.VoiceSession = sid => voice.IsVoiceSession(Tenant, sid);
        return new Rig(voice, verdicts, env, speech);
    }

    /// <summary>The label every narration below answers with.</summary>
    private const string NarratedLabel = "Pull request open, review can start";

    /// <summary>A narration call's answer in the shape the prompt asks for: the label line, the narration line and the
    /// words, between the markers.</summary>
    private static string Answer(string spoken, string label = NarratedLabel)
        => $"{Core.Drivers.SessionAskRunner.AnswerBeginMarker}\n{NarrationCall.LabelTag} {label}\n{NarrationCall.NarrationTag}\n{spoken}\n{Core.Drivers.SessionAskRunner.AnswerEndMarker}";

    private static async Task HostTurnEndAsync(Rig rig, SessionVerbClient route)
    {
        var signal = new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);
        var judging = rig.Verdicts.StartTurnEnd(signal);
        if (rig.Voice.IsVoiceSession(signal.Tenant, signal.SessionId) && !rig.Verdicts.IsHeld(signal.Tenant, signal.SessionId))
            await rig.Voice.GenerateAsync(signal.Tenant, signal.SessionId, route, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
        await judging;
        await rig.Voice.WaitForNarrationCallsAsync();
    }

    private static string MenuSuffix => SpokenPhrases.WaitingScreenMenuNarrationSuffix.In(SpokenLanguages.English);

    // ================================================================= when the call is made

    /// <summary>ONE CLIP FOR ONE STOP, and this count is the atomic ruling's headline benefit. It was TWO until
    /// 2026-09-18 - the judge's words synthesised and played, then the narration's synthesised and played over
    /// them - which is what restarted audio mid-sentence and re-worded a row while the owner looked at it.</summary>
    [Fact]
    public async Task AVoiceSessionsStop_MakesExactlyOneNarrationCall_AndExactlyOneClipOfItsWords()
    {
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(verdict.Failed);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(TimeSpan.FromSeconds(TurnVerdictSettings.NarrationCallTimeoutSeconds), Assert.Single(rig.Env.NarratorTimeouts));
        Assert.Equal(1, rig.Speech.Calls);
        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal(Narrated, ready.Spoken);
        Assert.Equal(verdict.VerdictId, ready.SourceIdentity);
    }

    [Fact]
    public async Task ALaterRefreshOfTheSameStop_OnAVoiceSession_DoesNotMakeTheNarrationCallAgain()
    {
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);
        await HostTurnEndAsync(rig, route);

        // The same unchanged screen: the verdict is reused, and a clip for that verdict id is already made.
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false);
        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    /// <summary>
    /// A SESSION THAT IS NOT A VOICE SESSION GETS NO NARRATION CALL AT ITS TURN END (owner ruling, 28 September 2026).
    /// Nobody is listening, so the model is asked only for the colour; the row shows its plain state label, and the
    /// words are made only when a person asks for them.
    /// </summary>
    [Fact]
    public async Task ATurnEndOnASessionThatIsNotAVoiceSession_MakesNoNarrationCall_AndStoresNoWords()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.NarratorCalls);
        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(verdict.Failed);
        Assert.Null(verdict.Narration);
        Assert.Null(verdict.NarrationFailureReason);
        Assert.Null(rig.Voice.Get(Tenant, Sid));
    }

    /// <summary>The same stop on a VOICE session: one narration call, and its text is the reading's one text.</summary>
    [Fact]
    public async Task ATurnEndOnAVoiceSession_MakesOneNarrationCall_AndSavesItsTextOnTheVerdict()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        // ONE TEXT, READ OR HEARD (contract v3): the narration IS the summary and IS the spoken text.
        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.Equal(Narrated, verdict.Narration);
        Assert.Equal(Narrated, verdict.Spoken);
        Assert.Equal(Narrated, verdict.Summary);
    }

    [Fact]
    public async Task ATurnEndOnASessionAnotherSessionOwns_MakesNoNarrationCall()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);
        var judged = await rig.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);
        Assert.Equal(1, rig.Env.NarratorCalls);   // control: the same stop, owned by the user, is narrated

        var owned = Build();
        owned.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        owned.Voice.Mark(Tenant, Sid);
        // Owned by a live session from the moment it was judged: a session that becomes owned while its verdict is
        // being formed. The held check before the narration call stops it; ownership is answered here by whether the
        // judge has run.
        owned.Env.Held = _ => owned.Env.JudgeCalls > 0;
        var outcome = await owned.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);

        Assert.Equal(0, owned.Env.NarratorCalls);
        Assert.Null(owned.Env.Latest(Tenant, Sid)!.Narration);
    }

    // ================================================================= the Wingman is on the plan

    // Pricing Change mission, B1: the judgement is itself a Wingman model call, so a stop whose owner's plan lacks the
    // Wingman is not judged at all - and with no reading there is nothing to narrate and no model call of any kind.
    // (Before B1 the judge was still asked for such a stop and the narration was replaced by the Pro sentence; that
    // judge call was a model call on our meter for a plan that does not include it.)

    [Fact]
    public async Task AnAccountWhosePlanLacksTheWingman_IsNotJudgedOrNarrated_AndNoModelIsCalled()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Env.Plan = () => NarrationPlan.NeedsPro;
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(0, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.Null(rig.Env.Latest(Tenant, Sid));
        Assert.Contains(rig.Env.Records, r => r.Cause == ActivityCauses.NotOnPlan);
    }

    [Fact]
    public async Task AnAccountWhosePlanCouldNotBeRead_IsNotJudged_AndIsNeverToldToUpgrade()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Env.Plan = () => NarrationPlan.Unknown;

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(0, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.Null(rig.Env.Latest(Tenant, Sid));
        Assert.Contains(rig.Env.Records, r => r.Cause == ActivityCauses.PlanUnknown);
    }

    [Fact]
    public async Task AStopReadBeforeVoiceWasOn_IsNarratedOnceWhenVoiceIsTurnedOn_AndThenSpoken()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));
        Assert.Equal(0, rig.Env.NarratorCalls);   // not a voice session yet: colour only (owner ruling, 28 September 2026)

        // Voice is switched on afterwards, on the same unchanged stop: the reading is reused, its words are made once
        // and saved, and they are spoken.
        rig.Voice.Mark(Tenant, Sid);
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        Assert.EndsWith(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);

        // A second pass on the same stop makes no second call: the words are saved on the reading.
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    /// <summary>
    /// ISSUE #4154. The same stop as above, but reached the way production reaches it: voice is switched on for a
    /// session that is already waiting, nothing on the screen changes, and the only thing that comes past is the idle
    /// voice sweep - which reads the screen first, decides whether the attempt would cost anything, and only then
    /// generates. The stop's narration and its clip must still arrive, with no new turn.
    /// </summary>
    [Fact]
    public async Task AStopReadBeforeVoiceWasOn_IsNarratedByTheIdleSweep_WithNoNewTurn()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        var route = RouteServing("dir-1", rig.Env.Screen);
        await HostTurnEndAsync(rig, route);
        Assert.Equal(0, rig.Env.NarratorCalls);   // not a voice session yet: colour only

        rig.Voice.Mark(Tenant, Sid);
        var input = await rig.Voice.PrepareSweepGenerationAsync(Tenant, Sid, route);
        var slots = 0;
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false,
            onProviderReached: () => slots++, sweepInput: input);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, slots);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        Assert.EndsWith(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    /// <summary>One idle-sweep pass over the rig's session, exactly as GatewayHost runs it. Returns the slots it spent.</summary>
    private static async Task<int> SweepPassAsync(Rig rig, SessionVerbClient route)
    {
        var input = await rig.Voice.PrepareSweepGenerationAsync(Tenant, Sid, route);
        var slots = 0;
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false,
            onProviderReached: () => slots++, sweepInput: input);
        await rig.Voice.WaitForNarrationCallsAsync();
        return slots;
    }

    /// <summary>
    /// ISSUE #4154, THE STARVATION. The sweep has three slots per cycle for every account on the Gateway, and visits voice
    /// sessions in the same order every cycle. A stop whose late narration already failed can get no more words
    /// automatically, so a pass over it plays nothing and costs nothing - and it must not spend a slot either, or a few
    /// such sessions ahead in the order hold all three slots for ever and no other session is narrated again.
    /// </summary>
    [Fact]
    public async Task AStopWhoseLateNarrationFailed_SpendsNoSweepSlotOnLaterPasses()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        var route = RouteServing("dir-1", rig.Env.Screen);
        await HostTurnEndAsync(rig, route);
        rig.Voice.Mark(Tenant, Sid);

        Assert.Equal(1, await SweepPassAsync(rig, route));   // the one automatic attempt is a real cost
        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    /// <summary>
    /// The same starvation from a voice session's own reading: its narration call failed inside the reading, the reading is
    /// stored with that failure and no words, and only a person asking buys another call. The sweep must spend no slot on it.
    /// </summary>
    [Fact]
    public async Task AVoiceSessionsStopWhoseNarrationFailedInsideTheReading_SpendsNoSweepSlot()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        var route = RouteServing("dir-1", rig.Env.Screen);
        rig.Voice.Mark(Tenant, Sid);
        await HostTurnEndAsync(rig, route);
        Assert.NotNull(rig.Env.Latest(Tenant, Sid)!.NarrationFailureReason);
        var callsAfterTheReading = rig.Env.NarratorCalls;

        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(callsAfterTheReading, rig.Env.NarratorCalls);
    }

    /// <summary>
    /// The same starvation from a session with no reading at all (found in review): a brand-new session sitting at its
    /// prompt is never read, so a pass over it asks nobody and stores nothing - and must not spend a slot on every cycle.
    /// </summary>
    [Fact]
    public async Task ABrandNewVoiceSession_WithNoReading_SpendsNoSweepSlot()
    {
        var rig = Build();
        rig.Env.Facts = sid => new SessionDto { SessionId = sid, ActivityState = "WaitingForInput", IsBrandNew = true };
        var route = RouteServing("dir-1", () => Screen(Sid, "> "));
        rig.Voice.Mark(Tenant, Sid);

        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(0, rig.Env.JudgeCalls);
        Assert.Null(rig.Env.Latest(Tenant, Sid));
    }

    /// <summary>
    /// ISSUE #4154, THE SWITCH. Switching voice on is a person asking for the stop in front of them. A late narration for this
    /// same stop that failed during an EARLIER voice period - voice mode switched off, by hand or by auto-off - must not leave
    /// the stop refused for good: switching voice on again gives the stop one more automatic attempt, and its words and clip
    /// arrive with no new turn.
    /// </summary>
    [Fact]
    public async Task SwitchingVoiceOnAgain_AfterTheStopsLateNarrationFailed_NarratesItWithNoNewTurn()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        var route = RouteServing("dir-1", rig.Env.Screen);
        await HostTurnEndAsync(rig, route);
        rig.Voice.Mark(Tenant, Sid);
        await SweepPassAsync(rig, route);
        Assert.Equal(1, rig.Env.NarratorCalls);

        rig.Voice.Unmark(Tenant, Sid);
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);

        Assert.Equal(1, await SweepPassAsync(rig, route));
        Assert.Equal(2, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        Assert.EndsWith(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);

        // Marking a session that is ALREADY on voice is not a new request: it gives back nothing.
        Assert.Equal(0, await SweepPassAsync(rig, route));
        Assert.Equal(2, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task ExplainOnAStopReadWithoutWords_ReturnsTheWordsInTheSameAnswer_WithOneNarrationCall()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));
        Assert.Equal(0, rig.Env.NarratorCalls);   // not a voice session: the turn end read the colour only

        // The person presses explain on the unchanged stop: the reading is reused and its words are made while they wait.
        var narration = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Null(narration.Error);
        Assert.EndsWith(Narrated, narration.Spoken);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        // One text, read or heard: words made late are stored in the same three fields a reading's own words are.
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Summary);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Spoken);
        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
    }

    /// <summary>
    /// ONE PRESS, ONE PAID CALL, EVEN WHEN IT FAILS. The late narration call is made while the person waits; when it
    /// gives no words, the press says so and does not start a second call behind it. A second PRESS may ask again.
    /// </summary>
    [Fact]
    public async Task ExplainOnAStopReadWithoutWords_WhoseNarrationFails_PaysOneCall_AndSaysSo()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var first = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(WingmanVoiceService.RetryingLine, first.Spoken);
        Assert.True(first.Retrying);
        Assert.Null(first.Error);

        // THE CONTROL: the person pressing again is allowed exactly one more call.
        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();
        Assert.Equal(2, rig.Env.NarratorCalls);
    }

    /// <summary>
    /// A PRESS WHILE THE CALL IS STILL RUNNING is told so, never handed an empty answer, and pays for nothing.
    /// </summary>
    [Fact]
    public async Task ExplainWhileTheLateNarrationIsRunning_SaysItIsStillBeingWritten_AndMakesNoSecondCall()
    {
        var rig = Build();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Answer(Narrated);
        };
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var firstPress = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        Assert.Equal(WingmanVoiceService.StillWritingLine, second.Spoken);
        Assert.Null(second.Error);

        release.SetResult();
        var first = await firstPress;
        await rig.Voice.WaitForNarrationCallsAsync();
        Assert.EndsWith(Narrated, first.Spoken);
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task AStopReadBeforeVoiceWasOn_WhoseLateNarrationFails_IsNotAskedAgainOnEveryPass()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        rig.Voice.Mark(Tenant, Sid);
        for (var pass = 0; pass < 3; pass++)
        {
            await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);
            await rig.Voice.WaitForNarrationCallsAsync();
        }

        // One attempt, and the idle sweep does not pay for another on every pass; a person asking still can.
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Null(rig.Env.Latest(Tenant, Sid)!.Narration);
    }

    [Fact]
    public async Task AVoiceSessionsNarration_IsSavedOnTheVerdictToo()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
    }

    /// <summary>
    /// EXPLAIN ON A STOP NOBODY HAS READ YET MAKES THE WHOLE READING AND WAITS FOR IT. Until contract v3 it came
    /// back at once with the judge's own short text and the narration replaced it seconds later; the judge answers
    /// no prose now, so there is nothing to come back with early, and the owner's ruling is that nothing is shown
    /// until the whole reading exists. The wait is bounded by the two calls' own deadlines.
    /// </summary>
    [Fact]
    public async Task Explain_OnAStopNobodyHasReadYet_WaitsForTheWholeReading_AndReturnsItsWords()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;

        // Started, NOT awaited: awaiting before the narrator answers would deadlock the test, because the explain
        // now waits for the reading and the reading waits for this narrator.
        var explain = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));
        Assert.False(explain.IsCompleted, "the explain must not answer with a reading that is only half made");

        release.SetResult(Answer(Narrated));
        var narration = await explain.WaitAsync(TimeSpan.FromSeconds(30));
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, narration.Spoken);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
    }

    [Fact]
    public async Task ASecondExplainForTheSameVerdict_MakesNoSecondNarrationCall()
    {
        var rig = Build();
        var route = RouteServing("dir-1", rig.Env.Screen);

        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();
        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    // ================================================================= what the call is given

    /// <summary>
    /// A PICKER STOP IS NEEDS-YOU, AND ITS NARRATION SAYS TO OPEN THE SESSION TO CHOOSE (phase 4; the owner, 25
    /// September: "A menu is simply needs you, and the narration says open the session to choose. Nobody is told to
    /// press a button that is not there"). Code decides it at the picker step, with no model call; Call B is handed the
    /// word, the step's reason, and the code-owned closing sentence - and no menu, no options, no KEYS/REPLY line.
    ///
    /// IN VOICE MODE TOO (phase 3 review, finding F1): the session here is a voice session, and the clip the listener
    /// hears is Call B's own words - the ones told to end with "open the session to choose" - with nothing appended.
    /// </summary>
    [Fact]
    public async Task APickerStop_InVoiceMode_IsDecidedByCode_AndItsNarrationIsToldToSayOpenTheSessionToChoose()
    {
        var rig = Build(menu: true);
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(0, rig.Env.JudgeCalls);
        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.Equal("picker", stored.DecidedBy);
        Assert.Equal("needs-you", stored.State);
        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.Contains("What the stop is: needs-you", prompt);
        Assert.Contains("Why: " + stored.DecisionReason, prompt);
        Assert.Contains(NarrationCall.MenuClosingInstruction, prompt);
        Assert.Contains("open the session to choose", prompt);
        Assert.DoesNotContain("How the person answers", prompt);
        Assert.DoesNotContain("The menu's question:", prompt);
        Assert.DoesNotContain("press a button", prompt);
        // The listener hears exactly Call B's words, and the reading carries Call B's label.
        Assert.True(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(NarratedLabel, stored.Label);
    }

    [Fact]
    public async Task AReplyVerdict_IsHandedInAsAReply()
    {
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.DoesNotContain("How the person answers", prompt);
        Assert.DoesNotContain(NarrationCall.MenuClosingInstruction, prompt);
        Assert.DoesNotContain("The menu's question:", prompt);
        Assert.Contains(ReplyText, prompt);
    }

    // ================================================================= what the listener hears

    [Fact]
    public async Task TheMenuSentence_IsNotAppendedToTheNarrationCallsWords()
    {
        var rig = Build(menu: true);
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal(Narrated, ready.Spoken);
        Assert.DoesNotContain(MenuSuffix.Trim(), ready.Spoken);
    }

    /// <summary>
    /// THERE ARE NO JUDGE'S WORDS FOR THE MENU SENTENCE TO BE APPENDED TO ANY MORE, and this test is what says so.
    /// Until contract v3 a menu stop whose narration call failed still played the judge's own text with the
    /// press-a-button sentence after it. The judge answers no prose now, so a failed narration leaves the reading
    /// with nothing to say and nothing is played - see AFailedNarrationCall_LeavesTheReadingWithNoWordsAtAll for
    /// the measured cost of that, which is the mission's largest accepted loss.
    /// </summary>
    [Fact]
    public async Task AMenuStopWhoseNarrationFailed_HasNoWordsAtAll_AndNoMenuSentenceEither()
    {
        var rig = Build(menu: true);
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(0, rig.Speech.Calls);
        // The JUDGEMENT itself survived - only the words are missing. The row still knows what the stop is: its
        // state. Call B failed, so there is no label either, and the row shows its plain state label.
        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);
        Assert.Equal("", stored.Narration ?? "");
        Assert.Equal("needs-you", stored.State);
    }

    /// <summary>
    /// THE MISSION'S LARGEST ACCEPTED LOSS, WRITTEN DOWN. Until contract v3 the judge answered its own short
    /// "spoken" text, so a narration call that failed still left something playable. That field is cut, so a stop
    /// whose narration call fails now has NO WORDS AT ALL and a listener hears nothing for it.
    ///
    /// It is not re-attempted by any automatic path, which is unchanged and deliberate - the idle sweep comes past
    /// every forty-five seconds and a stop that keeps failing would keep costing a model call. A PERSON asking
    /// again does get a second call: AfterAFailedNarrationCall_APersonAskingAgain_MakesASecondCall.
    ///
    /// THE JUDGEMENT IS KEPT. Failing the whole reading over the second call would be worse: a failed reading is
    /// stored against the screen and never asked again, so it would manufacture exactly the permanent silences
    /// this mission exists to remove.
    /// </summary>
    [Fact]
    public async Task AFailedNarrationCall_LeavesTheReadingWithNoWordsAtAll_AndIsNotReattempted()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        await HostTurnEndAsync(rig, route);
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(0, rig.Speech.Calls);
        // TWO attempts, both inside the one reading, and then no automatic path asks again. It was one until
        // 19 September 2026 - see the note on NarrateForReadingAsync for why a second was added the moment
        // these words became the only words a reading has.
        Assert.Equal(2, rig.Env.NarratorCalls);
        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);   // the JUDGEMENT stands
        Assert.Equal("", stored.Narration ?? "");
    }

    /// <summary>
    /// THE SECOND ATTEMPT IS WHAT SAVES THE READING when the first one simply did not answer, and this is the
    /// test that says so. The words exist, the row carries them, and the listener hears them - from a reading
    /// that would have been silent the day before.
    ///
    /// REVERT PROOF: take the second attempt out of NarrateForReadingAsync and this goes red with no narration
    /// on the record, no clip, and one call.
    /// </summary>
    [Fact]
    public async Task ANarrationWhoseFirstAttemptDidNotAnswer_IsSavedByTheSecond()
    {
        var rig = Build();
        var attempts = 0;
        rig.Env.Narrator = (_, _) => Interlocked.Increment(ref attempts) == 1
            ? throw new TimeoutException("the narration call did not answer")
            : Task.FromResult(Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(2, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
        // Still ONE clip: the second attempt happens inside the reading, before anything is stored, so nothing
        // was synthesised and then replaced.
        Assert.Equal(1, rig.Speech.Calls);
    }

    /// <summary>An answer that came back with no words is re-attempted on the same terms as one that did not
    /// come back at all - both are a reading with nothing to say.</summary>
    [Fact]
    public async Task ANarrationWhoseFirstAttemptAnsweredNothing_IsAlsoAskedAgain()
    {
        var rig = Build();
        var attempts = 0;
        rig.Env.Narrator = (_, _) => Task.FromResult(
            Interlocked.Increment(ref attempts) == 1 ? Answer("   ") : Answer(Narrated));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(2, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
    }

    /// <summary>A RATE LIMIT IS NOT ASKED AGAIN. It named a delay, and a reading has nowhere to wait it out -
    /// asking again immediately spends a call to be refused by the same limit and holds the row open longer.</summary>
    [Fact]
    public async Task ANarrationRefusedByARateLimit_IsNotAskedAgain()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new CcDirector.Gateway.Wingman.WingmanModelRateLimitedException(
            "the model rate limited the narration call", TimeSpan.FromSeconds(30));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal("", rig.Env.Latest(Tenant, Sid)!.Narration ?? "");
    }

    /// <summary>A narration call that answers with nothing, twice, is the same case as one that fails twice:
    /// no words, and nothing played.</summary>
    [Fact]
    public async Task ANarrationCallThatAnswersNoWords_LeavesTheReadingWithNoWordsEither()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer("   "));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal("", rig.Env.Latest(Tenant, Sid)!.Narration ?? "");
        Assert.Equal(2, rig.Env.NarratorCalls);
    }

    // ============================================== a failed call does not make the stop permanently unnarratable

    [Fact]
    public async Task AfterAFailedNarrationCall_APersonAskingAgain_MakesASecondCall_AndItsWordsBecomeTheClip()
    {
        // THE STOP IS UNCHANGED, so the stored verdict is REUSED and its id is the same one the failed turn-end call
        // claimed. That is the case where pressing "Generate narration now" was refused the claim, made no call at all,
        // and left the person with the judge's short text however many times they pressed. (A stop that has moved on
        // mints a new verdict id and was never affected - measured recovering on the live fleet, so this test is
        // deliberately written on the unchanged screen, which is the case that stays broken.)
        //
        // REVERT PROOF: take ReleaseNarrationClaimForRequest back out of NarrateStopOnRequestAsync and this goes red
        // with NarratorCalls still 1 and the judge's words still on the clip.
        var rig = Build();
        var calls = 0;
        rig.Env.Narrator = (_, _) =>
        {
            calls++;
            // BOTH of the reading's own attempts fail, because the reading now makes two. Only then is there a
            // wordless stop for a person to rescue, which is what this test is about.
            if (calls <= 2) throw new TimeoutException("the narration call did not answer");
            return Task.FromResult(Answer(Narrated));
        };
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        await HostTurnEndAsync(rig, route);
        Assert.Equal(2, rig.Env.NarratorCalls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));   // nothing playable at all: contract v3 cut the judge's words

        // The person presses "Generate narration now".
        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(3, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Env.Latest(Tenant, Sid)!.Narration);
        Assert.EndsWith(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task AfterAFailedNarrationCall_TheAutomaticPathsStillDoNotReattempt()
    {
        // The other half of the same rule, and the reason the release is a PERSON'S verb rather than a blanket one: the
        // idle sweep comes past every forty-five seconds, so a stop that keeps failing would keep costing a model call.
        // Only a person's ask releases a spent claim; a turn end and a refresh do not.
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        await HostTurnEndAsync(rig, route);
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false);
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        // TWO: the reading's own pair of attempts, and not one more however many times an automatic path comes
        // past. The pair is bounded and happens once, inside one reading; what this test forbids is the sweep
        // turning a stop that keeps failing into a standing model bill.
        Assert.Equal(2, rig.Env.NarratorCalls);
    }

    /// <summary>An impatient second tap while the reading is still being made joins it rather than paying for a
    /// second pair of calls. Unchanged in substance by contract v3 - what changed is that the tap now waits for
    /// the reading it joined instead of being handed the judge's words while it ran.</summary>
    [Fact]
    public async Task WhileTheReadingIsBeingMade_APersonAsking_DoesNotStartASecondCall()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        var signal = new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);
        var judging = rig.Verdicts.StartTurnEnd(signal);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));

        // The reading is still waiting on `release`; the person asks anyway. Started, not awaited.
        var explain = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);

        release.SetResult(Answer(Narrated));
        await explain.WaitAsync(TimeSpan.FromSeconds(30));
        await judging;
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
    }

    /// <summary>A reading whose session went back to work before it finished is not played: the words describe a
    /// screen that is gone. Contract v3 makes this cleaner rather than harder - the whole reading is discarded,
    /// where before a narration could answer late and be written over a record that had already been stored.</summary>
    [Fact]
    public async Task AReadingThatFinishesAfterTheSessionWorkedAgain_IsNotPlayed()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;

        var explain = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));
        rig.Voice.OnSessionWorking(Tenant, Sid);

        release.SetResult(Answer(Narrated));
        await explain.WaitAsync(TimeSpan.FromSeconds(30));
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.NotEqual(Narrated, rig.Voice.Get(Tenant, Sid)?.Spoken);
    }

    [Fact]
    public void ANarrationOf1150CharactersOfFullSentences_ThatRunsMidSentencePast1200_IsCutAtItsLastFullSentence()
    {
        var fullSentences = FullSentencesOfLength(1150);
        var answer = fullSentences + " And this last sentence runs on past the bound " + string.Join(" ", Enumerable.Repeat("without", 20)) + " ending";
        Assert.Equal(1150, fullSentences.Length);                            // CONTROL: 1,150 characters of full sentences
        Assert.True(answer.Length > NarrationCall.MaxChars);                  // CONTROL: and the answer crosses 1,200 mid-sentence

        var parsed = NarrationCall.ParseAnswer(Answer(answer));

        Assert.Null(parsed.FailureReason);
        Assert.Equal(fullSentences, parsed.Spoken);
    }

    /// <summary>Whole sentences, joined by single spaces, exactly <paramref name="length"/> characters long.</summary>
    private static string FullSentencesOfLength(int length)
    {
        const string sentence = "The branch is pushed and the review can start.";
        var text = sentence;
        while (text.Length + 1 + sentence.Length + 1 + 10 <= length) text += " " + sentence;
        var last = "It " + new string('o', length - text.Length - 1 - 5) + "k.";   // one more full sentence to the exact length
        return text + " " + last;
    }

    [Fact]
    public void ANarrationUnderTheBound_IsNotCut_EvenWhenItDoesNotEndASentence()
    {
        var answer = string.Concat(Enumerable.Repeat("The branch is pushed and the review can start. ", 20)) + "and one more clause";
        Assert.True(answer.Length < NarrationCall.MaxChars);

        var parsed = NarrationCall.ParseAnswer(Answer(answer));

        Assert.Equal(answer.Trim(), parsed.Spoken);
    }

    [Fact]
    public void TheNarrationBound_Is1200_AndIsNowTheOnlyProseBoundThereIs()
    {
        // The judge's own 900-character "spoken" field was the other bound. Contract v3 cut that field, so this
        // is the only one left, and the text it bounds is the ONE body a reading has - read or heard.
        Assert.Equal(1200, NarrationCall.MaxChars);
        Assert.Contains("1,200 characters", WingmanTranslator.FidelityPrompt);
        Assert.DoesNotContain("900 characters", WingmanTranslator.FidelityPrompt);
    }

    [Fact]
    public void ANumberOrAFileNameIsNotASentenceEnd_AndNoSentenceInsideTheBoundKeepsNoWords()
    {
        // "65.1" and "scene.py" have a full stop with no space after it: not an end.
        Assert.Equal("Acceptance went to 65.1 per cent.", NarrationCall.CapAtLastSentence("Acceptance went to 65.1 per cent. And scene.py moved", 40));
        Assert.Equal("", NarrationCall.CapAtLastSentence(string.Join(" ", Enumerable.Repeat("word", 400)), NarrationCall.MaxChars));
    }

    // ================================================================= a refused record with a menu

    /// <summary>
    /// A REFUSED JUDGEMENT IS NOT NARRATED AT ALL (contract v3, and v4 keeps it), and this is the test that says so.
    /// Until 2026-09-18 a refused answer that still carried a readable menu was narrated in the menu shape, from a
    /// decision salvaged out of the raw reply. Under v4 a refused answer is one that is not one of the three words, and
    /// it holds nothing to salvage. A picker screen no longer reaches the model at all - code decides it - so this runs
    /// on a stop only the model can decide.
    /// </summary>
    [Fact]
    public async Task ARefusedRecord_IsNotNarratedAtAll_AndItsRowOffersNoButtons()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.RefusedButReadableMenu(MenuQuestion));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        // The row: refused, and nothing on it that a phone could turn into a button.
        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.True(verdict.Failed);
        Assert.Equal("", verdict.AnswerVia);
        Assert.Null(verdict.Menu);
        Assert.Empty(verdict.Options);
        // No second call was bought for an answer there is nothing to narrate from, and nothing is playable.
        Assert.Empty(rig.Env.NarratorPrompts);
        Assert.Equal(0, rig.Speech.Calls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
    }

    /// <summary>The other half of the same rule: a voice refresh over a refused record does not resurrect it
    /// either. It reuses the stored refusal without asking the judge again, and buys no narration call for it.</summary>
    [Fact]
    public async Task AVoiceRefreshThatReusesARefusedRecord_StillNarratesNothing()
    {
        var rig = Build();
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.RefusedButReadableMenu(MenuQuestion));

        rig.Voice.Mark(Tenant, Sid);
        await rig.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.True(rig.Env.Latest(Tenant, Sid)!.Failed);

        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.JudgeCalls);         // the refusal is reused, never re-judged
        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
    }

    // ================================================================= inspection round 1

    [Fact]
    public async Task Explain_AfterTheNarrationWasStored_KeepsTheNarration()
    {
        // The spoken reply in voice mode narrates the same stop twice: the turn end and the voice-turn route. Explain
        // used to store the judge's words again over the narration, and the claim was spent, so it was lost for good.
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);
        await HostTurnEndAsync(rig, route);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);   // CONTROL: the narration is what plays

        var narration = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(Narrated, narration.Spoken);
        Assert.Equal(1, rig.Speech.Calls);   // nothing synthesised again - and one clip for the stop, never two
    }

    /// <summary>
    /// AN EXPLAIN THAT ARRIVES WHILE THE READING IS STILL BEING MADE JOINS IT, and comes back with that reading's
    /// words - it does not pay for a second pair of model calls and it does not lose the narration.
    ///
    /// THE SHAPE OF THIS TEST CHANGED WITH CONTRACT v3, and the change is the ruling, not a workaround. A reading
    /// is now atomic: the narration call runs INSIDE the judgement, so the flight an explain joins covers both
    /// calls rather than just the first. The person pressing explain therefore waits for the whole reading - which
    /// is what "nothing is shown until the whole reading exists" means at this button - where before they were
    /// handed the judge's short text and the fuller narration replaced it seconds later.
    ///
    /// IT IS BOUNDED, and that is the thing to keep true. The flight ends when the narration call's own deadline
    /// does, so the wait is the reading's deadline and never forever. This test AWAITS THE EXPLAIN LAST for that
    /// reason: awaiting it before releasing the narrator deadlocks the test itself - the explain waits for the
    /// reading, the reading waits for a narrator this test has not released yet, and nothing moves. Production
    /// cannot reach that state because the narrator there always answers or times out.
    /// </summary>
    [Fact]
    public async Task Explain_WhileTheReadingIsStillBeingMade_JoinsIt_AndDoesNotLoseTheNarration()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);
        var turnEnd = rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: true);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));

        // The person presses explain while that call is still out. Started, NOT awaited - see the note above.
        var explain = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        Assert.False(explain.IsCompleted, "the explain must wait for the reading, not answer with half of one");

        release.SetResult(Answer(Narrated));
        var narration = await explain.WaitAsync(TimeSpan.FromSeconds(30));
        await turnEnd;
        await rig.Voice.WaitForNarrationCallsAsync();

        // ONE pair of calls for one stop, and the explain got the reading's own words.
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(Narrated, narration.Spoken);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task ANarrationWhoseSpeechIsStillBeingMade_WhenTheSessionWorksAgain_IsNotStored()
    {
        // Staleness was checked before synthesis only: a Working edge during synthesis let the old stop's narration be
        // written back as ready on a session that is working.
        //
        // ONE CLIP, NOT TWO, SINCE CONTRACT v3: the judge answers no prose, so there is no first draft to synthesise
        // and the reading's only clip is call 1. The window this guards is unchanged - it is the synthesis, which
        // happens after the record is settled and where the verdict service can no longer see a Working edge.
        var rig = Build();
        rig.Speech.HoldCall = 1;   // the reading's one clip
        rig.Voice.Mark(Tenant, Sid);

        var turnEnd = HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));
        Assert.True(await WaitUntil(() => rig.Speech.Calls == 1));
        rig.Voice.OnSessionWorking(Tenant, Sid);
        rig.Speech.Release.SetResult();
        await turnEnd;
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Null(rig.Voice.Get(Tenant, Sid));
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
    }

    [Fact]
    public void AStoredV3KeysRecord_OnAnAccountWithItsOwnInstructions_IsToldToSayOpenTheSessionToChoose()
    {
        // Contract v4 never writes a keys record, but a v3 record reused on an unchanged screen still carries one.
        // Custom instructions replace the whole fidelity prompt; the closing sentence for a menu lives in the
        // code-owned decision block, so it cannot be edited away - and since phase 4 it sends the person to the
        // session rather than to a button.
        var package = new TurnVerdictPackage { ScreenRows = MenuRows, LatestReply = "Choose one.", AgentKind = "ClaudeCode" };
        var v3Keys = new TurnVerdictDto
        {
            ContractVersion = "v3.1",
            Verdict = "needed-you",
            AnswerVia = "keys",
            Menu = new TurnVerdictMenuDto { Question = MenuQuestion },
        };

        var prompt = NarrationCall.BuildPrompt(SpokenLanguages.English, "Speak briefly and plainly.", package, v3Keys);

        Assert.DoesNotContain(WingmanTranslator.FidelityPrompt.Trim(), prompt);   // CONTROL: the shipped prompt was replaced
        Assert.Contains(NarrationCall.MenuClosingInstruction, prompt);
        Assert.DoesNotContain("How the person answers", prompt);
        Assert.DoesNotContain(MenuQuestion, prompt);
        Assert.DoesNotContain("press a button", prompt);
    }

    [Fact]
    public async Task AReplyStop_OnAnAccountWithItsOwnInstructions_IsNotToldToPressAButton()
    {
        var rig = Build();
        rig.Env.Custom = "Speak briefly and plainly.";
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.DoesNotContain("press a button", prompt);
    }
}
