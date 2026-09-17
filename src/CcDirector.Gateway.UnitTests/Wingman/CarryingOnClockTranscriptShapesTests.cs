using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Claude;
using CcDirector.Core.History;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using static CcDirector.Gateway.Tests.Wingman.TurnVerdictTestDoubles;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// ISSUE #2992, ROUND 2: WHICH OWNED-SESSION STOPS HOLD AN OWNER PURPLE, FROM REAL TRANSCRIPT SHAPES.
///
/// Round 1 read "Working" off the Director's transcript state and counted it as a turn in progress. That state says
/// "Working" for a tool call waiting on a PERSON (a question, a plan approval, a permission prompt) and for a
/// conversation whose last line is an interrupt - all of them silent, none of them the agent working. Each held its
/// owner purple forever, where before the change the owner went red ten minutes after the Worker went quiet.
///
/// Every test here starts from transcript lines in the shape Claude Code writes them, written to a file and read
/// through the Director's own path: <see cref="ClaudeTranscriptReader"/> for the messages,
/// <see cref="TurnPushBuilder.Map"/> for the pushed turns, and <see cref="HistoryStateDeriver"/> for the state. The
/// batch goes into the real turn store, and the clock runs through the real sweep over the production environment.
/// The one thing injected is the reader the host hands the environment; the host's own wiring of it is proved in
/// Gateway.Tests (<c>CarryingOnClockHostWiringTests</c>).
///
/// The shapes are from this machine's transcripts: an interrupt is a user line whose text is exactly
/// "[Request interrupted by user]" or "[Request interrupted by user for tool use]", the second following an error
/// tool result carrying the same words.
/// </summary>
public sealed class CarryingOnClockTranscriptShapesTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private const string Architect = "71f6faae-0000-4000-8000-00000000a001";
    private const string Worker = "3f511b7d-0000-4000-8000-00000000a002";

    private static readonly DateTime ArchitectStopped = new(2026, 9, 17, 0, 45, 31, DateTimeKind.Utc);
    private static readonly DateTime Judged = new(2026, 9, 17, 0, 45, 50, DateTimeKind.Utc);
    private static readonly DateTime Prompted = new(2026, 9, 17, 1, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WorkerLastTerminalWrite = new(2026, 9, 17, 1, 8, 14, DateTimeKind.Utc);
    private static readonly DateTime ToolCalled = new(2026, 9, 17, 1, 9, 53, DateTimeKind.Utc);
    private static readonly DateTime WentQuiet = new(2026, 9, 17, 1, 9, 55, DateTimeKind.Utc);
    private static readonly DateTime QuietEdgePush = WentQuiet.AddSeconds(10);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _transcript =
        Path.Combine(Path.GetTempPath(), "cc-2992-worker-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly List<string> _lines = new();
    private long _rosterSequence;

    public void Dispose()
    {
        _harness.Dispose();
        try { File.Delete(_transcript); } catch { /* best effort */ }
    }

    // ================================================================= transcript lines, as Claude Code writes them

    private static string Line(string type, DateTime at, object content) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["parentUuid"] = null,
        ["isSidechain"] = false,
        ["type"] = type,
        ["message"] = new Dictionary<string, object> { ["role"] = type, ["content"] = content },
        ["uuid"] = Guid.NewGuid().ToString(),
        ["timestamp"] = at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["sessionId"] = "c1a0de00-0000-4000-8000-000000002992",
    });

    private static string Prompt(DateTime at, string text) => Line("user", at, text);

    private static string UserText(DateTime at, string text)
        => Line("user", at, new[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } });

    private static string AssistantText(DateTime at, string text)
        => Line("assistant", at, new[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } });

    private static string ToolCall(DateTime at, string id, string name, object input)
        => Line("assistant", at, new[] { new Dictionary<string, object> { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input } });

    private static string ToolResult(DateTime at, string id, string text, bool isError = false)
        => Line("user", at, new[] { new Dictionary<string, object> { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text, ["is_error"] = isError } });

    // ================================================================= the rig

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
        VerdictId = "carry-2992-r2",
        JudgedAtUtc = Judged,
        TurnEndObservedAtUtc = ArchitectStopped,
        ScreenHash = "hash-2992-r2",
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

    private sealed record Rig(TurnVerdictService Seat, GatewayTurnVerdictEnvironment Env, SessionTurnStore Turns, PushedSessionStore Roster);

    private Rig Build(Func<DateTime> clock, string workerTerminal, DateTime workerLastWrite)
    {
        var pushed = new PushedSessionStore();
        pushed.RegisterConnection(Tenant, "dir-1", "conn-1");
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
            turnTail: Reader(turns),
            nowUtc: clock);
        env.Store(Tenant, Architect, CarryingOn());
        var rig = new Rig(new TurnVerdictService(env), env, turns, pushed);
        Terminal(rig, workerTerminal, workerLastWrite);
        return rig;
    }

    /// <summary>The reader the host hands the environment, over the real turn store.</summary>
    private static Func<TenantId, string, SessionTurnTail?> Reader(SessionTurnStore turns)
        => (_, sid) => turns.ReadTail(sid, SessionTurnState.TailLength);

    /// <summary>What the Director's terminal detector reports for the Worker.</summary>
    private void Terminal(Rig rig, string workerTerminal, DateTime workerLastWrite)
        => Assert.True(rig.Roster.ApplySnapshot(Tenant, "dir-1", "conn-1", ++_rosterSequence, new[]
        {
            Row(Architect, "WaitingForInput", ArchitectStopped),
            Row(Worker, workerTerminal, workerLastWrite, controller: Architect),
        }));

    /// <summary>
    /// Claude Code writes these lines, then the Director pushes the conversation exactly as it does in production:
    /// the messages read by the transcript reader, mapped to pushed turns, and the state derived from the same file
    /// for a live process. A push repeats every turn already held, which the store skips.
    /// </summary>
    private void DirectorPushes(Rig rig, DateTime pushedAt, params string[] newLines)
    {
        _lines.AddRange(newLines);
        File.WriteAllLines(_transcript, _lines);

        var turns = TurnPushBuilder.Map(ClaudeTranscriptReader.Read(_transcript).MainThread.Messages);
        var state = HistoryStateDeriver.DeriveFromFile(_transcript, isProcessAlive: true).ToString();
        rig.Turns.Append("dir-1", new TurnPushBatch
        {
            SessionId = Worker,
            Generation = _transcript,
            GenerationStartedUtc = Prompted.AddHours(-1),
            Agent = "ClaudeCode",
            IsSupported = true,
            HistoryState = state,
            StartOrdinal = 0,
            TotalCount = turns.Count,
            Turns = turns.ToList(),
        }, pushedAt);
    }

    private static void AssertStillCarryingOn(Rig rig)
    {
        var latest = rig.Env.Latest(Tenant, Architect)!;
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, latest.Verdict);
        Assert.NotEqual(TurnVerdictWatchdog.ExpiredLabel, latest.Label);
    }

    private static void AssertExpired(Rig rig)
    {
        var latest = rig.Env.Latest(Tenant, Architect)!;
        Assert.Equal(TurnVerdictVocabulary.NeededYou, latest.Verdict);
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, latest.Label);
    }

    /// <summary>The owner runs out exactly ten minutes after <paramref name="stopped"/>: not a second before.</summary>
    private static void AssertExpiresTenMinutesAfter(Rig rig, Action<DateTime> setNow, DateTime stopped)
    {
        setNow(stopped.AddMinutes(10).AddSeconds(-1));
        Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));
        AssertStillCarryingOn(rig);

        setNow(stopped.AddMinutes(10));
        Assert.Equal(1, rig.Seat.ExpireCarryingOn(Tenant));
        AssertExpired(rig);
    }

    // ================================================================= the original issue: a long silent command

    [Fact]
    public void ALongSilentBashCall_DoesNotExpireItsOwner()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WorkerLastTerminalWrite);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "run the gateway suite"),
            ToolCall(ToolCalled, "toolu_bash_2992", "Bash", new { command = "test-local.ps1 -Parked" }));

        foreach (var at in new[] { WorkerLastTerminalWrite.AddMinutes(10), WentQuiet.AddMinutes(12), ToolCalled.AddMinutes(45) })
        {
            now = at;
            Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));
            AssertStillCarryingOn(rig);
        }
        Assert.Equal((0, 1), (rig.Env.OwnedSessions(Tenant, Architect)!.Working, rig.Env.OwnedSessions(Tenant, Architect)!.InTurn));
    }

    // ================================================================= a tool call that waits on a person

    [Fact]
    public void AnAskUserQuestionCallWithNoResult_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "decide which database to use"),
            ToolCall(ToolCalled, "toolu_ask_2992", "AskUserQuestion", new { questions = new[] { new { question = "Which database?" } } }));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    [Fact]
    public void AnExitPlanModeCallWithNoResult_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "plan the migration"),
            ToolCall(ToolCalled, "toolu_plan_2992", "ExitPlanMode", new { plan = "Move the table." }));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    [Fact]
    public void ABashCallBlockedOnAPermissionPrompt_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForPerm", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "delete the build folder"),
            ToolCall(ToolCalled, "toolu_perm_2992", "Bash", new { command = "rm -rf build" }));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    // ================================================================= an interrupt as the last line

    [Fact]
    public void AnInterruptDuringAToolCall_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "run the gateway suite"),
            ToolCall(ToolCalled, "toolu_int_2992", "Bash", new { command = "test-local.ps1" }),
            ToolResult(WentQuiet, "toolu_int_2992", "[Request interrupted by user for tool use]", isError: true),
            UserText(WentQuiet, "[Request interrupted by user for tool use]"));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    [Fact]
    public void AnInterruptWhileTheAgentWasWriting_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "summarise the logs"),
            AssistantText(ToolCalled, "Reading the logs now."),
            UserText(WentQuiet, "[Request interrupted by user]"));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    [Fact]
    public void ALocalCommandOutputAsTheLastLine_ExpiresItsOwnerTenMinutesAfterTheWorkerWentQuiet()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WentQuiet);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "summarise the logs"),
            AssistantText(ToolCalled, "The logs are clean."),
            Prompt(WentQuiet, "<local-command-stdout>Set model to Opus</local-command-stdout>"));

        AssertExpiresTenMinutesAfter(rig, at => now = at, WentQuiet);
    }

    // ================================================================= the stop moment is the conversation's, not a push's

    [Fact]
    public void TheTurnEnds_ThenTheDirectorReconnects_TheStopMomentIsStillWhenTheTurnEndedInTheConversation()
    {
        var now = Judged;
        var rig = Build(() => now, "WaitingForInput", WorkerLastTerminalWrite);
        DirectorPushes(rig, QuietEdgePush,
            Prompt(Prompted, "run the gateway suite"),
            ToolCall(ToolCalled, "toolu_end_2992", "Bash", new { command = "test-local.ps1 -Parked" }));

        now = ToolCalled.AddMinutes(9);
        Assert.Equal(0, rig.Seat.ExpireCarryingOn(Tenant));

        // The command returns and the Worker writes its report; the terminal wrote it, and the Director pushes at
        // the quiet edge ten seconds later.
        var commandFinished = new DateTime(2026, 9, 17, 1, 17, 47, DateTimeKind.Utc);
        var turnEnded = commandFinished.AddSeconds(30);
        Terminal(rig, "WaitingForInput", turnEnded.AddSeconds(3));
        DirectorPushes(rig, turnEnded.AddSeconds(13),
            ToolResult(commandFinished, "toolu_end_2992", "1634 passed"),
            AssistantText(turnEnded, "The suite is green."));

        // A Gateway deploy at 01:26: the Director reconnects and pushes the same conversation again.
        DirectorPushes(rig, new DateTime(2026, 9, 17, 1, 26, 0, DateTimeKind.Utc));

        AssertExpiresTenMinutesAfter(rig, at => now = at, turnEnded);
    }

    // ================================================================= the reading of one session, by itself

    private static StoredTurn Said(string role, DateTime at, params HistoryPartDto[] parts) => new(role, parts, at, IsMeta: false, IsSidechain: false);

    private static HistoryPartDto Call(string name, string id = "toolu_x") => new() { Kind = "ToolUse", ToolName = name, ToolId = id, Text = "{}" };

    private static HistoryPartDto Words(string text) => new() { Kind = "Text", Text = text };

    private static SessionTurnTail Tail(string? state, params StoredTurn[] turns) => new(Worker, "ClaudeCode", true, state, turns);

    private static SessionDto WorkerRow(string terminal) => Row(Worker, terminal, WorkerLastTerminalWrite, controller: Architect);

    [Fact]
    public void Read_ATerminalThatIsPrinting_IsInATurn_WhateverTheConversationSays()
    {
        var reading = SessionTurnState.Read(Tail("NeedsYou", Said("Assistant", ToolCalled, Words("Done."))), WorkerRow("Working"));

        Assert.True(reading.InTurn);
    }

    [Theory]
    [InlineData("Working", true)]
    [InlineData("WaitingForInput", false)]
    public void Read_NoStoredConversation_ReadsTheTerminal(string terminal, bool inTurn)
    {
        var reading = SessionTurnState.Read(null, WorkerRow(terminal));

        Assert.Equal(inTurn, reading.InTurn);
        Assert.Equal(WorkerLastTerminalWrite, reading.StoppedAtUtc);
    }

    [Fact]
    public void Read_AnAgentWithNoDerivedState_OrAnUnreadableOne_ReadsTheTerminal()
    {
        var pending = Said("Assistant", ToolCalled, Call("Bash"));

        foreach (var tail in new[] { Tail(null, pending), Tail("Working", pending) with { IsSupported = false } })
        {
            var reading = SessionTurnState.Read(tail, WorkerRow("WaitingForInput"));
            Assert.False(reading.InTurn);
            Assert.Equal(WorkerLastTerminalWrite, reading.StoppedAtUtc);
        }
    }

    [Theory]
    [InlineData("BackgroundRunning")]
    [InlineData("Idle")]
    [InlineData("SomethingANewerDirectorSays")]
    public void Read_BackgroundWork_AnExitedProcess_OrAnUnknownState_ReadsTheTerminal(string state)
    {
        var reading = SessionTurnState.Read(Tail(state, Said("Assistant", ToolCalled, Call("Bash"))), WorkerRow("WaitingForInput"));

        Assert.False(reading.InTurn);
        Assert.Equal(WorkerLastTerminalWrite, reading.StoppedAtUtc);
    }

    [Fact]
    public void Read_ARunningToolCall_IsInATurn()
    {
        var reading = SessionTurnState.Read(Tail("Working", Said("Assistant", ToolCalled, Call("Bash"))), WorkerRow("WaitingForInput"));

        Assert.True(reading.InTurn);
    }

    [Fact]
    public void Read_ParallelCalls_OneOfWhichAsksThePerson_ReadsTheTerminal()
    {
        var reading = SessionTurnState.Read(Tail("Working",
                Said("User", Prompted, Words("go")),
                Said("Assistant", ToolCalled, Call("AskUserQuestion", "toolu_a")),
                Said("Assistant", ToolCalled, Call("Bash", "toolu_b"))),
            WorkerRow("WaitingForInput"));

        Assert.False(reading.InTurn);
    }

    [Fact]
    public void Read_ARealPromptWithNoReply_IsInATurn_ButAMetaLineIsNot()
    {
        var prompt = Said("User", WentQuiet, Words("now fix the tests"));
        Assert.True(SessionTurnState.Read(Tail("Working", prompt), WorkerRow("WaitingForInput")).InTurn);

        var meta = prompt with { IsMeta = true };
        Assert.False(SessionTurnState.Read(Tail("Working", meta), WorkerRow("WaitingForInput")).InTurn);
    }

    [Fact]
    public void Read_WorkingButTheStoredConversationEndsInAReply_ReadsTheTerminal()
    {
        // The push that carries the new turn has not arrived yet: the stored end and the state disagree.
        var reading = SessionTurnState.Read(Tail("Working", Said("Assistant", ToolCalled, Words("Done."))), WorkerRow("WaitingForInput"));

        Assert.False(reading.InTurn);
        Assert.Equal(WorkerLastTerminalWrite, reading.StoppedAtUtc);
    }

    [Fact]
    public void Read_AnEndedTurn_StoppedWhenTheConversationSaysItEnded()
    {
        var reading = SessionTurnState.Read(Tail("NeedsYou", Said("Assistant", ToolCalled, Words("Done."))), WorkerRow("WaitingForInput"));

        Assert.False(reading.InTurn);
        Assert.Equal(ToolCalled, reading.StoppedAtUtc);
    }
}
