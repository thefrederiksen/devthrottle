using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// What the Wingman tab shows, proven THROUGH THE REAL WRITE PATH (the Wingman inspector, phase 2, rulings 1 and 2): the
/// real <see cref="TurnVerdictService"/> judges and expires, the real <see cref="TurnVerdictTraceWriter"/> stamps each
/// trace with the real <see cref="TurnVerdictTraceRowStamp"/> over a real <see cref="PushedSessionStore"/> and the real
/// roster fold, the real <see cref="TurnVerdictTraceStore"/> keeps it, and the real handler serves it. No trace here is
/// built by hand - a hand-built trace with a colour on it would prove only that the fold repeats what it was given.
///
/// WHAT IS STILL A DOUBLE: the seat's environment (<see cref="FakeTurnVerdictEnvironment"/>, which holds the verdicts in
/// memory and answers for the judge) and the live verdict source over it. The production wiring of the stamp - the
/// display push's own fold, handed in by the host - is proven by the hosted route test in Gateway.Tests.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanStopsWritePathTests : IDisposable
{
    private static readonly TenantId Account = TenantId.Local;
    private static readonly string Sid = Guid.NewGuid().ToString();
    private const string ReplyText = "I have pushed the branch and opened the pull request.";
    private static readonly DateTime JudgedAt = new(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    private DeviceRegistry? _devices;

    public void Dispose()
    {
        _devices?.Dispose();
        _harness.Dispose();
    }

    // This test's own device store: the parameterless registry opens the default one, which every test class shares.
    private DeviceRegistry Devices()
    {
        if (_devices is null)
        {
            var path = _harness.LegacyPath("devices.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _devices = new DeviceRegistry(path);
        }
        return _devices;
    }

    /// <summary>The live verdict source over the seat's own verdicts, as the production row source is over the store.</summary>
    private sealed class EnvRows : ITurnVerdictRowSource
    {
        private readonly FakeTurnVerdictEnvironment _env;
        public EnvRows(FakeTurnVerdictEnvironment env) => _env = env;
        public bool ColourEnabled(TenantId tenant) => _env.Knobs.ColourEnabled;
        public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant) => _env.SnapshotLatest(tenant);
        public bool IsReading(TenantId tenant, string sessionId) => false;
    }

    private sealed record Rig(
        FakeTurnVerdictEnvironment Env,
        TurnVerdictService Service,
        TurnVerdictTraceWriter Writer,
        TurnVerdictTraceStore Store,
        PushedSessionStore Pushed,
        EnvRows Rows);

    private static void RosterFold(TenantId tenant, List<SessionDto> sessions, ITurnVerdictRowSource rows)
        => GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, tenant: tenant, turnVerdictRows: rows);

    private Rig Build(params string[] rosterSessionIds)
    {
        var env = new FakeTurnVerdictEnvironment
        {
            Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            Screen = () => Screen(Sid, ReplyText, "> "),
            Conversation = _ => Reply("push it", ReplyText),
            Clock = () => JudgedAt,
        };
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Account, "director-write-path", "conn-write-path");
        Assert.True(pushed.ApplySnapshot(Account, "director-write-path", "conn-write-path", 1,
            rosterSessionIds.Select(id => new SessionDto { SessionId = id, Name = id, ActivityState = "WaitingForInput", LastActivityAt = DateTime.UtcNow }).ToList()));
        var rows = new EnvRows(env);
        var store = new TurnVerdictTraceStore(_harness.Open());
        var stamp = new TurnVerdictTraceRowStamp(t => pushed.SnapshotConnected(t), rows, RosterFold);
        var writer = new TurnVerdictTraceWriter(store.Append, stamp.Stamp);
        env.TraceWriter = writer;
        return new Rig(env, new TurnVerdictService(env), writer, store, pushed, rows);
    }

    private WingmanStopsResponse Read(Rig rig)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[CcDirector.Gateway.Util.AuthMiddleware.AuthenticatedDeviceItemKey] =
            new DeviceCredentialIdentity("device-1", null, "phone", "active");
        var result = GatewayEndpoints.ReadWingmanStops(ctx, Sid, null,
            new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new SingleTenantContext(), Devices()), rig.Store, rig.Pushed);
        return Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<WingmanStopsResponse>>(result).Value!;
    }

    /// <summary>The colour the row wears NOW, by the same fold - the control that proves the row really moved.</summary>
    private static string ColourNow(Rig rig)
    {
        var sessions = rig.Pushed.SnapshotConnected(Account).Select(r => r.Session).ToList();
        RosterFold(Account, sessions, rig.Rows);
        return sessions.Single(s => s.SessionId == Sid).EffectiveColor!;
    }

    private static TurnVerdictDto CarryingOn(DateTime judgedAt) => new()
    {
        VerdictId = "v-carrying-on",
        JudgedAtUtc = judgedAt,
        TurnEndObservedAtUtc = judgedAt.AddSeconds(-5),
        ScreenHash = "old-screen",
        Model = FakeTurnVerdictEnvironment.Model,
        ContractVersion = "v2",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Evidence = ReplyText,
        Label = "Watching the test run",
        Summary = "It is watching the test run by itself.",
        AnswerVia = "reply",
        Options = new List<TurnVerdictOptionDto>(),
        Risk = "none",
        Spoken = "It is watching the test run.",
    };

    /// <summary>The judge's "carrying-on" answer in the contract v3 shape - five fields, and none of the
    /// seven the owner cut on 2026-09-18. It is built through the canned builder so the narration call that
    /// now runs inside the same reading answers with these words.</summary>
    private static string ContinuesAloneAnswer()
        => FakeTurnVerdictEnvironment.CarryingOn("Watching the test run", "It is watching the test run and will report back.");

    [Fact]
    public async Task A_stop_on_a_session_that_is_not_on_the_roster_records_no_colour_and_says_not_recorded()
    {
        // The roster holds another session; the judged one has gone from it.
        var rig = Build(Guid.NewGuid().ToString());
        rig.Env.Judge = (_, _) => Task.FromResult(FakeTurnVerdictEnvironment.Finished(ReplyText, "The branch is pushed."));

        await rig.Service.StartTurnEnd(new TurnEndSignal(Sid, "director-write-path", Account, JudgedAt, IsNewTurn: true));
        await rig.Writer.CompleteAsync();

        var trace = Assert.Single(rig.Store.History(Account, Sid));
        Assert.Null(trace.RowColour);
        Assert.Null(trace.RowLabel);
    }

    [Fact]
    public async Task A_carrying_on_judgement_records_no_clock_because_there_is_none()
    {
        var rig = Build(Sid);
        rig.Env.Judge = (_, _) => Task.FromResult(ContinuesAloneAnswer());
        rig.Env.Owned = _ => new OwnedSessionsFacts(Working: 1, Live: 1, Stopped: 0, NeedYou: 0, LastActivityAtUtc: JudgedAt);

        await rig.Service.StartTurnEnd(new TurnEndSignal(Sid, "director-write-path", Account, JudgedAt, IsNewTurn: true));
        await rig.Writer.CompleteAsync();

        // The carrying-on clock was removed (owner ruling, 2026-09-28): a new carrying-on stop records no deadline and
        // the inspector describes no clock for it.
        var stop = Assert.Single(Read(rig).Stops);
        Assert.Null(stop.Did.Clock);
    }

}
