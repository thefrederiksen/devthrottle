using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
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
        var voice = new WingmanVoiceService((_, _, _) => Task.FromResult<IAgentBrain>(translator), vault, settings,
            Path.Combine(_dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(speech), turnVerdicts: verdicts);
        env.VoiceSession = sid => voice.IsVoiceSession(Tenant, sid);
        return new Rig(voice, verdicts, env, speech);
    }

    /// <summary>A narration call's answer, wrapped in the markers the prompt asks for.</summary>
    private static string Answer(string spoken)
        => $"{Core.Drivers.SessionAskRunner.AnswerBeginMarker}\n{spoken}\n{Core.Drivers.SessionAskRunner.AnswerEndMarker}";

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
    /// A SESSION THAT IS NOT IN VOICE MODE STILL GETS THE WHOLE READING. Until contract v3 the narration call was
    /// the voice path's to make, so this shape - a refresh that does not enrol the session - got the judge's short
    /// text and no second call. The reading is now both calls for every stop that answers to the USER, so the words
    /// exist whether or not anybody is listening, and the clip is those words.
    ///
    /// This costs nothing new: a session that answers to the user was already buying a narration call at every stop
    /// with voice off - see ATurnEndOnASessionThatAnswersToTheUser_WithVoiceOff below, which predates v3. A session
    /// a LIVE session owns still gets none, which ATurnEndOnASessionAnotherSessionOwns holds.
    /// </summary>
    [Fact]
    public async Task ASessionThatIsNotAVoiceSession_StillGetsTheWholeReading_AndItsClipIsTheNarrationsWords()
    {
        var rig = Build();

        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None,
            showReadingWindow: false, markAsVoiceSession: false);

        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(1, rig.Speech.Calls);
    }

    // ================================================================= every stop of a session that answers to the user

    [Fact]
    public async Task ATurnEndOnASessionThatAnswersToTheUser_WithVoiceOff_MakesOneNarrationCall_AndSavesItsTextOnTheVerdict()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        // ONE TEXT, READ OR HEARD (contract v3): the narration IS the summary and IS the spoken text. The judge
        // answered its own shorter "spoken" field until 2026-09-18, and a row then showed a short version and a
        // long version of one turn depending on which reader you asked.
        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.Equal(Narrated, verdict.Narration);
        Assert.Equal(Narrated, verdict.Spoken);
        Assert.Equal(Narrated, verdict.Summary);
        // Nobody is listening, so no audio was made.
        Assert.Null(rig.Voice.Get(Tenant, Sid));
    }

    [Fact]
    public async Task ATurnEndOnASessionAnotherSessionOwns_MakesNoNarrationCall()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        var judged = await rig.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, judged.Kind);
        Assert.Equal(1, rig.Env.NarratorCalls);   // control: the same stop, owned by the user, is narrated

        var owned = Build();
        owned.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        // Owned by a live session from the moment it was judged: the case of a session a Fleet Manager owns, which is
        // still judged. The fake cannot say "held but judged", so ownership is answered by whether the judge has run.
        owned.Env.Held = _ => owned.Env.JudgeCalls > 0;
        var outcome = await owned.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);

        Assert.Equal(0, owned.Env.NarratorCalls);
        Assert.Null(owned.Env.Latest(Tenant, Sid)!.Narration);
    }

    // ================================================================= the narration needs a Pro account

    [Fact]
    public async Task AnAccountWhosePlanLacksTheWingman_GetsTheProSentenceAsItsNarration_AndNoModelCall()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        rig.Env.Plan = () => NarrationPlan.NeedsPro;

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.Equal(NarrationPlanRule.NeedsProText, rig.Env.Latest(Tenant, Sid)!.Narration);
    }

    [Fact]
    public async Task AVoiceSessionOnAPlanWithoutTheWingman_HearsTheProSentence()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        rig.Env.Plan = () => NarrationPlan.NeedsPro;
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.EndsWith(NarrationPlanRule.NeedsProText, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task AnAccountWhosePlanCouldNotBeRead_GetsNoNarration_AndIsNeverToldToUpgrade()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        rig.Env.Plan = () => NarrationPlan.Unknown;

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.Null(rig.Env.Latest(Tenant, Sid)!.Narration);
    }

    [Fact]
    public async Task ASavedNarration_IsNotMadeAgain_AndIsWhatVoiceSpeaksWhenVoiceIsTurnedOnLater()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));
        Assert.Equal(1, rig.Env.NarratorCalls);

        // Voice is switched on afterwards: the saved text is spoken as it is, and no second call is made for this verdict.
        rig.Voice.Mark(Tenant, Sid);
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.EndsWith(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task AVoiceSessionsNarration_IsSavedOnTheVerdictToo()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Narrated);
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

    [Fact]
    public async Task AKeysVerdict_IsNarratedFromTheJudgesMenu_NotFromTheModelsOwnReadingOfTheScreen()
    {
        var rig = Build(menu: true);
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.Contains("How the person answers: KEYS", prompt);
        Assert.Contains("The menu's question: " + MenuQuestion, prompt);
        Assert.Contains("1. Proceed (recommended) - Carries on with the change.", prompt);
        Assert.Contains("2. Stop - Leaves the change unmade.", prompt);
        // The shape the prompt asks for, from the version 10 rules.
        Assert.Contains("press a button on the phone", prompt);
        Assert.DoesNotContain("OPEN WITH THE SESSION TITLE", prompt);
    }

    [Fact]
    public async Task AReplyVerdict_IsHandedInAsAReply()
    {
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.Contains("How the person answers: REPLY", prompt);
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
        // The JUDGEMENT itself survived - only the words are missing. The row still knows what the stop is.
        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);
        Assert.Equal("", stored.Narration ?? "");
        Assert.NotEqual("", stored.Label);
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
        Assert.Equal(1, rig.Env.NarratorCalls);   // one call, and no automatic path asks again
        var stored = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(stored.Failed, stored.FailureReason);   // the JUDGEMENT stands
        Assert.Equal("", stored.Narration ?? "");
    }

    /// <summary>A narration call that answers with nothing is the same case as one that fails: no words, and
    /// nothing played. Both are counted as one call.</summary>
    [Fact]
    public async Task ANarrationCallThatAnswersNoWords_LeavesTheReadingWithNoWordsEither()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer("   "));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal("", rig.Env.Latest(Tenant, Sid)!.Narration ?? "");
        Assert.Equal(1, rig.Env.NarratorCalls);
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
            if (calls == 1) throw new TimeoutException("the narration call did not answer");
            return Task.FromResult(Answer(Narrated));
        };
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        await HostTurnEndAsync(rig, route);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));   // nothing playable at all: contract v3 cut the judge's words

        // The person presses "Generate narration now".
        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(2, rig.Env.NarratorCalls);
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

        Assert.Equal(1, rig.Env.NarratorCalls);
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

        var spoken = NarrationCall.SpokenFrom(Answer(answer));

        Assert.Equal(fullSentences, spoken);
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

        var spoken = NarrationCall.SpokenFrom(Answer(answer));

        Assert.Equal(answer.Trim(), spoken);
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
    /// A REFUSED JUDGEMENT IS NOT NARRATED AT ALL (contract v3), and this is the test that says so. Until
    /// 2026-09-18 a refused answer that still carried a readable menu was narrated in the menu shape, from a
    /// decision salvaged out of the raw reply - the row offered no buttons and a listener was told to press one.
    /// A reading that could not be read now says it could not be read, rather than half-speaking.
    /// </summary>
    [Fact]
    public async Task ARefusedRecordWithAMenu_IsNotNarratedAtAll_AndItsRowOffersNoButtons()
    {
        var rig = Build(menu: true);
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
        var rig = Build(menu: true);
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

    /// <summary>
    /// A NEW STOP IS READ WHILE THE OLD STOP'S READING IS STILL RUNNING, and it is the NEW one that plays. The
    /// old reading is cancelled by the Working edge between them, so nothing it produces is ever heard.
    ///
    /// The shape moved with contract v3: the call that used to run on past the turn end was the narration alone,
    /// detached, and the risk was that awaiting it inside the per-session gate would drop the next stop. The
    /// narration is inside the reading now, so what runs long is the READING - a bigger thing to be stuck behind,
    /// and the same rule has to hold.
    /// </summary>
    [Fact]
    public async Task ANewStopsTurnEnd_WhileTheOldStopsReadingRuns_IsStillRead()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string secondNarration = "The pull request is merged.";
        // The FIRST reading's narration call hangs on the release; every later one answers at once. One delegate
        // serves both readings, so it has to tell them apart - and the second must not be stuck behind the first.
        //
        // THE HANGING CALL HONOURS ITS CANCELLATION TOKEN, and that is not test decoration. A reading is cancelled
        // when the session goes back to work, and the whole point of this test is the stop that arrives after that
        // edge. A fake that ignores the token models a provider that cannot be cancelled, which no real one is, and
        // it makes the reading outlive the screen it was about - so the test would be measuring the fake.
        var narratorCalls = 0;
        rig.Env.Narrator = (_, ct) =>
            Interlocked.Increment(ref narratorCalls) == 1
                ? release.Task.WaitAsync(ct)
                : Task.FromResult(Answer(secondNarration));
        rig.Voice.Mark(Tenant, Sid);
        var first = rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: true);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));

        // The person answers; the session works and stops again on a new reply.
        rig.Voice.OnSessionWorking(Tenant, Sid);
        // The Working edge cancels the old reading, and the agent then takes a turn before it stops again, so by
        // the time the new stop arrives the old reading has unwound. Waited for rather than assumed, because a
        // provider notices its cancellation when it notices it.
        //
        // A NEW STOP ARRIVING INSIDE THAT UNWIND IS A DIFFERENT CASE, AND IT IS NOT FIXED: it finds the cancelled
        // flight still on the gate, joins it, and is handed that flight's cancellation instead of a reading of its
        // own. The turn end fires once, so that stop is then never read. It is a pre-existing race - joining is by
        // session, and a cancelled flight stays registered until it unwinds - and contract v3 widens the window it
        // needs, because a reading being cancelled is a model call being cancelled rather than a store. Not fixed
        // here: it wants the admission lock reworked so a cancelled flight cannot be joined, which is not a change
        // to make in the same breath as this one.
        Assert.True(await WaitUntil(() => first.IsCompleted), "the cancelled reading never unwound");
        const string second = "I have merged the pull request.";
        rig.Env.Screen = () => Screen(Sid, second, "> ");
        rig.Env.Conversation = _ => Reply("merge it", second);
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(second, secondNarration));
        var secondTurnEnd = rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: true);
        var finishedBeforeTheOldReadingAnswered = await Task.WhenAny(secondTurnEnd, Task.Delay(TimeSpan.FromSeconds(10))) == secondTurnEnd;
        var afterSecondTurnEnd = rig.Voice.Get(Tenant, Sid)?.Spoken;

        release.SetResult(Answer(Narrated));
        await first;
        await secondTurnEnd;
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.True(finishedBeforeTheOldReadingAnswered, "the new stop's reading waited on the old stop's reading");
        Assert.Equal(secondNarration, afterSecondTurnEnd);
        Assert.Equal(2, rig.Env.JudgeCalls);
        // And the old reading, when it finally answered, was not played over the new one.
        Assert.Equal(secondNarration, rig.Voice.Get(Tenant, Sid)!.Spoken);
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
    public async Task AKeysStop_OnAnAccountWithItsOwnInstructions_IsStillToldToEndByPressingAButton()
    {
        // Custom instructions replace the whole fidelity prompt; the closing sentence for a menu lives in the code-owned
        // decision block, so it cannot be edited away.
        var rig = Build(menu: true);
        rig.Env.Custom = "Speak briefly and plainly.";
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.DoesNotContain(WingmanTranslator.FidelityPrompt.Trim(), prompt);   // CONTROL: the shipped prompt was replaced
        Assert.Contains("How the person answers: KEYS", prompt);
        Assert.Contains("end by telling the person to press a button on the phone to choose", prompt);
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
