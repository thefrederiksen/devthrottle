using System.Security.Claims;
using System.Text;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using Outcome = CcDirector.Gateway.Wingman.VoiceListeningLedger.AnswerOutcome;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 2: THE GATEWAY SEES "ANSWERED" (owner ruling 28 September 2026).
///
/// A voice session's stop is answered when its next turn begins with a message from the owner. The brief's hard
/// requirement is the control: the agent starting again BY ITSELF - a session carrying on, a fleet message waking
/// it - must never count, or a fleet of carrying-on sessions switches voice off while the owner is still in the car.
/// The Director reports the owner's submissions as <see cref="SessionDto.LastOwnerTurnAtUtc"/>, and a self-resume or
/// an agent-driven turn leaves it where it was; these pin that the observer reads exactly that, and that the stop is
/// settled once, heard or unheard, against the narration the ledger holds.
/// </summary>
public sealed class VoiceAnswerObserverTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly DateTime OwnerTurn1 = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OwnerTurn2 = new(2026, 9, 28, 9, 30, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _settingsData = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-voice-answer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _settingsData.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private WingmanVoiceService Voice()
    {
        Directory.CreateDirectory(_dir);
        Func<TenantId, Core.Configuration.WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => Task.FromResult<IAgentBrain>(null!);
        var settings = new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));
        return new WingmanVoiceService(brain, new KeyVault(Path.Combine(_dir, "vault.json")), settings, false, Path.Combine(_dir, "voice-sessions.json"));
    }

    /// <summary>The completed-turn count the session stands at while it sits at the stop under test.</summary>
    private const int TurnsAtTheStop = 3;

    private static SessionDto Row(string sid, string activity, DateTime? ownerTurn, string? workingOrigin = null, int? turns = TurnsAtTheStop) => new()
    {
        SessionId = sid,
        ActivityState = activity,
        LastOwnerTurnAtUtc = ownerTurn,
        WorkingOrigin = workingOrigin,
        TurnCount = turns,
    };

    /// <summary>A voice session sitting at a stop with a ready narration, already seen once by the observer.</summary>
    private (WingmanVoiceService Voice, VoiceAnswerObserver Observer, string Sid) AtAStop()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn1)));   // baseline
        voice.StoreReadyAudioForTest(Tenant, sid, "The branch is pushed.", "I pushed the branch.", Encoding.ASCII.GetBytes("ID3a"));
        return (voice, observer, sid);
    }

    [Fact]
    public void Observe_TheOwnerAnswersWithoutPlaying_IsUnheard()
    {
        var (_, observer, sid) = AtAStop();

        var outcome = observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner));

        Assert.Equal(Outcome.Unheard, outcome);
    }

    [Fact]
    public void Observe_TheOwnerAnswersAfterPlaying_IsHeard()
    {
        var (voice, observer, sid) = AtAStop();
        Assert.True(voice.Listening.NotePlayed(Tenant, sid, voice.Get(Tenant, sid)!.AtUtc));

        var outcome = observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner));

        Assert.Equal(Outcome.Heard, outcome);
    }

    /// <summary>THE CONTROL the brief requires: the agent carries on by itself. It works again, but nobody submitted
    /// anything, so the owner-turn stamp does not move - the stop is not answered. Its next turn has begun, so it is
    /// retired, and an owner message later is never judged against it (review of step 2).</summary>
    [Fact]
    public void Observe_ASessionThatResumesByItself_IsNotAnAnswerAndRetiresTheStop()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn1, workingOrigin: null)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn1, turns: TurnsAtTheStop + 1)));
        Assert.Null(voice.Listening.StopFor(Tenant, sid));

        Assert.Equal(Outcome.NoNarration, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner, turns: TurnsAtTheStop + 1)));
    }

    /// <summary>A fleet message or another agent's prompt wakes the session: agent-driven work, owner stamp untouched.</summary>
    [Fact]
    public void Observe_ASessionWokenByAnAgent_IsNotAnAnswerAndRetiresTheStop()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn1, WorkingOrigins.Agent)));

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
        Assert.Equal(Outcome.NoNarration, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
    }

    /// <summary>
    /// Review of step 2: pushes can be missed. The agent carries on by itself and finishes while the tunnel is down, so
    /// no Working push is ever seen; the next push already shows the owner's later message. The stop's next turn was
    /// not his - the completed-turn count gives that away - so it is retired, not judged.
    /// </summary>
    [Fact]
    public void Observe_AMissedSelfResumeThenAnOwnerMessage_IsNotAnAnswer()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner, turns: TurnsAtTheStop + 1)));

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
    }

    /// <summary>The owner's own whole turn missed - he answered and the agent finished before the next push: one turn
    /// completed, and it was his. Still his answer.</summary>
    [Fact]
    public void Observe_AMissedOwnerTurnThatHasFinished_IsStillHisAnswer()
    {
        var (_, observer, sid) = AtAStop();

        Assert.Equal(Outcome.Unheard, observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn2, turns: TurnsAtTheStop + 1)));
    }

    /// <summary>Two turns completed across the gap and one was the owner's: which came first cannot be read, so nothing
    /// is judged.</summary>
    [Fact]
    public void Observe_MoreTurnsCompletedThanTheOwnersOwn_IsNotJudged()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn2, turns: TurnsAtTheStop + 2)));

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
    }

    /// <summary>A Director that does not report the completed-turn count (older than the field), or a count that went
    /// backwards (the session restarted): the history cannot be read, so the owner's message is never judged.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(TurnsAtTheStop - 1)]
    public void Observe_ATurnCountThatCannotBeRead_IsNeverJudged(int? turns)
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner, turns: turns)));

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
    }

    /// <summary>
    /// Review of step 2: the agent resumes by itself and the Working push is missed; the next push the Gateway sees
    /// already has it waiting on a permission prompt, or still working. Either retires the stop, so the owner's
    /// approval or message that follows is not judged against it.
    /// </summary>
    [Theory]
    [InlineData("WaitingForPerm")]
    [InlineData("Working")]
    public void Observe_ASessionFirstSeenMovedOnWithoutTheOwner_RetiresTheStop(string activity)
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, activity, OwnerTurn1)));

        Assert.Null(voice.Listening.StopFor(Tenant, sid));
        Assert.Equal(Outcome.NoNarration, observer.Observe(Tenant, Row(sid, activity, OwnerTurn2, WorkingOrigins.Owner)));
    }

    /// <summary>A stop can sit at a permission prompt too (the voice sweep narrates it), and pushes that repeat that
    /// state are not the session moving on: the owner's approval is still his answer.</summary>
    [Fact]
    public void Observe_AStopAtAPermissionPromptThatTheOwnerApproves_IsHisAnswer()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForPerm", OwnerTurn1)));
        voice.StoreReadyAudioForTest(Tenant, sid, "It wants to push.", "May I push?", Encoding.ASCII.GetBytes("ID3a"));
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForPerm", OwnerTurn1)));

        Assert.Equal(Outcome.Unheard, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
    }

    [Fact]
    public void Observe_ASecondPushOfTheSameOwnerTurn_SettlesTheStopOnlyOnce()
    {
        var (_, observer, sid) = AtAStop();

        Assert.Equal(Outcome.Unheard, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn2, turns: TurnsAtTheStop + 1)));
    }

    [Fact]
    public void Observe_TheOwnerAnswersAStopWithNoNarration_JudgesNothing()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn1));

        Assert.Equal(Outcome.NoNarration, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
    }

    /// <summary>First sight - including the first push after a Gateway restart - records the stamp and judges nothing,
    /// because the Gateway did not see that owner turn happen.</summary>
    [Fact]
    public void Observe_FirstSightOfASessionWithAnOwnerTurn_JudgesNothing()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        voice.StoreReadyAudioForTest(Tenant, sid, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3a"));

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
        Assert.NotNull(voice.Listening.StopFor(Tenant, sid));
    }

    [Fact]
    public void Observe_ASessionNotOnVoice_IsNeverJudged()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();

        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn1)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
    }

    // ================================================================= through the hub

    /// <summary>
    /// The seam, end to end: a Director pushes the delta that reports the owner's submission through the real hub, and
    /// the stop is settled. The self-resuming push before it, through the same hub, settles nothing.
    /// </summary>
    [Fact]
    public void PushDelta_ThroughTheHub_SettlesTheStopOnTheOwnersTurnAndNotOnASelfResume()
    {
        var voice = Voice();
        var observer = new VoiceAnswerObserver(voice);
        var sid = Guid.NewGuid().ToString();
        voice.Mark(Tenant, sid);
        var registry = new DirectorRegistry(Path.Combine(_dir, "registry"));
        try
        {
            var store = new PushedSessionStore(() => OwnerTurn2);
            var stats = new GatewayInputStatsAggregator(Path.Combine(_dir, "gateway-stats.db"));
            var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry(), hosted: false);
            var hub = new DirectorHub(store, registry, InputStatsHandle.Available(stats), new GatewayStreamRegistry(), boundary,
                voiceAnswers: observer) { Context = new FakeHubCallerContext("conn-1") };
            hub.Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });

            hub.PushSnapshot(1, new[] { Row(sid, "WaitingForInput", OwnerTurn1) });
            voice.StoreReadyAudioForTest(Tenant, sid, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3a"));

            hub.PushDelta(2, Row(sid, "Working", OwnerTurn1, workingOrigin: null));   // carries on by itself
            Assert.Null(voice.Listening.StopFor(Tenant, sid));                       // retired, not answered

            hub.PushDelta(3, Row(sid, "WaitingForInput", OwnerTurn1));
            voice.StoreReadyAudioForTest(Tenant, sid, "Next.", "Next reply.", Encoding.ASCII.GetBytes("ID3b"));
            Assert.NotNull(voice.Listening.StopFor(Tenant, sid));
            hub.PushDelta(4, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)); // the owner answers
            Assert.Null(voice.Listening.StopFor(Tenant, sid));
        }
        finally
        {
            registry.Dispose();
        }
    }

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            Features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new HttpContextFeatureImpl { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() });
        }

        private sealed class HttpContextFeatureImpl : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
        {
            public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
