using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Three rules a third review round found missing, each of them an unnamed state falling somewhere
/// permissive (issue #2722).
///
///  - THE SEAT COUNTS HAD NO DENOMINATOR. They were bounded by the size of the fleet, so "four seats were
///    owed, one came back, none is missing" was a valid document whose scope derived to "all". The
///    unnamed state was "some owed seats are not accounted for", and it fell into success.
///  - AN AUTHORED WORKSPACE COULD CLAIM A RUN. Nothing stopped a hand-written list of seats saying a
///    Director had been restarted, which made origin a label rather than an invariant.
///  - A CAPTURED SEAT COULD BE ANONYMOUS. The capture path accepted a seat with no session id, and
///    because the seat rule matches stored seats to incoming ones by id, that record could then never be
///    written to again by anybody.
/// </summary>
public sealed class WorkspaceOwedSeatsAndOriginRulesTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private WorkspaceStore NewStore() => new(_h.Open());

    private static WorkspaceSeat Seat(string id, string name, bool owed)
        => new()
        {
            SessionId = id,
            Name = name,
            Agent = "ClaudeCode",
            RepoPath = @"D:\ReposFred\devthrottle_internal",
            Restore = owed
                ? new WorkspaceSeatRestore
                {
                    Decision = WorkspaceRestoreDecisions.Restore,
                    Why = "real continuing work",
                    Command = "cc-devthrottle session spawn ...",
                }
                : new WorkspaceSeatRestore
                {
                    Decision = WorkspaceRestoreDecisions.Close,
                    Why = "its work is finished",
                },
        };

    /// <summary>A captured workspace with <paramref name="owed"/> seats decided restore, and one closed.</summary>
    private static WorkspaceDocument Captured(int owed)
    {
        var doc = new WorkspaceDocument
        {
            Id = "director-restart",
            Name = "DevThrottle_1 restart",
            Origin = WorkspaceOrigins.Captured,
            Machine = "SOREN_NORTH",
            DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        };

        for (var i = 0; i < owed; i++)
            doc.Seats.Add(Seat($"1111111{i}-1111-1111-1111-111111111111", $"owed {i}", owed: true));
        doc.Seats.Add(Seat("22222222-2222-2222-2222-222222222222", "closed", owed: false));

        foreach (var seat in doc.Seats.Where(x => x.Restore!.Decision == WorkspaceRestoreDecisions.Restore))
            doc.RestoreAfterRestart.Add(seat.SessionId!);

        return doc;
    }

    // ---- The counts account for exactly the seats that were owed ------------------------------------

    [Fact]
    public void An_unaccounted_owed_seat_cannot_be_recorded_as_everything_came_back()
    {
        var doc = Captured(owed: 4);
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 1 };

        // Four owed, one restored, nothing said to be missing. The scope derives to "all" - a run that
        // brought back a quarter of the fleet certifying itself as complete.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Create(doc, Now));
        Assert.Contains("accounts for 1", ex.Message);
        Assert.Contains("4 seat(s)", ex.Message);
    }

    [Fact]
    public void Restoring_more_than_was_owed_is_refused_too()
    {
        var doc = Captured(owed: 1);
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 4 };

        Assert.Contains("accounts for 4", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Create(doc, Now)).Message);
    }

    [Fact]
    public void Claiming_seats_came_back_when_none_was_owed_is_refused()
    {
        var doc = Captured(owed: 0);
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 4 };

        Assert.Contains("0 seat(s)", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Create(doc, Now)).Message);
    }

    [Fact]
    public void Every_owed_seat_accounted_for_is_accepted_and_derives_the_right_scope()
    {
        var store = NewStore();

        var all = Captured(owed: 3);
        all.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        all.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 3 };
        store.Create(all, Now);
        Assert.Equal(WorkspaceSeatOutcomes.All_, store.Get("director-restart")!.SeatOutcome!.Scope);

        var some = Captured(owed: 3);
        some.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        some.SeatOutcome = new WorkspaceSeatOutcome
        {
            RestoredCount = 2,
            NotRestoredCount = 1,
            NotRestoredWhy = "its work turned out to be finished",
        };
        store.Save(some, Now);
        Assert.Equal(WorkspaceSeatOutcomes.Some, store.Get("director-restart")!.SeatOutcome!.Scope);

        var none = Captured(owed: 3);
        none.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        none.SeatOutcome = new WorkspaceSeatOutcome
        {
            NotRestoredCount = 3,
            NotRestoredWhy = "the restore was never run",
        };
        store.Save(none, Now);
        Assert.Equal(WorkspaceSeatOutcomes.None, store.Get("director-restart")!.SeatOutcome!.Scope);
    }

    [Fact]
    public void Nothing_owed_and_nothing_accounted_for_is_the_fourth_answer()
    {
        var doc = Captured(owed: 0);
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome();

        NewStore().Create(doc, Now);

        // "Nothing was owed" is a different answer from "everything owed is missing", and this is the
        // case that would read as success if they shared a value.
        Assert.Equal(WorkspaceSeatOutcomes.NothingToRestore,
            NewStore().Get("director-restart")!.SeatOutcome!.Scope);
    }

    // ---- An authored workspace is not the record of a run --------------------------------------------

    [Fact]
    public void An_authored_workspace_cannot_claim_a_Director_was_restarted()
    {
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
            DirectorOutcome = WorkspaceDirectorOutcomes.Restarted,
        };

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("not the record of a run", ex.Message);
    }

    [Fact]
    public void An_authored_workspace_cannot_claim_seats_came_back()
    {
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
            SeatOutcome = new WorkspaceSeatOutcome(),
        };

        Assert.Contains("not the record of a run", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Save(doc, Now)).Message);
    }

    // ---- A captured seat names its session ----------------------------------------------------------

    [Fact]
    public void The_capture_path_refuses_a_seat_that_names_no_session()
    {
        var doc = Captured(owed: 0);
        doc.Seats.Add(new WorkspaceSeat
        {
            SessionId = null,
            Name = "an anonymous seat",
            Agent = "ClaudeCode",
            RepoPath = @"D:\repo",
        });

        // Stored, this seat could never be written to again: the seat rule matches by id, so a seat with
        // none is unmatchable for ever and every later write to the workspace would be refused.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Create(doc, Now));
        Assert.Contains("names no session", ex.Message);
    }

    [Fact]
    public void An_authored_seat_may_still_name_no_session_because_it_never_was_one()
    {
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
        };

        NewStore().Save(doc, Now);
        Assert.Null(NewStore().Get("morning-fleet")!.Seats[0].SessionId);
    }
}
