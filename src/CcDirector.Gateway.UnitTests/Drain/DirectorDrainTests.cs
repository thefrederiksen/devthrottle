using CcDirector.ControlApi.Drain;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// The drain itself: the five behaviours the hand-run got right by attention alone, asserted as code.
///
/// Every test here drives the REAL <see cref="DirectorDrain"/>. Only the two ends are faked - the live
/// sessions and the Gateway store - because a test that started eighteen agent processes to prove a close
/// ORDER would prove nothing about the order and take an hour.
///
/// The clock and the waits are injected, so a ninety-minute deadline is exercised in milliseconds.
/// </summary>
public class DirectorDrainTests
{
    private DateTime _now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);

    private DirectorDrain NewDrain(
        FakeSessionControl sessions, FakeWorkspaceSink sink, Action<DrainProgress>? progress = null)
        => new(sessions, sink, "director-under-test", progress,
            utcNow: () => _now,
            delay: (d, _) => { _now = _now.Add(d); return Task.CompletedTask; });

    private static DrainOptions Options(TimeSpan? deadline = null) => new()
    {
        WorkspaceId = "test-drain",
        WorkspaceName = "Test drain",
        Reason = "update to 2.0.6",
        HandoverDeadline = deadline ?? TimeSpan.FromMinutes(90),
        PollInterval = TimeSpan.FromSeconds(10),
        ReapTimeout = TimeSpan.FromMinutes(6),
    };

    // ================= message the chain, not the roster =================

    [Fact]
    public async Task Drain_MessagesTheHeadsOnly_NotEverySeat()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Linux Support - Architect"),
            DrainTestRig.Seat("mgr", "Linux Support - Manager", reportsTo: "arch", order: 1),
            DrainTestRig.Seat("w1", "Linux Support - VM Worker", reportsTo: "mgr", order: 2),
            DrainTestRig.Seat("solo", "Motivation videos", order: 3),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);

        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        foreach (var s in seats)
            DrainTestRig.WriteHandover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        // Two heads, two drain messages. The Manager and the Worker are reached by their own Architect,
        // which is the whole economy of this: seven messages covered seventeen sessions in the real run.
        var drainMessages = sessions.Sent.Where(m => m.Text.Contains("START NOTHING NEW")).ToList();
        Assert.Equal(2, drainMessages.Count);
        Assert.Equal(new[] { "arch", "solo" }, drainMessages.Select(m => m.SessionId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Drain_TellsASeniorToCollectItsOwnSeatsFirst_AndDoesNotTellALeafThat()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
            DrainTestRig.Seat("solo", "Standalone", order: 2),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        foreach (var s in seats)
            DrainTestRig.WriteHandover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var toArchitect = sessions.Sent.First(m => m.SessionId == "arch").Text;
        var toSolo = sessions.Sent.First(m => m.SessionId == "solo").Text;

        Assert.Contains("seats reporting to you", toArchitect);
        Assert.DoesNotContain("seats reporting to you", toSolo);

        // The three load-bearing phrases, in every drain message.
        foreach (var text in new[] { toArchitect, toSolo })
        {
            Assert.Contains("START NOTHING NEW", text);
            Assert.Contains("YOU WILL NOT BE KILLED", text);
            Assert.Contains("NO SECRETS", text);
            Assert.Contains("move-session", text);
        }
    }

    [Fact]
    public async Task Drain_TellsEachSeatTheExactPathItWatches()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seat = DrainTestRig.Seat("solo", "A session with / awkward : characters");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var expected = DrainPaths.HandoverFor(dir.Path, "solo", seat.Name);
        DrainTestRig.WriteHandover(dir.Path, "solo", seat.Name, DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        // A path the seat and the drain compute differently is a seat that looks unreachable while its
        // document sits on disk. The message must name the file the drain is actually watching.
        Assert.Contains(expected, sessions.Sent.First(m => m.SessionId == "solo").Text);
        Assert.Equal(expected, sink.Last.Seats.Single().HandoverPath);
    }

    // ================= close leaf-first =================

    [Fact]
    public async Task Drain_ClosesLeafFirst_NeverASeniorBeforeItsSubordinates()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("mgr", "Manager", reportsTo: "arch", order: 1),
            DrainTestRig.Seat("w1", "Worker 1", reportsTo: "mgr", order: 2),
            DrainTestRig.Seat("w2", "Worker 2", reportsTo: "mgr", order: 3),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        foreach (var s in seats)
            DrainTestRig.WriteHandover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.Equal(4, sessions.Flagged.Count);
        Assert.True(sessions.Flagged.IndexOf("w1") < sessions.Flagged.IndexOf("mgr"));
        Assert.True(sessions.Flagged.IndexOf("w2") < sessions.Flagged.IndexOf("mgr"));
        Assert.True(sessions.Flagged.IndexOf("mgr") < sessions.Flagged.IndexOf("arch"));
    }

    [Fact]
    public async Task Drain_ASeniorWhoseWorkerIsStillWritingIsNotClosed()
    {
        // The near-miss from the first real drain, as a test: an Architect ready to close while two of its
        // Workers were still writing. Their documents would then have reached nobody.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 0 };
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        // Only the Architect writes. The Worker never does.
        DrainTestRig.WriteHandover(dir.Path, "arch", "Architect", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(5)), dir.Path);

        Assert.DoesNotContain("arch", sessions.Flagged);
        Assert.Contains("arch", sessions.Live);
        Assert.Null(result.Document.Seats.Single(s => s.SessionId == "arch").ClosedAtUtc);
        Assert.False(result.ReadyToRestart);
    }

    // ================= a seat with nothing to hand over reports UP =================

    [Fact]
    public async Task Drain_ASeatCoveredByItsSenior_IsCovered_WithNoFileInventedForIt()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager R-D"),
            DrainTestRig.Seat("wa", "Cube R-D Worker A", reportsTo: "mgr", order: 1),
            DrainTestRig.Seat("wb", "Cube R-D Worker B", reportsTo: "mgr", order: 2),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var managerDoc = DrainTestRig.WriteHandover(dir.Path, "mgr", "Manager R-D", DrainTestRig.Block(
            covered: new[]
            {
                ("wa", "Read its brief, wrote no code; a fresh Manager re-seats it on WORKER-RD-A.md."),
                ("wb", "Same - the modified files in the shared worktree are mine, not its."),
            }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        foreach (var id in new[] { "wa", "wb" })
        {
            var seat = result.Document.Seats.Single(s => s.SessionId == id);
            Assert.Equal(WorkspaceDrainStates.Covered, seat.DrainState);
            Assert.Equal("mgr", seat.CoveredBy);
            Assert.Equal(managerDoc, seat.HandoverPath);            // its SENIOR's document
            Assert.Equal(WorkspaceRestoreDecisions.Close, seat.Restore!.Decision);
            Assert.False(string.IsNullOrWhiteSpace(seat.CoveredNote));
        }

        // NO FILE IS INVENTED. Exactly one document exists: the Manager's.
        Assert.Single(Directory.GetFiles(dir.Path, "*.md"));
        Assert.True(result.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_RejectsACoveredClaimForASeatThatDoesNotReportThroughTheClaimer()
    {
        // A senior may account only for its own subtree. A claim on somebody else's seat would write that
        // seat off with no standing to do it, and it would then never be drained at all.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("mgrA", "Manager A"),
            DrainTestRig.Seat("mgrB", "Manager B", order: 1),
            DrainTestRig.Seat("wB", "Worker of B", reportsTo: "mgrB", order: 2),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        DrainTestRig.WriteHandover(dir.Path, "mgrA", "Manager A",
            DrainTestRig.Block(covered: new[] { ("wB", "I am writing this one off.") }));
        DrainTestRig.WriteHandover(dir.Path, "mgrB", "Manager B", DrainTestRig.Block());
        DrainTestRig.WriteHandover(dir.Path, "wB", "Worker of B", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var worker = result.Document.Seats.Single(s => s.SessionId == "wB");
        Assert.Equal(WorkspaceDrainStates.Drained, worker.DrainState);
        Assert.Null(worker.CoveredBy);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("does not report to it") && p.Contains("rejected"));
    }

    [Fact]
    public async Task Drain_RejectsACoveredClaimNamingASeatThatIsNotOnThisDirector()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("mgr", "Manager");
        sessions.Live.Add("mgr");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        DrainTestRig.WriteHandover(dir.Path, "mgr", "Manager",
            DrainTestRig.Block(covered: new[] { ("a-session-somewhere-else", "not here") }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("not a seat on this Director"));
    }

    // ================= the reap is asynchronous =================

    [Fact]
    public async Task Drain_RecordsAClosedTimeOnlyWhenTheSessionIsGENUINELYABSENT()
    {
        // Marking a session done is a FLAG. The session stays in the fleet, state Running, for a while
        // afterwards, and a close time stamped at the flag is not true.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 3 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.NotNull(result.Document.Seats.Single().ClosedAtUtc);
        Assert.DoesNotContain("solo", sessions.Live);

        // The record was written more than once - and there is a save in which the seat is flagged and
        // still has no close time. That is the state a close stamped at the flag would have skipped.
        Assert.Contains(sink.Saves, s =>
            s.Seats.Count == 1 && s.Seats[0].DrainState == WorkspaceDrainStates.Drained
            && s.Seats[0].ClosedAtUtc is null);
    }

    [Fact]
    public async Task Drain_ASessionThatNeverGoesAway_IsNotForcedAndHasNoCloseTime()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        sessions.NeverReaped.Add("stuck");
        var seat = DrainTestRig.Seat("stuck", "A session mid-turn for ever");
        sessions.Live.Add("stuck");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "stuck", seat.Name, DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(
            Options(TimeSpan.FromMinutes(3)), dir.Path);

        Assert.Null(result.Document.Seats.Single().ClosedAtUtc);
        Assert.Contains("stuck", sessions.Live);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems, p => p.Contains("NOT forced"));
    }

    // ================= never force =================

    [Fact]
    public async Task Drain_ABlockedSeatIsNeverFlagged_AndTheRestartDoesNotHappen()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        DrainTestRig.WriteHandover(dir.Path, "arch", "Architect", DrainTestRig.Block());
        DrainTestRig.WriteHandover(dir.Path, "w", "Worker", DrainTestRig.Block(
            state: "blocked", restore: null, why: null,
            blockedReason: "A release is being published; stopping now leaves a half-pushed tag."));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var worker = result.Document.Seats.Single(s => s.SessionId == "w");
        Assert.Equal(WorkspaceDrainStates.Blocked, worker.DrainState);
        Assert.Contains("half-pushed tag", worker.BlockedReason!);

        // Nothing was flagged: not the blocked seat, and not its Architect, which the leaf-first gate
        // holds open behind it.
        Assert.Empty(sessions.Flagged);
        Assert.Equal(2, sessions.Live.Count);
        Assert.Equal(WorkspaceOutcomes.Blocked, result.Document.Outcome);
        Assert.False(result.ReadyToRestart);
        Assert.Contains("half-pushed tag", result.NotReadyReason!);
    }

    [Fact]
    public async Task Drain_ASeatThatNeverWritesIsUnreachableAfterTheDeadline_AndKeepsRunning()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("quiet", "A session that never answered");
        sessions.Live.Add("quiet");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        Assert.Equal(WorkspaceDrainStates.Unreachable, result.Document.Seats.Single().DrainState);
        Assert.Contains("quiet", sessions.Live);
        Assert.Empty(sessions.Flagged);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems, p => p.Contains("never wrote a handover"));
    }

    // ================= re-read at close time =================

    [Fact]
    public async Task Drain_AHandoverAmendedAfterItWasRead_IsRereadAndTheLaterVersionWins()
    {
        // In the first real drain an Architect amended its document AFTER the index said "read and
        // verified" - it landed one last thing, deleted its branch and removed three worktrees, then said
        // to re-read. An entry saying "read" can be describing an earlier version of the file.
        using var dir = new TempDir();

        // The real window, which is why this matters at all: an Architect writes its document early and
        // then WAITS, because the leaf-first gate will not let it close until its Worker has gone. It
        // lands one last thing in that gap.
        var sessions = new AmendingSessionControl(dir.Path, amendFor: "arch", whenWorkerFlagged: "w")
        {
            PollsBeforeReap = 2,
        };
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        DrainTestRig.WriteHandover(dir.Path, "arch", "Architect",
            DrainTestRig.Block(restore: true, why: "There is still work here."));
        DrainTestRig.WriteHandover(dir.Path, "w", "Worker", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var architect = result.Document.Seats.Single(x => x.SessionId == "arch");
        Assert.Equal(WorkspaceRestoreDecisions.Close, architect.Restore!.Decision);
        Assert.Equal("Merged to main after writing this; there is nothing left to do.", architect.Restore.Why);
        Assert.Null(architect.Restore.Command);
        Assert.DoesNotContain("arch", result.Document.RestoreAfterRestart);
        Assert.Contains(result.Document.Integrity!.Problems, p => p.Contains("amended its handover"));
        Assert.Contains(sessions.Sent, m => m.Text.Contains("changed after it was first read"));
    }

    /// <summary>
    /// A senior that amends its own handover while it is waiting for its Worker to be reaped - the real
    /// sequence from the first drain, where a session landed one last thing after its index entry already
    /// said "read and verified".
    /// </summary>
    private sealed class AmendingSessionControl : FakeSessionControl
    {
        private readonly string _dir;
        private readonly string _amendFor;
        private readonly string _trigger;
        private bool _done;

        public AmendingSessionControl(string dir, string amendFor, string whenWorkerFlagged)
        {
            _dir = dir;
            _amendFor = amendFor;
            _trigger = whenWorkerFlagged;
        }

        public override bool MarkForDeletion(string sessionId, string reason)
        {
            var ok = base.MarkForDeletion(sessionId, reason);
            if (!_done && string.Equals(sessionId, _trigger, StringComparison.OrdinalIgnoreCase))
            {
                _done = true;
                var path = DrainPaths.HandoverFor(_dir, _amendFor, "Architect");
                File.AppendAllText(path,
                    Environment.NewLine
                    + "Added after this document was first read: the branch merged."
                    + Environment.NewLine + Environment.NewLine
                    + DrainTestRig.Block(
                        restore: false,
                        why: "Merged to main after writing this; there is nothing left to do."));
            }
            return ok;
        }
    }

    // ================= the lock =================

    [Fact]
    public async Task Drain_ASecondDrainOnTheSameDirectorIsRefused()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("quiet", "Never answers");
        sessions.Live.Add("quiet");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var gate = new TaskCompletionSource();
        var first = new DirectorDrain(sessions, sink, "director-under-test", null,
            utcNow: () => _now,
            delay: async (_, _) => { await gate.Task.ConfigureAwait(false); _now = _now.AddMinutes(5); });

        var running = first.RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // The first drain is parked in its poll. A second must not start behind it: two drains on one
        // Director would message every seat twice and race each other to close them.
        var ex = await Assert.ThrowsAsync<DrainAlreadyRunningException>(() =>
            NewDrain(new FakeSessionControl(), new FakeWorkspaceSink()).RunAsync(Options(), dir.Path));
        Assert.Contains("already running", ex.Message);
        Assert.Contains("test-drain", ex.Message);

        gate.SetResult();
        await running;

        // And the lock is released, so the next one may run.
        Assert.Null(DirectorDrain.Running);
    }

    // ================= the record =================

    [Fact]
    public async Task Drain_TheRecordIsWrittenAsItGoes_NotOnceAtTheEnd()
    {
        // A drain that only writes its record at the end has no record of a drain that was interrupted -
        // and the interruption is exactly when somebody needs it.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.True(sink.Saves.Count >= 3);
        Assert.Equal(WorkspaceOutcomes.Draining, sink.Saves[0].Outcome);
        Assert.Equal(WorkspaceOutcomes.Drained, sink.Last.Outcome);
    }

    [Fact]
    public async Task Drain_TheRestoreListIsBuiltFromWhatEachSeatSaidAboutITSELF()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Linux Support - Architect"),
            DrainTestRig.Seat("mgr", "Linux Support - Manager", reportsTo: "arch", order: 1),
            DrainTestRig.Seat("done", "A finished mission", order: 2),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        DrainTestRig.WriteHandover(dir.Path, "arch", seats[0].Name,
            DrainTestRig.Block(restore: true, why: "Head of a mission with real continuing work."));
        DrainTestRig.WriteHandover(dir.Path, "mgr", seats[1].Name,
            DrainTestRig.Block(restore: false, why: "My Architect re-seats me from the committed brief."));
        DrainTestRig.WriteHandover(dir.Path, "done", seats[2].Name,
            DrainTestRig.Block(restore: false, why: "Merged and finished."));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.Equal(new[] { "arch" }, result.Document.RestoreAfterRestart);

        var architect = result.Document.Seats.Single(s => s.SessionId == "arch");
        Assert.Equal(WorkspaceRestoreDecisions.Restore, architect.Restore!.Decision);
        Assert.Contains("cc-devthrottle session spawn", architect.Restore.Command!);
        Assert.Contains(DrainRestoreCommand.NewDirectorToken, architect.Restore.Command!);
        Assert.Contains("--standalone", architect.Restore.Command!);
        Assert.Contains(architect.HandoverPath!, architect.Restore.Command!);
    }

    [Fact]
    public async Task Drain_RollsUpEveryOwnerQuestionWordForWord()
    {
        // A question buried in a paragraph inside a session that no longer exists is a question nobody
        // ever asks. Emptying a Director scatters them unless they are collected here.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("a", "One honest spoken figure - Manager"),
            DrainTestRig.Seat("b", "Email Spine - Architect", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        DrainTestRig.WriteHandover(dir.Path, "a", seats[0].Name, DrainTestRig.Block(
            questions: new[] { "Deploy the merged ring change (pull request 2718)? It needs your go." }));
        DrainTestRig.WriteHandover(dir.Path, "b", seats[1].Name, DrainTestRig.Block(
            questions: new[] { "Which mailbox should the spine send from?", "Do we keep the digest?" }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.Equal(3, result.Document.OwnerQuestions.Count);
        Assert.Contains(result.Document.OwnerQuestions,
            q => q.FromSessionId == "a"
              && q.Question == "Deploy the merged ring change (pull request 2718)? It needs your go.");
        Assert.Contains(result.Document.OwnerQuestions, q => q.FromName == "Email Spine - Architect");
    }

    [Fact]
    public async Task Drain_ASecretInAHandoverStopsTheRestart_AndTheRecordDoesNotCarryTheSecret()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Linux Support - VM Worker");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        const string Secret = "Zx9kkQQmm44rrSS";
        DrainTestRig.WriteHandover(dir.Path, "solo", seat.Name, DrainTestRig.Block(),
            body: DrainTestRig.Body + $"\n\nThe virtual machine password: {Secret}\n");

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var integrity = result.Document.Integrity!;
        Assert.False(result.ReadyToRestart);
        var finding = Assert.Single(integrity.SecretFindings);
        Assert.Equal("assigned-password", finding.Pattern);
        Assert.Equal("solo", finding.SeatSessionId);
        Assert.DoesNotContain(Secret, finding.RedactedExcerpt);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(result.Document));
        Assert.Contains("secret", result.NotReadyReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Drain_TheIntegrityBlockRecordsThatTheSweepWasProvedAbleToFail()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var integrity = result.Document.Integrity!;
        Assert.True(integrity.SweepPatternsTotal > 0);
        Assert.Equal(integrity.SweepPatternsTotal, integrity.SweepPatternsProved);
        Assert.Empty(integrity.SweepProofFailures);
        Assert.Equal(1, integrity.DocumentsSwept);
        Assert.True(result.ReadyToRestart);
        Assert.Null(result.NotReadyReason);
    }

    [Fact]
    public async Task Drain_ASeatThatWroteNoBlockIsStillDrained_ButTheRecordSaysSomebodyMustReadIt()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", block: "");

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var s = result.Document.Seats.Single();
        Assert.Equal(WorkspaceDrainStates.Drained, s.DrainState);
        Assert.Equal(WorkspaceRestoreDecisions.Undecided, s.Restore!.Decision);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems, p => p.Contains("declared no drain-report block"));
    }

    [Fact]
    public async Task Drain_AHalfWrittenHandoverIsTreatedAsStillBeingWritten()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        File.WriteAllText(DrainPaths.HandoverFor(dir.Path, "solo", "Standalone"), "## Where I am\n");

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // Read half-done it would have been closed on a stub. It is not read, so the seat is recorded as
        // one that never finished - which is true, and keeps it running.
        Assert.Equal(WorkspaceDrainStates.Unreachable, result.Document.Seats.Single().DrainState);
        Assert.Empty(sessions.Flagged);
    }

    [Fact]
    public async Task Drain_ASeatAlreadyGoneBeforeTheMessage_IsRecordedRatherThanWaitedFor()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();     // nothing live at all
        var seat = DrainTestRig.Seat("vanished", "A session that exited during the capture");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var s = result.Document.Seats.Single();
        Assert.Equal(WorkspaceDrainStates.Unreachable, s.DrainState);
        Assert.NotNull(s.ClosedAtUtc);
        Assert.Empty(sessions.Sent);
        Assert.False(result.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_ADrainMessageThatCouldNotBeDeliveredIsSaidOutLoud()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        sessions.Live.Add("solo");
        sessions.RefuseSendTo.Add("solo");
        var seat = DrainTestRig.Seat("solo", "Standalone");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("could not be delivered") && p.Contains("no way of knowing"));
    }

    [Fact]
    public async Task Drain_ReportsProgressAsItGoes()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var phases = new List<string>();
        await NewDrain(sessions, sink, p => phases.Add(p.Phase)).RunAsync(Options(), dir.Path);

        Assert.Equal("capturing", phases[0]);
        Assert.Contains("messaging", phases);
        Assert.Contains("collecting", phases);
        Assert.Contains("checking", phases);
        Assert.Equal("finished", phases[^1]);
    }

    [Fact]
    public async Task Drain_CapturesWithTheCallersOwnIdAndReason()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var options = Options();
        options.DrivenBySessionId = "the-session-that-pressed-it";
        await NewDrain(sessions, sink).RunAsync(options, dir.Path);

        Assert.Equal("test-drain", sink.CaptureRequest!.Id);
        Assert.Equal("director-under-test", sink.CaptureRequest.DirectorId);
        Assert.Equal("update to 2.0.6", sink.CaptureRequest.Reason);
        Assert.Equal("the-session-that-pressed-it", sink.CaptureRequest.DrivenBySessionId);

        // The driver's Director is recorded so a later reader can see it would NOT have survived the
        // restart - the drain records the fact and does not refuse on it.
        Assert.Equal("director-under-test", sink.CaptureRequest.DrivenByDirectorId);
    }

    [Fact]
    public async Task Drain_TheDocumentTheStoreReceivesCarriesEveryLaterChange()
    {
        // The store returns its own round-tripped copy. If the drain adopted that copy it would be writing
        // into a different object graph from the one it stores, and every change after the first save
        // would go nowhere. The last save must carry the close times, which happen long after it.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        DrainTestRig.WriteHandover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.NotNull(sink.Last.Seats.Single().ClosedAtUtc);
        Assert.NotNull(sink.Last.Integrity);
        Assert.NotNull(sink.Last.CompletedAtUtc);
    }
}
