using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.Restart;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.UnitTests.Restart;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The four things that finish the smart shutdown: "Cancel and keep working", "Shut down and ignore all
/// sessions", the record for an operating system shutdown, and the restart purpose.
///
/// EVERY TEST HERE WATCHES THE REAL RUN, as in <see cref="SmartShutdownRunTests"/>: the real
/// <see cref="DirectorSmartShutdown"/> over the real <see cref="DirectorDrain"/>. A cancel brings sessions
/// back through the REAL <see cref="DirectorRestore"/>; only its Gateway is a fake, fed from the very
/// record the run saved. The launcher is asked through the real launcher step over the restart cycle
/// tests' own Gateway fake. No test builds a snapshot or a result by hand.
///
/// In the collection every test that takes one of the Director's one-at-a-time gates shares.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public sealed class SmartShutdownCancelIgnoreRestartTests
{
    private const string ThisDirector = "director-under-test";
    private const string RestartIsOff = "THE RESTART IS OFF";
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private DateTime _now = Start;
    private int _polls;

    // ================= the rig =================

    /// <summary>
    /// The Gateway as the REAL restore sees it, on the rig. The workspace it serves is the record the run
    /// last saved - read at the moment the restore first asks, so a restore that ran before the cancelled
    /// record was saved would find a record with nobody decided "restore" and bring nobody back. A spawn
    /// puts a new session on the same fake Director the run was shutting down.
    /// </summary>
    private sealed class RigRestoreGateway : IRestoreGateway
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FakeSessionControl _sessions;
        private readonly FakeWorkspaceSink _sink;
        private WorkspaceDocument? _stored;
        private int _next;

        public RigRestoreGateway(FakeSessionControl sessions, FakeWorkspaceSink sink)
        {
            _sessions = sessions;
            _sink = sink;
        }

        public List<NewSessionRequest> Spawns { get; } = new();

        /// <summary>The record exactly as the restore first read it.</summary>
        public WorkspaceDocument? ReadAtFirst { get; private set; }

        /// <summary>Spawns whose name is here are refused outright with this reason.</summary>
        public Dictionary<string, string> RefuseByName { get; } = new();

        public Task<WorkspaceDocument?> GetWorkspaceAsync(string id, CancellationToken ct)
        {
            if (_stored is null)
            {
                _stored = Clone(_sink.Last);
                ReadAtFirst = Clone(_sink.Last);
            }
            return Task.FromResult<WorkspaceDocument?>(_stored);
        }

        public Task<WorkspaceDocument> RecordMarkAsync(string workspaceId, WorkspaceRestoreMark mark, CancellationToken ct)
        {
            var doc = _stored ?? throw new InvalidOperationException("a mark was written before the workspace was read");
            var seat = doc.Seats.FirstOrDefault(s => s.SessionId == mark.SeatSessionId);
            if (seat is not null)
            {
                seat.Restore ??= new WorkspaceSeatRestore();
                switch (mark.Kind)
                {
                    case WorkspaceRestoreMarkKinds.Started:
                        seat.Restore.StartedToken = mark.Token;
                        seat.Restore.StartedByDirectorId = mark.DirectorId;
                        break;
                    case WorkspaceRestoreMarkKinds.Restored:
                        seat.RestoredSessionId = mark.RestoredSessionId;
                        seat.Restore.StartedToken = null;
                        break;
                    case WorkspaceRestoreMarkKinds.Failed:
                        seat.Restore.Failure = mark.Failure;
                        if (mark.NothingStarted) seat.Restore.StartedToken = null;
                        break;
                }
            }
            return Task.FromResult(doc);
        }

        public Task<RestoreRoster> GetRosterAsync(CancellationToken ct)
            => Task.FromResult(new RestoreRoster(
                _sessions.Live.Select(id => new SessionDto { SessionId = id, DirectorId = ThisDirector }).ToList(),
                new[] { new DirectorReachabilityDto { DirectorId = ThisDirector, State = DirectorReachabilityDto.StateOnline } }));

        public Task<SessionDto> SpawnOnThisDirectorAsync(NewSessionRequest request, CancellationToken ct)
        {
            Spawns.Add(request);
            _sessions.Journal?.Add($"spawn:{request.Name}");
            if (RefuseByName.TryGetValue(request.Name ?? "", out var why))
                throw new GatewaySpawnFailedException(409, why);

            var id = $"back-{++_next}";
            _sessions.Live.Add(id);
            return Task.FromResult(new SessionDto { SessionId = id, DirectorId = ThisDirector, Name = request.Name });
        }

        private static WorkspaceDocument Clone(WorkspaceDocument doc)
            => JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(doc, Json), Json)!;
    }

    /// <summary>The cancel's way back: the REAL restore, against the same Director, over the rig.</summary>
    private BringBackClosedSessions RealRestore(RigRestoreGateway gateway)
        => async (workspaceId, seats, ct) =>
        {
            var restore = new DirectorRestore(gateway, ThisDirector, () => _now);
            var result = await restore.RunAsync(
                new WorkspaceRestoreOrder { WorkspaceId = workspaceId, Seats = seats.ToList() }, ct);
            return new SmartShutdownBringBack(result.Seats, null);
        };

    private DirectorSmartShutdown Engine(
        FakeSessionControl sessions,
        FakeWorkspaceSink sink,
        string directory,
        RigRestoreGateway? restore = null,
        IRestartCycleGateway? launcher = null,
        Action<int>? onPoll = null,
        Func<CancellationToken, Task>? reachGateway = null,
        bool? launcherWouldRestartThis = true,
        bool noGatewayClient = false)
        => new(
            () => noGatewayClient
                ? null
                : new DirectorDrain(sessions, sink, ThisDirector, null,
                    utcNow: () => _now,
                    delay: (d, _) =>
                    {
                        _now = _now.Add(d);
                        onPoll?.Invoke(++_polls);
                        return Task.CompletedTask;
                    }),
            reachGateway ?? (_ => Task.CompletedTask),
            () => new DirectorRestartEligibilityDto
            {
                Eligible = launcherWouldRestartThis,
                Reason = "this Director runs as the named instance 'slot5'",
            },
            "Test Director",
            directory,
            () => _now,
            bringBack: restore is null ? null : RealRestore(restore),
            sessions: sessions,
            launcherGateway: launcher is null ? null : () => launcher,
            machine: "TEST_MACHINE",
            exePath: @"C:\app\cc-director.exe");

    private sealed class Watched
    {
        public required SmartShutdownResult Result { get; init; }
        public required List<SmartShutdownSnapshot> Seen { get; init; }
        public SmartShutdownSessionProgress FinalRow(string id) => Result.Final.Sessions.Single(r => r.SessionId == id);
    }

    /// <summary>Start a run and watch ALL of it: the run is held at its first step until the handler is
    /// attached, so no snapshot is raised before anybody is listening.</summary>
    private async Task<Watched> RunWatchedAsync(
        FakeSessionControl sessions,
        FakeWorkspaceSink sink,
        string directory,
        RigRestoreGateway? restore = null,
        IRestartCycleGateway? launcher = null,
        SmartShutdownPurpose purpose = SmartShutdownPurpose.Close,
        Action<int, ISmartShutdownRun>? onPoll = null,
        Action<SmartShutdownSnapshot, ISmartShutdownRun>? onSnapshot = null)
    {
        var listening = new TaskCompletionSource();
        ISmartShutdownRun? run = null;
        var engine = Engine(sessions, sink, directory, restore, launcher,
            onPoll: n => onPoll?.Invoke(n, run!),
            reachGateway: _ => listening.Task);

        run = engine.Start(new SmartShutdownRequest(purpose, TimeSpan.FromMinutes(10), "update to 2.9.0"));
        var seen = new List<SmartShutdownSnapshot>();
        run.Changed += s =>
        {
            seen.Add(s);
            onSnapshot?.Invoke(s, run);
        };
        listening.SetResult();

        return new Watched { Result = await run.Completion, Seen = seen };
    }

    private static (FakeSessionControl Sessions, FakeWorkspaceSink Sink) Rig(params WorkspaceSeat[] seats)
    {
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        var journal = new List<string>();
        sessions.Journal = journal;
        sink.Journal = journal;
        return (sessions, sink);
    }

    private static WorkspaceSeat Seat(string id, string name, string? reportsTo = null, int order = 0)
    {
        var seat = DrainTestRig.Seat(id, name, reportsTo, order);
        seat.ClaudeSessionId = $"conversation-of-{id}";
        return seat;
    }

    /// <summary>The position in the journal of the n-th save, where n is an index into sink.Saves.</summary>
    private static int JournalPositionOfSave(List<string> journal, int saveIndex)
        => journal.Select((entry, at) => (entry, at)).Where(e => e.entry == "save").Select(e => e.at).ElementAt(saveIndex);

    // ================= Cancel and keep working =================

    // Shows: a cancel pressed while the run is collecting, with one session already shut down and two
    // still open, brings the closed one back through the real restore onto the same Director from the
    // handover it had just written, tells the two open ones the restart is off and leaves them running,
    // saves the record marked cancelled BEFORE the restore reads it, ends Cancelled - and from the press
    // onwards no session is ended or interrupted. It also shows when cancel is offered: never before the
    // record exists, always from then until the press, never after.
    [Fact]
    public async Task CancelAndKeepWorking_WhileCollecting_BringsTheClosedSessionBack_TellsTheOpenOnes_AndMarksTheRecordCancelled()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Hands over at once"), Seat("one", "Still working", order: 1), Seat("two", "Still working too", order: 2));
        var handover = sessions.Handover(dir.Path, "done", "Hands over at once", DrainTestRig.Block(restore: true, why: "Work is left."));
        var restore = new RigRestoreGateway(sessions, sink);
        SmartShutdownSnapshot? whenPressed = null;
        var journalLengthWhenPressed = -1;

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore, onPoll: (n, run) =>
        {
            if (n != 3) return;
            whenPressed = run.Current;
            journalLengthWhenPressed = sessions.Journal!.Count;
            run.CancelAndKeepWorking();
        });

        // The moment of the press: collecting, one session verifiably gone, cancel on offer.
        Assert.NotNull(whenPressed);
        Assert.Equal(SmartShutdownPhase.Collecting, whenPressed!.Phase);
        Assert.True(whenPressed.CanCancel);
        Assert.Equal(SmartShutdownSessionState.ShutDown, whenPressed.Sessions.Single(r => r.SessionId == "done").State);

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        Assert.Equal(Start.AddSeconds(30), _now);

        // The closed one came back through the real restore, on the same Director, from its handover.
        var spawn = Assert.Single(restore.Spawns);
        Assert.Equal("Hands over at once", spawn.Name);
        Assert.Contains(handover, spawn.PrePrompt);
        Assert.Null(spawn.ControllerSessionId);
        Assert.Equal(new[] { "back-1", "one", "two" }, sessions.Live.OrderBy(x => x).ToArray());

        // The two open ones were told, once each, and the closed one was not.
        Assert.Equal(new[] { "one", "two" },
            sessions.Sent.Where(m => m.Text.Contains(RestartIsOff)).Select(m => m.SessionId).OrderBy(x => x).ToArray());

        // Nothing was ended or interrupted, before the press or after it.
        Assert.Empty(sessions.Ended);
        Assert.Empty(sessions.Interrupted);
        Assert.DoesNotContain(sessions.Journal!.Skip(journalLengthWhenPressed), e => e.StartsWith("end:") || e.StartsWith("interrupt:"));
        Assert.DoesNotContain(sessions.Journal!.Skip(journalLengthWhenPressed), e => e.StartsWith("send:done"));

        // The record: cancelled, still a smart shutdown's, and saved that way BEFORE the restore read it
        // and before anything was spawned.
        Assert.NotNull(sink.Last.CancelledAtUtc);
        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, sink.Last.ShutdownKind);
        Assert.NotNull(restore.ReadAtFirst!.CancelledAtUtc);
        var firstCancelledSave = sink.Saves.FindIndex(d => d.CancelledAtUtc is not null);
        Assert.True(JournalPositionOfSave(sessions.Journal!, firstCancelledSave) < sessions.Journal!.IndexOf("spawn:Hands over at once"),
            "a session was brought back before the record said the shutdown was cancelled: " + string.Join(", ", sessions.Journal!));
        Assert.All(sink.Saves.Take(firstCancelledSave), d => Assert.Null(d.CancelledAtUtc));

        // The rows, and the phases.
        Assert.Equal(SmartShutdownSessionState.BroughtBack, watched.FinalRow("done").State);
        Assert.Contains("back-1", watched.FinalRow("done").Detail);
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("one").State);
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("two").State);
        Assert.All(watched.Result.Final.Sessions, r => Assert.Equal(SmartShutdownWords.StateLabel(r.State), r.StateLabel));
        var phases = watched.Seen.Select(s => s.Phase).ToList();
        Assert.Equal(
            new[] { SmartShutdownPhase.Starting, SmartShutdownPhase.Asking, SmartShutdownPhase.Collecting, SmartShutdownPhase.Cancelling, SmartShutdownPhase.Finished },
            phases.Where((p, i) => i == 0 || phases[i - 1] != p).ToArray());
        Assert.Contains("1 of the 1 already shut down were brought back", watched.Result.Detail);

        // When cancel is offered: not before the record, always until the press, never after it.
        Assert.All(watched.Seen.Where(s => s.WorkspaceId is null), s => Assert.False(s.CanCancel));
        Assert.All(watched.Seen.Where(s => s.WorkspaceId is not null && s.Phase < SmartShutdownPhase.Cancelling), s => Assert.True(s.CanCancel));
        Assert.All(watched.Seen.Where(s => s.Phase >= SmartShutdownPhase.Cancelling), s =>
        {
            Assert.False(s.CanCancel);
            Assert.False(s.CanShutDownNow);
        });
        Assert.Null(DirectorSmartShutdown.Active);
    }

    // Shows: a session that handed over and was flagged for closing, but is still open when the cancel
    // comes, is NOT left to be closed a minute later: its flag is taken back, it gets its own name back,
    // it is told the restart is off, its row ends "still running", and it is not started a second time.
    [Fact]
    public async Task CancelAndKeepWorking_ASessionFlaggedButStillOpen_HasItsCloseTakenBack_AndIsNotBroughtBackTwice()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Handed over, not yet gone"));
        sessions.PollsBeforeReap = 100;
        sessions.Handover(dir.Path, "done", "Handed over, not yet gone", DrainTestRig.Block(restore: true));
        var restore = new RigRestoreGateway(sessions, sink);

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore,
            onPoll: (n, run) => { if (n == 2) run.CancelAndKeepWorking(); });

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        Assert.Equal(new[] { "done" }, sessions.Flagged);
        Assert.Equal(new[] { "done" }, sessions.DeletionCancelled);
        Assert.Equal("Handed over, not yet gone", sessions.Renamed[^1].Name);
        Assert.Contains(sessions.Sent, m => m.SessionId == "done" && m.Text.Contains(RestartIsOff));
        Assert.Empty(restore.Spawns);
        Assert.Equal(new[] { "done" }, sessions.Live.ToArray());
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("done").State);
    }

    // Shows: with a lead and the session under it BOTH already shut down, the cancel brings the lead back
    // first and the session under it second, under the lead's NEW id - the real restore's own order and
    // owner rule, reached from the cancel.
    [Fact]
    public async Task CancelAndKeepWorking_ALeadAndTheSessionUnderItBothClosed_TheLeadComesBackFirst()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("worker", "Mission - Developer", reportsTo: "lead", order: 0),
            Seat("lead", "Mission - Tech Lead", order: 1),
            Seat("open", "Never answers", order: 2));
        sessions.Handover(dir.Path, "worker", "Mission - Developer", DrainTestRig.Block(restore: true));
        sessions.Handover(dir.Path, "lead", "Mission - Tech Lead", DrainTestRig.Block(restore: true));
        var restore = new RigRestoreGateway(sessions, sink);
        SmartShutdownSnapshot? whenPressed = null;

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore, onPoll: (n, run) =>
        {
            if (n != 6) return;
            whenPressed = run.Current;
            run.CancelAndKeepWorking();
        });

        Assert.Equal(SmartShutdownSessionState.ShutDown, whenPressed!.Sessions.Single(r => r.SessionId == "lead").State);
        Assert.Equal(SmartShutdownSessionState.ShutDown, whenPressed.Sessions.Single(r => r.SessionId == "worker").State);

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        Assert.Equal(new[] { "Mission - Tech Lead", "Mission - Developer" }, restore.Spawns.Select(s => s.Name).ToArray());
        Assert.Null(restore.Spawns[0].ControllerSessionId);
        Assert.Equal("back-1", restore.Spawns[1].ControllerSessionId);
        Assert.Equal(SmartShutdownSessionState.BroughtBack, watched.FinalRow("lead").State);
        Assert.Equal(SmartShutdownSessionState.BroughtBack, watched.FinalRow("worker").State);
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("open").State);
    }

    // Shows: the usual cancel - sessions close leaf first, so the session under a lead is already shut
    // down while its lead is still open. It comes back under that lead's own, unchanged id, because the
    // lead was never closed; and a session that said it need NOT be brought back comes back all the same,
    // with its own answer kept in the record's reason.
    [Fact]
    public async Task CancelAndKeepWorking_ASessionUnderALeadThatIsStillOpen_ComesBackUnderThatLead_WhateverItAnswered()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("lead", "Mission - Tech Lead"),
            Seat("worker", "Mission - Developer", reportsTo: "lead", order: 1));
        sessions.Handover(dir.Path, "worker", "Mission - Developer", DrainTestRig.Block(restore: false, why: "My part is finished."));
        var restore = new RigRestoreGateway(sessions, sink);

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore,
            onPoll: (n, run) => { if (n == 3) run.CancelAndKeepWorking(); });

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        var spawn = Assert.Single(restore.Spawns);
        Assert.Equal("Mission - Developer", spawn.Name);
        Assert.Equal("lead", spawn.ControllerSessionId);
        Assert.Equal(SmartShutdownSessionState.BroughtBack, watched.FinalRow("worker").State);
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("lead").State);

        var saved = sink.Last.Seats.Single(s => s.SessionId == "worker");
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Restore!.Decision);
        Assert.Contains("it said it need not be brought back", saved.Restore.Why);
        Assert.Contains("My part is finished.", saved.Restore.Why);
    }

    // Shows: a cancel that arrives after the limit was reached, or after "Shut down now" was chosen, is
    // ignored: nobody is told the restart is off, nobody is brought back, the record is not marked
    // cancelled, every session is ended, and the run still ends Emptied.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndKeepWorking_AfterTheLimitOrAfterShutDownNow_IsIgnored_AndTheRunStillEndsEmptied(bool afterShutDownNow)
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "Never answers"), Seat("two", "Never answers either", order: 1));
        var restore = new RigRestoreGateway(sessions, sink);
        var pressedCancel = 0;

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore,
            onPoll: (n, run) =>
            {
                if (!afterShutDownNow || n != 3) return;
                run.ShutDownNow();
                pressedCancel++;
                run.CancelAndKeepWorking();
            },
            onSnapshot: (snapshot, run) =>
            {
                if (afterShutDownNow || snapshot.Phase != SmartShutdownPhase.EndingAtLimit) return;
                Assert.False(snapshot.CanCancel);
                pressedCancel++;
                run.CancelAndKeepWorking();
            });

        Assert.True(pressedCancel > 0, "the cancel was never pressed, so this test showed nothing");
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Equal(afterShutDownNow ? Start.AddSeconds(30) : Start.AddMinutes(10), _now);
        Assert.Equal(new[] { "one", "two" }, sessions.Ended.Select(e => e.SessionId).OrderBy(x => x).ToArray());
        Assert.Empty(sessions.Live);
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains(RestartIsOff));
        Assert.Empty(restore.Spawns);
        Assert.All(sink.Saves, d => Assert.Null(d.CancelledAtUtc));
        Assert.DoesNotContain(watched.Seen, s => s.Phase == SmartShutdownPhase.Cancelling);
        Assert.All(watched.Seen.Where(s => s.Phase >= SmartShutdownPhase.EndingAtLimit), s => Assert.False(s.CanCancel));
    }

    // Shows: cancel pressed twice is honoured once - each open session is told once, the closed one is
    // brought back once, and the record is marked cancelled by exactly one run of the cancel.
    [Fact]
    public async Task CancelAndKeepWorking_PressedTwice_IsHonouredOnce()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Hands over at once"), Seat("open", "Still working", order: 1));
        sessions.Handover(dir.Path, "done", "Hands over at once", DrainTestRig.Block(restore: true));
        var restore = new RigRestoreGateway(sessions, sink);

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore, onPoll: (n, run) =>
        {
            if (n != 3) return;
            run.CancelAndKeepWorking();
            run.CancelAndKeepWorking();
        });

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        Assert.Single(sessions.Sent, m => m.Text.Contains(RestartIsOff));
        Assert.Single(restore.Spawns);
        Assert.Single(watched.Seen.Select(s => s.Phase).Where((p, i) => p == SmartShutdownPhase.Cancelling
            && (i == 0 || watched.Seen[i - 1].Phase != SmartShutdownPhase.Cancelling)));
    }

    // Shows: a session that cannot be brought back never stops the others - the other closed session
    // still comes back - and both its own row and the result say why, in the restore's own words.
    [Fact]
    public async Task CancelAndKeepWorking_ASessionThatCannotBeBroughtBack_DoesNotStopTheOthers_AndTheRowAndTheResultSayWhy()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("first", "Cannot come back"),
            Seat("second", "Comes back", order: 1),
            Seat("open", "Never answers", order: 2));
        sessions.Handover(dir.Path, "first", "Cannot come back", DrainTestRig.Block(restore: true));
        sessions.Handover(dir.Path, "second", "Comes back", DrainTestRig.Block(restore: true));
        var restore = new RigRestoreGateway(sessions, sink);
        restore.RefuseByName["Cannot come back"] = "its repository is no longer on this machine";

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore,
            onPoll: (n, run) => { if (n == 3) run.CancelAndKeepWorking(); });

        Assert.Equal(SmartShutdownOutcome.Cancelled, watched.Result.Outcome);
        Assert.Equal(2, restore.Spawns.Count);

        Assert.Equal(SmartShutdownSessionState.BroughtBack, watched.FinalRow("second").State);
        Assert.Equal(SmartShutdownSessionState.ShutDown, watched.FinalRow("first").State);
        Assert.Contains("It could not be brought back", watched.FinalRow("first").Detail);
        Assert.Contains("its repository is no longer on this machine", watched.FinalRow("first").Detail);
        Assert.Contains("NOT brought back: Cannot come back", watched.Result.Detail);
        Assert.Contains("its repository is no longer on this machine", watched.Result.Detail);
        Assert.Equal(SmartShutdownSessionState.KeptRunning, watched.FinalRow("open").State);
    }

    // Shows: an engine that was given no way to bring a session back never offers cancel, and a press is
    // ignored - the run goes on to the limit and ends Emptied.
    [Fact]
    public async Task CancelAndKeepWorking_WithNoWayToBringSessionsBack_IsNeverOffered_AndAPressIsIgnored()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("silent", "Never answers"));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, restore: null,
            onPoll: (n, run) => { if (n == 3) run.CancelAndKeepWorking(); });

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.All(watched.Seen, s => Assert.False(s.CanCancel));
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains(RestartIsOff));
        Assert.Equal(Start.AddMinutes(10), _now);
    }

    // ================= Shut down and ignore all sessions =================

    // Shows: ignore all writes the record BEFORE the first session is ended - the order of the calls on
    // the rig - and that record carries every conversation id, the ignore-all mark, and every session
    // decided "close"; every session is then ended, nobody is asked anything, and the record is saved
    // again with the close times.
    [Fact]
    public async Task ShutDownIgnoringAllAsync_WritesTheRecordBeforeTheFirstSessionIsEnded_AndEndsEverySession()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "First"), Seat("two", "Second", order: 1), Seat("three", "Third", order: 2));

        var result = await Engine(sessions, sink, dir.Path).ShutDownIgnoringAllAsync(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, "everything here is finished"),
            CancellationToken.None);

        Assert.True(result.RecordWritten);
        Assert.Null(result.RecordRefusal);
        Assert.Equal(sink.Last.Id, result.WorkspaceId);
        Assert.Equal(3, result.SessionsEnded);
        Assert.Empty(sessions.Live);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Interrupted);

        var journal = sessions.Journal!;
        Assert.Equal("save", journal[0]);
        Assert.Equal(new[] { "end:one", "end:three", "end:two" }, journal.Where(e => e.StartsWith("end:")).OrderBy(x => x).ToArray());
        Assert.True(journal.IndexOf("save") < journal.FindIndex(e => e.StartsWith("end:")),
            "a session was ended before the record was saved: " + string.Join(", ", journal));

        var first = sink.Saves[0];
        Assert.Equal(WorkspaceShutdownKinds.IgnoreAll, first.ShutdownKind);
        Assert.Equal("everything here is finished", first.Reason);
        Assert.Equal(new[] { "conversation-of-one", "conversation-of-two", "conversation-of-three" },
            first.Seats.Select(s => s.ClaudeSessionId).ToArray());
        Assert.All(first.Seats, s => Assert.Equal(WorkspaceRestoreDecisions.Close, s.Restore!.Decision));
        Assert.All(first.Seats, s => Assert.Null(s.ClosedAtUtc));
        Assert.All(sink.Last.Seats, s => Assert.NotNull(s.ClosedAtUtc));
        Assert.Null(DirectorDrain.Running);
    }

    // Shows: with the Gateway unreachable - it does not answer, or this Director has no client at all -
    // the record is NOT written and the result says why, and every session is STILL ended, because the
    // owner chose to discard them.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutDownIgnoringAllAsync_TheGatewayUnreachable_SaysTheRecordWasNotWrittenAndWhy_AndStillEndsEverySession(bool noGatewayClient)
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "First"), Seat("two", "Second", order: 1));
        sink.CaptureFails = new HttpRequestException("No connection could be made to gateway.example");

        var result = await Engine(sessions, sink, dir.Path, noGatewayClient: noGatewayClient).ShutDownIgnoringAllAsync(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null), CancellationToken.None);

        Assert.False(result.RecordWritten);
        Assert.Null(result.WorkspaceId);
        Assert.Contains(noGatewayClient ? "not connected to a Gateway" : "No connection could be made to gateway.example", result.RecordRefusal);
        Assert.Equal(2, result.SessionsEnded);
        Assert.Empty(sessions.Live);
        Assert.Empty(sink.Saves);
        Assert.Null(DirectorDrain.Running);
    }

    /// <summary>A Director on which a working session starts another one at the moment the first session
    /// is ended - which is after the record was written AND after the list of sessions to end was taken,
    /// the exact window nothing interrupts the sessions in.</summary>
    private sealed class StartsAnotherSessionWhileBeingEnded : FakeSessionControl
    {
        private bool _started;

        public override Task<DrainEnd> EndAsync(string sessionId, string reason)
        {
            if (!_started)
            {
                _started = true;
                Live.Add("late");
            }
            return base.EndAsync(sessionId, reason);
        }
    }

    // Shows: a session that appears after the record is written - started by a working session while the
    // others are being ended - is ended too, by the one further pass, and is not left for the closing
    // application to end without a trace: the result says in words that it was ended and is in no record,
    // naming it, and the record's own problems say the same. The record itself never names it, because it
    // did not exist when the record was written.
    [Fact]
    public async Task ShutDownIgnoringAllAsync_ASessionThatAppearsAfterTheRecordIsWritten_IsEndedToo_AndTheResultSaysItIsInNoRecord()
    {
        using var dir = new TempDir();
        var sessions = new StartsAnotherSessionWhileBeingEnded { PollsBeforeReap = 1 };
        sessions.Live.Add("one");
        sessions.Live.Add("two");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(Seat("one", "First"), Seat("two", "Second", order: 1)) };
        var journal = new List<string>();
        sessions.Journal = journal;
        sink.Journal = journal;

        var result = await Engine(sessions, sink, dir.Path).ShutDownIgnoringAllAsync(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null), CancellationToken.None);

        Assert.Empty(sessions.Live);
        Assert.Equal(3, result.SessionsEnded);
        Assert.Equal("save", journal[0]);
        Assert.Equal("end:late", journal.Last(e => e.StartsWith("end:")));
        Assert.Single(journal, e => e == "end:late");

        Assert.True(result.RecordWritten);
        Assert.NotNull(result.Detail);
        Assert.Contains("late", result.Detail);
        Assert.Contains("NOT in the record", result.Detail);
        Assert.DoesNotContain("one", result.Detail);
        Assert.DoesNotContain("two", result.Detail);

        Assert.DoesNotContain(sink.Saves[0].Seats, s => s.SessionId == "late");
        Assert.Contains(sink.Last.Integrity!.Problems, p => p.Contains("late") && p.Contains("NOT in the record"));
        Assert.Null(DirectorDrain.Running);
    }

    // Shows: a session that will not end is named in the result with the reason the stop path gave, and
    // the others are still ended; and a run with nothing to add says nothing (the detail is null only
    // when every session ended and every one was in the record, which the first half of this test holds).
    [Fact]
    public async Task ShutDownIgnoringAllAsync_ASessionThatWillNotEnd_IsNamedInTheResultWithTheReason()
    {
        using var dir = new TempDir();
        var (clean, cleanSink) = Rig(Seat("one", "First"));
        var nothingToSay = await Engine(clean, cleanSink, dir.Path).ShutDownIgnoringAllAsync(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null), CancellationToken.None);
        Assert.Null(nothingToSay.Detail);

        var (sessions, sink) = Rig(Seat("stuck", "Will not die"), Seat("two", "Second", order: 1));
        sessions.RefuseEndWithReason["stuck"] = "the process would not exit";

        var result = await Engine(sessions, sink, dir.Path).ShutDownIgnoringAllAsync(
            new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null), CancellationToken.None);

        Assert.Equal(1, result.SessionsEnded);
        Assert.Equal(new[] { "stuck" }, sessions.Live.ToArray());
        Assert.Contains("stuck", result.Detail);
        Assert.Contains("the process would not exit", result.Detail);
        Assert.DoesNotContain("NOT in the record", result.Detail);
        Assert.Single(sessions.Ended, e => e.SessionId == "stuck");
    }

    // ================= the operating system shutdown record =================

    // Shows: the record for an operating system shutdown is saved with every conversation id, as a smart
    // shutdown's record with every session noted as ended without a handover, and the call ends nothing,
    // interrupts nothing and asks nothing - every session is still there when it returns.
    [Fact]
    public async Task RecordAndLetEndAsync_SavesTheRecordWithEveryConversationId_AndNeverInterruptsOrEndsASession()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "First"), Seat("two", "Second", order: 1));
        sessions.MidTurn.Add("one");

        var result = await Engine(sessions, sink, dir.Path).RecordAndLetEndAsync(CancellationToken.None);

        Assert.True(result.RecordWritten);
        Assert.Equal(sink.Last.Id, result.WorkspaceId);
        Assert.Equal(0, result.SessionsEnded);
        Assert.Empty(sessions.Interrupted);
        Assert.Empty(sessions.Ended);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Flagged);
        Assert.Equal(new[] { "save" }, sessions.Journal!.ToArray());
        Assert.Equal(new[] { "one", "two" }, sessions.Live.OrderBy(x => x).ToArray());

        var saved = Assert.Single(sink.Saves);
        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, saved.ShutdownKind);
        Assert.Equal(new[] { "conversation-of-one", "conversation-of-two" }, saved.Seats.Select(s => s.ClaudeSessionId).ToArray());
        Assert.All(saved.Seats, s => Assert.Equal(WorkspaceDrainStates.EndedAtLimit, s.DrainState));
        Assert.All(saved.Seats, s => Assert.Null(s.HandoverPath));
        Assert.Contains("operating system", saved.Integrity!.NotReadyReason);
    }

    // Shows: when the Gateway does not answer, the operating system record says it was not written and
    // why, and still touches no session.
    [Fact]
    public async Task RecordAndLetEndAsync_TheGatewayUnreachable_SaysWhy_AndTouchesNoSession()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "First"));
        sink.CaptureFails = new HttpRequestException("No connection could be made to gateway.example");

        var result = await Engine(sessions, sink, dir.Path).RecordAndLetEndAsync(CancellationToken.None);

        Assert.False(result.RecordWritten);
        Assert.Contains("No connection could be made to gateway.example", result.RecordRefusal);
        Assert.Empty(sessions.Journal!);
        Assert.Contains("one", sessions.Live);
    }

    // ================= the restart purpose =================

    // Shows: with the restart purpose the launcher is asked only AFTER every session is verifiably absent
    // - nothing is live at the moment of the ask, and the ask comes after the last close in the order of
    // calls - through the restart cycle's own step, which checks the machine again first; the run passes
    // through the restarting phase and ends RestartAccepted.
    [Fact]
    public async Task Start_WithTheRestartPurpose_AsksTheLauncherOnlyAfterEverySessionIsGone_AndEndsRestartAccepted()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Hands over at once"), Seat("silent", "Never answers", order: 1));
        sessions.Handover(dir.Path, "done", "Hands over at once", DrainTestRig.Block(restore: true));
        var launcher = new DirectorRestartCycleTests.Gateway();
        List<string>? liveWhenAsked = null;
        launcher.WhenLauncherAsked = () =>
        {
            liveWhenAsked = sessions.Live.ToList();
            sessions.Journal!.Add("launcher-asked");
        };

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, launcher: launcher, purpose: SmartShutdownPurpose.Restart);

        Assert.Equal(SmartShutdownOutcome.RestartAccepted, watched.Result.Outcome);
        Assert.NotNull(liveWhenAsked);
        Assert.Empty(liveWhenAsked!);
        Assert.Equal(1, launcher.CapabilityChecks);
        Assert.Equal(1, launcher.LauncherAsks);
        Assert.Equal("launcher-asked", sessions.Journal![^1]);
        Assert.True(sessions.Journal!.IndexOf("end:silent") < sessions.Journal!.IndexOf("launcher-asked"));

        var phases = watched.Seen.Select(s => s.Phase).ToList();
        Assert.Equal(SmartShutdownPhase.Restarting, phases[^2]);
        Assert.Equal(SmartShutdownPhase.Finished, phases[^1]);
        var restarting = watched.Seen.First(s => s.Phase == SmartShutdownPhase.Restarting);
        Assert.Equal(restarting.Total, restarting.Gone);
        Assert.False(restarting.CanCancel);
        Assert.False(restarting.CanShutDownNow);
        Assert.Contains("launcher accepted", watched.Result.Detail);
    }

    // Shows: a launcher that refuses gives RestartRefused with the launcher's own reason, and the record
    // stands - still a smart shutdown's, not cancelled, with the handed-over session still decided
    // "restore" - so the way up can offer it. A machine that fails the re-check is refused WITHOUT the
    // launcher ever being asked.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WithTheRestartPurpose_ALauncherThatRefuses_EndsRestartRefusedWithItsReason_AndTheRecordStands(bool refusedAtTheRecheck)
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Hands over at once"));
        sessions.Handover(dir.Path, "done", "Hands over at once", DrainTestRig.Block(restore: true, why: "Work is left."));
        var launcher = new DirectorRestartCycleTests.Gateway();
        if (refusedAtTheRecheck)
            launcher.Capability = new MachineRestartCapabilityDto
            {
                Verdict = RestartVerdict.CannotRestart, Reason = "the launcher on this machine is not running",
                GuardedRestart = CapabilityState.Available, GuardedRestartReason = "declares the guard",
            };
        else
            launcher.LauncherAnswer = new LauncherRestartAnswer(409, "{\"error\":\"the Director is not empty\"}");

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, launcher: launcher, purpose: SmartShutdownPurpose.Restart);

        Assert.Equal(SmartShutdownOutcome.RestartRefused, watched.Result.Outcome);
        Assert.Contains(refusedAtTheRecheck ? "the launcher on this machine is not running" : "the Director is not empty", watched.Result.Detail);
        Assert.Equal(refusedAtTheRecheck ? 0 : 1, launcher.LauncherAsks);
        Assert.Empty(sessions.Live);

        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, sink.Last.ShutdownKind);
        Assert.Null(sink.Last.CancelledAtUtc);
        var saved = sink.Last.Seats.Single();
        Assert.Equal(WorkspaceDrainStates.Drained, saved.DrainState);
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Restore!.Decision);
        Assert.Contains(watched.Result.WorkspaceId!, watched.Result.Detail);
    }

    /// <summary>A Gateway that dies in the seconds between the drain's final save and the launcher step:
    /// either the machine cannot be checked again, or the check answers and the ask itself never returns.</summary>
    private sealed class GatewayThatDies : IRestartCycleGateway
    {
        public bool DiesOnlyAtTheAsk;
        public int CapabilityChecks;
        public int LauncherAsks;

        public Task<MachineRestartCapabilityDto> CheckCapabilityAsync(string machine, CancellationToken ct)
        {
            CapabilityChecks++;
            if (!DiesOnlyAtTheAsk) throw new HttpRequestException("No connection could be made to gateway.example");
            return Task.FromResult(new MachineRestartCapabilityDto
            {
                Verdict = RestartVerdict.CanRestart, Reason = "can be restarted",
                GuardedRestart = CapabilityState.Available, GuardedRestartReason = "declares the guard",
            });
        }

        public Task<LauncherRestartAnswer> AskOwnLauncherRestartOnlyIfEmptyAsync(string machine, string? exePath, CancellationToken ct)
        {
            LauncherAsks++;
            throw new HttpRequestException("The connection to gateway.example was closed");
        }

        public Task ReportAsync(string machine, string requestId, DirectorRestartProgressReport report, CancellationToken ct)
            => Task.CompletedTask;
    }

    // Shows: a Gateway that dies at the launcher step - the re-check throws, or the ask itself throws -
    // ends the run RestartRefused with the error's own words, NOT as a shutdown that stopped on an error:
    // the Director is empty, the record stands uncancelled with its session still decided "restore", and
    // the owner is told it is offered on the next start. When the ask was sent and no answer came back,
    // the words say that the launcher may still act on it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_WithTheRestartPurpose_AGatewayThatDiesAtTheLauncherStep_EndsRestartRefusedWithTheReason_AndTheRecordStands(bool diesOnlyAtTheAsk)
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("done", "Hands over at once"));
        sessions.Handover(dir.Path, "done", "Hands over at once", DrainTestRig.Block(restore: true, why: "Work is left."));
        var launcher = new GatewayThatDies { DiesOnlyAtTheAsk = diesOnlyAtTheAsk };

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, launcher: launcher, purpose: SmartShutdownPurpose.Restart);

        Assert.Equal(SmartShutdownOutcome.RestartRefused, watched.Result.Outcome);
        Assert.Equal(1, launcher.CapabilityChecks);
        Assert.Equal(diesOnlyAtTheAsk ? 1 : 0, launcher.LauncherAsks);
        Assert.Contains(
            diesOnlyAtTheAsk ? "The connection to gateway.example was closed" : "No connection could be made to gateway.example",
            watched.Result.Detail);
        Assert.Contains(diesOnlyAtTheAsk ? "its answer never came back" : "The launcher was not asked", watched.Result.Detail);
        Assert.DoesNotContain("stopped on an error", watched.Result.Detail);
        Assert.Contains(watched.Result.WorkspaceId!, watched.Result.Detail);
        Assert.Contains("offered when the Director is next started", watched.Result.Detail);
        Assert.Empty(sessions.Live);

        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, sink.Last.ShutdownKind);
        Assert.Null(sink.Last.CancelledAtUtc);
        var saved = sink.Last.Seats.Single();
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Restore!.Decision);

        var phases = watched.Seen.Select(s => s.Phase).ToList();
        Assert.Equal(SmartShutdownPhase.Restarting, phases[^2]);
        Assert.Equal(SmartShutdownPhase.Finished, phases[^1]);
        Assert.Null(DirectorSmartShutdown.Active);
    }

    // Shows: Start with the restart purpose on a Director its launcher would not restart throws
    // InvalidOperationException carrying the eligibility reason, before anything is touched: no session
    // is asked, nothing is captured, no run is left under way, and the launcher is never asked.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Start_WithTheRestartPurpose_WhenTheLauncherWouldNotRestartThis_ThrowsWithTheReason_AndTouchesNoSession(bool? eligible)
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        var launcher = new DirectorRestartCycleTests.Gateway();
        var engine = Engine(sessions, sink, dir.Path, launcher: launcher, launcherWouldRestartThis: eligible);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            engine.Start(new SmartShutdownRequest(SmartShutdownPurpose.Restart, SmartShutdownTimes.Default, null)));

        Assert.Contains("this Director runs as the named instance 'slot5'", refused.Message);
        Assert.Empty(sessions.Journal!);
        Assert.Null(sink.CaptureRequest);
        Assert.Contains("solo", sessions.Live);
        Assert.Null(DirectorSmartShutdown.Active);
        Assert.Equal(0, launcher.CapabilityChecks);
        Assert.Equal(0, launcher.LauncherAsks);
    }

    // Shows: with the close purpose the launcher is never asked and the machine is never re-checked, even
    // though a launcher is wired: the run ends Emptied and the caller closes the application.
    [Fact]
    public async Task Start_WithTheClosePurpose_NeverAsksTheLauncher()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        var launcher = new DirectorRestartCycleTests.Gateway();

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, launcher: launcher, purpose: SmartShutdownPurpose.Close);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Equal(0, launcher.CapabilityChecks);
        Assert.Equal(0, launcher.LauncherAsks);
        Assert.DoesNotContain(watched.Seen, s => s.Phase == SmartShutdownPhase.Restarting);
    }

    // ================= how a real Director brings sessions back =================

    private sealed class FakeBringBackGateway : IBringBackGateway
    {
        public List<(string WorkspaceId, string DirectorId, string[] Seats)> Requests { get; } = new();
        public Exception? Refuse { get; set; }
        public int Reads { get; private set; }

        /// <summary>What the record says on the n-th read (1-based); the last entry repeats.</summary>
        public List<WorkspaceDocument> Record { get; } = new();

        public Task RequestRestoreAsync(string workspaceId, string directorId, IReadOnlyList<string> seatSessionIds, CancellationToken ct)
        {
            Requests.Add((workspaceId, directorId, seatSessionIds.ToArray()));
            return Refuse is null ? Task.CompletedTask : Task.FromException(Refuse);
        }

        public Task<WorkspaceDocument?> GetWorkspaceAsync(string workspaceId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<WorkspaceDocument?>(Record[Math.Min(Reads, Record.Count) - 1]);
        }
    }

    private GatewaySmartShutdownBringBack BringBack(FakeBringBackGateway gateway)
        => new(() => gateway, ThisDirector, () => _now, (d, _) => { _now = _now.Add(d); return Task.CompletedTask; });

    // Shows: on a real Director the closed sessions are brought back by asking the Gateway's restore door
    // for a restore onto THIS Director, naming exactly those seats, and then reading the record until
    // every one of them has an answer - here one that came back and one that did not, on the third read.
    [Fact]
    public async Task BringBackAsync_AsksTheRestoreDoorForThisDirector_AndReadsTheRecordUntilEverySeatHasAnAnswer()
    {
        var gateway = new FakeBringBackGateway();
        var nothingYet = DrainTestRig.Document(Seat("a", "First"), Seat("b", "Second"));
        var half = DrainTestRig.Document(Seat("a", "First"), Seat("b", "Second"));
        half.Seats[0].RestoredSessionId = "new-a";
        var all = DrainTestRig.Document(Seat("a", "First"), Seat("b", "Second"));
        all.Seats[0].RestoredSessionId = "new-a";
        all.Seats[1].Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Failure = "the Gateway did not start it" };
        gateway.Record.AddRange(new[] { nothingYet, half, all });

        var back = await BringBack(gateway).BringBackAsync("ws-1", new[] { "a", "b" }, CancellationToken.None);

        var request = Assert.Single(gateway.Requests);
        Assert.Equal(("ws-1", ThisDirector), (request.WorkspaceId, request.DirectorId));
        Assert.Equal(new[] { "a", "b" }, request.Seats);
        Assert.Equal(3, gateway.Reads);
        Assert.Null(back.CouldNotStart);
        Assert.Equal("new-a", back.Seats.Single(s => s.SessionId == "a").RestoredSessionId);
        Assert.Equal("the Gateway did not start it", back.Seats.Single(s => s.SessionId == "b").Failure);
    }

    // Shows: a restore the Gateway will not take is handed back as the sentence that says why, and the
    // record is never read; and a restore that was taken but never answers for a seat stops waiting
    // after its patience and says exactly that about that seat, keeping the answer it did get.
    [Fact]
    public async Task BringBackAsync_ARestoreNotTaken_SaysWhy_AndOneThatNeverAnswers_SaysSoAfterItsPatience()
    {
        var refusing = new FakeBringBackGateway { Refuse = new InvalidOperationException("Director 'other' is already restoring workspace \"ws-1\"") };
        var refused = await BringBack(refusing).BringBackAsync("ws-1", new[] { "a" }, CancellationToken.None);
        Assert.Contains("already restoring", refused.CouldNotStart);
        Assert.Empty(refused.Seats);
        Assert.Equal(0, refusing.Reads);

        var silent = new FakeBringBackGateway();
        var half = DrainTestRig.Document(Seat("a", "First"), Seat("b", "Second"));
        half.Seats[0].RestoredSessionId = "new-a";
        silent.Record.Add(half);
        var began = _now;

        var back = await BringBack(silent).BringBackAsync("ws-1", new[] { "a", "b" }, CancellationToken.None);

        Assert.Equal(began + GatewaySmartShutdownBringBack.Patience, _now);
        Assert.Equal("new-a", back.Seats.Single(s => s.SessionId == "a").RestoredSessionId);
        Assert.Contains("still says nothing about this session", back.Seats.Single(s => s.SessionId == "b").Failure);
    }
}
