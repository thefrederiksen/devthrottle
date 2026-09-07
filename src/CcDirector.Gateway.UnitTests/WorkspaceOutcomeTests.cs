using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// What happened to the DIRECTOR and what happened to the SEATS are two independent facts, and there is
/// no third field summarising them (issue #2722).
///
/// The defect this shape exists to end: the hand-written index had one coarse word, and "restored" meant
/// "the Director was restarted AND the seats came back" - two answers welded into one token - so the
/// first combination nobody happened to weld had no word at all. That combination is not exotic. A drain
/// that BLOCKS has already closed, leaf-first, every seat that handed over cleanly, so the expected
/// result of never forcing is a half-gone fleet with no restart, whose seats must then be brought back
/// WITHOUT one.
///
/// Three fixes were tried before this one, and the first two are worth knowing because each looked
/// finished: a fifth word hid the same defect one case further out, and DERIVING the word from the pair
/// stopped it contradicting them but still welded them - a refused restart and a session that would not
/// stop both derived "blocked", which are the two most confusable results a run can have. So the word is
/// gone. These tests hold what replaced it: every combination expressible, and no summary to be read
/// instead of the facts.
/// </summary>
public sealed class WorkspaceOutcomeTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private WorkspaceStore NewStore() => new(_h.Open());

    /// <summary>
    /// Store a captured workspace the way it really happens: CREATED by the capture path, then updated
    /// by ordinary writes. An ordinary write cannot mint a captured workspace - that is the provenance
    /// rule - so a test that used Save for both would be testing a sequence that cannot occur.
    /// </summary>
    private void Store(WorkspaceDocument doc)
    {
        var store = NewStore();
        if (store.Get(doc.Id) is null) store.Create(doc, Now);
        else store.Save(doc, Now);
    }

    private static WorkspaceDocument Captured()
        => new()
        {
            Id = "director-restart",
            Name = "DevThrottle_1 restart",
            Origin = WorkspaceOrigins.Captured,
            Machine = "SOREN_NORTH",
            DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
            // FOUR seats, because the seat outcome counts are bounded by the size of the fleet they
            // describe. A one-seat fixture would have made "four restored" a valid document.
            Seats =
            {
                new WorkspaceSeat
                {
                    SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c",
                    Name = "Linux Support - Architect",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                },
                new WorkspaceSeat
                {
                    SessionId = "e777d59f-33d3-4732-8ec8-764e368db408",
                    Name = "Linux Support - Manager",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                    SortOrder = 1,
                },
                new WorkspaceSeat
                {
                    SessionId = "8f894218-1b2c-4d3e-9f01-2a3b4c5d6e7f",
                    Name = "Linux Support - VM Worker",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle",
                    SortOrder = 2,
                },
                new WorkspaceSeat
                {
                    SessionId = "0da65999-7795-4abf-bd64-94be7a7438b1",
                    Name = "Cube R-D Worker D",
                    Agent = "ClaudeCode",
                    RepoPath = "D:/ReposMindzie/worktrees/ns-cube-rd",
                    SortOrder = 3,
                },
            },
        };

    // ---- The combination that had no word ------------------------------------------------------------

    [Fact]
    public void A_blocked_drain_whose_closed_seats_came_back_with_no_restart_is_fully_expressible()
    {
        var doc = Captured();
        doc.Seats[0].DrainState = WorkspaceDrainStates.Blocked;
        doc.Seats[0].BlockedReason = "mid-merge, cannot reach a clean stop";
        doc.DirectorOutcome = WorkspaceDirectorOutcomes.NotRestarted;
        doc.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 4 };

        Store(doc);
        var got = NewStore().Get("director-restart")!;

        // The two facts are both there, and they are separate.
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, got.DirectorOutcome);
        Assert.Equal(WorkspaceSeatOutcomes.All_, got.SeatOutcome!.Scope);
        Assert.Equal(4, got.SeatOutcome.RestoredCount);

        // And the seat that would not stop is still recorded as what it was, in its own words, rather
        // than collapsed into a single word for the run.
        Assert.Equal(WorkspaceDrainStates.Blocked, got.Seats[0].DrainState);
        Assert.Equal("mid-merge, cannot reach a clean stop", got.Seats[0].BlockedReason);
    }

    [Theory]
    // The Director came back and everything owed came back with it: what used to be called "restored".
    [InlineData(WorkspaceDirectorOutcomes.Restarted, WorkspaceSeatOutcomes.All_)]
    // The Director came back and nothing has been restored yet: what used to be called "restarted".
    [InlineData(WorkspaceDirectorOutcomes.Restarted, WorkspaceSeatOutcomes.None)]
    // The Director came back and only SOME seats did - a combination the four words never had.
    [InlineData(WorkspaceDirectorOutcomes.Restarted, WorkspaceSeatOutcomes.Some)]
    // Nothing was owed and the Director came back.
    [InlineData(WorkspaceDirectorOutcomes.Restarted, WorkspaceSeatOutcomes.NothingToRestore)]
    // The restart was REFUSED and the seats came back anyway - two facts the old word could not separate
    // from a session that would not stop, because both were "blocked".
    [InlineData(WorkspaceDirectorOutcomes.RestartRefused, WorkspaceSeatOutcomes.All_)]
    [InlineData(WorkspaceDirectorOutcomes.RestartRefused, WorkspaceSeatOutcomes.NothingToRestore)]
    // No restart at all, and the seats brought back: the combination that started this.
    [InlineData(WorkspaceDirectorOutcomes.NotRestarted, WorkspaceSeatOutcomes.All_)]
    [InlineData(WorkspaceDirectorOutcomes.NotRestarted, WorkspaceSeatOutcomes.Some)]
    [InlineData(WorkspaceDirectorOutcomes.NotRestarted, WorkspaceSeatOutcomes.None)]
    [InlineData(WorkspaceDirectorOutcomes.NotRestarted, WorkspaceSeatOutcomes.NothingToRestore)]
    public void Every_combination_of_the_two_facts_is_expressible_and_survives_a_round_trip(
        string director, string scope)
    {
        var doc = Captured();
        doc.DirectorOutcome = director;
        doc.SeatOutcome = scope switch
        {
            WorkspaceSeatOutcomes.All_ => new WorkspaceSeatOutcome { RestoredCount = 2 },
            WorkspaceSeatOutcomes.None => new WorkspaceSeatOutcome
                { NotRestoredCount = 2, NotRestoredWhy = "the restore has not been run" },
            WorkspaceSeatOutcomes.Some => new WorkspaceSeatOutcome
                { RestoredCount = 1, NotRestoredCount = 1, NotRestoredWhy = "its work is finished" },
            _ => new WorkspaceSeatOutcome(),
        };
        Assert.Equal(scope, doc.SeatOutcome.Scope);

        Store(doc);

        var got = NewStore().Get("director-restart")!;
        Assert.Equal(director, got.DirectorOutcome);
        Assert.Equal(scope, got.SeatOutcome!.Scope);
    }

    [Fact]
    public void An_authored_workspace_answers_neither_question_because_it_is_not_the_record_of_a_run()
    {
        var doc = Captured();
        doc.Origin = WorkspaceOrigins.Authored;
        doc.DirectorId = null;
        doc.Machine = null;

        Assert.Null(doc.DirectorOutcome);
        Assert.Null(doc.SeatOutcome);
    }

    [Fact]
    public void A_document_carrying_the_retired_outcome_word_keeps_it_verbatim_and_is_not_ruled_by_it()
    {
        // The hand-written index has this field, and so does anything written before it was retired.
        // It is not modelled, so it lands in Unknown and is handed back untouched - not obeyed, and not
        // lost either.
        const string json = """
            {
              "schemaVersion": 1,
              "id": "director-restart",
              "name": "DevThrottle_1 restart",
              "origin": "captured",
              "directorId": "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
              "outcome": "restored",
              "directorOutcome": "not-restarted",
              "seats": []
            }
            """;

        var doc = System.Text.Json.JsonSerializer.Deserialize<WorkspaceDocument>(
            json, WorkspaceStore.DocumentJsonOptions)!;

        // The word in the file said "restored"; the facts say no restart happened. The facts are what
        // the document means, and the word is just carried.
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, doc.DirectorOutcome);
        Assert.Equal("restored", doc.Unknown!["outcome"].GetString());

        Store(doc);
        var got = NewStore().Get("director-restart")!;
        Assert.Equal(WorkspaceDirectorOutcomes.NotRestarted, got.DirectorOutcome);
        Assert.Equal("restored", got.Unknown!["outcome"].GetString());
    }

    // ---- The seat outcome cannot say two different things ------------------------------------------

    [Fact]
    public void The_scope_is_the_counts_and_cannot_disagree_with_them()
    {
        // Not "the scope must match the counts" - there is no scope to mismatch. It is a view of the two
        // numbers, so a caller insisting otherwise changes nothing.
        var doc = Captured();
        doc.SeatOutcome = new WorkspaceSeatOutcome
        {
            Scope = WorkspaceSeatOutcomes.All_,
            RestoredCount = 1,
            NotRestoredCount = 1,
            NotRestoredWhy = "its work is finished",
        };

        Assert.Equal(WorkspaceSeatOutcomes.Some, doc.SeatOutcome.Scope);

        Store(doc);
        Assert.Equal(WorkspaceSeatOutcomes.Some, NewStore().Get("director-restart")!.SeatOutcome!.Scope);
    }

    [Fact]
    public void A_seat_that_did_not_come_back_must_say_why()
    {
        var doc = Captured();
        doc.SeatOutcome = new WorkspaceSeatOutcome
        {
            RestoredCount = 3,
            NotRestoredCount = 1,
        };

        // A missing seat with no reason beside it is indistinguishable from one nobody noticed.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Create(doc, Now));
        Assert.Contains("notRestoredWhy", ex.Message);
    }

    [Fact]
    public void Nothing_owed_and_nothing_missing_are_different_answers()
    {
        var store = NewStore();

        var nothingOwed = Captured();
        nothingOwed.SeatOutcome = new WorkspaceSeatOutcome();
        Store(nothingOwed);
        Assert.Equal(WorkspaceSeatOutcomes.NothingToRestore,
            store.Get("director-restart")!.SeatOutcome!.Scope);

        var everythingMissing = Captured();
        everythingMissing.SeatOutcome = new WorkspaceSeatOutcome
        {
            NotRestoredCount = 4,
            NotRestoredWhy = "the restore was never run",
        };
        Store(everythingMissing);
        var got = store.Get("director-restart")!;

        // If these two shared a value, the record could not tell "nobody was owed anything" from "four
        // seats are gone and nobody came back for them".
        Assert.Equal(WorkspaceSeatOutcomes.None, got.SeatOutcome!.Scope);
        Assert.Equal(4, got.SeatOutcome.NotRestoredCount);
    }

    [Fact]
    public void An_unknown_director_outcome_is_refused()
    {
        var badDirector = Captured();
        badDirector.DirectorOutcome = "half-restarted";
        Assert.Contains("directorOutcome", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Create(badDirector, Now)).Message);

        // There is no matching case for the seat scope: it is derived from the counts, so an unknown
        // value cannot exist to be refused.
        var seat = new WorkspaceSeatOutcome { Scope = "most" };
        Assert.Equal(WorkspaceSeatOutcomes.NothingToRestore, seat.Scope);
    }
}
