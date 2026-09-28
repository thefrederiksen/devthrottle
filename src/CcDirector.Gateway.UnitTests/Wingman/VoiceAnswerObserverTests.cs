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
        return new WingmanVoiceService(brain, new KeyVault(Path.Combine(_dir, "vault.json")), settings, Path.Combine(_dir, "voice-sessions.json"));
    }

    private static SessionDto Row(string sid, string activity, DateTime? ownerTurn, string? workingOrigin = null) => new()
    {
        SessionId = sid,
        ActivityState = activity,
        LastOwnerTurnAtUtc = ownerTurn,
        WorkingOrigin = workingOrigin,
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
    /// anything, so the owner-turn stamp does not move - and the stop is not answered, and stays on the ledger.</summary>
    [Fact]
    public void Observe_ASessionThatResumesByItself_IsNotAnAnswer()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn1, workingOrigin: null)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn1)));

        Assert.NotNull(voice.Listening.StopFor(Tenant, sid));
    }

    /// <summary>A fleet message or another agent's prompt wakes the session: agent-driven work, owner stamp untouched.</summary>
    [Fact]
    public void Observe_ASessionWokenByAnAgent_IsNotAnAnswer()
    {
        var (voice, observer, sid) = AtAStop();

        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn1, WorkingOrigins.Agent)));

        Assert.NotNull(voice.Listening.StopFor(Tenant, sid));
    }

    [Fact]
    public void Observe_ASecondPushOfTheSameOwnerTurn_SettlesTheStopOnlyOnce()
    {
        var (_, observer, sid) = AtAStop();

        Assert.Equal(Outcome.Unheard, observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "Working", OwnerTurn2, WorkingOrigins.Owner)));
        Assert.Null(observer.Observe(Tenant, Row(sid, "WaitingForInput", OwnerTurn2)));
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
            var boundary = new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry());
            var hub = new DirectorHub(store, registry, InputStatsHandle.Available(stats), new GatewayStreamRegistry(), boundary,
                voiceAnswers: observer) { Context = new FakeHubCallerContext("conn-1") };
            hub.Hello(new DirectorStreamHello { DirectorId = "dir-A", Version = "test" });

            hub.PushSnapshot(1, new[] { Row(sid, "WaitingForInput", OwnerTurn1) });
            voice.StoreReadyAudioForTest(Tenant, sid, "Spoken.", "Reply.", Encoding.ASCII.GetBytes("ID3a"));

            hub.PushDelta(2, Row(sid, "Working", OwnerTurn1, workingOrigin: null));   // carries on by itself
            Assert.NotNull(voice.Listening.StopFor(Tenant, sid));

            hub.PushDelta(3, Row(sid, "WaitingForInput", OwnerTurn1));
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
