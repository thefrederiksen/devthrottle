using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// ISSUE #2992: THE CARRYING-ON CLOCK MUST NOT MARK AN OWNER RED WHILE ITS WORKER IS INSIDE A LONG SILENT COMMAND.
///
/// On 2026-09-16 an Architect said it would wait for its Worker and was judged continues-alone. The Worker then ran
/// one foreground command that printed nothing for about eight minutes. Its terminal went quiet, the terminal-state
/// detector called it waiting after ten seconds, and the clock - reading "is any owned session working" and "when
/// did one last write" off the terminal - marked the Architect "Said it would continue and did not" while the
/// Worker's turn was still open.
///
/// The regression runs the issue's timeline through the REAL sweep: <see cref="TurnVerdictService.ExpireCarryingOn"/>
/// over the PRODUCTION environment, the real push store holding the silent Worker, the real turn store holding what
/// the Director pushed, and the real verdict store. Only the clock is injected. The moments are the issue's own.
///
/// NOT PROVEN HERE: that the Director pushes "Working" for a turn with an open tool call at the moment the terminal
/// goes quiet. That is <c>HistoryStateDeriver</c> and <c>TurnPusher</c> on the Director, covered by their own tests;
/// this file starts from the head the Gateway stores.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class CarryingOnClockOwnedTurnTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private const string Architect = "71f6faae-0000-4000-8000-000000000001";
    private const string Worker = "3f511b7d-0000-4000-8000-000000000002";

    // The issue's timeline, UTC.
    private static readonly DateTime ArchitectStopped = new(2026, 9, 17, 0, 45, 31, DateTimeKind.Utc);
    private static readonly DateTime Judged = new(2026, 9, 17, 0, 45, 50, DateTimeKind.Utc);
    private static readonly DateTime WorkerLastTerminalWrite = new(2026, 9, 17, 1, 8, 14, DateTimeKind.Utc);
    private static readonly DateTime CommandStarted = new(2026, 9, 17, 1, 9, 53, DateTimeKind.Utc);
    private static readonly DateTime ClockExpiredOnTheOldCode = new(2026, 9, 17, 1, 18, 14, DateTimeKind.Utc);
    private static readonly DateTime CommandFinished = new(2026, 9, 17, 1, 17, 47, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SessionDto Row(string sid, string activityState, DateTime lastActivity, string? controller = null) => new()
    {
        SessionId = sid,
        Name = "a session named " + sid,
        Agent = "ClaudeCode",
        ActivityState = activityState,
        Status = "Running",
        RepoPath = "repo",
        CreatedAt = ArchitectStopped.AddHours(-1),
        LastActivityAt = lastActivity,
        IsControlled = controller is not null,
        ControllerSessionId = controller,
    };

    private static TurnVerdictDto CarryingOn() => new()
    {
        VerdictId = "carry-2992",
        JudgedAtUtc = Judged,
        TurnEndObservedAtUtc = ArchitectStopped,
        ScreenHash = "hash-2992",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Evidence = "I will wait for the Worker's message.",
        Label = "Waiting for its Worker",
        Summary = "It is waiting for its Worker to report.",
        AnswerVia = "reply",
        Risk = "none",
        Spoken = "It is waiting for its Worker to report.",
    };

    private static PushedTurn Turn(int ordinal, string role, DateTime at, HistoryPartDto part) => new()
    {
        Ordinal = ordinal,
        Role = role,
        Parts = { part },
        Timestamp = new DateTimeOffset(at),
    };

    private static TurnPushBatch Push(string historyState, params PushedTurn[] turns) => new()
    {
        SessionId = Worker,
        Generation = @"C:\transcripts\worker.jsonl",
        GenerationStartedUtc = ArchitectStopped.AddHours(-1),
        Agent = "ClaudeCode",
        HistoryState = historyState,
        StartOrdinal = turns.Length == 0 ? 0 : turns[0].Ordinal,
        TotalCount = turns.Length == 0 ? 0 : turns[^1].Ordinal + 1,
        Turns = turns.ToList(),
    };

    private sealed record Rig(TurnVerdictService Seat, GatewayTurnVerdictEnvironment Env, SessionTurnStore Turns);

    private Rig Build(Func<DateTime> clock)
    {
        // The Worker as the Director's terminal reports it: quiet for ten seconds, so WaitingForInput, and a last
        // write from before the command started. This row never changes below - the terminal is silent throughout.
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
        Assert.True(pushed.ApplySnapshot(Tenant, "dir-1", "conn-1", 1, new[]
        {
            Row(Architect, "WaitingForInput", ArchitectStopped),
            Row(Worker, "WaitingForInput", WorkerLastTerminalWrite, controller: Architect),
        }));

        var turns = new SessionTurnStore(_harness.Open());
        var env = new GatewayTurnVerdictEnvironment(
            settings: _ => TurnVerdictSettings.Defaults with { JudgeEnabled = true, ColourEnabled = true, SettleMs = 0 },
            pushedSessions: pushed,
            streamStale: Stale,
            route: (_, directorId) => RouteServing(directorId, () => null),
            conversation: (_, _) => null,
            judgeBrain: (_, _) => throw new InvalidOperationException("the clock asks no model"),
            judgeModel: _ => FakeTurnVerdictEnvironment.Model,
            store: new TurnVerdictStore(_harness.Open()),
            traces: new TurnVerdictTraceWriter((_, _) => { }),
            language: _ => SpokenLanguages.English,
            customSpokenRules: () => null,
            isVoiceSession: (_, _) => false,
            fleetManagerSessionId: _ => null,
            // The production reader, over the real turn store.
            turnState: (_, sid) => SessionTurnState.From(turns.ReadHead(sid)),
            nowUtc: clock);
        env.Store(Tenant, Architect, CarryingOn());
        return new Rig(new TurnVerdictService(env), env, turns);
    }

    /// <summary>The Director's push at its own turn-end edge, ten seconds into the silent command: the transcript's
    /// last message is the assistant's tool call with no result, so the state is Working.</summary>
    private static void WorkerStartsTheSilentCommand(SessionTurnStore turns)
        => turns.Append("dir-1", Push("Working",
                Turn(0, "User", CommandStarted.AddMinutes(-2), new HistoryPartDto { Kind = "Text", Text = "run the gateway suite" }),
                Turn(1, "Assistant", CommandStarted, new HistoryPartDto { Kind = "ToolUse", ToolName = "Bash", ToolId = "toolu_2992", Text = "{\"command\":\"test-local.ps1 -Parked\"}" })),
            CommandStarted.AddSeconds(10));

    [Fact]
    public void ExpireCarryingOn_AWorkerInsideALongSilentCommand_KeepsItsOwnerPurple()
    {
        var now = Judged;
        var rig = Build(() => now);
        WorkerStartsTheSilentCommand(rig.Turns);

        // CONTROL: on the Worker's terminal alone - nothing working, last write 01:08:14 - the owner ran out at
        // 01:18:14, the moment the issue records.
        var terminalOnly = new OwnedSessionsFacts(Working: 0, Stopped: 1, NeedYou: 0, InTurn: 0, LastStoppedAtUtc: WorkerLastTerminalWrite);
        Assert.Equal(ClockExpiredOnTheOldCode, TurnVerdictWatchdog.DeadlineFor(CarryingOn(), terminalOnly));

        // The moment it went red on the old code, and then well over ten minutes of silence after it.
        foreach (var at in new[] { ClockExpiredOnTheOldCode, ClockExpiredOnTheOldCode.AddMinutes(12), CommandStarted.AddMinutes(45) })
        {
            now = at;
            Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));
            var latest = rig.Env.Latest(Tenant, Architect)!;
            Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, latest.Verdict);
            Assert.NotEqual(TurnVerdictWatchdog.ExpiredLabel, latest.Label);
        }

        // CONTROL: the roster really did say the Worker was not working, the whole time.
        var owned = rig.Env.OwnedSessions(Tenant, Architect)!;
        Assert.Equal(0, owned.Working);
        Assert.Equal(1, owned.InTurn);
    }

    [Fact]
    public void ExpireCarryingOn_TheWorkersTurnEnds_TheOwnerGoesRedTenMinutesLater()
    {
        var now = Judged;
        var rig = Build(() => now);
        WorkerStartsTheSilentCommand(rig.Turns);

        now = ClockExpiredOnTheOldCode;
        Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));

        // The command returns, the Worker writes its report and its turn ends; the Director pushes at the quiet edge.
        var turnEndRecorded = CommandFinished.AddSeconds(40);
        rig.Turns.Append("dir-1", Push("NeedsYou",
                Turn(2, "User", CommandFinished, new HistoryPartDto { Kind = "ToolResult", ToolId = "toolu_2992", Text = "1634 passed" }),
                Turn(3, "Assistant", CommandFinished.AddSeconds(30), new HistoryPartDto { Kind = "Text", Text = "The suite is green." })),
            turnEndRecorded);

        now = turnEndRecorded.AddMinutes(10).AddSeconds(-1);
        Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, rig.Env.Latest(Tenant, Architect)!.Verdict);

        now = turnEndRecorded.AddMinutes(10);
        Assert.Equal(1, rig.Seat.ExpireCarryingOn(Tenant));
        var latest = rig.Env.Latest(Tenant, Architect)!;
        Assert.Equal(TurnVerdictVocabulary.NeededYou, latest.Verdict);
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, latest.Label);
    }

    // ================================================================= the turn signal, by itself

    private static SessionTurnHeadEntity Head(string? historyState, bool supported = true) => new()
    {
        SessionId = Worker,
        HistoryState = historyState,
        IsSupported = supported,
        UpdatedAtUtc = CommandStarted,
    };

    [Theory]
    [InlineData("Working", true)]
    [InlineData("BackgroundRunning", true)]
    [InlineData("NeedsYou", false)]
    [InlineData("Idle", false)]
    public void From_AKnownState_SaysWhetherTheTurnIsOpen_AndWhenItWasRecorded(string state, bool inTurn)
    {
        var turn = SessionTurnState.From(Head(state));

        Assert.NotNull(turn);
        Assert.Equal(inTurn, turn!.InTurn);
        Assert.Equal(CommandStarted, turn.RecordedAtUtc);
    }

    [Fact]
    public void From_NoHead_AnUnreadableAgent_NoState_OrAnUnknownState_IsNoSignal()
    {
        Assert.Null(SessionTurnState.From(null));
        Assert.Null(SessionTurnState.From(Head("Working", supported: false)));
        Assert.Null(SessionTurnState.From(Head(null)));
        Assert.Null(SessionTurnState.From(Head("Thinking")));
    }

    // ================================================================= the owned facts the clock reads

    private static List<(string DirectorId, SessionDto Session)> Roster(string workerTerminal) => new()
    {
        ("dir-1", Row(Architect, "WaitingForInput", ArchitectStopped)),
        ("dir-1", Row(Worker, workerTerminal, WorkerLastTerminalWrite, controller: Architect)),
    };

    [Fact]
    public void For_ATurnSignal_OverrulesTerminalSilence_AndItsRecordedMomentIsTheStop()
    {
        var open = TurnVerdictOwnedSessions.For(Roster("WaitingForInput"), Architect, _ => new SessionTurnState(true, CommandStarted));
        Assert.Equal((0, 1), (open!.Working, open.InTurn));

        var ended = TurnVerdictOwnedSessions.For(Roster("WaitingForInput"), Architect, _ => new SessionTurnState(false, CommandFinished));
        Assert.Equal(0, ended!.InTurn);
        Assert.Equal(CommandFinished, ended.LastStoppedAtUtc);
    }

    [Fact]
    public void For_ATerminalThatIsWriting_IsInsideATurn_EvenBeforeThePushSaysSo()
    {
        var facts = TurnVerdictOwnedSessions.For(Roster("Working"), Architect, _ => new SessionTurnState(false, CommandStarted));

        Assert.Equal(1, facts!.InTurn);
    }

    [Theory]
    [InlineData("Working", 1)]
    [InlineData("WaitingForInput", 0)]
    public void For_AnAgentWithNoTurnSignal_ReadsItsTerminal_ByRule(string terminal, int inTurn)
    {
        var facts = TurnVerdictOwnedSessions.For(Roster(terminal), Architect, _ => null);

        Assert.Equal(inTurn, facts!.InTurn);
        Assert.Equal(WorkerLastTerminalWrite, facts.LastStoppedAtUtc);
    }
}
