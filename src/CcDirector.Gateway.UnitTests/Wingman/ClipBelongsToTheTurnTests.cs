using System.Net;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE AUDIO YOU CAN PLAY BELONGS TO THE TURN YOU ARE LOOKING AT (owner's rule, 2026-09-18).
///
/// The narration TEXT is permanent - written for every turn of a session the user owns. The CLIP is a rendering of
/// ONE turn, and the moment the session moves on it describes the past. The owner's words: "as soon as it starts
/// working the voices are now old voices, not relevant any more, and it's really just an audio clip."
///
/// The deletion that was supposed to enforce that runs on the Working EDGE, and this codebase documents in several
/// places that the edge is sampled and a quick turn can be missed. When it is missed, a window opens that is exactly
/// the "old voice" the owner heard: the session works unobserved, its next turn ends, the roster goes back to
/// waiting - and before the new audio is synthesised the Gateway still holds the previous clip, so the folded verdict
/// says READY and the phone plays the PREVIOUS turn as though it were this one. Both ends agree, and both are wrong.
///
/// These pin the state check that replaces the edge: a clip whose stop is not the stop in hand stops being playable
/// AT ONCE, not when its replacement arrives.
/// </summary>
public sealed class ClipBelongsToTheTurnTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Sid = "sid-clip-turn";
    private const string FirstReply = "I have pushed the branch.";
    private const string FirstSpoken = "The branch is pushed.";
    private const string SecondReply = "I have opened the pull request as well.";
    private const string SecondSpoken = "The pull request is open.";
    private static readonly DateTime ObservedAt = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cliptrn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Speech that can be HELD, so the window between "a newer stop was seen" and "its audio exists"
    /// can be inspected. That window is the whole subject here.</summary>
    private sealed class HoldableSpeech : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        public int HoldCall { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == HoldCall)
            {
                Reached.TrySetResult();
                await Release.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        }
    }

    private sealed record Rig(WingmanVoiceService Voice, TurnVerdictService Verdicts, FakeTurnVerdictEnvironment Env, HoldableSpeech Speech);

    private Rig Build()
    {
        Directory.CreateDirectory(_dir);
        var vault = new KeyVault(Path.Combine(_dir, "keys.vault"));
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_harness.Open()));
        var env = new FakeTurnVerdictEnvironment();
        // THE CLIP'S WORDS COME FROM THE NARRATION CALL from contract v3 - the judge answers no prose at all -
        // so the default narrator stands, and it answers with the words each stop was canned with. Silencing it
        // here, as this test did while the judge still wrote the words, now leaves every stop with no clip and
        // makes the question this class asks - which stop does the clip belong to - unaskable.
        var speech = new HoldableSpeech();
        var translator = new CountingBrain(() => "a translation nobody should have asked for");
        var verdicts = new TurnVerdictService(env);
        var voice = new WingmanVoiceService((_, _, _) => Task.FromResult<IAgentBrain>(translator), vault, settings,
            Path.Combine(_dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(speech), turnVerdicts: verdicts);
        env.VoiceSession = sid => voice.IsVoiceSession(Tenant, sid);
        return new Rig(voice, verdicts, env, speech);
    }

    /// <summary>Put the session on a given stop: the screen it is sitting on, and the reply the judge read.</summary>
    private static void StopIs(Rig rig, string[] rows, string reply, string spoken)
    {
        rig.Env.Screen = () => Screen(Sid, rows);
        rig.Env.Conversation = _ => Reply("do the work", reply);
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(reply, spoken));
        // SAID EXPLICITLY, because these tests assert on the WORDS a listener hears. Contract v3 leaves no prose
        // on the judge's answer at all, so those words can only come from the narration call - and leaving that
        // to the double's shared default would make this test depend on a value any sibling test can overwrite.
        rig.Env.Narrator = (_, _) => Task.FromResult(spoken);
    }

    private static async Task TurnEndAsync(Rig rig)
    {
        var signal = new TurnEndSignal(Sid, "dir-1", Tenant, ObservedAt, IsNewTurn: true);
        var judging = rig.Verdicts.StartTurnEnd(signal);
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None,
            showReadingWindow: true);
        await judging;
    }

    [Fact]
    public async Task ANewerStop_MakesThePreviousClipUnplayableAtOnce_NotWhenItsReplacementArrives()
    {
        // REVERT PROOF: restore the later-user-message CONDITION on the drop (this conversation has no later user
        // message) and this goes red - the old clip is still playable, and still says the old words, throughout the
        // whole window in which the new audio is being made.
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        StopIs(rig, new[] { "branch pushed", "> waiting" }, FirstReply, FirstSpoken);
        await TurnEndAsync(rig);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Contains(FirstSpoken, rig.Voice.Get(Tenant, Sid)!.Spoken);
        var firstVerdictId = rig.Env.Latest(Tenant, Sid)!.VerdictId;

        // The agent worked and finished another turn. NOTHING observes the Working edge here - that is the point.
        StopIs(rig, new[] { "pull request opened", "> waiting" }, SecondReply, SecondSpoken);
        rig.Speech.HoldCall = rig.Speech.Calls + 1;   // hold the NEXT synthesis, so the window stays open

        var running = TurnEndAsync(rig);
        await rig.Speech.Reached.Task;   // the new stop has been judged; its audio does not exist yet

        // THE WINDOW. This is where the phone used to be handed the previous turn's clip and told it was ready.
        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Null(rig.Voice.Get(Tenant, Sid));

        rig.Speech.Release.SetResult();
        await running;

        // And the replacement is this turn's, from this turn's stop.
        var ready = rig.Voice.Get(Tenant, Sid)!;
        Assert.Contains(SecondSpoken, ready.Spoken);
        Assert.NotEqual(firstVerdictId, ready.SourceIdentity);
        Assert.Equal(rig.Env.Latest(Tenant, Sid)!.VerdictId, ready.SourceIdentity);
    }

    [Fact]
    public async Task TheSameStopSeenAgain_KeepsItsClip_SoAListenerIsNeverCutOff()
    {
        // THE NEGATIVE CONTROL, and the rule this change must not break: issue #1322 - never pull the rug on a
        // listener. Re-narrating the SAME stop (the idle sweep comes past, the screen has not changed) must leave
        // the playable clip exactly where it is. Without this, the change above would clear audio on every sweep
        // pass and the assertion in the other test would still pass.
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        StopIs(rig, new[] { "branch pushed", "> waiting" }, FirstReply, FirstSpoken);
        await TurnEndAsync(rig);
        var first = rig.Voice.Get(Tenant, Sid)!;
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));

        // The same screen and the same reply: the stored verdict is reused, so this is the same stop.
        await rig.Voice.GenerateAsync(Tenant, Sid, RouteServing("dir-1", rig.Env.Screen), CancellationToken.None,
            showReadingWindow: false);

        Assert.True(rig.Voice.HasVoice(Tenant, Sid));
        var still = rig.Voice.Get(Tenant, Sid)!;
        Assert.Equal(first.SourceIdentity, still.SourceIdentity);
        Assert.Equal(first.Spoken, still.Spoken);
    }

    [Fact]
    public async Task TheWorkingEdge_StillClearsTheClip_WhenItIsObserved()
    {
        // The edge is not removed, only stopped being the only defence. When it IS observed it still clears the clip
        // immediately, which is what makes the play control disappear the instant a session goes blue rather than at
        // its next turn end.
        var rig = Build();
        rig.Voice.Mark(Tenant, Sid);

        StopIs(rig, new[] { "branch pushed", "> waiting" }, FirstReply, FirstSpoken);
        await TurnEndAsync(rig);
        Assert.True(rig.Voice.HasVoice(Tenant, Sid));

        rig.Voice.OnSessionWorking(Tenant, Sid);

        Assert.False(rig.Voice.HasVoice(Tenant, Sid));
        Assert.Null(rig.Voice.Get(Tenant, Sid));
    }
}
