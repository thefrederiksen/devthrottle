using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The voice merge (the Wingman-on-every-turn mission, slice C): a narration is the stop's verdict. Counted on
/// both fakes - the judge and the translator the voice service still holds - because "one model call per
/// stop" is a claim about BOTH, and a test that counted only one could pass beside a second call on the other.
///
/// The turn-end sequence below is the host's own, in the host's order (GatewayHost.StartAsync onTurnEnd): the
/// verdict seat first, then the voice refresh for a voice session its owning session is not holding.
/// </summary>
public sealed class TurnVerdictVoiceMergeTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-voice";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private const string Spoken = "The retention sweep. The branch is pushed and the pull request is open.";
    private static readonly DateTime ObservedAt = new(2026, 9, 15, 11, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tvvm-" + Guid.NewGuid().ToString("N"));

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

    private sealed record Rig(
        WingmanVoiceService Voice,
        TurnVerdictService Verdicts,
        FakeTurnVerdictEnvironment Env,
        CountingSpeech Speech,
        CountingBrain Translator);

    private Rig Build()
    {
        Directory.CreateDirectory(_dir);
        var vault = new KeyVault(Path.Combine(_dir, "keys.vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        var env = new FakeTurnVerdictEnvironment();
        var verdicts = new TurnVerdictService(env);
        var speech = new CountingSpeech();
        // The translator the voice service still owns (the menu check uses it). Every narration it was asked
        // for before this slice is counted here, and the count must stay at zero.
        var translator = new CountingBrain(() => "a translation nobody should have asked for");
        var voice = new WingmanVoiceService((_, _, _, _) => Task.FromResult<IAgentBrain>(translator), vault, settings,
            Path.Combine(_dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(speech), turnVerdicts: verdicts);
        env.VoiceSession = sid => voice.IsVoiceSession(Tenant, sid);
        return new Rig(voice, verdicts, env, speech, translator);
    }

    private static TurnEndSignal Signal(DateTime at) => new(Sid, "dir-1", Tenant, at, IsNewTurn: true);

    /// <summary>The host's turn-end wiring for these two services, in its order.</summary>
    private static async Task HostTurnEndAsync(Rig rig, TurnEndSignal signal, SessionVerbClient route)
    {
        var judging = rig.Verdicts.StartTurnEnd(signal);
        if (rig.Voice.IsVoiceSession(signal.Tenant, signal.SessionId) && !rig.Verdicts.IsHeld(signal.Tenant, signal.SessionId))
            await rig.Voice.GenerateAsync(signal.Tenant, signal.SessionId, route, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
        await judging;
    }

    [Fact]
    public async Task AVoiceSessionsStop_CostsOneJudgeCall_AndZeroTranslatorCalls_AndItsAudioIsTheVerdictsSpokenSection()
    {
        var rig = Build();
        rig.Env.Screen = () => Screen(Sid, ReplyText, "> ");
        rig.Env.Conversation = _ => Reply("push it", ReplyText);
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, Signal(ObservedAt), RouteServing("dir-1", rig.Env.Screen));

        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Translator.Asks);
        Assert.Equal(1, rig.Speech.Calls);
        var stored = rig.Env.Latest(Tenant, Sid)!;
        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal(Spoken, ready.Spoken);
        Assert.Equal(stored.VerdictId, ready.SourceIdentity);
        Assert.Equal("agent-reply", ready.SourceKind);
        Assert.Equal(ReplyText, ready.Reply);
    }

    [Fact]
    public async Task ATerminalFailureStop_WithAnEmptyConversation_IsTerminalFailure_OnTheVerdictPathAndOnTheVoicePath_ForOneCall()
    {
        // The disagreement slice A left open: an empty conversation used to make the voice path answer "nothing
        // to narrate" without reading the screen, while the verdict path read it and found the failure.
        var rig = Build();
        rig.Env.Screen = () => Screen(Sid,
            "continue with the work",
            "Error: API key auth failed for provider openai-mindzie",
            ">");
        rig.Env.Conversation = _ => new StoredConversation(true, new List<Contracts.TurnWidgetDto>());
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.NeedsYou(
            "Testing pi. The provider rejected its application key, so the session could not answer."));
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, Signal(ObservedAt), RouteServing("dir-1", rig.Env.Screen));

        var stored = rig.Env.Latest(Tenant, Sid)!;
        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal("terminal-failure", stored.PackageKind);
        Assert.Equal("terminal-failure", ready.SourceKind);
        Assert.Contains("API key auth failed", ready.Reply);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Translator.Asks);
        Assert.Equal(1, rig.Speech.Calls);
    }

    [Fact]
    public async Task ABrandNewVoiceSession_HasNothingToNarrate_AndCostsNothing()
    {
        // The one stop the voice path still does not narrate: a session that has taken no turn. Nothing is read,
        // nobody is asked, nothing is synthesised, and the honest "nothing to narrate" is what the screen gets.
        var rig = Build();
        rig.Env.Facts = sid => new Contracts.SessionDto { SessionId = sid, ActivityState = "WaitingForInput", IsBrandNew = true };
        rig.Voice.Mark(Tenant, Sid);

        await HostTurnEndAsync(rig, Signal(ObservedAt), RouteServing("dir-1", () => Screen(Sid, "> ")));

        Assert.True(rig.Voice.NothingToNarrateFor(Tenant, Sid));
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Equal(0, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Env.ScreenReads);
        Assert.Equal(0, rig.Speech.Calls);
        Assert.Equal(0, rig.Translator.Asks);
    }

    [Fact]
    public async Task ExplainPressedWhileAStopIsQueuedBehindAClosingJudgement_NarratesTheQueuedStopsVerdict_NotTheEndingOnes()
    {
        // Issue #2905, round 2 inspection, finding 1. Explain awaited the ending judgement's completion and was handed that
        // judgement's words, although the stop on the screen was the one queued behind it and was about to be judged by the
        // successor.
        //
        // REACHED AT THE EXACT POINT: from the seam that runs once the ending judgement has closed its joined list, the
        // screen moves on, a new stop arrives and queues behind it, and Explain is pressed - all while that judgement still
        // holds the gate.
        const string firstSpoken = "The retention sweep. The first stop is finished.";
        const string secondSpoken = "The retention sweep. The second stop needs nothing either.";
        var rig = Build();
        var screen = Screen(Sid, ReplyText, "> ");
        rig.Env.Screen = () => screen;
        rig.Env.Conversation = _ => Reply("push it", ReplyText);
        var judgeCalls = 0;
        rig.Env.Judge = (_, _) => Task.FromResult(Interlocked.Increment(ref judgeCalls) == 1
            ? FakeTurnVerdictEnvironment.Finished(ReplyText, firstSpoken)
            : FakeTurnVerdictEnvironment.Finished("I have also written the release notes.", secondSpoken));
        var route = RouteServing("dir-1", () => screen);

        Task<TurnVerdictOutcome>? queued = null;
        Task<WingmanVoiceService.StopNarration>? explain = null;
        var queuedAndUnansweredWhenPressed = false;
        rig.Verdicts.OnJoinedListClosedForTests = _ =>
        {
            if (queued is not null) return;
            screen = Screen(Sid, "I have also written the release notes.", "> ");
            rig.Env.Conversation = _ => Reply("and the notes?", "I have also written the release notes.");
            queued = rig.Verdicts.StartTurnEnd(Signal(ObservedAt.AddMinutes(1)));
            explain = rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);
            queuedAndUnansweredWhenPressed = !queued.IsCompleted && !explain.IsCompleted;
        };

        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await rig.Verdicts.StartTurnEnd(Signal(ObservedAt)).WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.NotNull(explain);
        Assert.True(queuedAndUnansweredWhenPressed, "Explain was not pressed while the stop was queued behind the ending judgement");

        var narration = await explain!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TurnVerdictOutcomeKind.Judged, (await queued!.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
        Assert.Null(narration.Error);
        Assert.Contains("second stop", narration.Spoken);
        Assert.DoesNotContain("first stop", narration.Spoken);
        Assert.Equal(rig.Env.Latest(Tenant, Sid)!.VerdictId, narration.VerdictId);
        Assert.Equal(2, rig.Env.JudgeCalls);   // one call per stop: Explain asked nothing of its own
    }

    [Fact]
    public async Task ExplainOnANonVoiceSession_PlaysTheVerdict_StaysNonVoice_AndItsNextStopSynthesisesNothing()
    {
        var rig = Build();
        var screen = Screen(Sid, ReplyText, "> ");
        rig.Env.Screen = () => screen;
        rig.Env.Conversation = _ => Reply("push it", ReplyText);
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, Spoken));
        var route = RouteServing("dir-1", () => screen);
        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));   // CONTROL: not in voice mode

        var narration = await rig.Voice.NarrateStopOnRequestAsync(Tenant, Sid, route, markAsVoiceSession: false);

        Assert.Null(narration.Error);
        Assert.Equal(Spoken, narration.Spoken);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));            // it plays
        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));     // and stays non-voice
        Assert.Equal(1, rig.Speech.Calls);
        Assert.Equal(1, rig.Env.JudgeCalls);
        Assert.Equal(0, rig.Translator.Asks);

        // Its next stop, on a new screen: judged (the judge switch is on), and NOTHING is synthesised.
        screen = Screen(Sid, "I have also written the release notes.", "> ");
        rig.Env.Conversation = _ => Reply("and the notes?", "I have also written the release notes.");
        await HostTurnEndAsync(rig, Signal(ObservedAt.AddMinutes(5)), route);

        Assert.Equal(2, rig.Env.JudgeCalls);                     // CONTROL: the stop really happened and was judged
        Assert.Equal(1, rig.Speech.Calls);
        Assert.False(rig.Voice.IsVoiceSession(Tenant, Sid));
        Assert.Equal(0, rig.Translator.Asks);
    }
}
