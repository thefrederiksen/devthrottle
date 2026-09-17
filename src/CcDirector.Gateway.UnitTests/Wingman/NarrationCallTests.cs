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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
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
    }

    private static string MenuSuffix => SpokenPhrases.WaitingScreenMenuNarrationSuffix.In(SpokenLanguages.English);

    // ================================================================= when the call is made

    [Fact]
    public async Task AVoiceSessionsStop_MakesExactlyOneNarrationCall_AndItsWordsReplaceTheJudgesClip()
    {
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.False(verdict.Failed);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(1, rig.Env.NarratorCalls);
        Assert.Equal(TimeSpan.FromSeconds(TurnVerdictSettings.NarrationCallTimeoutSeconds), Assert.Single(rig.Env.NarratorTimeouts));
        // Two clips for one stop: the judge's words first, so the phone was never silent, then the narration's.
        Assert.Equal(2, rig.Speech.Calls);
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

    [Fact]
    public async Task ASessionThatIsNotAVoiceSession_MakesNoNarrationCall_WhenItsStopIsNarrated()
    {
        // The model-leg retry's own shape: a narration that does not enrol the session. Its judge text is stored, and
        // nobody is listening, so there is no second call. REVERT PROOF: drop the voice-session condition on the
        // turn-end path and this goes red with one call.
        var rig = Build();

        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None,
            showReadingWindow: false, markAsVoiceSession: false);

        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(JudgeSpoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(0, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task ATurnEndOnASessionNobodyIsListeningTo_MakesNoNarrationCall()
    {
        var rig = Build();

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task Explain_OnASessionThatHadNoNarrationCall_MakesOne_AndReturnsTheJudgesWordsWithoutWaitingForIt()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;

        var narration = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);

        // Answered while the narration call is still running: the judge's words, already playable.
        Assert.Equal(JudgeSpoken, narration.Spoken);
        Assert.Equal(JudgeSpoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));

        release.SetResult(Answer(Narrated));
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Env.NarratorCalls);
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

    [Fact]
    public async Task TheMenuSentence_IsStillAppendedToTheJudgesWords()
    {
        var rig = Build(menu: true);
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal(JudgeSpoken + MenuSuffix, ready.Spoken);
    }

    [Fact]
    public async Task AFailedNarrationCall_LeavesTheJudgesWordsPlayable_AndIsNotReattempted()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => throw new TimeoutException("the narration call did not answer");
        rig.Voice.Mark(Tenant, Sid);
        var route = RouteServing("dir-1", rig.Env.Screen);

        await HostTurnEndAsync(rig, route);
        await rig.Voice.GenerateAsync(Tenant, Sid, route, CancellationToken.None, showReadingWindow: false);

        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(JudgeSpoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(1, rig.Speech.Calls);
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task ANarrationCallThatAnswersNoWords_LeavesTheJudgesWordsPlayable()
    {
        var rig = Build();
        rig.Env.Narrator = (_, _) => Task.FromResult(Answer("   "));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(JudgeSpoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        Assert.Equal(1, rig.Env.NarratorCalls);
    }

    [Fact]
    public async Task ANarrationThatAnswersAfterTheSessionWorkedAgain_IsNotStored()
    {
        var rig = Build();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Env.Narrator = (_, _) => release.Task;

        await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), markAsVoiceSession: false);
        Assert.True(await WaitUntil(() => rig.Env.NarratorCalls == 1));
        rig.Voice.OnSessionWorking(Tenant, Sid);

        release.SetResult(Answer(Narrated));
        await rig.Voice.WaitForNarrationCallsAsync();

        Assert.Equal(1, rig.Speech.Calls);
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
    public void TheNarrationBound_Is1200_AndTheJudgesSpokenFieldKeeps900()
    {
        Assert.Equal(1200, NarrationCall.MaxChars);
        Assert.Equal(900, Core.Wingman.TurnVerdictContract.MaxSpokenChars);
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

    [Fact]
    public async Task ARefusedRecordWithAMenu_IsNarratedInTheMenuShape_AndItsRowStillOffersNoButtons()
    {
        var rig = Build(menu: true);
        // The receipt is not on the screen, so the contract refuses the answer; it is still readable JSON with a menu.
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu(MenuQuestion, "a receipt the screen does not show", JudgeSpoken));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, RouteServing("dir-1", rig.Env.Screen));

        // The row: refused, and nothing on it that a phone could turn into a button.
        var verdict = rig.Env.Latest(Tenant, Sid)!;
        Assert.True(verdict.Failed);
        Assert.Equal("", verdict.AnswerVia);
        Assert.Null(verdict.Menu);
        Assert.Empty(verdict.Options);
        // The narration call: given the judge's menu as it wrote it, so it is asked for the menu shape.
        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.Contains("How the person answers: KEYS", prompt);
        Assert.Contains("The menu's question: " + MenuQuestion, prompt);
        Assert.Contains("1. Proceed (recommended) - Carries on with the change.", prompt);
        Assert.Equal(Narrated, rig.Voice.Get(Tenant, Sid)!.Spoken);
    }

    [Fact]
    public async Task AnExplainThatReusesARefusedRecordWithAMenu_IsStillGivenTheMenu()
    {
        var rig = Build(menu: true);
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Menu(MenuQuestion, "a receipt the screen does not show", JudgeSpoken));

        // Nobody is listening at the turn end: the record is judged and refused, with no narration call.
        await rig.Verdicts.StartTurnEnd(new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true));
        Assert.Equal(0, rig.Env.NarratorCalls);
        Assert.True(rig.Env.Latest(Tenant, Sid)!.Failed);

        // A voice refresh reuses that refused record (it never asks the judge again) and the call it makes is given the menu.
        rig.Voice.Mark(Tenant, Sid);
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None, showReadingWindow: false);

        Assert.Equal(1, rig.Env.JudgeCalls);
        var prompt = Assert.Single(rig.Env.NarratorPrompts);
        Assert.Contains("How the person answers: KEYS", prompt);
        Assert.Contains("The menu's question: " + MenuQuestion, prompt);
    }
}
