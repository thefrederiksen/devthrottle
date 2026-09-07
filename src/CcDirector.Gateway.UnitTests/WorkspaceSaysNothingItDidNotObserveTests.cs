using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A workspace says nothing it did not observe, and nothing is written over on a maybe (issue #2722).
///
/// These are the store's half of the single pass an independent review round asked for. Its lead finding
/// was about the desktop, but three of its ten were the same shape here: an AUTHORED workspace could
/// claim a restart through any field except the two that had been thought of; a write that could not
/// establish whether an id was free was allowed to overwrite it anyway; and a field the schema does not
/// model was preserved at two levels out of twelve, so the real restart index lost its only continuation
/// prompt and the correction of a wrong incident classification.
/// </summary>
public sealed class WorkspaceSaysNothingItDidNotObserveTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 6, 22, 2, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private WorkspaceStore NewStore() => new(_h.Open());

    private static WorkspaceDocument Authored()
        => new()
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
        };

    // ---- An authored workspace claims no run, through ANY field --------------------------------------

    [Theory]
    // directorVersionAfter is deliberately NOT here: an authored workspace is refused for carrying it
    // by the capture-header rule, which fires first and says so in its own words. Asserting it here
    // would be asserting on whichever rule happens to be checked first.
    [InlineData("completedAtUtc")]
    [InlineData("restartCommand")]
    [InlineData("launcherUpdate")]
    [InlineData("restartBlocked")]
    [InlineData("restartMechanism")]
    [InlineData("restartPerformed")]
    [InlineData("restoredBy")]
    [InlineData("a seat drainState")]
    [InlineData("a seat handoverPath")]
    [InlineData("a seat closedAtUtc")]
    [InlineData("a seat restoredSessionId")]
    [InlineData("a seat restore decision")]
    public void An_authored_workspace_cannot_claim_a_run_through_any_field(string field)
    {
        // Refusing only directorOutcome and seatOutcome left every other way of saying the same thing
        // open: a caller could create a clean authored workspace and then write a populated
        // restartPerformed block into it, with both outcome fields absent, and the store would keep it as
        // a record saying a Director restarted.
        var doc = Authored();
        switch (field)
        {
            case "directorVersionAfter": doc.DirectorVersionAfter = "2.0.6"; break;
            case "completedAtUtc": doc.CompletedAtUtc = Later; break;
            case "restartCommand": doc.RestartCommand = new WorkspaceRestartCommand { Method = "POST" }; break;
            case "launcherUpdate": doc.LauncherUpdate = new WorkspaceLauncherUpdate { To = "2.0.4" }; break;
            case "restartBlocked": doc.RestartBlocked = new WorkspaceRestartBlocked { Cause = "wedged" }; break;
            case "restartMechanism": doc.RestartMechanism = new WorkspaceRestartMechanism { LauncherPid = 1 }; break;
            case "restartPerformed": doc.RestartPerformed = new WorkspaceRestartPerformed { DirectorPid = 1 }; break;
            case "restoredBy": doc.RestoredBy = new WorkspaceRestoredBy { SessionId = "s" }; break;
            case "a seat drainState": doc.Seats[0].DrainState = WorkspaceDrainStates.Drained; break;
            case "a seat handoverPath": doc.Seats[0].HandoverPath = @"C:\vault\x.md"; break;
            case "a seat closedAtUtc": doc.Seats[0].ClosedAtUtc = Later; break;
            case "a seat restoredSessionId": doc.Seats[0].RestoredSessionId = "s"; break;
            case "a seat restore decision":
                doc.Seats[0].Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Close };
                break;
            default: throw new InvalidOperationException("unhandled case " + field);
        }

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("not the record of a run", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void The_rule_holds_on_an_UPDATE_and_not_only_on_a_create()
    {
        // The path that made it reachable: create clean, then PUT the claim. Creation and update take
        // different routes through the store, and only the first was guarded.
        var store = NewStore();
        store.Save(Authored(), Now);

        var edited = Authored();
        edited.RestartPerformed = new WorkspaceRestartPerformed
        {
            DirectorPid = 18692,
            DirectorVersionAfter = "2.0.6",
            VerifiedBy = "nobody, this never happened",
        };

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(edited, Later));
        Assert.Contains("restartPerformed", ex.Message);
    }

    [Fact]
    public void An_authored_workspace_with_nothing_to_claim_is_still_accepted()
    {
        // The rule refuses claims, not workspaces. A plain morning fleet still goes in.
        NewStore().Save(Authored(), Now);
        Assert.Equal("Morning fleet", NewStore().Get("morning-fleet")!.Name);
    }

    // ---- A write happens on positively established absence -------------------------------------------

    [Fact]
    public void A_create_only_write_refuses_rather_than_replacing_what_is_there()
    {
        var store = NewStore();
        store.Save(Authored(), Now);

        var second = Authored();
        second.Name = "somebody else's morning fleet";

        // "List, check the id is absent, then write" is three operations with two gaps, and anything
        // created in either gap is silently overwritten. One atomic create closes it.
        var ex = Assert.Throws<WorkspaceConflictException>(() => store.CreateAuthored(second, Later));
        Assert.Contains("already exists", ex.Message);
        Assert.Equal("Morning fleet", store.Get("morning-fleet")!.Name);
    }

    [Fact]
    public void A_create_only_write_still_stores_a_workspace_that_is_genuinely_new()
    {
        var store = NewStore();
        store.CreateAuthored(Authored(), Now);
        Assert.Equal("Morning fleet", store.Get("morning-fleet")!.Name);
    }

    // ---- What the schema does not model is kept at EVERY level ---------------------------------------

    [Fact]
    public void A_field_inside_a_nested_object_survives_a_read_and_a_write()
    {
        // The exact fields the real hand-written index carries and this build has no name for. They live
        // INSIDE typed objects, which is why preserving them at the document and seat level only was a
        // rule that looked complete and dropped all six - including the only continuation prompt in the
        // record and the later correction of a wrong incident classification.
        const string json = """
            {
              "id": "director-restart",
              "name": "DevThrottle_1 restart",
              "origin": "authored",
              "seats": [
                { "name": "a seat", "agent": "ClaudeCode", "repoPath": "D:/repo",
                  "restore": { "decision": "undecided",
                               "seedPrompt": "Read the handover and continue.",
                               "workInProgress": { "branch": "restart-phase-3" } } }
              ]
            }
            """;

        var doc = JsonSerializer.Deserialize<WorkspaceDocument>(json, WorkspaceStore.DocumentJsonOptions)!;
        Assert.Equal("Read the handover and continue.",
            doc.Seats[0].Restore!.Unknown!["seedPrompt"].GetString());

        var store = NewStore();
        store.Save(doc, Now);
        var got = store.Get("director-restart")!;

        Assert.Equal("Read the handover and continue.",
            got.Seats[0].Restore!.Unknown!["seedPrompt"].GetString());
        Assert.Equal("restart-phase-3",
            got.Seats[0].Restore!.Unknown!["workInProgress"].GetProperty("branch").GetString());
    }

    [Fact]
    public void A_nested_bag_is_measured_like_every_other_field()
    {
        // Keeping a field this build cannot read is not a reason to stop counting it. Otherwise the way
        // past every cap is to put a megabyte in a field nobody has invented yet, one level down.
        var doc = Authored();
        doc.RestartCommand = new WorkspaceRestartCommand
        {
            Method = "POST",
            Unknown = new Dictionary<string, JsonElement>
            {
                ["body"] = JsonDocument.Parse(
                    "\"" + new string('x', WorkspaceValidation.MaxUnknownBytes + 10) + "\"").RootElement.Clone(),
            },
        };

        // (Authored refuses a restartCommand outright, so this asserts on the capture path instead.)
        doc.Origin = WorkspaceOrigins.Captured;
        doc.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Create(doc, Now));
        Assert.Contains("restartCommand.unknown", ex.Message);
    }
}
