using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A captured workspace records what the Gateway READ from a Director, and an ordinary write can only
/// ever change a judgment somebody made about it (issue #2722).
///
/// This file exists because the first version of the rule looked closed and was not. It preserved the
/// document HEADER - origin, machine, Director - and left the SEATS writable, so a caller could keep the
/// genuine header and replace its contents: a record saying a fleet was read firsthand from a named
/// Director, whose seats were typed. The header was the part everybody looked at; the seats are the part
/// that gets acted on after the sessions are gone.
///
/// So the seat SET is fixed too, and every fact the Gateway observed about a seat is restored on each
/// write. What a caller may change is the handover path, the drain state, the restore decision, and what
/// came back.
/// </summary>
public sealed class WorkspaceCapturedSeatsAreImmutableTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 6, 22, 2, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private WorkspaceStore NewStore() => new(_h.Open());

    private const string SeatId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";

    private static WorkspaceDocument Captured()
        => new()
        {
            Id = "director-restart",
            Name = "DevThrottle_1 restart",
            Origin = WorkspaceOrigins.Captured,
            Machine = "SOREN_NORTH",
            DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
            DirectorName = "DevThrottle_1",
            DirectorVersionBefore = "2.0.5",
            StartedAtUtc = Now,
            Seats =
            {
                new WorkspaceSeat
                {
                    SessionId = SeatId,
                    Name = "Linux Support - Architect",
                    Agent = "ClaudeCode",
                    Model = "claude-opus-5",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                    Role = "Manager",
                    ReportsTo = "e777d59f-33d3-4732-8ec8-764e368db408",
                    ClaudeSessionId = "cea12a53-460d-4fb3-8133-fb87106cfd07",
                    StateAtDrain = new WorkspaceSeatState { Status = "Running", TurnCount = 7 },
                    SortOrder = 0,
                },
            },
        };

    private WorkspaceStore WithCapturedWorkspace()
    {
        var store = NewStore();
        store.Create(Captured(), Now);
        return store;
    }

    [Fact]
    public void A_write_cannot_change_what_the_Gateway_observed_about_a_seat()
    {
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.Seats[0].Name = "something else entirely";
        edited.Seats[0].Agent = "Codex";
        edited.Seats[0].Model = "a model it never ran";
        edited.Seats[0].RepoPath = @"C:\fake";
        edited.Seats[0].Role = "Architect";
        edited.Seats[0].ReportsTo = "00000000-0000-0000-0000-000000000000";
        edited.Seats[0].ClaudeSessionId = "not its transcript";
        edited.Seats[0].StateAtDrain = new WorkspaceSeatState { Status = "Exited", TurnCount = 0 };

        // ...while writing a legitimate judgment at the same time.
        edited.Seats[0].DrainState = WorkspaceDrainStates.Drained;
        edited.Seats[0].HandoverPath = @"C:\vault\5ff9ab8b - Linux Support - Architect.md";
        store.Save(edited, Later);

        var got = store.Get("director-restart")!;
        var seat = Assert.Single(got.Seats);

        // The judgment landed.
        Assert.Equal(WorkspaceDrainStates.Drained, seat.DrainState);
        Assert.Equal(@"C:\vault\5ff9ab8b - Linux Support - Architect.md", seat.HandoverPath);

        // Everything the Gateway read is exactly as captured.
        Assert.Equal("Linux Support - Architect", seat.Name);
        Assert.Equal("ClaudeCode", seat.Agent);
        Assert.Equal("claude-opus-5", seat.Model);
        Assert.Equal(@"D:\ReposFred\devthrottle_internal", seat.RepoPath);
        Assert.Equal("Manager", seat.Role);
        Assert.Equal("e777d59f-33d3-4732-8ec8-764e368db408", seat.ReportsTo);
        Assert.Equal("cea12a53-460d-4fb3-8133-fb87106cfd07", seat.ClaudeSessionId);
        Assert.Equal("Running", seat.StateAtDrain!.Status);
        Assert.Equal(7, seat.StateAtDrain.TurnCount);
    }

    [Fact]
    public void A_write_cannot_add_a_seat_that_was_never_captured()
    {
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.Seats.Add(new WorkspaceSeat
        {
            SessionId = "11111111-1111-1111-1111-111111111111",
            Name = "a session that never ran",
            Agent = "ClaudeCode",
            RepoPath = @"C:\fake",
        });

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(edited, Later));
        Assert.Contains("11111111-1111-1111-1111-111111111111", ex.Message);
        Assert.Single(store.Get("director-restart")!.Seats);
    }

    [Fact]
    public void A_write_cannot_empty_a_captured_fleet()
    {
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.Seats.Clear();

        // "No seats" is what a Director that had finished looks like. A write that could say it would be
        // able to turn a record of eighteen sessions into a record of none.
        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(edited, Later));
        Assert.Contains(SeatId, ex.Message);
        Assert.Single(store.Get("director-restart")!.Seats);
    }

    [Fact]
    public void A_write_cannot_smuggle_in_a_seat_that_names_no_session()
    {
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.Seats.Add(new WorkspaceSeat
        {
            SessionId = null,
            Name = "an anonymous seat",
            Agent = "ClaudeCode",
            RepoPath = @"C:\fake",
        });

        // A seat with no session id matches nothing stored, so it cannot be checked against anything -
        // and letting it through is how an invented seat rides in beside real ones.
        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(edited, Later));
        Assert.Contains("name no session", ex.Message);
    }

    [Fact]
    public void An_authored_workspace_created_by_hand_cannot_carry_a_capture_header()
    {
        // Refusing only origin was not enough: an "authored" document carrying a machine and a Director
        // keeps them for ever, because every later write restores what is stored - a forged fleet record
        // in two steps, which afterwards not even its author can clear.
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Machine = "FAKE",
            DirectorId = "fake-director",
            DirectorName = "Moved Director",
            DirectorVersionBefore = "9.9.9",
            StartedAtUtc = Now,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
        };

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("machine", ex.Message);
        Assert.Contains("directorId", ex.Message);
        Assert.Contains("startedAtUtc", ex.Message);
    }

    [Fact]
    public void An_authored_workspace_can_still_have_its_seats_edited_freely()
    {
        // The seat rule is about CAPTURED facts. An authored workspace has none, so nothing here is
        // restored and a person can rearrange their own morning fleet.
        var store = NewStore();
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "first", Agent = "ClaudeCode", RepoPath = @"D:\one" } },
        };
        store.Save(doc, Now);

        var edited = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats =
            {
                new WorkspaceSeat { Name = "renamed", Agent = "Codex", RepoPath = @"D:\two" },
                new WorkspaceSeat { Name = "added", Agent = "ClaudeCode", RepoPath = @"D:\three", SortOrder = 1 },
            },
        };
        store.Save(edited, Later);

        var got = store.Get("morning-fleet")!;
        Assert.Equal(2, got.Seats.Count);
        Assert.Equal("renamed", got.Seats[0].Name);
        Assert.Equal("Codex", got.Seats[0].Agent);
    }

    [Fact]
    public void An_update_that_omits_the_fields_it_is_not_allowed_to_set_is_accepted()
    {
        // The provenance fields are documented as ignored on an update, so validating the INCOMING copy
        // of them would refuse a legitimate write for a value the store was about to overwrite anyway.
        // A caller that read the workspace, changed a judgment and sent it back without a directorId
        // used to get a 400 about a field they cannot set.
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.Origin = WorkspaceOrigins.Authored;
        edited.Machine = null;
        edited.DirectorId = null;
        edited.DirectorName = null;
        edited.DirectorVersionBefore = null;
        edited.StartedAtUtc = null;
        edited.Seats[0].DrainState = WorkspaceDrainStates.Drained;

        store.Save(edited, Later);

        var got = store.Get("director-restart")!;
        Assert.Equal(WorkspaceOrigins.Captured, got.Origin);
        Assert.Equal("SOREN_NORTH", got.Machine);
        Assert.Equal("6d4523e2-ed03-4ae6-ac1c-71d00a37bad1", got.DirectorId);
        Assert.Equal(WorkspaceDrainStates.Drained, got.Seats[0].DrainState);
    }

    [Fact]
    public void An_update_that_keeps_the_captured_origin_but_omits_the_Director_is_accepted()
    {
        // The case that pins WHERE validation runs. This body says "captured" with no directorId, which
        // is invalid on its own - a captured workspace must name its Director - and perfectly fine once
        // the stored Director has been put back. Validating before that restore refuses a legitimate
        // write for a value the caller is not allowed to set in the first place.
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.DirectorId = null;
        edited.Seats[0].DrainState = WorkspaceDrainStates.Drained;

        store.Save(edited, Later);

        var got = store.Get("director-restart")!;
        Assert.Equal("6d4523e2-ed03-4ae6-ac1c-71d00a37bad1", got.DirectorId);
        Assert.Equal(WorkspaceDrainStates.Drained, got.Seats[0].DrainState);
    }

    // ---- The seat counts describe seats that exist ---------------------------------------------------

    [Fact]
    public void The_seat_outcome_cannot_account_for_more_seats_than_the_workspace_has()
    {
        var store = WithCapturedWorkspace();

        var edited = Captured();
        edited.SeatOutcome = new WorkspaceSeatOutcome { RestoredCount = 4 };

        // One seat, four restored. Without this every count in the record is a number somebody typed
        // rather than something that happened.
        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(edited, Later));
        Assert.Contains("accounts for 4", ex.Message);
    }

    // ---- What this build does not understand is still measured ---------------------------------------

    [Fact]
    public void Unrecognised_fields_are_kept_but_not_unlimited()
    {
        var store = NewStore();

        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
            Unknown = new Dictionary<string, JsonElement>
            {
                ["fromTheFuture"] = JsonDocument.Parse(
                    "\"" + new string('x', WorkspaceValidation.MaxUnknownBytes + 10) + "\"").RootElement.Clone(),
            },
        };

        // "We do not know what this is" cannot mean "it is not measured": otherwise an authenticated
        // caller who cannot put a megabyte in "name" puts it in a field nobody has invented yet.
        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Now));
        Assert.Contains("this build does not know", ex.Message);
    }

    [Fact]
    public void A_seats_unrecognised_fields_are_measured_too()
    {
        var store = NewStore();

        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats =
            {
                new WorkspaceSeat
                {
                    Name = "a seat",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\repo",
                    Unknown = Enumerable.Range(0, WorkspaceValidation.MaxUnknownFields + 5)
                        .ToDictionary(i => $"field{i}", _ => JsonDocument.Parse("1").RootElement.Clone()),
                },
            },
        };

        Assert.Contains("this build does not know", Assert.Throws<WorkspaceValidationException>(
            () => store.Save(doc, Now)).Message);
    }

    [Fact]
    public void A_seat_field_from_a_newer_build_survives_a_read_and_a_write()
    {
        // Raw JSON, so this fails if the [JsonExtensionData] attribute is removed from the SEAT - a test
        // that set the dictionary directly would pass either way, because the property serializes on its
        // own name without the attribute.
        const string json = """
            {
              "id": "morning-fleet",
              "name": "Morning fleet",
              "origin": "authored",
              "seats": [
                { "name": "a seat", "agent": "ClaudeCode", "repoPath": "D:/repo",
                  "seatFieldFromTheFuture": { "kept": true, "count": 3 } }
              ]
            }
            """;

        var doc = JsonSerializer.Deserialize<WorkspaceDocument>(json, WorkspaceStore.DocumentJsonOptions)!;
        Assert.True(doc.Seats[0].Unknown!["seatFieldFromTheFuture"].GetProperty("kept").GetBoolean());

        var store = NewStore();
        store.Save(doc, Now);

        var got = store.Get("morning-fleet")!;
        Assert.True(got.Seats[0].Unknown!["seatFieldFromTheFuture"].GetProperty("kept").GetBoolean());
        Assert.Equal(3, got.Seats[0].Unknown!["seatFieldFromTheFuture"].GetProperty("count").GetInt32());
    }

    // ---- The agent name has to name an agent that exists ---------------------------------------------

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("ClaudeCode, Codex")]
    public void A_numeric_or_combined_agent_value_is_refused(string agent)
    {
        // Enum.TryParse accepts all three for a non-flags enum, so parsing ALONE would store an agent
        // that exists nowhere and hand it to the code that starts a process.
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            Seats = { new WorkspaceSeat { Name = "a seat", Agent = agent, RepoPath = @"D:\repo" } },
        };

        Assert.Contains("not an agent this Gateway knows", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Save(doc, Now)).Message);
    }
}
