using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Git;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1177 (Phase 1): unit tests for the shared <see cref="SessionCommandExecutor"/> - the single
/// command core the Director's REST endpoints and its Gateway stream down-channel both call. Verifies the
/// <c>prompt</c> verb's guards and effect against a real <see cref="SessionManager"/> holding an embedded
/// buffer-only session (reusing <see cref="ExecuteActionTestBackend"/>), so the executor's behaviour is
/// asserted the same way the REST endpoint's was - by the exact bytes reaching the session buffer.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionCommandExecutorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (SessionManager sm, Session session, ExecuteActionTestBackend backend) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session, backend);
    }

    private static DirectorCommand PromptCommand(string sessionId, PromptRequest req) => new()
    {
        CommandId = "cmd-1",
        Verb = "prompt",
        SessionId = sessionId,
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task DispatchAsync_Prompt_DeliversTextAndReturnsAcceptedResponse()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "hello", AppendEnter = false });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.True(result.Ok);
            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("cmd-1", result.CommandId);
            Assert.NotNull(result.BodyJson);

            var response = JsonSerializer.Deserialize<PromptResponse>(result.BodyJson, Json);
            Assert.NotNull(response);
            Assert.True(response.Accepted);

            // AppendEnter=false => raw SendInput, so the exact bytes land in the session buffer.
            Assert.NotNull(backend.Buffer);
            var written = Encoding.UTF8.GetString(backend.Buffer.DumpAll());
            Assert.Contains("hello", written);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_ExitedSession_ReturnsConflict()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            backend.RaiseProcessExited(1); // drive Session.Status to Exited via the real backend event
            Assert.True(session.Status is SessionStatus.Exited or SessionStatus.Failed);

            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.False(result.Ok);
            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = PromptCommand("not-a-guid", new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = PromptCommand(Guid.NewGuid().ToString(), new PromptRequest { Text = "hello" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_Prompt_EmptyText_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = PromptCommand(session.Id.ToString(), new PromptRequest { Text = "" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    // ---------- interrupt ----------

    [Fact]
    public async Task DispatchAsync_Interrupt_ClaudeSession_ReturnsOk()
    {
        var (sm, session, _) = NewSession(); // default AgentKind is ClaudeCode, whose driver interrupts cleanly
        try
        {
            var command = new DirectorCommand { CommandId = "i1", Verb = "interrupt", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("i1", result.CommandId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_DriverRefuses_ReturnsConflict()
    {
        var (sm, session, _) = NewSession();
        session.AgentKind = Core.Agents.AgentKind.Pi; // pi's driver has no safe hard interrupt -> NotSupportedException
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Interrupt_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "interrupt", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- escape ----------

    [Fact]
    public async Task DispatchAsync_Escape_ClaudeSession_ReturnsOk()
    {
        var (sm, session, _) = NewSession(); // ClaudeCode's driver soft-cancels cleanly
        try
        {
            var command = new DirectorCommand { CommandId = "e1", Verb = "escape", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("e1", result.CommandId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Escape_DriverRefuses_ReturnsConflict()
    {
        var (sm, session, _) = NewSession();
        session.AgentKind = Core.Agents.AgentKind.Copilot; // copilot's soft-cancel is not live-verified -> NotSupportedException
        try
        {
            var command = new DirectorCommand { Verb = "escape", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.False(string.IsNullOrEmpty(result.Error));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Escape_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "escape", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- hold ----------

    [Fact]
    public async Task DispatchAsync_Hold_EmptyPayload_DefaultsToOnHoldTrue()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand { CommandId = "h1", Verb = "hold", SessionId = session.Id.ToString(), PayloadJson = "" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.True(session.OnHold);
            var response = JsonSerializer.Deserialize<HoldResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(response);
            Assert.True(response.OnHold);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_OnHoldFalse_UnparksSession()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.ApplyGatewayHold(HoldState.Held); // start held (the Gateway's ruling, mirrored)
            var command = new DirectorCommand
            {
                Verb = "hold",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new HoldRequest { OnHold = false }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.False(session.OnHold);
        }
        finally { sm.Dispose(); }
    }

    // ---------- set-display-state reconciles the raw hold mirror (inspection finding 3) ----------

    [Fact]
    public async Task DispatchAsync_SetDisplayState_HoldStateNone_HealsAStaleOnHold()
    {
        // FINDING 3. The desktop's raw Session.OnHold drives the rail's Snooze-versus-Unsnooze menu and was
        // healed only by a one-shot, unretried hold mirror. The reliable, change-gated display-state channel
        // now carries HoldState too, so a session left stale-held (a dropped None mirror) self-heals the next
        // time the Gateway stamps its fold down.
        var (sm, session, _) = NewSession();
        try
        {
            session.ApplyGatewayHold(HoldState.Held); // a stale mirror from an earlier snooze
            Assert.True(session.OnHold);

            var command = new DirectorCommand
            {
                Verb = "set-display-state",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SetDisplayStateRequest
                {
                    EffectiveColor = "red",
                    StateLabel = "Needs you",
                    TriageBucket = "needsYou",
                    HoldState = HoldStates.None,
                }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.False(session.OnHold); // the fold-down healed the raw mirror
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SetDisplayState_BlankHoldState_LeavesTheMirrorUntouched()
    {
        // An older Gateway that does not send HoldState must NOT be read as "force None": a blank value
        // normalises to null and the existing mirror stands. (No fallback; no silent clobber.)
        var (sm, session, _) = NewSession();
        try
        {
            session.ApplyGatewayHold(HoldState.Held);

            var command = new DirectorCommand
            {
                Verb = "set-display-state",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SetDisplayStateRequest
                {
                    EffectiveColor = "grey",
                    HoldState = null,
                }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.True(session.OnHold); // untouched
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "hold", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Hold_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "hold", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- kill ----------

    [Fact]
    public async Task DispatchAsync_Kill_ExistingSession_KillsAndRemoves()
    {
        var (sm, session, _) = NewSession();
        var id = session.Id;
        try
        {
            var command = new DirectorCommand { CommandId = "k1", Verb = "kill", SessionId = id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("k1", result.CommandId);
            Assert.Null(sm.GetSession(id)); // removed from tracking
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_EscalatesOnTheFleetGraceWindow_FastByDefault()
    {
        // The kill verb is the FLEET/remote stop path, so it force-escalates on the shorter FleetKillGraceMs
        // window (1500ms by default), NOT the 5000ms desktop GracefulShutdownTimeoutSeconds.
        var (sm, session, backend) = NewSession();
        try
        {
            var command = new DirectorCommand { CommandId = "k-fast", Verb = "kill", SessionId = session.Id.ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.True(result.Ok);
            Assert.Equal(1500, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_HonorsAConfiguredFleetGrace()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions { FleetKillGraceMs = 800 });
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        try
        {
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { Verb = "kill", SessionId = session.Id.ToString() });

            Assert.Equal(800, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_DisabledFleetGrace_FallsBackToStandardWindow()
    {
        // FleetKillGraceMs disabled (null) -> the fleet stop uses the standard GracefulShutdownTimeoutSeconds
        // (2s here), byte-identical to before the faster-STOP change.
        var sm = new SessionManager(new Core.Configuration.AgentOptions { FleetKillGraceMs = null, GracefulShutdownTimeoutSeconds = 2 });
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        try
        {
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                new DirectorCommand { Verb = "kill", SessionId = session.Id.ToString() });

            Assert.Equal(2000, backend.LastGracefulTimeoutMs);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_NoRowOnThisDirector_IsAlreadyStoppedNotNotFound()
    {
        // DELIBERATELY CHANGED (mission "Stop a session", RULING 3). This test used to assert NotFound.
        // The ruling says the owning Director is asked to stop the session whether or not it still has a
        // row for it, and that a stop must NEVER fail because there is nothing left to stop - so a session
        // this Director has no row for is an alreadyStopped SUCCESS, not a 404. The failure being designed
        // out is a second stop returning an error, which an operator reads as "it is still alive".
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "kill", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.NotEqual(DirectorCommandStatus.NotFound, result.Status);
            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.Null(stop.ProcessId);
            Assert.False(stop.ProcessEnded);
            Assert.False(stop.RowRemoved);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Kill_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "kill", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- kill: the honest answer (mission "Stop a session", Seat 1) ----------
    //
    // These drive SessionCommandExecutor.KillAsync directly rather than through DispatchAsync, because the
    // two facts the verb now reports - is that process id alive, and does that worktree have uncommitted
    // changes - are injected seams. Driving them through the dispatcher would mean starting a real process
    // and running a real git, which is exactly what the seams exist to avoid.

    /// <summary>A backend that can carry a REAL-LOOKING process id, which ExecuteActionTestBackend cannot
    /// (its ProcessId is hard-coded to 0, so every session built on it looks like a session that never held
    /// a process). Nothing here is started or killed: liveness is asked of the injected check, not of the
    /// backend, which is the whole point of the change under test.</summary>
    private sealed class StopTestBackend : ISessionBackend
    {
        public int ProcessId { get; init; }
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(4096);

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>A session on its own temporary directory, holding the given process id. The directory is
    /// distinct per test on purpose: it is what makes an assertion on the reported worktree path mean
    /// "the tree THIS session held" rather than "some constant that happens to match".</summary>
    private static (SessionManager sm, Session session, string worktree) NewSessionHolding(int processId)
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var worktree = Directory.CreateTempSubdirectory("stop-a-session-").FullName;
        var session = sm.CreateEmbeddedSession(worktree, null, new StopTestBackend { ProcessId = processId });
        return (sm, session, worktree);
    }

    private static DirectorCommand KillCommand(Guid sessionId) =>
        new() { CommandId = "stop-1", Verb = "kill", SessionId = sessionId.ToString() };

    private static DirectorStopResult StopResultOf(DirectorCommandResult result)
    {
        Assert.Equal(DirectorCommandStatus.Ok, result.Status);
        Assert.NotNull(result.BodyJson);
        var dto = JsonSerializer.Deserialize<DirectorStopResult>(result.BodyJson!, Json);
        Assert.NotNull(dto);
        return dto!;
    }

    /// <summary>A probe that answers without touching git. Success=false is the "could not tell" answer.</summary>
    private static Func<string, CancellationToken, Task<GitCountResult>> ProbeAnswering(bool success, int count) =>
        (_, _) => Task.FromResult(new GitCountResult(success, count));

    [Fact]
    public async Task Kill_LiveProcessFoundAndEnded_ReportsStoppedWithTheProcessId()
    {
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        var id = session.Id;
        try
        {
            // Alive when the facts are captured, gone when the stop is checked - the ordinary successful stop.
            int checks = 0;
            var pidsAsked = new List<int>();
            bool IsAlive(int pid) { pidsAsked.Add(pid); return ++checks == 1; }

            var result = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), IsAlive, ProbeAnswering(true, 0));

            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.Stopped, stop.Verdict);
            Assert.True(stop.ProcessEnded);
            Assert.True(stop.RowRemoved);
            Assert.Equal(4242, stop.ProcessId);
            Assert.All(pidsAsked, pid => Assert.Equal(4242, pid));   // it asked about THIS session's process
            Assert.Null(sm.GetSession(id));                          // and the row really is gone
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_RowWithNoLiveProcess_IsAlreadyStoppedAndStillClearsTheRow()
    {
        // Ruling 3: clearing a leftover row is a thing that HAPPENED, so RowRemoved is true even though
        // there was no process to end. An operator who is not told will keep looking for that row.
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        var id = session.Id;
        try
        {
            var result = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), _ => false, ProbeAnswering(true, 0));

            var stop = StopResultOf(result);
            Assert.Equal(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.True(stop.RowRemoved);
            Assert.Null(sm.GetSession(id));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_SecondStopStraightAfterTheFirst_IsStillAnAlreadyStoppedSuccess()
    {
        // The failure Ruling 3 exists to design out: a second run returning an error, which an operator
        // reads as "it is still alive".
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        var id = session.Id;
        try
        {
            int checks = 0;
            bool IsAlive(int _) => ++checks == 1;

            var first = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), IsAlive, ProbeAnswering(true, 0));
            Assert.Equal(SessionStopVerdict.Stopped, StopResultOf(first).Verdict);

            var second = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), IsAlive, ProbeAnswering(true, 0));

            var stop = StopResultOf(second);
            Assert.Equal(DirectorCommandStatus.Ok, second.Status);
            Assert.Equal(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.False(stop.ProcessEnded);
            Assert.False(stop.RowRemoved);   // there was no row left for this one to remove
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_DirtyWorktree_ReportsTrueAndNamesThePath()
    {
        // Ruling 2: the stop goes through without argument, and the answer names the tree it left behind.
        var (sm, session, worktree) = NewSessionHolding(processId: 4242);
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(session.Id), _ => false, ProbeAnswering(success: true, count: 3));

            var stop = StopResultOf(result);
            Assert.True(stop.WorktreeHadUncommittedChanges);
            Assert.Equal(worktree, stop.WorktreePath);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_CleanWorktree_ReportsFalse()
    {
        var (sm, session, worktree) = NewSessionHolding(processId: 4242);
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(session.Id), _ => false, ProbeAnswering(success: true, count: 0));

            var stop = StopResultOf(result);
            Assert.False(stop.WorktreeHadUncommittedChanges);
            Assert.Equal(worktree, stop.WorktreePath);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_WorktreeProbeFails_ReportsNullNotFalse()
    {
        // THE TEST THE FIELD EXISTS FOR (issue 516). A probe that did not run does not know the tree is
        // clean, and false is what every reader downstream takes as verified-clean. It must be null.
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        try
        {
            var result = await SessionCommandExecutor.KillAsync(
                sm, KillCommand(session.Id), _ => false, ProbeAnswering(success: false, count: 0));

            var stop = StopResultOf(result);
            Assert.Null(stop.WorktreeHadUncommittedChanges);
            Assert.NotEqual(false, stop.WorktreeHadUncommittedChanges);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_WorktreeProbeThrows_ReportsNullAndDoesNotFailTheStop()
    {
        // A probe that faults or times out is the same UNKNOWN as one that answers Success=false - and it
        // must never be able to fail the stop, which is the whole reason it is caught.
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        var id = session.Id;
        try
        {
            Task<GitCountResult> Explode(string _, CancellationToken __) =>
                throw new OperationCanceledException("the git probe timed out");

            var result = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), _ => false, Explode);

            var stop = StopResultOf(result);
            Assert.Null(stop.WorktreeHadUncommittedChanges);
            Assert.Equal(SessionStopVerdict.AlreadyStopped, stop.Verdict);
            Assert.True(stop.RowRemoved);
            Assert.Null(sm.GetSession(id));
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task Kill_ProcessWouldNotDie_IsAnErrorNamingTheProcessId()
    {
        // Ruling 3's third failure. Reporting this as a success is the worse of the two mistakes: the
        // operator would read "stopped" and stop looking. The row is left in place on purpose.
        var (sm, session, _) = NewSessionHolding(processId: 4242);
        var id = session.Id;
        try
        {
            var result = await SessionCommandExecutor.KillAsync(sm, KillCommand(id), _ => true, ProbeAnswering(true, 0));

            Assert.Equal(DirectorCommandStatus.Error, result.Status);
            Assert.NotNull(result.Error);
            Assert.Contains("would not die", result.Error);
            Assert.Contains("4242", result.Error);
            Assert.NotNull(sm.GetSession(id));   // the row stays, so the operator can see it and try again
        }
        finally { sm.Dispose(); }
    }

    // ---------- patch (rename) ----------

    [Fact]
    public async Task DispatchAsync_Patch_RenamesSessionAndReturnsUpdatedDto()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand
            {
                CommandId = "p1",
                Verb = "patch",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "Renamed-Session" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("p1", result.CommandId);
            Assert.Equal("Renamed-Session", session.CustomName);

            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("Renamed-Session", dto.Name);
            Assert.Equal("dir-A", dto.DirectorId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_EmptyName_ClearsCustomName()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.CustomName = "Something"; // start with a custom name
            var command = new DirectorCommand
            {
                Verb = "patch",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "   " }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.CustomName); // whitespace-only clears to null (falls back to default)
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand
            {
                Verb = "patch",
                SessionId = Guid.NewGuid().ToString(),
                PayloadJson = JsonSerializer.Serialize(new SessionUpdateRequest { Name = "x" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Patch_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "patch", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- create (heaviest verb) ----------

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path (ConPty
    // spawn, name-at-birth, wingman opt-in) without depending on an installed coding-agent CLI.
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    private static DirectorCommand CreateCommand(NewSessionRequest req) => new()
    {
        CommandId = "c1",
        Verb = "create",
        SessionId = "",
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task DispatchAsync_Create_MakesSessionWithRightAgentNameAndWingman()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "create-executor-test",
                WingmanEnabled = false,
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("RawCli", dto.Agent);
            Assert.Equal("create-executor-test", dto.Name);
            Assert.Equal("dir-A", dto.DirectorId);

            // The session actually exists in the manager with the requested wingman opt-out.
            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            var session = sm.GetSession(sid);
            Assert.NotNull(session);
            Assert.False(session.WingmanEnabled);
        }
        finally { sm.Dispose(); }
    }

    // ---------- auto-name + IsAutoNamed (chunk 3) ----------

    [Fact]
    public async Task DispatchAsync_Create_Worker_GetsTaskFlavoredAutoName_AndIsAutoNamedTrue()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Purpose = "implement #799",
                ControllerSessionId = Guid.NewGuid().ToString(), // controlled at birth -> Worker
                // no Name -> auto-composed
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("implement #799", dto.Name); // task-flavored: the purpose leads, no repo prefix
            Assert.True(dto.IsAutoNamed);
            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            Assert.True(sm.GetSession(sid)?.IsAutoNamed);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_NonWorker_GetsRepoScopedAutoName()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var repoFolder = SessionName.FolderName(Path.GetTempPath());
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Purpose = "implement #799", // no controller -> not a worker
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal($"{repoFolder}: implement #799", dto.Name); // repo-scoped, as before
            Assert.True(dto.IsAutoNamed);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_ExplicitName_IsNotAutoNamed()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "my chosen name",
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("my chosen name", dto.Name);
            Assert.False(dto.IsAutoNamed); // an explicit --name is never marked auto-named
        }
        finally { sm.Dispose(); }
    }

    // ---------- set-role verb + create-time explicit role (chunk 2.5) ----------

    private static DirectorCommand SetRoleCommand(string sid, string? role) => new()
    {
        CommandId = "sr1",
        Verb = "set-role",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new SetRoleRequest { Role = role }, Json),
    };

    [Fact]
    public async Task DispatchAsync_SetRole_SetsNormalizedStickyRole_AndReturnsUpdatedSession()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", SetRoleCommand(session.Id.ToString(), "architect"));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("Architect", session.ExplicitRole); // normalized casing + sticky on the session
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("Architect", dto.ExplicitRole);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SetRole_UnknownRole_ReturnsBadRequest_Unchanged()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", SetRoleCommand(session.Id.ToString(), "Wizard"));

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Null(session.ExplicitRole);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SetRole_BlankRole_ClearsExplicitRole()
    {
        var (sm, session, _) = NewSession();
        try
        {
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A", SetRoleCommand(session.Id.ToString(), "Architect"));
            Assert.Equal("Architect", session.ExplicitRole);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", SetRoleCommand(session.Id.ToString(), ""));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.ExplicitRole); // cleared -> reverts to auto-derivation
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_SetRole_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", SetRoleCommand(Guid.NewGuid().ToString(), "Architect"));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_WithExplicitRole_SetsStickyRole()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "role-create-test",
                Role = "architect",
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal("Architect", dto.ExplicitRole);
            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            Assert.Equal("Architect", sm.GetSession(sid)?.ExplicitRole);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_WithUnknownRole_ReturnsBadRequest_NoSessionCreated()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var before = sm.ListSessions().Count;
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "bad-role",
                Role = "Wizard",
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(before, sm.ListSessions().Count); // rejected before creation - no orphan
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_MissingRepoPath_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = "", Agent = "RawCli", Command = TestShellPath });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_UnknownAgent_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "NotARealAgent" });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_WeakExplicitName_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var repo = Path.GetTempPath();
            var folder = Path.GetFileName(repo.TrimEnd('\\', '/'));
            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = repo,
                Agent = "RawCli",
                Command = TestShellPath,
                Name = folder, // bare repo folder name is a weak explicit name -> rejected (issue #800)
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Create_RawCliWithoutCommand_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = CreateCommand(new NewSessionRequest { RepoPath = Path.GetTempPath(), Agent = "RawCli", Command = null });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- wingman-goal (increment 6: side-effecting verb via the services context) ----------

    [Fact]
    public async Task DispatchAsync_WingmanGoal_SetsGoalAndReturnsBody()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var command = new DirectorCommand
            {
                CommandId = "g1",
                Verb = "wingman-goal",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "ship the stream" }, Json),
            };

            // services=null -> the cache-warm side effect is skipped, but the goal is still set.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("ship the stream", session.WingmanGoal);
            Assert.NotNull(result.BodyJson);
            Assert.Contains("ship the stream", result.BodyJson);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_EmptyGoal_ClearsIt()
    {
        var (sm, session, _) = NewSession();
        try
        {
            session.SetWingmanGoal("old goal");
            var command = new DirectorCommand
            {
                Verb = "wingman-goal",
                SessionId = session.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = null }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.WingmanGoal);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand
            {
                Verb = "wingman-goal",
                SessionId = Guid.NewGuid().ToString(),
                PayloadJson = JsonSerializer.Serialize(new WingmanGoalRequest { Goal = "x" }, Json),
            };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_WingmanGoal_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "wingman-goal", SessionId = "not-a-guid" };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_UnknownVerb_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { CommandId = "x", Verb = "no-such-verb", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally
        {
            sm.Dispose();
        }
    }

    [Fact]
    public async Task DispatchAsync_UnknownVerb_NamesTheVerb()
    {
        // Fail loud: the unknown-verb result names the offending verb (Gateway Cleanup Phase 0, Ruling B).
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var command = new DirectorCommand { Verb = "totally-made-up-verb", SessionId = Guid.NewGuid().ToString() };

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Contains("totally-made-up-verb", result.Error);
        }
        finally { sm.Dispose(); }
    }

    // ---------- turns (Gateway Cleanup Phase 0: representative READ exemplar) ----------

    private static DirectorCommand TurnsCommand(string sid) => new() { CommandId = "t1", Verb = "turns", SessionId = sid };

    [Fact]
    public async Task DispatchAsync_Turns_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TurnsCommand("not-a-guid"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Turns_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TurnsCommand(Guid.NewGuid().ToString()));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Turns_ClaudeSessionNotYetLinked_ReturnsOkWithNoSessionIdStatus()
    {
        // A fresh ClaudeCode session has no Claude session id yet: the REST route returned this as a 200 with
        // status "no_session_id" (a domain state, not an error), so the tunnel verb returns DirectorCommandStatus.Ok
        // with that body. This exercises the read returning a serialized DTO on the success path.
        var (sm, session, _) = NewSession();
        try
        {
            Assert.True(string.IsNullOrEmpty(session.ClaudeSessionId));
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TurnsCommand(session.Id.ToString()));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("t1", result.CommandId);
            var resp = JsonSerializer.Deserialize<TurnsResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.Equal("no_session_id", resp.Status);
            Assert.Equal(session.Id.ToString(), resp.SessionId);
        }
        finally { sm.Dispose(); }
    }

    // ---------- resize (Gateway Cleanup Phase 0: representative WRITE exemplar) ----------

    private static DirectorCommand ResizeCommand(string sid, int cols, int rows) => new()
    {
        CommandId = "rz1",
        Verb = "resize",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new ResizeRequest { Cols = cols, Rows = rows }, Json),
    };

    [Fact]
    public async Task DispatchAsync_Resize_ValidGrid_ReturnsAcceptedAndSettledGrid()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ResizeCommand(session.Id.ToString(), 100, 40));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("rz1", result.CommandId);
            var resp = JsonSerializer.Deserialize<ResizeResponse>(result.BodyJson ?? "", Json);
            Assert.NotNull(resp);
            Assert.True(resp.Accepted);
            // The verb reports the grid the session actually settled on (clamped to the PTY's limits).
            Assert.Equal((int)session.CurrentCols, resp.Cols);
            Assert.Equal((int)session.CurrentRows, resp.Rows);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Resize_NonPositiveGrid_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ResizeCommand(session.Id.ToString(), 0, 40));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Resize_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ResizeCommand(Guid.NewGuid().ToString(), 100, 40));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_Resize_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", ResizeCommand("not-a-guid", 100, 40));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- terminal-input (Gateway Cleanup Phase 0: the unary keystroke write, NOT a stream verb) ----------

    private static DirectorCommand TerminalInputCommand(string sid, string base64Bytes) => new()
    {
        CommandId = "ti1",
        Verb = "terminal-input",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new TerminalInputRequest { Bytes = base64Bytes }, Json),
    };

    [Fact]
    public async Task DispatchAsync_TerminalInput_DeliversBytesToSession()
    {
        var (sm, session, backend) = NewSession();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("hi");
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TerminalInputCommand(session.Id.ToString(), Convert.ToBase64String(bytes)));

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal("ti1", result.CommandId);
            Assert.NotNull(backend.Buffer);
            var written = Encoding.UTF8.GetString(backend.Buffer.DumpAll());
            Assert.Contains("hi", written);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_TerminalInput_NotBase64_ReturnsBadRequest()
    {
        var (sm, session, _) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TerminalInputCommand(session.Id.ToString(), "not valid base64 !!!"));
            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task DispatchAsync_TerminalInput_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", TerminalInputCommand(Guid.NewGuid().ToString(), Convert.ToBase64String(new byte[] { 1, 2, 3 })));
            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // ---------- composition: fail loud on a duplicate verb (Gateway Cleanup Phase 0, Ruling B) ----------

    private sealed class StubArea : ISessionCommandArea
    {
        public StubArea(params string[] verbs) => Verbs = verbs;
        public IReadOnlyCollection<string> Verbs { get; }
        public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(DirectorCommandResult.Success());
    }

    [Fact]
    public void BuildVerbMap_DuplicateVerbAcrossAreas_ThrowsNamingTheVerb()
    {
        var areas = new ISessionCommandArea[] { new StubArea("alpha", "beta"), new StubArea("beta", "gamma") };

        var ex = Assert.Throws<InvalidOperationException>(() => SessionCommandExecutor.BuildVerbMap(areas));
        Assert.Contains("beta", ex.Message);
    }

    [Fact]
    public void BuildVerbMap_DistinctVerbs_BuildsMap()
    {
        var areas = new ISessionCommandArea[] { new StubArea("one", "two"), new StubArea("three") };

        var map = SessionCommandExecutor.BuildVerbMap(areas);

        Assert.Equal(3, map.Count);
        Assert.True(map.ContainsKey("one"));
        Assert.True(map.ContainsKey("three"));
    }
}
