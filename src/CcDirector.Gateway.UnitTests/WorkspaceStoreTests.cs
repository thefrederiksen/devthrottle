using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway-owned workspace store (issue #2722): a named set of seats, authored by hand or captured
/// from a running Director, held where the machine it describes cannot take it down.
///
/// The tests that matter most here are the REFUSALS. A workspace is read after the sessions it names have
/// been destroyed, by somebody who cannot go and check anything - so a document that looks complete and is
/// not actionable is worse than no document. Each refusal below is a specific way that has already been
/// possible to write one: a seat marked for restore with no command to run, a seat marked covered with
/// nothing named as covering it, a restore list pointing at a session that is not in the workspace.
///
/// Every test runs over an isolated on-disk SQLite database.
/// </summary>
public sealed class WorkspaceStoreTests : IDisposable
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

    [Fact]
    public void Save_then_Get_round_trips_the_whole_document()
    {
        var store = NewStore();
        var doc = Authored();
        doc.Description = "The seats I open every morning.";
        doc.Seats[0].OpeningPrompt = "Read PHASE-A-MANDATE.md and continue.";
        doc.Seats[0].Model = "claude-opus-5";

        store.Save(doc, Now);

        var got = store.Get("morning-fleet");
        Assert.NotNull(got);
        Assert.Equal("Morning fleet", got!.Name);
        Assert.Equal("The seats I open every morning.", got.Description);
        Assert.Equal(WorkspaceOrigins.Authored, got.Origin);
        Assert.Single(got.Seats);
        Assert.Equal("Linux Support - Architect", got.Seats[0].Name);
        Assert.Equal("ClaudeCode", got.Seats[0].Agent);
        Assert.Equal("Manager", got.Seats[0].Role);
        Assert.Equal("claude-opus-5", got.Seats[0].Model);
        Assert.Equal("Read PHASE-A-MANDATE.md and continue.", got.Seats[0].OpeningPrompt);
    }

    [Fact]
    public void A_workspace_survives_a_new_store_over_the_same_database()
    {
        NewStore().Save(Authored(), Now);

        // A fresh store, as after a Gateway restart. The whole point of moving off the machine is that
        // the record outlives the process that wrote it.
        var got = NewStore().Get("morning-fleet");
        Assert.NotNull(got);
        Assert.Single(got!.Seats);
    }

    [Fact]
    public void The_store_stamps_the_times_and_a_replace_keeps_the_created_time()
    {
        var store = NewStore();

        // A caller cannot backdate either timestamp: both are overwritten on the way in.
        var doc = Authored();
        doc.CreatedUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        doc.UpdatedUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Save(doc, Now);

        var again = Authored();
        again.Name = "Morning fleet, revised";
        store.Save(again, Later);

        var got = store.Get("morning-fleet")!;
        Assert.Equal(Now, got.CreatedUtc);
        Assert.Equal(Later, got.UpdatedUtc);
        Assert.Equal("Morning fleet, revised", got.Name);
    }

    [Fact]
    public void List_returns_summaries_newest_first_and_Delete_removes_one()
    {
        var store = NewStore();
        store.Save(Authored("morning-fleet", "Morning fleet"), Now);
        store.Save(Authored("evening-fleet", "Evening fleet"), Later);

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("evening-fleet", list[0].Id);
        Assert.Equal("morning-fleet", list[1].Id);
        Assert.Equal(1, list[0].SeatCount);

        Assert.True(store.Delete("evening-fleet"));
        Assert.False(store.Delete("evening-fleet"));
        Assert.Single(store.List());
        Assert.Null(store.Get("evening-fleet"));
    }

    [Fact]
    public void Create_refuses_to_replace_an_existing_workspace()
    {
        var store = NewStore();
        store.Create(Authored(), Now);

        // Capture creates; it never replaces. Overwriting would destroy a drain somebody is halfway
        // through, and it would do it silently.
        var ex = Assert.Throws<WorkspaceConflictException>(() => store.Create(Authored(), Later));
        Assert.Contains("already exists", ex.Message);
    }

    // ---------- The refusals: documents that would look complete and could not be acted on ----------

    [Fact]
    public void A_seat_marked_for_restore_must_carry_the_command_that_brings_it_back()
    {
        var doc = Authored();
        doc.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "Head of the mission with real continuing work.",
            Command = null,
        };

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("command that brings it back", ex.Message);
    }

    [Fact]
    public void A_covered_seat_must_name_the_seat_whose_document_accounts_for_it()
    {
        var doc = Authored();
        doc.Seats[0].DrainState = WorkspaceDrainStates.Covered;
        doc.Seats[0].CoveredBy = null;

        // Covered is the chain working, not a gap - but only if it says WHICH seat covers it. Without
        // that it is indistinguishable from a seat nobody ever reached.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("coveredBy", ex.Message);
    }

    [Fact]
    public void A_blocked_seat_must_say_what_it_is_blocked_on()
    {
        var doc = Authored();
        doc.Seats[0].DrainState = WorkspaceDrainStates.Blocked;

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("blockedReason", ex.Message);
    }

    [Fact]
    public void The_restore_list_may_only_name_seats_that_are_in_this_workspace()
    {
        var doc = Authored();
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        doc.RestoreAfterRestart.Add("5ff9ab8b-07d3-4b23-953b-6c853760b56c");
        doc.RestoreAfterRestart.Add("00000000-0000-0000-0000-000000000000");

        // This is the field a stranger acts on after the restart. An id that is in no seat sends them
        // looking for a session that was never captured.
        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("00000000-0000-0000-0000-000000000000", ex.Message);
    }

    [Fact]
    public void A_restore_list_naming_a_captured_seat_is_accepted()
    {
        var doc = Authored();
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        doc.RestoreAfterRestart.Add("5ff9ab8b-07d3-4b23-953b-6c853760b56c");

        NewStore().Save(doc, Now);
        Assert.Single(NewStore().Get("morning-fleet")!.RestoreAfterRestart);
    }

    [Fact]
    public void An_unknown_drain_state_is_refused_and_all_five_real_ones_are_accepted()
    {
        var store = NewStore();

        var bad = Authored();
        bad.Seats[0].DrainState = "finished";
        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(bad, Now));
        Assert.Contains("drainState", ex.Message);

        foreach (var state in WorkspaceDrainStates.All)
        {
            var doc = Authored();
            doc.Seats[0].DrainState = state;
            if (state == WorkspaceDrainStates.Covered) doc.Seats[0].CoveredBy = "9cec65de - Manager R-D";
            if (state == WorkspaceDrainStates.Blocked) doc.Seats[0].BlockedReason = "mid-merge";
            store.Save(doc, Now);
            Assert.Equal(state, store.Get("morning-fleet")!.Seats[0].DrainState);
        }
    }

    [Fact]
    public void A_captured_workspace_must_name_the_Director_it_came_from()
    {
        var doc = Authored();
        doc.Origin = WorkspaceOrigins.Captured;
        doc.DirectorId = null;

        var ex = Assert.Throws<WorkspaceValidationException>(() => NewStore().Save(doc, Now));
        Assert.Contains("directorId", ex.Message);
    }

    [Fact]
    public void A_seat_without_a_repository_or_an_agent_is_refused()
    {
        var store = NewStore();

        var noRepo = Authored();
        noRepo.Seats[0].RepoPath = "";
        Assert.Contains("repoPath", Assert.Throws<WorkspaceValidationException>(
            () => store.Save(noRepo, Now)).Message);

        var noAgent = Authored();
        noAgent.Seats[0].Agent = "";
        Assert.Contains("agent", Assert.Throws<WorkspaceValidationException>(
            () => store.Save(noAgent, Now)).Message);
    }

    [Fact]
    public void Two_seats_cannot_be_the_same_session()
    {
        var doc = Authored();
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        doc.Seats.Add(new WorkspaceSeat
        {
            SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c",
            Name = "the same session again",
            Agent = "ClaudeCode",
            RepoPath = @"D:\ReposFred\devthrottle",
        });

        Assert.Contains("repeats sessionId", Assert.Throws<WorkspaceValidationException>(
            () => NewStore().Save(doc, Now)).Message);
    }

    [Fact]
    public void An_id_that_is_not_a_slug_is_refused()
    {
        var store = NewStore();
        foreach (var id in new[] { "", "A", "Morning Fleet", "morning_fleet", "-morning", new string('x', 65) })
        {
            var doc = Authored();
            doc.Id = id;
            Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Now));
        }
    }

    [Fact]
    public void The_whole_restart_record_round_trips_including_what_was_written_back_afterwards()
    {
        // This is the shape the hand-written index of 2026-09-06 ended in: not just the drain, but the
        // launcher update that had to happen first, how the restart was actually asked for, what came
        // back, and who restored it. An index that stops at the drain cannot say later whether the
        // restart worked.
        var doc = Authored("director-restart-2026-09-06", "DevThrottle_1 restart");
        doc.Origin = WorkspaceOrigins.Captured;
        doc.Machine = "SOREN_NORTH";
        doc.DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        doc.DirectorName = "DevThrottle_1";
        doc.DirectorVersionBefore = "2.0.5";
        doc.DirectorVersionAfter = "2.0.6";
        doc.Outcome = WorkspaceOutcomes.Restored;
        doc.DrivenBySessionId = "3807b006-185a-419a-9897-c885455117ae";
        doc.DrivenByDirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1";
        doc.DrivenByNote = "Driver is ON the target Director - allowed only because the owner restarted by hand.";
        doc.OwnerQuestions.Add(new WorkspaceOwnerQuestion
        {
            FromSessionId = "2315b1b0-9e48-4dae-949e-8f4ffb72ebb5",
            FromName = "One honest spoken figure - Manager",
            Question = "Deploy the merged ring change? It needs your go.",
        });
        doc.RestartCommand = new WorkspaceRestartCommand
        {
            Method = "POST",
            Url = "{gateway}/machines/SOREN_NORTH/director/restart",
            ConfirmProtected = true,
            Note = "A session key is refused 403 session_key_out_of_scope.",
        };
        doc.LauncherUpdate = new WorkspaceLauncherUpdate
        {
            From = "1.9.8", To = "2.0.4", Result = "applied", AppliedAtUtc = Later,
        };
        doc.RestartBlocked = new WorkspaceRestartBlocked
        {
            State = "RESOLVED - was self-inflicted",
            CorrectedClaim = "An earlier entry here was backwards.",
            GuardVerdict = "The refusal was correct behaviour and must not be changed.",
        };
        doc.RestartMechanism = new WorkspaceRestartMechanism
        {
            Method = "named lifecycle signal to the launcher",
            Signal = @"Local\cc-director-launcher-restart-director-840af6cd0370",
            LauncherPid = 52312,
            LauncherVersion = "2.0.4",
        };
        doc.RestartPerformed = new WorkspaceRestartPerformed
        {
            AtLocal = "2026-09-06T18:03:02",
            DirectorPid = 18692,
            DirectorVersionAfter = "2.0.6",
            LauncherAfter = new WorkspaceLauncherAfter { Pid = 69640, Version = "2.0.6" },
            VerifiedBy = "session 44fea0ca - file versions and process start times",
            NotVerified = "that any restored session re-establishes its own tree",
        };
        doc.RestoredBy = new WorkspaceRestoredBy
        {
            SessionId = "44fea0ca-2a0f-48c1-ac7c-d4fe2669dad3",
            Name = "devthrottle_internal - restart",
            AtUtc = Later,
            Method = "seed written to a FILE per session and spawned with a one-line prompt pointing at it",
        };
        doc.Seats[0].SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";
        doc.Seats[0].DrainState = WorkspaceDrainStates.Drained;
        doc.Seats[0].HandoverPath = @"C:\...\5ff9ab8b - Linux Support - Architect.md";
        doc.Seats[0].Restore = new WorkspaceSeatRestore
        {
            Decision = WorkspaceRestoreDecisions.Restore,
            Why = "Head of the mission with real continuing work.",
            Command = "cc-devthrottle session spawn ...",
        };
        doc.Seats[0].ClosedAtUtc = Later;
        doc.Seats[0].RestoredSessionId = "a5b16478-c173-451c-9621-f74a1ad308ce";
        doc.Seats[0].RestoredSeedFile = "SEED-5ff9ab8b-linux-architect.md";
        doc.RestoreAfterRestart.Add("5ff9ab8b-07d3-4b23-953b-6c853760b56c");

        NewStore().Save(doc, Now);
        var got = NewStore().Get("director-restart-2026-09-06")!;

        Assert.Equal("SOREN_NORTH", got.Machine);
        Assert.Equal(WorkspaceOutcomes.Restored, got.Outcome);
        Assert.Equal("2.0.5", got.DirectorVersionBefore);
        Assert.Equal("2.0.6", got.DirectorVersionAfter);
        Assert.Single(got.OwnerQuestions);
        Assert.Equal("Deploy the merged ring change? It needs your go.", got.OwnerQuestions[0].Question);
        Assert.True(got.RestartCommand!.ConfirmProtected);
        Assert.Equal("2.0.4", got.LauncherUpdate!.To);
        Assert.Contains("must not be changed", got.RestartBlocked!.GuardVerdict);
        Assert.Equal(52312, got.RestartMechanism!.LauncherPid);
        Assert.Equal(69640, got.RestartPerformed!.LauncherAfter!.Pid);
        Assert.Contains("re-establishes its own tree", got.RestartPerformed.NotVerified);
        Assert.Equal("44fea0ca-2a0f-48c1-ac7c-d4fe2669dad3", got.RestoredBy!.SessionId);
        Assert.Equal("a5b16478-c173-451c-9621-f74a1ad308ce", got.Seats[0].RestoredSessionId);
        Assert.Equal("SEED-5ff9ab8b-linux-architect.md", got.Seats[0].RestoredSeedFile);
        Assert.Equal(new[] { "5ff9ab8b-07d3-4b23-953b-6c853760b56c" }, got.RestoreAfterRestart);
    }
}
