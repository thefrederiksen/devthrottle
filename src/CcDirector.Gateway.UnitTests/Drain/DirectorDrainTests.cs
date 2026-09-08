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
        FakeSessionControl sessions,
        FakeWorkspaceSink sink,
        Action<DrainProgress>? progress = null,
        Action<int>? onPoll = null)
    {
        // onPoll fires on each injected wait, numbered from one. It is the ONE deterministic seam a test
        // has for "something happens between two passes of the drain" - a seat finishing its document, a
        // senior withdrawing a coverage claim. Hooking a presence probe instead depends on how many times
        // the drain happens to ask, which is not a fact any test should assert on.
        var polls = 0;
        return new DirectorDrain(sessions, sink, "director-under-test", progress,
            utcNow: () => _now,
            delay: (d, _) =>
            {
                _now = _now.Add(d);
                onPoll?.Invoke(++polls);
                return Task.CompletedTask;
            });
    }

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
            sessions.Handover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

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
            sessions.Handover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

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
        sessions.Handover(dir.Path, "solo", seat.Name, DrainTestRig.Block());

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
            sessions.Handover(dir.Path, s.SessionId!, s.Name, DrainTestRig.Block());

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
        sessions.Handover(dir.Path, "arch", "Architect", DrainTestRig.Block());

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

        var managerDoc = sessions.Handover(dir.Path, "mgr", "Manager R-D", DrainTestRig.Block(
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

        sessions.Handover(dir.Path, "mgrA", "Manager A",
            DrainTestRig.Block(covered: new[] { ("wB", "I am writing this one off.") }));
        sessions.Handover(dir.Path, "mgrB", "Manager B", DrainTestRig.Block());
        sessions.Handover(dir.Path, "wB", "Worker of B", DrainTestRig.Block());

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

        sessions.Handover(dir.Path, "mgr", "Manager",
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
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

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
        sessions.Handover(dir.Path, "stuck", seat.Name, DrainTestRig.Block());

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

        sessions.Handover(dir.Path, "arch", "Architect", DrainTestRig.Block());
        sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block(
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
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, result.Document.DirectorOutcome);
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

        sessions.Handover(dir.Path, "arch", "Architect",
            DrainTestRig.Block(restore: true, why: "There is still work here."));
        sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var architect = result.Document.Seats.Single(x => x.SessionId == "arch");
        Assert.Equal(WorkspaceRestoreDecisions.Close, architect.Restore!.Decision);
        Assert.Equal("Merged to main after writing this; there is nothing left to do.", architect.Restore.Why);
        Assert.Null(architect.Restore.Command);
        Assert.DoesNotContain("arch", result.Document.RestoreAfterRestart);
        Assert.Contains(result.Document.Integrity!.Problems, p => p.Contains("amended its handover"));

        // NOTHING IS SAID TO IT. The closing message is gone: a message delivers a prompt, a prompt
        // starts a turn, the reaper waits out a turn - so telling a seat it is being closed gave it one
        // last chance to say something nobody would ever read. The fact that its document changed belongs
        // in the record, where it survives the session, and that is where it is asserted above.
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains("you are being closed"));
        Assert.Single(sessions.Sent, m => m.SessionId == "arch");   // the drain message, and only that
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
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.True(sink.Saves.Count >= 3);
        Assert.Null(sink.Saves[0].DirectorOutcome);
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, sink.Last.DirectorOutcome);
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

        sessions.Handover(dir.Path, "arch", seats[0].Name,
            DrainTestRig.Block(restore: true, why: "Head of a mission with real continuing work."));
        sessions.Handover(dir.Path, "mgr", seats[1].Name,
            DrainTestRig.Block(restore: false, why: "My Architect re-seats me from the committed brief."));
        sessions.Handover(dir.Path, "done", seats[2].Name,
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

        sessions.Handover(dir.Path, "a", seats[0].Name, DrainTestRig.Block(
            questions: new[] { "Deploy the merged ring change (pull request 2718)? It needs your go." }));
        sessions.Handover(dir.Path, "b", seats[1].Name, DrainTestRig.Block(
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
        sessions.Handover(dir.Path, "solo", seat.Name, DrainTestRig.Block(),
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
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

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
    public async Task Drain_ASeatThatWroteNoBlockIsNeverRecordedAsDrained()
    {
        // THE RECORD AND THE CODE MUST NOT DISAGREE. This used to write "drained" onto the seat and keep
        // "it did not really declare" in a private set - so the DURABLE half, the one a stranger reads
        // after every session is gone, was the optimistic one. Drain state is now only ever set from a
        // valid declaration; a seat that wrote a document and declared nothing ends "declined".
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", block: "");

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        var only = result.Document.Seats.Single();
        Assert.Equal(WorkspaceDrainStates.Declined, only.DrainState);
        Assert.NotNull(only.HandoverPath);                 // its document is kept and named
        Assert.Empty(sessions.Flagged);
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

        // The seat starts writing when it is messaged and never finishes: a stub above nothing and below
        // the floor.
        sessions.WhenMessaged = () =>
            File.WriteAllText(DrainPaths.HandoverFor(dir.Path, "solo", "Standalone"), "## Where I am\n");

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // Read half-done it would have been closed on a stub. It is not read, so the seat is recorded as
        // one that never finished - which is true, and keeps it running.
        Assert.Equal(WorkspaceDrainStates.Unreachable, result.Document.Seats.Single().DrainState);
        Assert.Empty(sessions.Flagged);
    }

    [Fact]
    public async Task Drain_AHandoverThatEXISTSAndCannotBeRead_IsNotReportedAsOneThatWasNeverWritten()
    {
        // COULD-NOT-READ IS NOT NOTHING-IS-THERE. A locked or unreadable handover used to be
        // indistinguishable from a seat that never wrote one, so the drain would have recorded that seat
        // "unreachable" - blaming a session for the drain's own inability to read its work, and closing
        // nothing while saying the wrong thing about why.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("locked", "A seat whose document cannot be opened");
        sessions.Live.Add("locked");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var path = sessions.Handover(dir.Path, "locked", seat.Name, DrainTestRig.Block());

        // FileShare.None: no other handle may open this file at all, which is what a real lock looks like.
        // Taken the instant the seat writes it, and held for the whole run.
        FileStream? exclusive = null;
        sessions.WhenMessaged = () =>
            exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);
            var integrity = result.Document.Integrity!;

            Assert.NotNull(exclusive);
            Assert.False(result.ReadyToRestart);
            Assert.Contains(integrity.Problems,
                p => p.Contains("IS on disk and could not be read")
                  && p.Contains("not the seat failing to write"));

            // AND THE SWEEP DID NOT CERTIFY IT. A document the sweep could not open produces no findings,
            // which is indistinguishable from a clean one - so it is not counted as swept and it is named.
            Assert.Equal(0, integrity.DocumentsSwept);
            Assert.Contains(integrity.Problems,
                p => p.Contains("secret sweep could not read")
                  && p.Contains("NOTHING here says that document is clean"));

            // Never forced: the seat is still running and was never asked to close.
            Assert.Empty(sessions.Flagged);
            Assert.Contains("locked", sessions.Live);
        }
        finally
        {
            exclusive?.Dispose();
        }
    }

    [Fact]
    public async Task Drain_TheSweepCountsWhatItREAD_NotWhatItLISTED()
    {
        // One readable document and one locked one, in the same directory. Two swept would be a lie, and
        // it is the lie the epic's own proof exists to prevent: a locked file certifying a document
        // nobody read as carrying no secrets.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("ok", "A readable seat"),
            DrainTestRig.Seat("bad", "A seat whose document is locked", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "ok", seats[0].Name, DrainTestRig.Block());
        var locked = sessions.Handover(dir.Path, "bad", seats[1].Name, DrainTestRig.Block());

        FileStream? exclusive = null;
        sessions.WhenMessaged = () =>
            exclusive = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

            Assert.Equal(2, Directory.GetFiles(dir.Path, "*.md").Length);
            Assert.Equal(1, result.Document.Integrity!.DocumentsSwept);
            Assert.False(result.ReadyToRestart);
        }
        finally
        {
            exclusive?.Dispose();
        }
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
    public async Task Drain_AnAskThatDIDNOTLANDIsRecordedAtOnce_NotAfterTheWholeDeadline()
    {
        // ASKED-AND-IT-DID-NOT-LAND IS NOT ASKED-AND-STILL-WAITING, and the delivery path says which at
        // the moment of the attempt: a wedged seat comes back refused, with the prompt never echoed into
        // its composer. Collapsing the two into one wait spends a ninety-minute deadline to learn
        // something already known, and then reports the seat as silent when it was never spoken to.
        //
        // The deadline here is ninety minutes and the drain does not use any of it: the seat is terminal
        // on the first pass, which is what makes this a blocked-path test that needs no timeout.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("wedged", "A seat whose composer never echoes"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "wedged", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        sessions.RefuseSendTo.Add("wedged");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var polls = 0;
        var result = await NewDrain(sessions, sink, onPoll: _ => polls++)
            .RunAsync(Options(TimeSpan.FromMinutes(90)), dir.Path);

        var wedged = result.Document.Seats.Single(s => s.SessionId == "wedged");
        Assert.Equal(WorkspaceDrainStates.Unreachable, wedged.DrainState);
        Assert.Null(wedged.ClosedAtUtc);
        Assert.Empty(sessions.Flagged);
        Assert.False(result.ReadyToRestart);

        // It says WHICH of the two silences it is, and that the subtree behind it is not being asked.
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("the ask did not land")
              && p.Contains("never asked rather than asked and silent")
              && p.Contains("reporting through it"));

        // And it did not sit out the deadline to find out. The Worker below it still has to time out -
        // it was never asked, because its senior never got the message to ask it - so the drain does
        // wait; what it does not do is wait to learn the thing it already knew.
        Assert.True(polls > 0, "the drain still waits for the seats it did reach");
    }

    [Fact]
    public async Task Drain_AWedgedSeatAndAThinkingSeatAreTOLDAPARTInTheSameRun()
    {
        // THE NEGATIVE CONTROL, and without it the early-detection change is an assertion rather than
        // evidence. "The ask did not land" is only better than concluding from ninety minutes of silence
        // if it LOOKS DIFFERENT from a seat that received the ask and is merely thinking. So both are in
        // this one run, and the signals that separate them are named:
        //
        //   wedged   - the delivery answers NOT DELIVERED and carries the submit protocol's own words,
        //              "the composer never echoed the typed text". Nothing was added to Sent. The seat is
        //              terminal at once, on a presence rather than on an absence.
        //   thinking - the delivery answers DELIVERED, the text is in Sent, and the seat has no drain
        //              state because it has not written its document yet. It waits, and the wait is real.
        //
        // If those two ever stopped differing, this test fails and the deadline would have to come back -
        // which would be a finding about the delivery path rather than a reason to wait.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("wedged", "A wedged seat"),
            DrainTestRig.Seat("thinking", "A seat that is thinking", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        sessions.WedgedWithReason["wedged"] =
            "the composer never echoed the typed text after 2 attempts";

        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        // The thinking seat answers late - after the wedged one has already been recorded.
        var path = sessions.Handover(dir.Path, "thinking", "A seat that is thinking", DrainTestRig.Block());
        File.Delete(path);
        var written = false;

        var result = await NewDrain(sessions, sink, onPoll: n =>
        {
            if (n < 3 || written) return;
            written = true;
            File.WriteAllText(path, DrainTestRig.Body + Environment.NewLine + Environment.NewLine
                                    + DrainTestRig.Block());
        }).RunAsync(Options(TimeSpan.FromMinutes(20)), dir.Path);

        var wedged = result.Document.Seats.Single(s => s.SessionId == "wedged");
        var thinking = result.Document.Seats.Single(s => s.SessionId == "thinking");

        // THE SIGNALS DIFFER, and here they are.
        Assert.DoesNotContain(sessions.Sent, m => m.SessionId == "wedged");        // nothing went in
        Assert.Contains(sessions.Sent, m => m.SessionId == "thinking");            // the ask landed
        Assert.Equal(WorkspaceDrainStates.Unreachable, wedged.DrainState);         // terminal at once
        Assert.Equal(WorkspaceDrainStates.Drained, thinking.DrainState);           // answered, in its time
        Assert.NotNull(thinking.ClosedAtUtc);
        Assert.Null(wedged.ClosedAtUtc);

        // And the record carries the delivery path's own words rather than the drain's inference.
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("composer never echoed"));
        Assert.False(result.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_ReportsProgressAsItGoes()
    {
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

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
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

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
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.NotNull(sink.Last.Seats.Single().ClosedAtUtc);
        Assert.NotNull(sink.Last.Integrity);
        Assert.NotNull(sink.Last.CompletedAtUtc);
    }

    // ================= a document is not a declaration =================
    //
    // Everything below came out of an adversarial review by a different agent family. Each one is a way a
    // session's work could have been destroyed on an answer the drain had not actually been given.

    [Fact]
    public async Task Drain_ASeatThatWroteADocumentButDeclaredNothingIsNOTClosed()
    {
        // A file above the size floor proves a seat WROTE something, not that it FINISHED. A seat still
        // writing leaves exactly that, and closing on it destroys the session that was going to finish it.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", block: "");

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // The handover is KEPT - it is real and must not be lost - and the seat keeps running. It is
        // never recorded "drained": nothing said it finished.
        Assert.Equal(WorkspaceDrainStates.Declined, result.Document.Seats.Single().DrainState);
        Assert.NotNull(result.Document.Seats.Single().HandoverPath);
        Assert.Empty(sessions.Flagged);
        Assert.Contains("solo", sessions.Live);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("nothing says it FINISHED") && p.Contains("has not been closed"));
    }

    [Fact]
    public async Task Drain_ASeatThatDeclaresAStateItMayNotDeclare_IsDeclinedRatherThanDrained()
    {
        // "state: drainned" used to leave the seat DRAINED - the permissive branch - and close the session
        // on an answer nobody understood. "unreachable" is the drain's word, "covered" is its senior's.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block(state: "drainned"));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        Assert.Equal(WorkspaceDrainStates.Declined, result.Document.Seats.Single().DrainState);
        Assert.Empty(sessions.Flagged);
        Assert.False(result.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_ASeatDeclaringUNREACHABLEAboutItselfIsNotBelieved()
    {
        // "unreachable" is a verdict the DRAIN reaches about a seat that never answered. A seat that
        // declares it about itself has said something it cannot know, and it must not buy itself a state
        // that looks accounted-for.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone",
            DrainTestRig.Block(state: WorkspaceDrainStates.Unreachable));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        Assert.Equal(WorkspaceDrainStates.Declined, result.Document.Seats.Single().DrainState);
        Assert.Empty(sessions.Flagged);
    }

    [Fact]
    public async Task Drain_AnAmendmentThatWITHDRAWSTheCleanStopStopsTheClose()
    {
        // The amendment case with the outcome that matters. Re-applying only the restore fields would have
        // left the seat drained and closed it on a document that now says it is blocked.
        using var dir = new TempDir();
        var sessions = new WithdrawingSessionControl(dir.Path);
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "arch", "Architect", DrainTestRig.Block());
        sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(3)), dir.Path);

        var architect = result.Document.Seats.Single(s => s.SessionId == "arch");
        Assert.Equal(WorkspaceDrainStates.Blocked, architect.DrainState);
        Assert.Contains("release", architect.BlockedReason!);
        Assert.DoesNotContain("arch", sessions.Flagged);
        Assert.Contains("arch", sessions.Live);
        Assert.False(result.ReadyToRestart);
    }

    /// <summary>Withdraws its clean stop while waiting for its Worker to be reaped.</summary>
    private sealed class WithdrawingSessionControl : FakeSessionControl
    {
        private readonly string _dir;
        private bool _done;

        public WithdrawingSessionControl(string dir) { _dir = dir; PollsBeforeReap = 2; }

        public override bool MarkForDeletion(string sessionId, string reason)
        {
            var ok = base.MarkForDeletion(sessionId, reason);
            if (!_done && sessionId == "w")
            {
                _done = true;
                File.AppendAllText(DrainPaths.HandoverFor(_dir, "arch", "Architect"),
                    Environment.NewLine + Environment.NewLine
                    + DrainTestRig.Block(
                        state: "blocked", restore: null, why: null,
                        blockedReason: "A release is being published; stopping now leaves a half-pushed tag."));
            }
            return ok;
        }
    }

    [Fact]
    public async Task Drain_AHandoverThatCannotBeReREADAtCloseTimeStopsTheClose()
    {
        // The session about to be closed is the only thing that could write that document again.
        using var dir = new TempDir();
        var sessions = new DeletingSessionControl(dir.Path);
        var seats = new[]
        {
            DrainTestRig.Seat("arch", "Architect"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "arch", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "arch", "Architect", DrainTestRig.Block());
        sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(3)), dir.Path);

        Assert.DoesNotContain("arch", sessions.Flagged);
        Assert.Contains("arch", sessions.Live);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("has NOT been closed"));
    }

    /// <summary>Deletes the Architect's document while its Worker is being closed.</summary>
    private sealed class DeletingSessionControl : FakeSessionControl
    {
        private readonly string _dir;
        private bool _done;

        public DeletingSessionControl(string dir) { _dir = dir; PollsBeforeReap = 2; }

        public override bool MarkForDeletion(string sessionId, string reason)
        {
            var ok = base.MarkForDeletion(sessionId, reason);
            if (!_done && sessionId == "w")
            {
                _done = true;
                File.Delete(DrainPaths.HandoverFor(_dir, "arch", "Architect"));
            }
            return ok;
        }
    }

    [Fact]
    public async Task Drain_ACloseRequestTheDirectorREFUSEDIsNotRecordedAsAClose()
    {
        // The error landing in the optimistic branch: a refused deletion recorded as flagged anyway, and a
        // later ambiguous absence written up as a close time for a session nobody ever asked to stop.
        using var dir = new TempDir();
        var sessions = new RefusingSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        Assert.Null(result.Document.Seats.Single().ClosedAtUtc);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("could not be flagged for deletion"));
    }

    private sealed class RefusingSessionControl : FakeSessionControl
    {
        public override bool MarkForDeletion(string sessionId, string reason) => false;
    }

    [Fact]
    public async Task Drain_ASessionThatAPPEAREDAfterTheCaptureBlocksTheRestart()
    {
        // A seat spawned after the roster was taken is in no document, no sweep and no record. The restart
        // would destroy it without a trace, which is the exact failure this whole exercise exists to stop.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());
        sessions.WhenMessaged = () => sessions.Live.Add("a-session-nobody-captured");

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("appeared after the capture") && p.Contains("without a trace"));
    }

    // ================= refusals before anything is touched =================

    [Fact]
    public async Task Drain_RefusesToStartOverADirectoryThatAlreadyHoldsDocuments()
    {
        // A drain cancelled and restarted inside the same minute lands on the same directory name, and
        // every stale document in it would be read as this run's - closing seats on handovers they did
        // not write.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        File.WriteAllText(Path.Combine(dir.Path, "left over from an earlier run.md"), "stale");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewDrain(sessions, sink).RunAsync(Options(), dir.Path));

        Assert.Contains("already holds", ex.Message);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Flagged);
    }

    [Fact]
    public async Task Drain_RefusesToStartOnAReportingChainThatLoopsBackOnItself()
    {
        // The chain decides what may be closed and when. On a cycle the leaf-first gate means nothing, and
        // a whole ring of sessions can be reaped before the record gets round to saying so.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seats = new[]
        {
            DrainTestRig.Seat("a", "A", reportsTo: "b"),
            DrainTestRig.Seat("b", "B", reportsTo: "a", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewDrain(sessions, sink).RunAsync(Options(), dir.Path));

        Assert.Contains("loops back on itself", ex.Message);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Flagged);
        Assert.Equal(2, sessions.Live.Count);
    }

    [Fact]
    public async Task Drain_RefusesWhenTwoSeatsWouldWriteTheSameDocument()
    {
        // An eight-character prefix collision, or two names that sanitize the same: the second write
        // overwrites the first and both seats get closed on one document.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        var seats = new[]
        {
            DrainTestRig.Seat("aaaaaaaa-1111-1111-1111-111111111111", "Same name"),
            DrainTestRig.Seat("aaaaaaaa-2222-2222-2222-222222222222", "Same name", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewDrain(sessions, sink).RunAsync(Options(), dir.Path));

        Assert.Contains("would both write", ex.Message);
        Assert.Empty(sessions.Sent);
    }

    [Fact]
    public async Task Drain_ASeatWhoseIdThisDirectorCannotAddressIsNeverCalledABSENT()
    {
        // IsPresent has two answers and the drain writes a CLOSE TIME on false. An id it cannot look up
        // would come back false, and "I could not look it up" would land in the record as "verified gone".
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        sessions.Undrivable.Add("not-an-id");
        var seats = new[]
        {
            DrainTestRig.Seat("solo", "Standalone"),
            DrainTestRig.Seat("not-an-id", "A seat with a malformed id", order: 1),
        };
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var bad = result.Document.Seats.Single(s => s.SessionId == "not-an-id");
        Assert.Null(bad.ClosedAtUtc);
        Assert.Null(bad.DrainState);
        Assert.DoesNotContain(sessions.Sent, m => m.SessionId == "not-an-id");
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("not one this Director can look up"));
    }

    [Fact]
    public async Task Drain_RefusesWhenOneSessionIdAppearsOnTwoSeats()
    {
        // Keeping the first row and dropping the rest looked tidy and was a lie: there is ONE session
        // behind that id, and it would have been messaged and closed using one row's name, controller and
        // restore intent while the other row was recorded as "left alone" - which the drain cannot do,
        // because it is the same session.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("solo", "First"),
            DrainTestRig.Seat("solo", "Second", order: 1),
        };
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewDrain(sessions, sink).RunAsync(Options(), dir.Path));

        Assert.Contains("more than once", ex.Message);
        Assert.Empty(sessions.Sent);
        Assert.Empty(sessions.Flagged);

        // AND THE REFUSAL IS IN THE RECORD. The capture is already on the Gateway by this point; a refusal
        // that only threw would leave a workspace reading "draining" for ever with nothing saying why.
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, sink.Last.DirectorOutcome);
        Assert.Contains(sink.Last.Integrity!.Problems, p => p.Contains("refused to start"));
        Assert.False(sink.Last.Integrity.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_AnInstanceRefusesToRunTwice()
    {
        // The closed set, the flagged set and the read stamps belong to ONE run. A seat closed in the
        // first would satisfy the leaf-first gate in the second while a live seat with that id is open.
        using var dir1 = new TempDir();
        using var dir2 = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir1.Path, "solo", "Standalone", DrainTestRig.Block());

        var drain = NewDrain(sessions, sink);
        await drain.RunAsync(Options(), dir1.Path);

        var ex = await Assert.ThrowsAsync<DrainAlreadyRunningException>(
            () => drain.RunAsync(Options(), dir2.Path));
        Assert.Contains("already been run", ex.Message);
    }

    [Fact]
    public async Task Drain_ARestoreCommandKeepsTheREALIdOfAControllerThatIsNotBeingRestarted()
    {
        // A controller on another Director survives the restart and keeps the id it has. A placeholder
        // there sends whoever runs the command hunting for a new id nobody will ever mint.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("w", "Worker", reportsTo: "a-controller-on-another-director");
        sessions.Live.Add("w");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "w", "Worker",
            DrainTestRig.Block(restore: true, why: "Real continuing work."));

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        var command = result.Document.Seats.Single().Restore!.Command!;
        Assert.Contains("--controlled-by a-controller-on-another-director", command);
        Assert.DoesNotContain("--controlled-by <the new id of", command);

        // The DIRECTOR placeholder is still there and must be: this Director does get a new identifier.
        Assert.Contains("--director " + DrainRestoreCommand.NewDirectorToken, command);
    }

    // ================= the one door off this machine =================

    [Theory]
    [InlineData("why")]
    [InlineData("blocked-reason")]
    [InlineData("question")]
    [InlineData("covered-note")]
    [InlineData("unparsed-line")]
    public async Task Drain_NothingASeatWROTEReachesTheGatewayUnswept(string where)
    {
        // THE SWEEP WAS GUARDING THE WRONG EXIT. It redacted its own finding excerpts while the drain
        // lifted a seat's prose straight out of its block and stored it verbatim - and several of those
        // fields are written before the sweep has run at all. So a token a seat typed into "why:" left
        // this machine inside a record built by the component whose job is to stop that.
        //
        // The remedy is NOT a list of fields to redact - that is the same enumeration gap in a different
        // coat, and the next field anybody adds is not on it. The guard is at the boundary, over the
        // payload. This test is a Theory for exactly that reason: it names five different places and the
        // code that protects them knows about none of them.
        const string Secret = "Zx9kkQQmm44rrSS";

        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        var block = where switch
        {
            "why" => DrainTestRig.Block(restore: true, why: $"Continue: the box password: {Secret}"),
            "blocked-reason" => DrainTestRig.Block(
                state: "blocked", restore: null, why: null,
                blockedReason: $"Waiting on a deploy; password: {Secret}"),
            "question" => DrainTestRig.Block(questions: new[] { $"Is the password: {Secret} still right?" }),
            "covered-note" => DrainTestRig.Block(covered: new[] { ("w", $"nothing of its own; password: {Secret}") }),
            _ => "<!-- drain-report\nstate: drained\nrestore: no\nwhy: done\n"
                 + $"resore: password: {Secret}\n-->",
        };

        sessions.Handover(dir.Path, "mgr", "Manager", block);
        if (where != "covered-note") sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block());

        await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // EVERY save, not only the last: several of these fields are stored long before the sweep runs.
        foreach (var stored in sink.Saves)
            Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(stored));
    }

    [Fact]
    public void RedactedForTransmission_HidesAValueInAFieldThisCodeHasNeverHeardOf()
    {
        // The property the boundary has that a list of fields cannot: it does not know what it is
        // protecting. A secret in the workspace's DESCRIPTION - a field no drain ever writes and no
        // redaction rule mentions - is hidden by the same pass, because what is checked is the payload.
        const string Secret = "Zx9kkQQmm44rrSS";
        var doc = DrainTestRig.Document(DrainTestRig.Seat("solo", "Standalone"));
        doc.Description = $"password: {Secret}";
        doc.Reason = $"api_key = {Secret}0000";

        var redacted = DirectorDrain.RedactedForTransmission(doc);

        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(redacted));
        Assert.Contains("[REDACTED", redacted.Description);

        // And the document still parses back into itself - the seats and the shape survive.
        Assert.Single(redacted.Seats);
        Assert.Equal("Standalone", redacted.Seats[0].Name);
        Assert.Equal(doc.Id, redacted.Id);
    }

    // ================= round three: what the fix round itself got wrong =================

    [Fact]
    public async Task Drain_ASeatThatWroteItsOwnDocumentIsNotCovered_WhateverItsSeniorSays()
    {
        // THE WORST FINDING OF ROUND TWO, and the fix round created it. A covered claim was rejected only
        // if the target had already been PROCESSED, so on the wrong iteration order a senior read first
        // could cover a worker whose own handover said "state: blocked" - the worker's document would
        // never be parsed, its questions would be dropped, and it would be closed as covered while the
        // record still read ready.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        // The Manager, read FIRST, claims the Worker. The Worker has written its own document, and it
        // says it is blocked.
        sessions.Handover(dir.Path, "mgr", "Manager",
            DrainTestRig.Block(covered: new[] { ("w", "nothing of its own") }));
        sessions.Handover(dir.Path, "w", "Worker", DrainTestRig.Block(
            state: "blocked", restore: null, why: null,
            blockedReason: "A release is being published; stopping now leaves a half-pushed tag."));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        var worker = result.Document.Seats.Single(s => s.SessionId == "w");
        Assert.Equal(WorkspaceDrainStates.Blocked, worker.DrainState);
        Assert.Null(worker.CoveredBy);
        Assert.Contains("half-pushed tag", worker.BlockedReason!);
        Assert.Empty(sessions.Flagged);                 // and its Manager is held open behind it
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("has written its OWN handover") && p.Contains("rejected"));
    }

    [Fact]
    public async Task Drain_ADocumentReadBeforeItsBlockIsAppendedStillCONVERGES()
    {
        // THE LATCH. A document read before its final block was appended used to be stamped "drained" on
        // that first read, and every later poll skipped it - so the seat never converged even after it
        // finished properly, and the drain waited out its whole deadline for a document that was ready.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        // Written WITHOUT a block, and finished two passes later - which is what a seat writing its
        // handover in pieces actually does.
        var path = sessions.Handover(dir.Path, "solo", "Standalone", block: "");

        var result = await NewDrain(sessions, sink, onPoll: n =>
        {
            if (n == 2) File.AppendAllText(path, Environment.NewLine + DrainTestRig.Block());
        }).RunAsync(Options(TimeSpan.FromMinutes(5)), dir.Path);

        var only = result.Document.Seats.Single();
        Assert.Equal(WorkspaceDrainStates.Drained, only.DrainState);
        Assert.NotNull(only.ClosedAtUtc);
        Assert.Contains("solo", sessions.Flagged);
        Assert.True(result.ReadyToRestart);
    }

    [Fact]
    public async Task Drain_ABlockCarryingALineItDidNotUnderstandIsNotActedOnAtAll()
    {
        // A seat that wrote a line the drain could not read has said something, and the drain does not
        // know what. Acting on the REST of that block - closing the seat, applying its covers - is the
        // same trinary failure as an unknown state: not-knowing landing on the permissive side.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "mgr", "Manager",
            "<!-- drain-report\nstate: drained\nresore: yes\ncovered: w | nothing of its own\n-->");

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(2)), dir.Path);

        // Neither the Manager's clean stop nor its coverage claim was acted on.
        Assert.Equal(WorkspaceDrainStates.Declined, result.Document.Seats.Single(s => s.SessionId == "mgr").DrainState);
        Assert.Null(result.Document.Seats.Single(s => s.SessionId == "w").CoveredBy);
        Assert.Empty(sessions.Flagged);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("resore") && p.Contains("Nothing in this block has been acted on"));
    }

    [Fact]
    public async Task Drain_ACoveredSeatIsNotClosedWhenItsSeniorsDocumentNoLongerNamesIt()
    {
        // A covered seat has no document of its own, so the only thing that accounts for it is its
        // senior's - and that was closed on without ever being re-read. If the claim has gone, nothing
        // accounts for the seat and it must not be closed on it.
        using var dir = new TempDir();
        var sessions = new UncoveringSessionControl(dir.Path, newBlock: null);
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "mgr", "Manager",
            DrainTestRig.Block(covered: new[] { ("w", "nothing of its own") }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(3)), dir.Path);


        Assert.DoesNotContain("w", sessions.Flagged);
        Assert.Contains("w", sessions.Live);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("covered by a document that"));
    }

    /// <summary>
    /// Rewrites the senior's document the first time the drain looks at the covered WORKER - the moment
    /// between the claim being recorded and the worker being closed on it. Hooked on the worker because
    /// the worker is not a head, so its first presence probe is the close pass.
    /// </summary>
    private sealed class UncoveringSessionControl : FakeSessionControl
    {
        private readonly string _dir;
        private readonly string? _newBlock;
        private bool _done;

        /// <param name="dir">The drain directory.</param>
        /// <param name="newBlock">The senior's document is rewritten with this block. NULL deletes the
        /// document instead, which is the could-not-read branch rather than the claim check.</param>
        public UncoveringSessionControl(string dir, string? newBlock)
        {
            _dir = dir;
            _newBlock = newBlock;
            PollsBeforeReap = 2;
        }

        public override bool IsPresent(string sessionId)
        {
            if (!_done && sessionId == "w")
            {
                _done = true;
                var path = DrainPaths.HandoverFor(_dir, "mgr", "Manager");
                if (_newBlock is null) File.Delete(path);
                else File.WriteAllText(path, DrainTestRig.Body + Environment.NewLine + Environment.NewLine + _newBlock);
            }
            return base.IsPresent(sessionId);
        }
    }

    [Fact]
    public async Task Drain_ACoveredSeatIsNotClosedWhenTheClaimIsWITHDRAWNFromAReadableDocument()
    {
        // The version of the previous test that actually reaches the claim check. Deleting the senior's
        // document exercises only the could-not-read branch - a mutation that always accepted the
        // coverage would still pass it. Here the document is perfectly readable, still a valid
        // declaration, and simply no longer names the worker.
        using var dir = new TempDir();
        var sessions = new UncoveringSessionControl(dir.Path, DrainTestRig.Block());
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "mgr", "Manager",
            DrainTestRig.Block(covered: new[] { ("w", "nothing of its own") }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(3)), dir.Path);

        Assert.DoesNotContain("w", sessions.Flagged);
        Assert.Contains("w", sessions.Live);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("no longer names it"));
    }

    [Fact]
    public async Task Drain_ACoveredSeatIsNotClosedWhenTheCOVERINGBlockStopsBeingADeclaration()
    {
        // And the third shape: the claim is still there, in a block that no longer amounts to a
        // declaration. A close-time check that is easier to pass than the check the claim passed when it
        // was made is not a check.
        using var dir = new TempDir();
        // The claim survives; the state line does not.
        var sessions = new UncoveringSessionControl(dir.Path, string.Join(
            Environment.NewLine, "<!-- drain-report", "covered: w | nothing of its own", "-->"));
        var seats = new[]
        {
            DrainTestRig.Seat("mgr", "Manager"),
            DrainTestRig.Seat("w", "Worker", reportsTo: "mgr", order: 1),
        };
        foreach (var s in seats) sessions.Live.Add(s.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seats) };

        sessions.Handover(dir.Path, "mgr", "Manager",
            DrainTestRig.Block(covered: new[] { ("w", "nothing of its own") }));

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(3)), dir.Path);

        Assert.DoesNotContain("w", sessions.Flagged);
        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("no longer amounts to a declaration"));
    }

    [Fact]
    public async Task Drain_ARefusedCloseIsAskedONCE_NotOnEveryPollForNinetyMinutes()
    {
        // A refused close used to stay eligible, so every ten-second poll re-renamed a live session, told
        // it again that it was being closed, and appended the same sentence to the record - for the rest
        // of the drain.
        using var dir = new TempDir();
        var sessions = new RefusingSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(TimeSpan.FromMinutes(30)), dir.Path);

        // Nothing was said to it at all: the flag goes first, and it failed.
        Assert.Empty(sessions.Renamed);
        Assert.DoesNotContain(sessions.Sent, m => m.Text.Contains("you are being closed"));
        Assert.Single(result.Document.Integrity!.Problems, p => p.Contains("could not be flagged"));
    }

    [Fact]
    public async Task Drain_ACaptureOfNothingItCanAddressSaysSO_NotThatTheDirectorWasEmpty()
    {
        // Undrivable seats are filtered out, so a capture made entirely of them reached the "no sessions
        // to drain" branch - hiding a real addressability fault behind the tidiest possible sentence.
        using var dir = new TempDir();
        var sessions = new FakeSessionControl();
        sessions.Undrivable.Add("not-an-id");
        var seat = DrainTestRig.Seat("not-an-id", "A seat with a malformed id");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.False(result.ReadyToRestart);
        Assert.Contains("not an empty Director", result.NotReadyReason!);
        Assert.DoesNotContain("had no sessions to drain", result.NotReadyReason!);
    }

    [Fact]
    public async Task Drain_AFailedLiveSessionListingIsNotReadAsAnEmptyFleet()
    {
        // The last check before a restart is allowed. An exception becoming "no extra sessions" is the
        // same fail-open as everything else here.
        using var dir = new TempDir();
        var sessions = new UnlistableSessionControl { PollsBeforeReap = 1 };
        var seat = DrainTestRig.Seat("solo", "Standalone");
        sessions.Live.Add("solo");
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(seat) };
        sessions.Handover(dir.Path, "solo", "Standalone", DrainTestRig.Block());

        var result = await NewDrain(sessions, sink).RunAsync(Options(), dir.Path);

        Assert.False(result.ReadyToRestart);
        Assert.Contains(result.Document.Integrity!.Problems,
            p => p.Contains("could not be listed") && p.Contains("NOTHING here says"));
    }

    private sealed class UnlistableSessionControl : FakeSessionControl
    {
        public override IReadOnlyList<string> LiveSessionIds()
            => throw new InvalidOperationException("the session registry is unreachable");
    }
}
