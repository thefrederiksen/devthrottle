using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The smart shutdown, all the way down: the time allowed, the stage at two thirds, the limit, "Shut down
/// now", and the progress a screen is shown.
///
/// EVERY TEST HERE WATCHES THE REAL RUN. The engine is the real <see cref="DirectorSmartShutdown"/> over
/// the real <see cref="DirectorDrain"/>; only the two ends are faked - the live sessions and the Gateway
/// store - exactly as in <see cref="DirectorDrainTests"/>. No test builds a snapshot by hand: a snapshot
/// asserted on here is one the engine raised.
///
/// The clock and the waits are injected, so sixty minutes run in milliseconds.
///
/// In the collection every test that takes one of the Director's one-at-a-time gates shares.
/// </summary>
[Collection(CcDirector.Gateway.UnitTests.Restart.DirectorGatesCollection.Name)]
public sealed class SmartShutdownRunTests
{
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
    private DateTime _now = Start;
    private int _polls;

    /// <summary>One smart shutdown on the rig, with everything it raised kept in order.</summary>
    private sealed class Watched
    {
        public required SmartShutdownResult Result { get; init; }

        /// <summary>Every snapshot raised, with the rig's clock at the moment it was raised.</summary>
        public required List<(DateTime At, SmartShutdownSnapshot Snapshot)> Seen { get; init; }

        public IEnumerable<SmartShutdownSessionProgress> RowsOf(string sessionId)
            => Seen.SelectMany(s => s.Snapshot.Sessions).Where(r => r.SessionId == sessionId);
    }

    private DirectorSmartShutdown Engine(
        FakeSessionControl sessions,
        FakeWorkspaceSink sink,
        string directory,
        Action<int>? onPoll = null,
        Func<CancellationToken, Task>? reachGateway = null,
        bool? launcherWouldRestartThis = true)
        => new(
            () => new DirectorDrain(sessions, sink, "director-under-test", null,
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
                Reason = "this is what the restart eligibility answer said",
            },
            "Test Director",
            directory,
            () => _now);

    /// <summary>
    /// Start a run and watch ALL of it. The run is held at its very first step - the question to the
    /// Gateway - until the handler is attached, so no snapshot can be raised before anybody is listening.
    /// </summary>
    private async Task<Watched> RunWatchedAsync(
        FakeSessionControl sessions,
        FakeWorkspaceSink sink,
        string directory,
        int minutes = 10,
        Action<int, ISmartShutdownRun>? onPoll = null,
        Action<ISmartShutdownRun>? beforeRelease = null)
    {
        var listening = new TaskCompletionSource();
        ISmartShutdownRun? run = null;
        var engine = Engine(sessions, sink, directory,
            onPoll: n => onPoll?.Invoke(n, run!),
            reachGateway: _ => listening.Task);

        run = engine.Start(new SmartShutdownRequest(
            SmartShutdownPurpose.Close, TimeSpan.FromMinutes(minutes), "update to 2.9.0"));
        var seen = new List<(DateTime, SmartShutdownSnapshot)>();
        beforeRelease?.Invoke(run);
        run.Changed += s => seen.Add((_now, s));
        listening.SetResult();

        var result = await run.Completion;
        return new Watched { Result = result, Seen = seen };
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

    private static WorkspaceSeat SavedSeat(FakeWorkspaceSink sink, string id)
        => sink.Last.Seats.Single(s => s.SessionId == id);

    // ================= a session that hands over in time =================

    // Shows: a session that writes its handover when asked is closed the way the drain has always closed
    // one - flagged, then verified gone - and is never interrupted and never ended; the record says it came
    // from a smart shutdown, and the session was told how long it had and was not promised it would live.
    [Fact]
    public async Task Start_ASessionThatHandsOverInTime_IsClosedAndNeverInterruptedOrEnded()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block(restore: true, why: "Work is left."));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Empty(sessions.Interrupted);
        Assert.Empty(sessions.Ended);
        Assert.Equal(new[] { "solo" }, sessions.Flagged);
        Assert.Empty(sessions.Live);

        var saved = SavedSeat(sink, "solo");
        Assert.Equal(WorkspaceDrainStates.Drained, saved.DrainState);
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Restore!.Decision);
        Assert.NotNull(saved.ClosedAtUtc);
        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, sink.Last.ShutdownKind);
        Assert.All(sink.Saves, s => Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, s.ShutdownKind));

        var request = sessions.Sent.Single(m => m.SessionId == "solo").Text;
        Assert.Contains("YOU HAVE 10 MINUTES", request);
        Assert.Contains("WRITE THE EXACT NEXT ACTION FIRST", request);
        Assert.DoesNotContain("YOU WILL NOT BE KILLED", request);

        Assert.Equal(SmartShutdownSessionState.ShutDown, watched.Result.Final.Sessions.Single().State);
        Assert.Equal(SmartShutdownPhase.Finished, watched.Result.Final.Phase);
        Assert.True(_now < Start.AddMinutes(1), "a session that hands over at once must not make the run wait");
    }

    // ================= two thirds =================

    // Shows: a session still in the middle of a turn at two thirds of the time allowed is interrupted, THEN
    // sent the short "hand over now" message, in that order and at that moment; it then writes its
    // handover, is closed the ordinary way, and is not ended at the limit.
    [Fact]
    public async Task Start_ASessionMidTurnAtTwoThirds_IsInterruptedAndAskedAgain_ThenHandsOverAndIsNotEnded()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("busy", "In a long turn"));
        sessions.MidTurn.Add("busy");
        sessions.HandoverWhenToldToHandOverNow(dir.Path, "busy", "In a long turn", DrainTestRig.Block(restore: true));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Equal(new[] { "busy" }, sessions.Interrupted);

        var second = sessions.Sent.Where(m => m.SessionId == "busy").Select(m => m.Text).ToList();
        Assert.Equal(2, second.Count);
        Assert.Contains("START NOTHING NEW", second[0]);
        Assert.Contains("HAND OVER NOW: THE EXACT NEXT ACTION FIRST", second[1]);
        Assert.Contains(DrainPaths.HandoverFor(dir.Path, "busy", "In a long turn"), second[1]);

        // The interrupt comes BEFORE the second message: a session that is working reads nothing until
        // its turn ends.
        var journal = sessions.Journal!;
        Assert.True(journal.IndexOf("interrupt:busy") < journal.LastIndexOf("send:busy"),
            "the session was asked again before it was interrupted: " + string.Join(", ", journal));

        // And at two thirds, not before and not a poll late: ten minutes allowed is four hundred seconds.
        var interrupting = watched.Seen.First(s => s.Snapshot.Phase == SmartShutdownPhase.Interrupting);
        Assert.Equal(Start.AddSeconds(400), interrupting.At);
        Assert.Contains(watched.RowsOf("busy"), r => r.State == SmartShutdownSessionState.Interrupted);

        Assert.Empty(sessions.Ended);
        Assert.Equal(WorkspaceDrainStates.Drained, SavedSeat(sink, "busy").DrainState);
        Assert.Equal(SmartShutdownSessionState.ShutDown, watched.Result.Final.Sessions.Single().State);
        Assert.True(_now < Start.AddMinutes(10), "it handed over after the interrupt, so the limit was never reached");
    }

    // Shows: a session whose agent has no safe interrupt (Pi) hands the refusal back; it is still sent the
    // short message, its row is never called interrupted because it was not, the refusal is on the row in
    // the agent's own words, and it is ended at the limit like any other session still present.
    [Fact]
    public async Task Start_ASessionThatCannotBeInterrupted_IsStillAskedAgain_IsNeverShownInterrupted_AndIsEndedAtTheLimit()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("pi", "A Pi session"));
        sessions.MidTurn.Add("pi");
        sessions.RefuseInterruptWithReason["pi"] = "Pi has no safe hard interrupt";

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(new[] { "pi" }, sessions.Interrupted);
        Assert.Contains(sessions.Sent, m => m.SessionId == "pi" && m.Text.Contains("HAND OVER NOW"));
        Assert.DoesNotContain(watched.RowsOf("pi"), r => r.State == SmartShutdownSessionState.Interrupted);
        Assert.Contains(watched.RowsOf("pi"), r => r.Detail is not null && r.Detail.Contains("Pi has no safe hard interrupt"));
        Assert.Contains(watched.RowsOf("pi"),
            r => r.State == SmartShutdownSessionState.Asked && r.Detail is not null && r.Detail.Contains("could not be interrupted"));

        Assert.Equal(new[] { "pi" }, sessions.Ended.Select(e => e.SessionId));
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, SavedSeat(sink, "pi").DrainState);
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
    }

    // ================= the limit =================

    // Shows: a session that never answers is asked again at two thirds WITHOUT an interrupt (it is not
    // mid-turn), is ended at the limit and not a poll before, and is recorded ended-at-limit with its
    // conversation id on the SAVED record - and that record, already saying so, reached the Gateway BEFORE
    // the session was ended, and was saved again after.
    [Fact]
    public async Task Start_ASessionThatNeverAnswers_IsEndedAtTheLimit_AndTheRecordNamingItWasSavedBeforeItWasEnded()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("silent", "Never answers"));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Empty(sessions.Interrupted);
        Assert.Contains(sessions.Sent, m => m.SessionId == "silent" && m.Text.Contains("HAND OVER NOW"));
        Assert.Equal(new[] { "silent" }, sessions.Ended.Select(e => e.SessionId));
        Assert.Empty(sessions.Flagged);
        Assert.Equal(Start.AddMinutes(10), _now);

        var saved = SavedSeat(sink, "silent");
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, saved.DrainState);
        Assert.Equal("conversation-of-silent", saved.ClaudeSessionId);
        Assert.NotNull(saved.ClosedAtUtc);
        Assert.Null(saved.HandoverPath);

        // THE ORDER. The n-th "save" in the journal is sink.Saves[n], so the first save that already
        // calls the session ended-at-limit can be placed against the end itself.
        var journal = sessions.Journal!;
        var firstSaveNamingIt = sink.Saves.FindIndex(d => d.Seats.Single().DrainState == WorkspaceDrainStates.EndedAtLimit);
        Assert.True(firstSaveNamingIt >= 0, "no save ever recorded the session as ended-at-limit");
        var savePositions = journal.Select((entry, at) => (entry, at)).Where(e => e.entry == "save").Select(e => e.at).ToList();
        var ended = journal.IndexOf("end:silent");
        Assert.True(savePositions[firstSaveNamingIt] < ended,
            "the session was ended before the record naming it reached the Gateway: " + string.Join(", ", journal));
        Assert.Equal("conversation-of-silent", sink.Saves[firstSaveNamingIt].Seats.Single().ClaudeSessionId);
        Assert.Null(sink.Saves[firstSaveNamingIt].Seats.Single().ClosedAtUtc);
        Assert.True(savePositions[^1] > ended, "the record was not saved again after the session was ended");

        Assert.Equal(SmartShutdownSessionState.EndedAtLimit, watched.Result.Final.Sessions.Single().State);
        Assert.Contains(sink.Last.Integrity!.Problems, p => p.Contains("conversation-of-silent") && p.Contains("was ended"));
    }

    // Shows: whatever a session had on disk when time ran out is kept and named on its seat, finished or
    // not - here a file far too short to be a handover - and its row said "writing" while it was there.
    [Fact]
    public async Task Start_ASessionWithAHalfWrittenHandoverAtTheLimit_IsEndedAndWhatItWroteIsKept()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("slow", "Writes slowly"));
        var path = DrainPaths.HandoverFor(dir.Path, "slow", "Writes slowly");
        sessions.WhenMessaged = () => File.WriteAllText(path, "## The exact next action\n\nRun the release gate.");

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Contains(watched.RowsOf("slow"), r => r.State == SmartShutdownSessionState.Writing);
        var saved = SavedSeat(sink, "slow");
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, saved.DrainState);
        Assert.Equal(path, saved.HandoverPath);
        Assert.True(File.Exists(path));
    }

    // Shows: a session that DID hand over and is merely still present at the limit (its process never
    // went) is ended so the Director is empty, but keeps its handover: it stays "drained" with its restore
    // answer, and is not thrown in with the sessions that never wrote one.
    [Fact]
    public async Task Start_ASessionThatHandedOverButIsStillPresentAtTheLimit_IsEndedAndKeepsItsHandover()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("stuck", "Hands over and never goes"));
        sessions.NeverReaped.Add("stuck");
        sessions.Handover(dir.Path, "stuck", "Hands over and never goes", DrainTestRig.Block(restore: true, why: "Work is left."));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(new[] { "stuck" }, sessions.Ended.Select(e => e.SessionId));
        var saved = SavedSeat(sink, "stuck");
        Assert.Equal(WorkspaceDrainStates.Drained, saved.DrainState);
        Assert.Equal(WorkspaceRestoreDecisions.Restore, saved.Restore!.Decision);
        Assert.NotNull(saved.ClosedAtUtc);
        Assert.Equal(SmartShutdownSessionState.ShutDown, watched.Result.Final.Sessions.Single().State);
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
    }

    // Shows: a session that will not die leaves the Director not empty, and the run says so - Failed, with
    // the stop path's own reason - instead of reporting an empty Director to a screen that would then close
    // the application over it.
    [Fact]
    public async Task Start_ASessionThatWillNotDie_EndsFailedNamingIt_NotEmptied()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("undead", "Will not die"));
        sessions.RefuseEndWithReason["undead"] = "its pooled worktree is held";

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Failed, watched.Result.Outcome);
        Assert.Contains("still on this Director", watched.Result.Detail);
        Assert.Contains(watched.RowsOf("undead"), r => r.Detail is not null && r.Detail.Contains("its pooled worktree is held"));
        Assert.NotEqual(SmartShutdownSessionState.EndedAtLimit, watched.Result.Final.Sessions.Single().State);
        Assert.Null(SavedSeat(sink, "undead").ClosedAtUtc);
    }

    // Shows: a session that appeared after the record was captured is ended with the rest, so "emptied" is
    // true, and the record says in plain words that it was there and is described nowhere.
    [Fact]
    public async Task Start_ASessionThatAppearedAfterTheCapture_IsEndedTooAndTheRecordSaysSo()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        sessions.Live.Add("stranger");

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Contains(sessions.Ended, e => e.SessionId == "stranger");
        Assert.Empty(sessions.Live);
        Assert.Contains(sink.Last.Integrity!.Problems, p => p.Contains("stranger") && p.Contains("NOT in this record"));
    }

    // ================= a lead with a session under it =================

    // Shows: leaf first still holds under a smart shutdown - the session under a lead is closed before the
    // lead - while the rows come out the other way round, lead first with its session under it, and the
    // session under a lead is shown as waiting to be asked BY its lead until its handover lands.
    [Fact]
    public async Task Start_ALeadWithASessionUnderIt_ClosesTheLeafFirst_AndListsTheLeadFirst()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("worker", "Mission - Developer", reportsTo: "lead", order: 0),
            Seat("solo", "Standalone", order: 1),
            Seat("lead", "Mission - Tech Lead", order: 2));
        sessions.Handover(dir.Path, "worker", "Mission - Developer", DrainTestRig.Block());
        sessions.Handover(dir.Path, "lead", "Mission - Tech Lead", DrainTestRig.Block());
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.True(sessions.Flagged.IndexOf("worker") < sessions.Flagged.IndexOf("lead"),
            "the lead was closed before the session under it: " + string.Join(", ", sessions.Flagged));

        // Only the two heads were asked by the Director.
        Assert.Equal(new[] { "lead", "solo" },
            sessions.Sent.Select(m => m.SessionId).Distinct().OrderBy(x => x).ToArray());

        var withRows = watched.Seen.Where(s => s.Snapshot.Sessions.Count > 0).ToList();
        Assert.NotEmpty(withRows);
        Assert.All(withRows, s => Assert.Equal(
            new[] { "solo", "lead", "worker" }, s.Snapshot.Sessions.Select(r => r.SessionId).ToArray()));
        Assert.All(withRows, s => Assert.Equal("lead", s.Snapshot.Sessions.Single(r => r.SessionId == "worker").OwnerSessionId));
        Assert.All(withRows, s => Assert.Null(s.Snapshot.Sessions.Single(r => r.SessionId == "lead").OwnerSessionId));
        Assert.Contains(watched.RowsOf("worker"), r => r.State == SmartShutdownSessionState.Pending);
    }

    // Shows: at the limit the order is leaf first too - the session under a lead is ended before its lead.
    [Fact]
    public async Task Start_ALeadAndTheSessionUnderItThatNeverAnswer_AreEndedLeafFirst()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("lead", "Mission - Tech Lead"),
            Seat("worker", "Mission - Developer", reportsTo: "lead", order: 1));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(new[] { "worker", "lead" }, sessions.Ended.Select(e => e.SessionId).ToArray());
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);

        // The session under the lead was never asked by the Director the first time; at two thirds it was,
        // because for it the short message is the only description of the handover it will ever get.
        Assert.Contains(sessions.Sent, m => m.SessionId == "worker" && m.Text.Contains("HAND OVER NOW"));
    }

    // ================= a wedged session =================

    // Shows: a session whose composer will not take the words is shown as "not delivered", with the
    // delivery path's own reason, in the very first snapshot after it was tried - no time has passed - and
    // is not written off: it is tried again at two thirds and ended at the limit.
    [Fact]
    public async Task Start_AWedgedSession_ShowsNotDeliveredWithTheReasonAtOnce_AndIsEndedAtTheLimit()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("wedged", "Cannot take the words"));
        sessions.WedgedWithReason["wedged"] = "the composer never echoed the typed text after 2 attempts";

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        var first = watched.Seen.First(s => s.Snapshot.Sessions.Any(r => r.State == SmartShutdownSessionState.NotDelivered));
        Assert.Equal(Start, first.At);
        Assert.Equal(SmartShutdownPhase.Asking, first.Snapshot.Phase);
        Assert.Contains("the composer never echoed the typed text after 2 attempts", first.Snapshot.Sessions.Single().Detail);

        Assert.Equal(new[] { "wedged" }, sessions.Ended.Select(e => e.SessionId));
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, SavedSeat(sink, "wedged").DrainState);
        Assert.Contains(sink.Last.Integrity!.Problems, p => p.Contains("could not be DELIVERED"));
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
    }

    // ================= Shut down now =================

    // Shows: "Shut down now", pressed while the run is collecting, ends every session still present at
    // once - thirty seconds in, long before two thirds - with nobody interrupted and nobody asked again;
    // pressing it a second time does nothing; and every snapshot after the press says the button is spent.
    [Fact]
    public async Task ShutDownNow_FromTheCollectingPhase_EndsEverySessionStillPresentAtOnce_AndOnlyOnce()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "Never answers"), Seat("two", "Never answers either", order: 1));
        SmartShutdownPhase? phaseWhenPressed = null;

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, onPoll: (n, run) =>
        {
            if (n != 3) return;
            phaseWhenPressed = run.Current.Phase;
            Assert.True(run.Current.CanShutDownNow);
            run.ShutDownNow();
            run.ShutDownNow();
        });

        Assert.Equal(SmartShutdownPhase.Collecting, phaseWhenPressed);
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Equal(new[] { "one", "two" }, sessions.Ended.Select(e => e.SessionId).OrderBy(x => x).ToArray());
        Assert.Equal(Start.AddSeconds(30), _now);
        Assert.Empty(sessions.Interrupted);
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains("HAND OVER NOW"));
        Assert.All(sink.Last.Seats, s => Assert.Equal(WorkspaceDrainStates.EndedAtLimit, s.DrainState));

        var ending = watched.Seen.Where(s => s.Snapshot.Phase == SmartShutdownPhase.EndingAtLimit).ToList();
        Assert.NotEmpty(ending);
        Assert.All(ending, s => Assert.False(s.Snapshot.CanShutDownNow));
        Assert.Contains(ending, s => s.Snapshot.Note is not null && s.Snapshot.Note.Contains("Shut down now"));
        Assert.False(watched.Result.Final.CanShutDownNow);
    }

    // Shows: "Shut down now" is honoured before anything has been asked too - pressed before the run's
    // first step, no session is ever sent a request, and every one is recorded and ended.
    [Fact]
    public async Task ShutDownNow_BeforeAnythingIsAsked_AsksNobodyAndEndsEverySession()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("one", "First"), Seat("two", "Second", order: 1));

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, beforeRelease: run => run.ShutDownNow());

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Empty(sessions.Sent);
        Assert.Equal(2, sessions.Ended.Count);
        Assert.Equal(Start, _now);
        Assert.All(sink.Last.Seats, s => Assert.Equal("conversation-of-" + s.SessionId, s.ClaudeSessionId));
    }

    // ================= the snapshots =================

    // Shows: one real run over six kinds of session reaches every state a row can be in short of a cancel,
    // and every phase short of a cancel and a restart; and EVERY snapshot raised on the way is whole - its
    // count is the count of its rows, its label says that count, every word is the engine's own, the two
    // times are where they belong, and cancel is never offered because it is not built.
    [Fact]
    public async Task Start_AcrossOneRealRun_EveryStateAndPhaseIsReached_AndEverySnapshotIsWhole()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(
            Seat("lead", "Mission - Tech Lead"),
            Seat("worker", "Mission - Developer", reportsTo: "lead", order: 1),
            Seat("busy", "In a long turn", order: 2),
            Seat("slow", "Writes slowly", order: 3),
            Seat("wedged", "Cannot take the words", order: 4),
            Seat("silent", "Never answers", order: 5));
        sessions.Handover(dir.Path, "lead", "Mission - Tech Lead", DrainTestRig.Block(
            covered: new[] { ("worker", "It had nothing of its own and reported up.") }));
        sessions.MidTurn.Add("busy");
        sessions.HandoverWhenToldToHandOverNow(dir.Path, "busy", "In a long turn", DrainTestRig.Block());
        sessions.WedgedWithReason["wedged"] = "the composer never echoed the typed text";
        var slowPath = DrainPaths.HandoverFor(dir.Path, "slow", "Writes slowly");
        sessions.WhenMessaged = () => File.WriteAllText(slowPath, "## The exact next action\n\nNot finished.");

        var watched = await RunWatchedAsync(sessions, sink, dir.Path);

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);

        var states = watched.Seen.SelectMany(s => s.Snapshot.Sessions).Select(r => r.State).ToHashSet();
        foreach (var expected in new[]
                 {
                     SmartShutdownSessionState.Pending, SmartShutdownSessionState.Asked,
                     SmartShutdownSessionState.NotDelivered, SmartShutdownSessionState.Writing,
                     SmartShutdownSessionState.HandedOver, SmartShutdownSessionState.Interrupted,
                     SmartShutdownSessionState.ShutDown, SmartShutdownSessionState.EndedAtLimit,
                 })
            Assert.Contains(expected, states);

        var phases = watched.Seen.Select(s => s.Snapshot.Phase).ToList();
        Assert.Equal(
            new[]
            {
                SmartShutdownPhase.Starting, SmartShutdownPhase.Asking, SmartShutdownPhase.Collecting,
                SmartShutdownPhase.Interrupting, SmartShutdownPhase.Collecting,
                SmartShutdownPhase.EndingAtLimit, SmartShutdownPhase.Finished,
            },
            phases.Where((p, i) => i == 0 || phases[i - 1] != p).ToArray());

        foreach (var (_, snapshot) in watched.Seen)
        {
            Assert.Equal(snapshot.Sessions.Count, snapshot.Total);
            var gone = snapshot.Sessions.Count(r =>
                r.State is SmartShutdownSessionState.ShutDown or SmartShutdownSessionState.EndedAtLimit);
            Assert.Equal(gone, snapshot.Gone);
            Assert.Equal($"{gone} of {snapshot.Sessions.Count} shut down", snapshot.CountLabel);
            Assert.Equal(SmartShutdownWords.PhaseLabel(snapshot.Phase), snapshot.PhaseLabel);
            Assert.All(snapshot.Sessions, r => Assert.Equal(SmartShutdownWords.StateLabel(r.State), r.StateLabel));
            Assert.Equal(Start, snapshot.StartedUtc);
            Assert.Equal(Start.AddSeconds(400), snapshot.InterruptAtUtc);
            Assert.Equal(Start.AddMinutes(10), snapshot.LimitUtc);
            Assert.False(snapshot.CanCancel);
        }

        // The record exists from the first snapshot that has rows, and the last one counts all six gone.
        Assert.Null(watched.Seen[0].Snapshot.WorkspaceId);
        Assert.All(watched.Seen.Where(s => s.Snapshot.Sessions.Count > 0), s => Assert.NotNull(s.Snapshot.WorkspaceId));
        Assert.Equal("6 of 6 shut down", watched.Result.Final.CountLabel);
        Assert.Equal(watched.Result.WorkspaceId, watched.Result.Final.WorkspaceId);

        // The covered session is handed over by its lead's document, and the row says so.
        Assert.Contains(watched.RowsOf("worker"),
            r => r.State == SmartShutdownSessionState.HandedOver && r.Detail!.Contains("reported up"));
    }

    // Shows: a snapshot is raised at least once per poll even when nothing at all changes between two polls
    // - here a single session that never answers, for ten minutes.
    [Fact]
    public async Task Start_WhileNothingChanges_StillRaisesASnapshotEveryPoll()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("silent", "Never answers"));
        var seenAtEachPoll = new List<int>();
        List<(DateTime, SmartShutdownSnapshot)>? seen = null;

        var listening = new TaskCompletionSource();
        var engine = Engine(sessions, sink, dir.Path,
            onPoll: _ => seenAtEachPoll.Add(seen!.Count), reachGateway: _ => listening.Task);
        var run = engine.Start(new SmartShutdownRequest(SmartShutdownPurpose.Close, TimeSpan.FromMinutes(5), null));
        seen = new List<(DateTime, SmartShutdownSnapshot)>();
        run.Changed += s => seen.Add((_now, s));
        listening.SetResult();
        await run.Completion;

        Assert.Equal(30, seenAtEachPoll.Count);
        for (var i = 1; i < seenAtEachPoll.Count; i++)
            Assert.True(seenAtEachPoll[i] > seenAtEachPoll[i - 1],
                $"no snapshot was raised between poll {i} and poll {i + 1}");
    }

    // Shows: a screen whose handler throws on every snapshot stops neither the shutdown nor the handler
    // attached after it.
    [Fact]
    public async Task Start_AChangedHandlerThatThrows_DoesNotStopTheRunOrTheNextHandler()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("silent", "Never answers"));
        var threw = 0;

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, minutes: 5,
            beforeRelease: run => run.Changed += _ => { threw++; throw new InvalidOperationException("a screen failed"); });

        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
        Assert.Equal(new[] { "silent" }, sessions.Ended.Select(e => e.SessionId));
        Assert.True(threw > 30, $"the throwing handler was only called {threw} times");
        Assert.Equal(threw, watched.Seen.Count);
    }

    // ================= the five allowed times =================

    /// <summary>The rig, noting the clock at the moment of each interrupt and each end.</summary>
    private sealed class ClockedSessions : FakeSessionControl
    {
        private readonly Func<DateTime> _clock;
        public ClockedSessions(Func<DateTime> clock) => _clock = clock;
        public List<DateTime> InterruptedAt { get; } = new();
        public List<DateTime> EndedAt { get; } = new();

        public override Task<DrainDelivery> InterruptAsync(string sessionId)
        {
            InterruptedAt.Add(_clock());
            return base.InterruptAsync(sessionId);
        }

        public override Task<DrainEnd> EndAsync(string sessionId, string reason)
        {
            EndedAt.Add(_clock());
            return base.EndAsync(sessionId, reason);
        }
    }

    // Shows: for each of the five times the owner may choose, a session that works through all of it is
    // interrupted at exactly two thirds and ended at exactly the limit, the snapshots name the same two
    // moments, and the session is told that number of minutes.
    [Theory]
    [InlineData(5, 200)]
    [InlineData(10, 400)]
    [InlineData(15, 600)]
    [InlineData(30, 1200)]
    [InlineData(60, 2400)]
    public async Task Start_EachAllowedTime_InterruptsAtTwoThirdsAndEndsAtTheLimit(int minutes, int twoThirdsInSeconds)
    {
        using var dir = new TempDir();
        var sessions = new ClockedSessions(() => _now);
        var seat = Seat("busy", "Works through all of it");
        sessions.Live.Add("busy");
        sessions.MidTurn.Add("busy");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var watched = await RunWatchedAsync(sessions, sink, dir.Path, minutes);

        Assert.Equal(new[] { Start.AddSeconds(twoThirdsInSeconds) }, sessions.InterruptedAt);
        Assert.Equal(new[] { Start.AddMinutes(minutes) }, sessions.EndedAt);
        Assert.All(watched.Seen, s =>
        {
            Assert.Equal(Start.AddSeconds(twoThirdsInSeconds), s.Snapshot.InterruptAtUtc);
            Assert.Equal(Start.AddMinutes(minutes), s.Snapshot.LimitUtc);
        });
        Assert.Contains($"YOU HAVE {minutes} MINUTES", sessions.Sent[0].Text);
        Assert.Equal(SmartShutdownOutcome.Emptied, watched.Result.Outcome);
    }

    // ================= the older path =================

    // Shows: a drain run with none of the new options set is the drain it has always been - it never asks
    // whether a session is mid-turn, never interrupts and never ends one, even a session that is mid-turn
    // the whole time; it still says "you will not be killed"; its record carries no smart shutdown mark;
    // and its own default of ninety minutes still ends with the silent session recorded unreachable and
    // left running.
    [Fact]
    public async Task Drain_WithNoSmartShutdownOptions_IsTheOlderDrain_NinetyMinutesAndNothingForced()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("silent", "Never answers"));
        sessions.MidTurn.Add("silent");
        var progress = new List<DrainProgress>();
        var drain = new DirectorDrain(sessions, sink, "director-under-test", progress.Add,
            utcNow: () => _now,
            delay: (d, _) => { _now = _now.Add(d); return Task.CompletedTask; });

        var result = await drain.RunAsync(new DrainOptions { WorkspaceId = "older", WorkspaceName = "Older drain" }, dir.Path);

        Assert.Empty(sessions.AskedWhetherMidTurn);
        Assert.Empty(sessions.Interrupted);
        Assert.Empty(sessions.Ended);
        Assert.Contains("silent", sessions.Live);
        Assert.Equal(WorkspaceDrainStates.Unreachable, result.Document.Seats.Single().DrainState);
        Assert.Equal(Start.AddMinutes(90), _now);
        Assert.False(result.ReadyToRestart);
        Assert.False(result.Emptied);
        Assert.Contains("nothing was forced", result.Document.Integrity!.Problems.Single(p => p.Contains("never wrote")));
        Assert.Null(result.Document.ShutdownKind);
        Assert.Contains("YOU WILL NOT BE KILLED", sessions.Sent.Single().Text);
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains("HAND OVER NOW"));
        Assert.Contains(progress, p => p.Phase == "collecting");
        Assert.Equal("blocked", progress[^1].Phase);
    }

    // ================= the Gateway cannot be reached =================

    // Shows: when the Gateway does not answer, the check refuses the smart shutdown and says why in the
    // Gateway's own words, while still answering the restart question from the existing eligibility answer.
    [Fact]
    public async Task CheckAsync_TheGatewayCannotBeReached_RefusesWithTheReason()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        var engine = Engine(sessions, sink, dir.Path,
            reachGateway: _ => throw new HttpRequestException("No connection could be made to gateway.example"));

        var answer = await engine.CheckAsync(SmartShutdownPurpose.Close, CancellationToken.None);

        Assert.False(answer.CanSmartShutdown);
        Assert.Contains("No connection could be made to gateway.example", answer.SmartShutdownRefusal);
        Assert.Contains("Nothing has been touched", answer.SmartShutdownRefusal);
        Assert.True(answer.CanRestart);
        Assert.Null(answer.RestartRefusal);
        Assert.Empty(sessions.Sent);
        Assert.Null(sink.CaptureRequest);
    }

    // Shows: a Director with no Gateway client at all is refused too, with the sentence for that; and a
    // Director its launcher would not restart says so from the existing eligibility answer, word for word.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task CheckAsync_NoGatewayClient_AndALauncherThatWouldNotRestartThis_SaysBothAndWhy(bool? eligible)
    {
        var engine = new DirectorSmartShutdown(
            () => null,
            _ => Task.CompletedTask,
            () => new DirectorRestartEligibilityDto { Eligible = eligible, Reason = "this Director runs as the named instance 'slot5'" },
            "Test Director");

        var answer = await engine.CheckAsync(SmartShutdownPurpose.Restart, CancellationToken.None);

        Assert.False(answer.CanSmartShutdown);
        Assert.Contains("not connected to a Gateway", answer.SmartShutdownRefusal);
        Assert.False(answer.CanRestart);
        Assert.Equal("this Director runs as the named instance 'slot5'", answer.RestartRefusal);
    }

    // Shows: a Gateway that answers lets the smart shutdown through, and the check itself changes nothing.
    [Fact]
    public async Task CheckAsync_TheGatewayAnswers_AllowsIt_AndTouchesNothing()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));

        var answer = await Engine(sessions, sink, dir.Path).CheckAsync(SmartShutdownPurpose.Close, CancellationToken.None);

        Assert.True(answer.CanSmartShutdown);
        Assert.Null(answer.SmartShutdownRefusal);
        Assert.Empty(sessions.Journal!);
        Assert.Null(sink.CaptureRequest);
        Assert.Null(DirectorDrain.Running);
    }

    // Shows: Start with the Gateway unreachable ends Refused with the reason and touches no session at
    // all - nothing sent, renamed, flagged, interrupted or ended, nothing captured and nothing saved.
    [Fact]
    public async Task Start_TheGatewayCannotBeReached_IsRefusedAndTouchesNoSession()
    {
        using var dir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        sessions.MidTurn.Add("solo");
        var engine = Engine(sessions, sink, dir.Path,
            reachGateway: _ => throw new HttpRequestException("No connection could be made to gateway.example"));

        var run = engine.Start(new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null));
        var result = await run.Completion;

        Assert.Equal(SmartShutdownOutcome.Refused, result.Outcome);
        Assert.Contains("No connection could be made to gateway.example", result.Detail);
        Assert.Null(result.WorkspaceId);
        Assert.Equal(SmartShutdownPhase.Finished, result.Final.Phase);
        Assert.False(result.Final.CanShutDownNow);

        Assert.Empty(sessions.Journal!);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Renamed);
        Assert.Empty(sessions.Flagged);
        Assert.Empty(sessions.Interrupted);
        Assert.Empty(sessions.Ended);
        Assert.Empty(sessions.AskedWhetherMidTurn);
        Assert.Contains("solo", sessions.Live);
        Assert.Null(sink.CaptureRequest);
        Assert.Empty(sink.Saves);
        Assert.Null(DirectorSmartShutdown.Active);
    }

    // ================= one at a time =================

    // Shows: Start while a smart shutdown is already under way throws InvalidOperationException saying so,
    // before it touches anything, and the first run is unharmed and finishes.
    [Fact]
    public async Task Start_WithARunAlreadyUnderWay_ThrowsSayingSo_AndTheFirstRunIsUnharmed()
    {
        using var dir = new TempDir();
        using var secondDir = new TempDir();
        var (sessions, sink) = Rig(Seat("solo", "Standalone"));
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        var held = new TaskCompletionSource();
        var first = Engine(sessions, sink, dir.Path, reachGateway: _ => held.Task)
            .Start(new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null));

        try
        {
            Assert.Same(first, DirectorSmartShutdown.Active);
            var (otherSessions, otherSink) = Rig(Seat("other", "Another"));
            var refused = Assert.Throws<InvalidOperationException>(() =>
                Engine(otherSessions, otherSink, secondDir.Path)
                    .Start(new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null)));

            Assert.Contains("already under way", refused.Message);
            Assert.Empty(otherSessions.Journal!);
            Assert.Null(otherSink.CaptureRequest);
        }
        finally
        {
            held.SetResult();
        }

        Assert.Equal(SmartShutdownOutcome.Emptied, (await first.Completion).Outcome);
        Assert.Null(DirectorSmartShutdown.Active);
    }

    // Shows: Start while the OLDER drain holds this Director throws InvalidOperationException naming that
    // drain's record, and touches nothing.
    [Fact]
    public async Task Start_WithADrainAlreadyRunning_ThrowsNamingIt_AndTouchesNothing()
    {
        using var firstDir = new TempDir();
        using var dir = new TempDir();
        var release = new TaskCompletionSource();
        var parked = new TaskCompletionSource();
        var firstSessions = new FakeSessionControl();
        firstSessions.Live.Add("first");
        var firstSink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(DrainTestRig.Seat("first", "Held by the older drain")) };
        var older = new DirectorDrain(firstSessions, firstSink, "director-under-test", null,
            utcNow: () => _now,
            delay: async (_, _) => { parked.TrySetResult(); await release.Task.ConfigureAwait(false); _now = _now.AddMinutes(5); });
        var olderRun = older.RunAsync(
            new DrainOptions { WorkspaceId = "the-older-drain", WorkspaceName = "older", HandoverDeadline = TimeSpan.FromMinutes(2) },
            firstDir.Path);
        await parked.Task;

        // The older drain is always let go, whatever the assertions do: it holds a process-wide gate.
        try
        {
            var (sessions, sink) = Rig(Seat("solo", "Standalone"));
            var refused = Assert.Throws<InvalidOperationException>(() =>
                Engine(sessions, sink, dir.Path)
                    .Start(new SmartShutdownRequest(SmartShutdownPurpose.Close, SmartShutdownTimes.Default, null)));

            Assert.Contains("the-older-drain", refused.Message);
            Assert.Empty(sessions.Journal!);
            Assert.Null(sink.CaptureRequest);
            Assert.Null(DirectorSmartShutdown.Active);
        }
        finally
        {
            release.SetResult();
            await olderRun;
        }
        Assert.Null(DirectorDrain.Running);
    }

    // ================= the host =================

    // Shows: the HOST hands a screen the real engine and never nothing; on a host with no Gateway client
    // the engine it hands back says so through its own check.
    [Fact]
    public async Task CreateSmartShutdown_OnAHostWithNoGatewayClient_IsTheRealEngine_AndItsCheckSaysWhy()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            var engine = host.CreateSmartShutdown();

            Assert.IsType<DirectorSmartShutdown>(engine);
            var answer = await engine.CheckAsync(SmartShutdownPurpose.Close, CancellationToken.None);
            Assert.False(answer.CanSmartShutdown);
            Assert.Contains("not connected to a Gateway", answer.SmartShutdownRefusal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    // Shows: an engine made while the HOST had no Gateway client answers from the client the host has NOW.
    // The host is driven through its own replacement path (the one a settings change takes), handed a
    // configuration with no Gateway address so nothing is dialled. Only a Gateway client says "Gateway is
    // not configured", so that sentence in the refusal proves the engine asked the host's current client.
    // An engine that kept the client it was made with (none) would say "not connected to a Gateway" for ever.
    [Fact]
    public async Task CreateSmartShutdown_AnEngineMadeBeforeTheHostHadAClient_AnswersFromTheClientTheHostHasNow()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            var engine = host.CreateSmartShutdown();
            var before = await engine.CheckAsync(SmartShutdownPurpose.Close, CancellationToken.None);
            Assert.False(before.CanSmartShutdown);
            Assert.Contains("not connected to a Gateway", before.SmartShutdownRefusal);
            Assert.Null(host.CreateDrain());

            await host.ReapplyGatewayAsync(() => new GatewayConfig());

            // The host has a client now: the drain half of the engine sees it...
            Assert.NotNull(host.CreateDrain());
            // ...and so must the reachability half of the SAME engine, made before the client existed.
            var after = await engine.CheckAsync(SmartShutdownPurpose.Close, CancellationToken.None);
            Assert.False(after.CanSmartShutdown);
            Assert.Contains("the Gateway could not be reached", after.SmartShutdownRefusal);
            Assert.Contains("Gateway is not configured", after.SmartShutdownRefusal);
            Assert.DoesNotContain("not connected to a Gateway", after.SmartShutdownRefusal);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
