using System.Security.Claims;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// NO RED FRAME BEFORE THE WINGMAN READS (the Wingman-on-every-turn mission, owner ruling 2026-09-15, slice E round 2).
///
/// Driven through the REAL hub, the REAL turn-end watcher, the REAL verdict seat and the REAL fold, with a recording
/// sender standing where the Director's tunnel is. What is asserted is the ORDER of the colours the fold hands the push
/// for one session - not only the colour it ends on - because the defect was a red frame pushed BEFORE the yellow one,
/// and a final colour cannot see a frame that came and went.
///
/// The seat's world is the faked environment (no clock, no tunnel, no model), except the roster it reads, which is the
/// push store the hub just wrote. The judge waits for the test to release it, so "while the Wingman reads" is a state
/// the test holds open rather than a race it hopes to catch.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class NoRedFrameBeforeTheWingmanReadsTests : IDisposable
{
    private const string Sid = "sid-stop";
    private const string Dir = "dir-A";
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);
    private static readonly TenantId Tenant = TenantId.Local;

    private readonly string _tempDir;
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _store;
    private readonly GatewayInputStatsAggregator _inputStats;
    private readonly DateTime _now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly TaskCompletionSource _judgeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string> _judgeAnswer = () => FakeTurnVerdictEnvironment.Finished(ReplyText, "The branch is pushed.");
    private readonly List<string> _pushed = new();
    private int _turnEnds;

    private FakeTurnVerdictEnvironment _env = null!;
    private TurnVerdictService _service = null!;
    private TurnEndWatcher _watcher = null!;
    private FleetDisplayStateObserver _display = null!;
    private DirectorHub _hub = null!;

    public NoRedFrameBeforeTheWingmanReadsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-no-red-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
        _store = new PushedSessionStore(() => _now);
        _inputStats = new GatewayInputStatsAggregator(Path.Combine(_tempDir, "gateway-stats.db"));
    }

    public void Dispose()
    {
        _judgeRelease.TrySetResult();
        _service?.Dispose();
        _watcher?.Dispose();
        _registry.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    // ================================================================= the rig

    /// <summary>The verdict rows the fold reads: the account's colours on, the seat's reading state, its stored verdicts.</summary>
    private sealed class SeatRows(FakeTurnVerdictEnvironment env, TurnVerdictService service) : ITurnVerdictRowSource
    {
        public bool ColourEnabled(TenantId tenant) => true;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => env.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => service.IsReading(tenant, sessionId);
    }

    /// <summary>
    /// Build the rig and bring the session to Working: the Director's snapshot, then a Working delta the watcher sees.
    /// Everything the fold pushes from here on is recorded, in order, as "colour/label".
    /// </summary>
    private void Start(Action<FakeTurnVerdictEnvironment>? arrange = null)
    {
        _env = new FakeTurnVerdictEnvironment
        {
            Screen = () => Screen(Sid, ReplyText, "> "),
            Conversation = _ => Reply("push it", ReplyText),
            Judge = async (_, ct) =>
            {
                await _judgeRelease.Task.WaitAsync(ct);
                return _judgeAnswer();
            },
            // The roster the seat reads is the one the hub has just accepted.
            Facts = sid => _store.SnapshotFresh(Tenant, Stale).Select(r => r.Session).FirstOrDefault(s => s.SessionId == sid),
        };
        arrange?.Invoke(_env);
        _service = new TurnVerdictService(_env);

        _watcher = new TurnEndWatcher(
            onTurnEnd: signal =>
            {
                Interlocked.Increment(ref _turnEnds);
                _service.OnTurnEnd(signal);
            },
            onSessionWorking: (tenant, sid, _) => _service.OnSessionWorking(tenant, sid),
            pushedSessions: _store,
            streamStale: Stale);

        _display = new FleetDisplayStateObserver(
            () => _store.SnapshotFresh(Tenant, Stale),
            rows => GatewayEndpoints.StampFleetRolesAndFold(rows, rows, needsYouStampFor: null, snoozeRegistry: null,
                tenant: Tenant, handRaises: null, turnVerdictRows: new SeatRows(_env, _service)),
            RecordPush);

        _hub = new DirectorHub(_store, _registry, InputStatsHandle.Available(_inputStats), new GatewayStreamRegistry(),
            SelfHostBoundary(), fleetDisplayState: _display, turnEnds: _watcher) { Context = new FakeHubCallerContext("conn-1") };
        _hub.Hello(new DirectorStreamHello { DirectorId = Dir, Version = "test" });
        _hub.PushSnapshot(1, new[] { Row("Working") });
        _hub.PushDelta(2, Row("Working"));
    }

    private Task<DirectorCommandResult?> RecordPush(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SetDisplayStateRequest>(command.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (command.SessionId == Sid)
            lock (_pushed) _pushed.Add($"{payload.EffectiveColor}/{payload.StateLabel}");
        return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
    }

    private string[] Pushed()
    {
        lock (_pushed) return _pushed.ToArray();
    }

    private static SessionDto Row(string activity) => new()
    {
        SessionId = Sid,
        Name = "devthrottle - the retention sweep",
        Agent = "ClaudeCode",
        ActivityState = activity,
    };

    private static CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry());

    // ================================================================= the four the plan names

    [Fact]
    public async Task AStopTheWingmanWillJudge_FirstPushesYellow_NeverRed_ThenTheVerdictsColour()
    {
        Start();
        Assert.Equal(new[] { "blue/Working" }, Pushed());   // CONTROL: the rig pushes, and the session is working

        _hub.PushDelta(3, Row("WaitingForInput"));

        // The push the turn end produced, in order. A stamp made after the screen read would put "red/Needs you" here.
        Assert.Equal(new[] { "blue/Working", "yellow/Wingman reading" }, Pushed());
        Assert.True(_service.IsReading(Tenant, Sid));
        Assert.Equal(1, _turnEnds);

        _judgeRelease.SetResult();
        Assert.True(await WaitUntil(() => !_service.IsReading(Tenant, Sid) && _env.StoredCount(Tenant, Sid) == 1),
            "the judgement did not finish");
        _display.Sweep();

        var pushed = Pushed();
        Assert.StartsWith("green/", pushed[^1]);
        Assert.DoesNotContain(pushed, p => p.StartsWith("red/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJudgeThatFails_EndsRed()
    {
        Start();
        _judgeAnswer = () => throw new HttpRequestException("No connection could be made.");

        _hub.PushDelta(3, Row("WaitingForInput"));
        Assert.Equal("yellow/Wingman reading", Pushed()[^1]);

        _judgeRelease.SetResult();
        Assert.True(await WaitUntil(() => !_service.IsReading(Tenant, Sid) && _env.StoredCount(Tenant, Sid) == 1),
            "the failed judgement did not finish");
        Assert.True(_env.Latest(Tenant, Sid)!.Failed);   // CONTROL: it really failed
        _display.Sweep();

        Assert.Equal(new[] { "blue/Working", "yellow/Wingman reading", "red/Needs you" }, Pushed());
    }

    [Fact]
    public async Task AHeldStop_IsRedFromTheFirstPush()
    {
        Start(env => env.Held = _ => true);

        _hub.PushDelta(3, Row("WaitingForInput"));

        Assert.Equal(new[] { "blue/Working", "red/Needs you" }, Pushed());
        Assert.False(_service.IsReading(Tenant, Sid));
        Assert.True(await WaitUntil(() => _env.Records.Any(r => r.Cause == ActivityCauses.Held)), "the held skip was not recorded");
        Assert.Equal(0, _env.ScreenReads);
        Assert.Equal(0, _env.JudgeCalls);
    }

    [Fact]
    public async Task AWorkingTransitionWhileTheWingmanReads_ClearsReading()
    {
        Start();
        _hub.PushDelta(3, Row("WaitingForInput"));
        Assert.True(_service.IsReading(Tenant, Sid));   // CONTROL: it really is reading

        _hub.PushDelta(4, Row("Working"));

        Assert.False(_service.IsReading(Tenant, Sid));
        Assert.Equal(new[] { "blue/Working", "yellow/Wingman reading", "blue/Working" }, Pushed());
        Assert.True(await WaitUntil(() => _env.Records.Any(r => r.EventType == ActivityEventTypes.TurnVerdictCancelled)),
            "the judgement was not cancelled");
    }

    // ================================================================= the reconcile sweep replays nothing

    [Fact]
    public async Task TheReconcileSweep_AfterAStopTheHubAlreadyFed_RaisesNoSecondTurnEnd()
    {
        Start();
        _hub.PushDelta(3, Row("WaitingForInput"));
        Assert.Equal(1, _turnEnds);

        await _watcher.SweepAsync(sweepAll: false);

        Assert.Equal(1, _turnEnds);

        // CONTROL: the sweep does raise a turn end for a stop its watcher has not seen - the same store, read by a
        // watcher with no memory of it - so the no-op above is the memory, not a sweep that reads nothing.
        var unseen = 0;
        using var fresh = new TurnEndWatcher(_ => Interlocked.Increment(ref unseen), (_, _, _) => { },
            pushedSessions: _store, streamStale: Stale);
        await fresh.SweepAsync(sweepAll: false);
        Assert.Equal(1, unseen);
    }

    // ================================================================= the fake SignalR caller

    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId)
        {
            ConnectionId = connectionId;
            // The self-host boundary resolves Local only for a connection that has an HttpContext, as a real negotiate does.
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
