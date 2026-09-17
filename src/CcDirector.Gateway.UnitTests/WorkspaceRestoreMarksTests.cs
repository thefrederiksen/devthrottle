using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// What a restore did to a workspace is PROVENANCE, and one Director restores a workspace at a time (the Message
/// Load mission, inspection 7, rulings 1, 3 and 4).
///
/// Inspection 7 found that a session key could PUT a boss seat's <c>restoredSessionId</c> to any live session, and
/// the Director would then start that boss's workers owned by it. These tests pin the store half of the fix: an
/// ordinary write keeps every stored restore mark, only the lease holder writes marks, a second Director is refused
/// the lease by name, and the spawn door's token record lands only on the seat whose token it carries.
/// </summary>
public sealed class WorkspaceRestoreMarksTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private const string Id = "drain-marks";
    private const string Boss = "b0000000-0000-0000-0000-000000000000";
    private const string Worker = "w0000000-0000-0000-0000-000000000000";
    private const string X = "99999999-9999-9999-9999-999999999999";
    private const string DirectorA = "director-a";
    private const string DirectorB = "director-b";

    public void Dispose() => _h.Dispose();

    private WorkspaceStore Captured()
    {
        var store = new WorkspaceStore(_h.Open());
        store.Create(new WorkspaceDocument
        {
            Id = Id,
            Name = "Drain",
            Origin = WorkspaceOrigins.Captured,
            Machine = "MAC",
            DirectorId = "old-director",
            Seats = new()
            {
                Seat(Boss, "Boss", null),
                Seat(Worker, "Worker", Boss),
            },
            RestoreAfterRestart = new() { Boss, Worker },
        }, Now);
        return store;
    }

    private static WorkspaceSeat Seat(string id, string name, string? reportsTo) => new()
    {
        SessionId = id,
        Name = name,
        Agent = "ClaudeCode",
        RepoPath = "/repos/devthrottle",
        ReportsTo = reportsTo,
        DrainState = WorkspaceDrainStates.Drained,
        HandoverPath = $"/handovers/{name}.md",
        Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore, Why = "test", Command = "cc-devthrottle director restore ..." },
    };

    private static WorkspaceRestoreMark Mark(string kind, string? seat = Boss, string director = DirectorA) => new()
    {
        DirectorId = director, Kind = kind, SeatSessionId = seat, Token = "token-1", RestoredSessionId = "restored-1", Failure = "it failed",
    };

    private static WorkspaceSeat SeatOf(WorkspaceDocument doc, string id) => doc.Seats.Single(s => s.SessionId == id);

    // ================= ruling 1: an ordinary write keeps every restore mark =================

    [Fact]
    public void Save_APutThatSetsRestoredSessionId_IsIgnored_ButTheDecisionAndHandoverAreKept()
    {
        var store = Captured();
        var copy = store.Get(Id)!;
        var boss = SeatOf(copy, Boss);
        boss.RestoredSessionId = X;
        boss.RestoredSeedFile = "/evil.md";
        boss.HandoverPath = "/handovers/new-boss.md";
        boss.Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Close, Why = "changed my mind",
            Failure = "made up", AttemptedAtUtc = Now, StartedToken = "forged", StartedAtUtc = Now, StartedByDirectorId = DirectorB,
        };
        copy.RestoreAfterRestart = new() { Worker };
        copy.RestoredBy = new WorkspaceRestoredBy { SessionId = X };
        copy.RestoreLease = new WorkspaceRestoreLease { DirectorId = DirectorB, GrantedAtUtc = Now, RenewedAtUtc = Now };

        store.Save(copy, Now);

        var got = store.Get(Id)!;
        var stored = SeatOf(got, Boss);
        Assert.Null(stored.RestoredSessionId);
        Assert.Null(stored.RestoredSeedFile);
        Assert.Null(stored.Restore!.Failure);
        Assert.Null(stored.Restore.AttemptedAtUtc);
        Assert.Null(stored.Restore.StartedToken);
        Assert.Null(stored.Restore.StartedAtUtc);
        Assert.Null(stored.Restore.StartedByDirectorId);
        Assert.Null(got.RestoredBy);
        Assert.Null(got.RestoreLease);
        // The judgments a caller does own landed.
        Assert.Equal(WorkspaceRestoreDecisions.Close, stored.Restore.Decision);
        Assert.Equal("/handovers/new-boss.md", stored.HandoverPath);
    }

    [Fact]
    public void Save_APutThatClearsOrDropsTheMarks_KeepsWhatTheRestoreWrote()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored), Now);

        var copy = store.Get(Id)!;
        SeatOf(copy, Boss).RestoredSessionId = null;
        SeatOf(copy, Boss).Restore = null;
        copy.RestoreAfterRestart = new() { Worker };
        copy.RestoreLease = null;
        store.Save(copy, Now);

        var got = store.Get(Id)!;
        Assert.Equal("restored-1", SeatOf(got, Boss).RestoredSessionId);
        Assert.Equal("token-1", SeatOf(got, Boss).Restore!.StartedToken);
        Assert.Equal(DirectorA, got.RestoreLease!.DirectorId);
    }

    [Fact]
    public void RecordRestoreMark_WithoutTheLease_OrFromAnotherDirector_OrAfterItLapsed_IsRefused()
    {
        var store = Captured();
        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored), Now));

        store.TakeRestoreLease(Id, DirectorA, null, Now);
        var other = Assert.Throws<WorkspaceConflictException>(
            () => store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored, director: DirectorB), Now));
        Assert.Contains(DirectorA, other.Message);

        var lapsed = Now + WorkspaceRestoreLease.Expiry;
        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored), lapsed));
        Assert.Null(SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
    }

    [Fact]
    public void RecordRestoreMark_AFailureAfterTheSeatWasRecordedRestored_DoesNotEraseIt()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);
        Assert.True(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Boss, Token = "token-1" }, DirectorA, "landed", Now));

        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Failed), Now);

        var seat = SeatOf(store.Get(Id)!, Boss);
        Assert.Equal("landed", seat.RestoredSessionId);
        Assert.Null(seat.Restore!.Failure);
    }

    [Fact]
    public void RecordRestoreMark_ARestoredSeat_IsNotStartedAgain_AndCannotBeRenamed()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored), Now);

        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now));
        var renamed = Mark(WorkspaceRestoreMarkKinds.Restored);
        renamed.RestoredSessionId = X;
        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, renamed, Now));
        Assert.Equal("restored-1", SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
    }

    [Fact]
    public void RecordRestoreMark_ARestoredMarkWithoutTheStartsToken_IsRefused()
    {
        // Inspection 11, ruling 1: "restored" completes a start this Director recorded, by its token.
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);

        var noToken = Mark(WorkspaceRestoreMarkKinds.Restored);
        noToken.Token = null;
        Assert.Throws<WorkspaceValidationException>(() => store.RecordRestoreMark(Id, noToken, Now));
        var wrong = Mark(WorkspaceRestoreMarkKinds.Restored);
        wrong.Token = "token-2";
        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, wrong, Now));
        Assert.Throws<WorkspaceConflictException>(() => store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored, seat: Worker), Now));
        Assert.Null(SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
        Assert.Null(SeatOf(store.Get(Id)!, Worker).RestoredSessionId);

        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Restored), Now);
        Assert.Equal("restored-1", SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
    }

    [Fact]
    public void RecordRestoredByClaim_ForADirectorThatDidNotStartTheSeat_RecordsNothing()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);

        Assert.False(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Boss, Token = "token-1" }, DirectorB, X, Now));
        Assert.Null(SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
    }

    [Fact]
    public void RecordRestoreMark_ARefusedCreateClearsTheToken_AMaybeKeepsIt()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Failed), Now);
        Assert.Equal("token-1", SeatOf(store.Get(Id)!, Boss).Restore!.StartedToken);

        var refused = Mark(WorkspaceRestoreMarkKinds.Failed);
        refused.NothingStarted = true;
        store.RecordRestoreMark(Id, refused, Now);
        var seat = SeatOf(store.Get(Id)!, Boss);
        Assert.Null(seat.Restore!.StartedToken);
        Assert.Equal("it failed", seat.Restore.Failure);
    }

    // ================= ruling 3: the spawn door records by token =================

    [Fact]
    public void RecordRestoredByClaim_OnlyTheMatchingTokenRecords()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now);

        Assert.False(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Boss, Token = "other" }, DirectorA, X, Now));
        Assert.False(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Worker, Token = "token-1" }, DirectorA, X, Now));
        Assert.Null(SeatOf(store.Get(Id)!, Boss).RestoredSessionId);

        Assert.True(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Boss, Token = "token-1" }, DirectorA, "new-boss", Now));
        Assert.False(store.RecordRestoredByClaim(new WorkspaceRestoreClaim { WorkspaceId = Id, SeatSessionId = Boss, Token = "token-1" }, DirectorA, "second", Now));
        Assert.Equal("new-boss", SeatOf(store.Get(Id)!, Boss).RestoredSessionId);
    }

    // ================= ruling 4: one Director per workspace =================

    [Fact]
    public void TakeRestoreLease_ASecondDirectorIsRefusedByName_UntilTheFirstFinishesOrLapses()
    {
        var store = Captured();
        Assert.True(store.TakeRestoreLease(Id, DirectorA, "asker", Now));

        var ex = Assert.Throws<WorkspaceConflictException>(() => store.TakeRestoreLease(Id, DirectorB, null, Now.AddMinutes(1)));
        Assert.Contains(DirectorA, ex.Message);
        Assert.False(store.TakeRestoreLease(Id, DirectorA, null, Now.AddMinutes(1)));

        // A mark renews it; the lapse counts from the last word.
        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Started), Now.AddMinutes(10));
        Assert.Throws<WorkspaceConflictException>(() => store.TakeRestoreLease(Id, DirectorB, null, Now.AddMinutes(20)));
        Assert.True(store.TakeRestoreLease(Id, DirectorB, null, Now.AddMinutes(10) + WorkspaceRestoreLease.Expiry));

        store.RecordRestoreMark(Id, Mark(WorkspaceRestoreMarkKinds.Finished, seat: null, director: DirectorB), Now.AddMinutes(30));
        Assert.Null(store.Get(Id)!.RestoreLease);
        Assert.True(store.TakeRestoreLease(Id, DirectorA, null, Now.AddMinutes(31)));
    }

    [Fact]
    public void ReleaseRestoreLease_ByAnotherDirector_ReleasesNothing()
    {
        var store = Captured();
        store.TakeRestoreLease(Id, DirectorA, null, Now);

        store.ReleaseRestoreLease(Id, DirectorB);

        Assert.Equal(DirectorA, store.Get(Id)!.RestoreLease!.DirectorId);
    }

    [Fact]
    public void An_authored_workspace_cannot_carry_a_lease_or_a_start()
    {
        var store = new WorkspaceStore(_h.Open());
        var doc = new WorkspaceDocument
        {
            Id = "authored",
            Name = "Mine",
            Seats = new() { new WorkspaceSeat { Name = "s", Agent = "ClaudeCode", RepoPath = "/r", Restore = new WorkspaceSeatRestore { StartedToken = "t" } } },
        };
        Assert.Contains("restore attempt", Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Now)).Message);

        doc.Seats[0].Restore = null;
        doc.RestoreLease = new WorkspaceRestoreLease { DirectorId = DirectorA };
        Assert.Contains("restoreLease", Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Now)).Message);
    }
}
