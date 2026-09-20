using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE STORE HALF OF "A SEAT IS MARKED WHEN IT IS DEALT WITH" (the Smart Director Restart mission, product
/// issue 3230).
///
/// A seat that ended without a handover is never brought back by a restore - it has no handover to read - so
/// its <see cref="WorkspaceSeat.RestoredSessionId"/> stays empty for ever, and nothing on the record used to
/// say the owner had already answered it. The way up therefore offered the same dead sessions again after
/// every restart. What closes it is a mark of kind <see cref="WorkspaceRestoreMarkKinds.Reopened"/>, and these
/// tests pin the four things that mark must do: be writable without a restore lease, refuse a second reopen,
/// let the Director that claimed it fill in the session id, and be unforgeable by an ordinary write.
///
/// IT LIVES UNDER Restart/ AND ITS NAME CARRIES "SmartRestart" ON PURPOSE. The mission's check is
/// <c>--filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"</c>, and a test whose name matches
/// neither is a test the mission never runs - which is a proof that certifies nothing.
/// </summary>
public sealed class SmartRestartReopenMarkStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 20, 12, 36, 0, DateTimeKind.Utc);
    private const string Id = "smart-shutdown-1";
    private const string Ended = "e0000000-0000-0000-0000-000000000000";
    private const string Lead = "10000000-0000-0000-0000-000000000000";
    private const string DirectorA = "director-a";
    private const string DirectorB = "director-b";

    public void Dispose() => _h.Dispose();

    private WorkspaceStore Captured()
    {
        var store = new WorkspaceStore(_h.Open());
        store.Create(new WorkspaceDocument
        {
            Id = Id,
            Name = "Smart shutdown",
            Origin = WorkspaceOrigins.Captured,
            Machine = "MAC",
            DirectorId = "old-director",
            ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
            Seats = new()
            {
                new WorkspaceSeat
                {
                    SessionId = Lead,
                    Name = "A lead",
                    Agent = "ClaudeCode",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.Drained,
                    HandoverPath = "/handovers/lead.md",
                    Restore = new WorkspaceSeatRestore
                    {
                        Decision = WorkspaceRestoreDecisions.Restore,
                        Why = "it was mid-task",
                        Command = "cc-devthrottle director restore ...",
                    },
                },
                new WorkspaceSeat
                {
                    SessionId = Ended,
                    Name = "A busy worker",
                    Agent = "ClaudeCode",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.EndedAtLimit,
                    ClaudeSessionId = "the-saved-conversation",
                    Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
                },
            },
            RestoreAfterRestart = new() { Lead },
        }, Now);
        return store;
    }

    private static WorkspaceRestoreMark Reopen(
        string seat = Ended, string director = DirectorA, string? reopenedAs = null) => new()
    {
        DirectorId = director,
        Kind = WorkspaceRestoreMarkKinds.Reopened,
        SeatSessionId = seat,
        ReopenedSessionId = reopenedAs,
    };

    private static WorkspaceSeat SeatOf(WorkspaceDocument doc, string id) => doc.Seats.Single(s => s.SessionId == id);

    /// <summary>
    /// A REOPEN NEEDS NO RESTORE LEASE, and this is the test that says so out loud. The lease is granted only
    /// by asking for a restore, and a reopen is not a restore: it starts one session, reads no ordering, and
    /// there is nothing for two Directors to interleave. A reopen that needed a lease could not be recorded at
    /// all, and then the seat could never be marked as dealt with.
    ///
    /// It also takes NO lease as a side effect - a reopen that quietly leased the workspace would block the
    /// bring back beside it for the next fifteen minutes.
    /// </summary>
    [Fact]
    public void AReopenMark_IsWrittenWithNoRestoreLease_AndTakesNone()
    {
        var store = Captured();

        var saved = store.RecordRestoreMark(Id, Reopen(), Now);

        var seat = SeatOf(saved, Ended);
        Assert.Equal(Now, seat.Restore!.ReopenedAtUtc);
        Assert.Equal(DirectorA, seat.Restore.ReopenedByDirectorId);
        Assert.Null(seat.Restore.ReopenedSessionId);
        Assert.Null(saved.RestoreLease);

        // And it touched nothing else: the seat's drain state and the lead beside it are as captured.
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, seat.DrainState);
        Assert.Null(seat.RestoredSessionId);
        Assert.Null(SeatOf(saved, Lead).Restore!.ReopenedAtUtc);
    }

    /// <summary>
    /// THE CLAIM IS WRITTEN FIRST AND THE SESSION ID FILLED IN AFTER, by the Director that claimed it. That is
    /// the order the engine uses, because a claim written only after a successful start would be missing for
    /// exactly the start whose answer never came back.
    /// </summary>
    [Fact]
    public void AReopenMark_LetsTheClaimingDirectorFillInWhichSessionTookIt()
    {
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(), Now);

        var saved = store.RecordRestoreMark(Id, Reopen(reopenedAs: "new-session-1"), Now.AddSeconds(4));

        var seat = SeatOf(saved, Ended);
        Assert.Equal("new-session-1", seat.Restore!.ReopenedSessionId);

        // The claim keeps the moment it was CLAIMED, not the moment it was completed: that is when the seat
        // stopped being offered.
        Assert.Equal(Now, seat.Restore.ReopenedAtUtc);
    }

    /// <summary>
    /// A SECOND REOPEN IS REFUSED, by name, and the refusal says why. This is the guarantee the whole mark
    /// exists for: two live agents in one saved conversation would interleave their turns into one transcript.
    /// Another Director cannot complete somebody else's claim either.
    /// </summary>
    [Fact]
    public void AReopenMark_OnASeatAlreadyReopened_IsRefused()
    {
        var store = Captured();
        store.RecordRestoreMark(Id, Reopen(reopenedAs: "new-session-1"), Now);

        var again = Assert.Throws<WorkspaceConflictException>(
            () => store.RecordRestoreMark(Id, Reopen(reopenedAs: "new-session-2"), Now.AddMinutes(1)));
        Assert.Contains("had its saved conversation reopened", again.Message);
        Assert.Contains("new-session-1", again.Message);

        var byAnother = Assert.Throws<WorkspaceConflictException>(
            () => store.RecordRestoreMark(Id, Reopen(director: DirectorB, reopenedAs: "new-session-3"), Now));
        Assert.Contains("had its saved conversation reopened", byAnother.Message);

        Assert.Equal("new-session-1", SeatOf(store.Get(Id)!, Ended).Restore!.ReopenedSessionId);
    }

    /// <summary>
    /// A DIRECTOR DOES NOT REOPEN A SEAT UNDERNEATH ANOTHER DIRECTOR'S RUNNING RESTORE. It is the one thing a
    /// reopen must not cut across, so it is the one lease check it keeps - and the SAME Director's own lease is
    /// no obstacle to it.
    /// </summary>
    [Fact]
    public void AReopenMark_WhileAnotherDirectorIsRestoring_IsRefused()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorB, null, Now);

        var refused = Assert.Throws<WorkspaceConflictException>(
            () => store.RecordRestoreMark(Id, Reopen(director: DirectorA), Now));
        Assert.Contains("is restoring workspace", refused.Message);
        Assert.Null(SeatOf(store.Get(Id)!, Ended).Restore!.ReopenedAtUtc);

        // Its own lease is not an obstacle, and the reopen does not release it.
        var saved = store.RecordRestoreMark(Id, Reopen(director: DirectorB), Now);
        Assert.NotNull(SeatOf(saved, Ended).Restore!.ReopenedAtUtc);
        Assert.Equal(DirectorB, saved.RestoreLease!.DirectorId);
    }

    /// <summary>
    /// AN ORDINARY WRITE CAN NEITHER FORGE THE CLAIM NOR ERASE IT. Both halves matter and they fail in
    /// opposite directions: a writer who could set it would make a session vanish from the way up's offer,
    /// and a writer who could clear it would put a dealt-with session back into it - which is the defect
    /// this mark was added to end.
    /// </summary>
    [Fact]
    public void AnOrdinaryWrite_CanNeitherForgeAReopenNorEraseOne()
    {
        var store = Captured();

        // Forging one: a caller marks the LEAD as reopened, which no restore ever did.
        var forging = store.Get(Id)!;
        SeatOf(forging, Lead).Restore!.ReopenedAtUtc = Now;
        SeatOf(forging, Lead).Restore!.ReopenedSessionId = "a-session-nobody-started";
        SeatOf(forging, Lead).Restore!.ReopenedByDirectorId = "somebody-else";
        store.Save(forging, Now);

        var afterForging = SeatOf(store.Get(Id)!, Lead);
        Assert.Null(afterForging.Restore!.ReopenedAtUtc);
        Assert.Null(afterForging.Restore.ReopenedSessionId);
        Assert.Null(afterForging.Restore.ReopenedByDirectorId);

        // Erasing one: the reopen is recorded, and a caller writes the seat back with it cleared.
        store.RecordRestoreMark(Id, Reopen(reopenedAs: "new-session-1"), Now);
        var erasing = store.Get(Id)!;
        SeatOf(erasing, Ended).Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided };
        store.Save(erasing, Now.AddMinutes(1));

        var afterErasing = SeatOf(store.Get(Id)!, Ended);
        Assert.Equal(Now, afterErasing.Restore!.ReopenedAtUtc);
        Assert.Equal("new-session-1", afterErasing.Restore.ReopenedSessionId);
        Assert.Equal(DirectorA, afterErasing.Restore.ReopenedByDirectorId);
    }

    /// <summary>
    /// THE CLOSED LIST IS WHAT AN OLDER GATEWAY TRIPS ON, and this is the shape of that refusal. A mark name
    /// this build does not know is refused with the names it does know - so the way up's own refusal, which
    /// carries the Gateway's words through to the person, reads as a Gateway that is behind rather than as a
    /// mystery. This build's list now names the reopen; a Gateway that has not been deployed does not.
    /// </summary>
    [Fact]
    public void AMarkKindThisBuildDoesNotKnow_IsRefusedWithTheKindsItDoes()
    {
        var store = Captured();

        var refused = Assert.Throws<WorkspaceValidationException>(
            () => store.RecordRestoreMark(
                Id,
                new WorkspaceRestoreMark { DirectorId = DirectorA, Kind = "dealt-with", SeatSessionId = Ended },
                Now));

        Assert.Contains("kind must be one of:", refused.Message);
        Assert.Contains(WorkspaceRestoreMarkKinds.Reopened, refused.Message);
    }
}
