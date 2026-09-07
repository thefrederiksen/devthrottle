using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Two things the workspace store has to refuse that are not about the SHAPE of a document (issue #2722).
///
/// FORGERY. The capture verb exists because the facts on a seat are ones the Gateway holds firsthand, and
/// the document is read after the sessions are gone, when nobody can check. That reason is worth nothing
/// if an ordinary write can also mint a record of a fleet that never ran, or move one that did onto
/// another machine. So the capture header - where a workspace came from - is written by the capture and
/// by nothing else, and these tests are how that is held.
///
/// MALFORMED BODIES. Every one of these used to reach the code as a NullReferenceException, which the
/// endpoint turned into a bare 500. A caller who sent something wrong has to be told what, and a null
/// seat list in particular must never be read as "an empty fleet" - that is a record saying nothing was
/// running on a machine, which is the most dangerous sentence this document can contain.
/// </summary>
public sealed class WorkspaceProvenanceAndMalformedBodyTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 6, 22, 2, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private WorkspaceStore NewStore() => new(_h.Open());

    private static WorkspaceDocument Authored(string id = "morning-fleet", string name = "Morning fleet")
        => new()
        {
            Id = id,
            Name = name,
            Origin = WorkspaceOrigins.Authored,
            Seats =
            {
                new WorkspaceSeat
                {
                    Name = "Linux Support - Architect",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                    Role = "Manager",
                    SortOrder = 0,
                },
            },
        };

    // ---- Forgery -----------------------------------------------------------------------------------

    [Fact]
    public void An_ordinary_write_cannot_mint_a_captured_workspace()
    {
        var doc = Authored();
        doc.Origin = WorkspaceOrigins.Captured;
        doc.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        doc.Machine = "A MACHINE THAT NEVER RAN THIS";

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("POST /gateway/workspaces", ex.Message);
    }

    [Fact]
    public void The_origin_alone_is_enough_to_refuse_a_minted_capture()
    {
        // NOTHING but the origin. The case above carries a machine and a Director as well, so it is
        // caught by the header rule too - which meant deleting the origin check left it green. Caught by
        // mutation, and this is the case that can only be refused by the check it is named for.
        var doc = Authored();
        doc.Origin = WorkspaceOrigins.Captured;

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("captured workspace can only be created by capturing", ex.Message);
    }

    [Fact]
    public void An_ordinary_write_cannot_rewrite_where_a_captured_workspace_came_from()
    {
        var store = NewStore();

        var captured = Authored("director-restart", "DevThrottle_1 restart");
        captured.Origin = WorkspaceOrigins.Captured;
        captured.Machine = "SOREN_NORTH";
        captured.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        captured.DirectorName = "DevThrottle_1";
        captured.DirectorVersionBefore = "2.0.5";
        captured.StartedAtUtc = Now;
        // A captured seat names its session - that is what a capture reads - and the seat rule matches
        // the incoming seats against the stored ones by that id.
        captured.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        captured.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "head of the mission",
            Command = "cc-devthrottle session spawn ...",
        };
        captured.RestoreAfterRestart.Add("5ff9ab8b-07d3-4b23-953b-6c853760b56c");
        store.Create(captured, Now);

        // The drain writes its judgments back through the ordinary path - and tries to move the record
        // to another machine at the same time.
        var edited = Authored("director-restart", "DevThrottle_1 restart");
        edited.Origin = WorkspaceOrigins.Authored;
        edited.Machine = "SOMEONE_ELSES_MACHINE";
        edited.DirectorId = "00000000-0000-0000-0000-000000000000";
        edited.DirectorName = "not the Director that ran";
        edited.DirectorVersionBefore = "9.9.9";
        edited.StartedAtUtc = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        edited.DirectorOutcome = WorkspaceDirectorOutcomes.Restarted;
        edited.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 1 };
        edited.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        edited.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "head of the mission",
            Command = "cc-devthrottle session spawn ...",
        };
        edited.RestoreAfterRestart.Add("5ff9ab8b-07d3-4b23-953b-6c853760b56c");
        edited.Seats[0].DrainState = WorkspaceDrainStates.Drained;
        // The one seat the outcome says came back names the session it came back as - a count on its
        // own is not evidence that anything was restored.
        edited.Seats[0].RestoredSessionId = "9a1b0c2d-0000-4000-8000-000000000000";
        store.Save(edited, Later);

        var got = store.Get("director-restart")!;

        // The judgments landed...
        Assert.Equal(WorkspaceDrainStates.Drained, got.Seats[0].DrainState);
        Assert.Equal(WorkspaceDirectorOutcomes.Restarted, got.DirectorOutcome);
        Assert.Equal(WorkspaceSeatOutcomes.All_, got.SeatOutcome!.Scope);

        // ...and where it came from did not move.
        Assert.Equal(WorkspaceOrigins.Captured, got.Origin);
        Assert.Equal("SOREN_NORTH", got.Machine);
        Assert.Equal("6d4523e2-ed03-4ae6-ac1c-71d00a37bad1", got.DirectorId);
        Assert.Equal("DevThrottle_1", got.DirectorName);
        Assert.Equal("2.0.5", got.DirectorVersionBefore);
        Assert.Equal(Now, got.StartedAtUtc);
    }

    [Fact]
    public void The_capture_verb_writes_the_header_it_was_given()
    {
        // The other side of the same rule: Create IS the trusted path, so what it says about where the
        // workspace came from is what is stored. A test that only proved the refusal could pass with the
        // capture broken too.
        var captured = Authored("director-restart", "DevThrottle_1 restart");
        captured.Origin = WorkspaceOrigins.Captured;
        captured.Machine = "SOREN_NORTH";
        captured.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        captured.DirectorVersionBefore = "2.0.5";
        // A captured seat names its session - the capture path refuses one that does not.
        captured.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";

        NewStore().Create(captured, Now);

        var got = NewStore().Get("director-restart")!;
        Assert.Equal(WorkspaceOrigins.Captured, got.Origin);
        Assert.Equal("SOREN_NORTH", got.Machine);
        Assert.Equal("2.0.5", got.DirectorVersionBefore);
    }

    // ---- Malformed bodies --------------------------------------------------------------------------

    [Fact]
    public void A_null_seat_list_is_refused_rather_than_read_as_an_empty_fleet()
    {
        var doc = Authored();
        doc.Seats = null!;

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("seats", ex.Message);
    }

    [Fact]
    public void Null_lists_and_null_entries_are_refused_as_bad_requests_not_as_crashes()
    {
        var store = NewStore();

        var nullQuestions = Authored();
        nullQuestions.OwnerQuestions = null!;
        Assert.Throws<WorkspaceValidationException>(() => store.Save(nullQuestions, Now));

        var nullQuestion = Authored();
        nullQuestion.OwnerQuestions.Add(null!);
        Assert.Throws<WorkspaceValidationException>(() => store.Save(nullQuestion, Now));

        var nullRestoreList = Authored();
        nullRestoreList.RestoreAfterRestart = null!;
        Assert.Throws<WorkspaceValidationException>(() => store.Save(nullRestoreList, Now));

        var nullSeat = Authored();
        nullSeat.Seats.Add(null!);
        Assert.Throws<WorkspaceValidationException>(() => store.Save(nullSeat, Now));
    }

    [Fact]
    public void An_agent_this_Gateway_cannot_run_is_refused_at_the_write()
    {
        var doc = Authored();
        doc.Seats[0].Agent = "Cursor9000";

        // Refusing here is what lets the desktop refuse to guess later: a stored workspace can never
        // carry an agent no Director could start.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("Cursor9000", ex.Message);
        Assert.Contains("ClaudeCode", ex.Message);
    }

    [Fact]
    public void Every_agent_the_product_can_run_is_accepted()
    {
        var store = NewStore();
        foreach (var agent in Enum.GetNames<Core.Agents.AgentKind>())
        {
            var doc = Authored();
            doc.Seats[0].Agent = agent;
            store.Save(doc, Now);
            Assert.Equal(agent, store.Get("morning-fleet")!.Seats[0].Agent);
        }
    }

    [Fact]
    public void A_nested_field_is_capped_like_a_top_level_one()
    {
        // The caps are not decoration: an authenticated key that cannot put a megabyte in "name" must
        // not be able to put one in "restartBlocked.cause" instead.
        // On a CAPTURED workspace, because an authored one is not the record of a run and refuses a
        // restartBlocked block before any cap is reached.
        var doc = Authored("director-restart");
        doc.Origin = WorkspaceOrigins.Captured;
        doc.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        doc.RestartBlocked = new WorkspaceRestartBlocked { Cause = new string('x', 4001) };
        Assert.Contains("restartBlocked.cause", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Create(doc, Now)).Message);

        var seatDoc = Authored();
        seatDoc.Seats[0].ModelDisplay = new ModelDisplay { Tooltip = new string('x', 4001) };
        Assert.Contains("modelDisplay.tooltip", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Save(seatDoc, Now)).Message);
    }

    // ---- Version skew --------------------------------------------------------------------------------

    [Fact]
    public void A_schema_version_below_one_is_refused_and_a_higher_one_is_kept()
    {
        var store = NewStore();

        var tooLow = Authored();
        tooLow.SchemaVersion = 0;
        Assert.Throws<WorkspaceValidationException>(() => store.Save(tooLow, Now));

        // A HIGHER version is accepted on purpose: this build must be able to hold, and hand back, a
        // document written by a newer one.
        var future = Authored();
        future.SchemaVersion = 7;
        store.Save(future, Now);
        Assert.Equal(7, store.Get("morning-fleet")!.SchemaVersion);
    }

    [Fact]
    public void Fields_this_build_does_not_know_survive_a_read_and_a_write()
    {
        // The whole reason the document is stored as a record rather than as columns. An older Gateway
        // reading a workspace written by a newer one must hand the unknown parts back untouched - this
        // document grew three whole blocks during one real run.
        var store = NewStore();
        var doc = Authored();
        doc.Unknown = new Dictionary<string, JsonElement>
        {
            ["somethingTheNextPhaseAdded"] = JsonDocument.Parse("\"the launcher key\"").RootElement.Clone(),
        };
        doc.Seats[0].Unknown = new Dictionary<string, JsonElement>
        {
            ["seatFieldFromTheFuture"] = JsonDocument.Parse("42").RootElement.Clone(),
        };
        store.Save(doc, Now);

        var got = store.Get("morning-fleet")!;
        Assert.Equal("the launcher key", got.Unknown!["somethingTheNextPhaseAdded"].GetString());
        Assert.Equal(42, got.Seats[0].Unknown!["seatFieldFromTheFuture"].GetInt32());

        // And they survive being written AGAIN by a build that still does not know them.
        store.Save(got, Later);
        var again = store.Get("morning-fleet")!;
        Assert.Equal("the launcher key", again.Unknown!["somethingTheNextPhaseAdded"].GetString());
        Assert.Equal(42, again.Seats[0].Unknown!["seatFieldFromTheFuture"].GetInt32());
    }
}
