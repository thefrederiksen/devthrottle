using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The fold that turns a Director's live sessions into a workspace (issue #2722).
///
/// The cases below are taken from the index written BY HAND during the first real drain, on 2026-09-06,
/// and each asserts that the fold produces the same fact the person wrote down. That is the direction that
/// matters: a test written from the fold would agree with the fold's mistakes, and the whole proof this
/// phase owes is that the capture and the hand-written index say the same things.
/// </summary>
public sealed class WorkspaceCaptureTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 17, 25, 34, DateTimeKind.Utc);

    private static WorkspaceCaptureRequest Request() => new()
    {
        Id = "director-restart-2026-09-06",
        Name = "DevThrottle_1 restart",
        DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        Reason = "first real run of the director-restart skill",
        DrivenBySessionId = "3807b006-185a-419a-9897-c885455117ae",
        DrivenByDirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        DrivenByNote = "Driver is ON the target Director.",
    };

    /// <summary>
    /// The Linux Support Architect exactly as the Gateway held it during the first drain. Its resolved
    /// role really was "Manager" even though the session is called Architect - that is the fleet-wide
    /// resolution, and the hand-written index recorded the resolved value, not the name.
    /// </summary>
    private static SessionDto LinuxArchitect() => new()
    {
        SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c",
        DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        Name = "Linux Support - Architect",
        Agent = "ClaudeCode",
        CurrentModel = "claude-opus-5",
        RepoPath = @"D:\ReposFred\devthrottle_internal",
        MachineName = "SOREN_NORTH",
        SessionRole = "Manager",
        Status = "Running",
        ActivityState = "WaitingForInput",
        StateLabel = "Needs you",
        TurnCount = 7,
        UncommittedCount = 5,
        ClaudeSessionId = "cea12a53-460d-4fb3-8133-fb87106cfd07",
        ClaudeTranscriptPath = @"C:\Users\soren\.claude\projects\D--ReposFred-devthrottle-internal\cea12a53.jsonl",
        CreatedAt = new DateTime(2026, 9, 6, 15, 46, 59, DateTimeKind.Utc),
        SortOrder = 0,
    };

    /// <summary>
    /// A Worker under the New Studio Cube Manager: attached to a mission, controlled by its Manager,
    /// seated on a workflow run, and with no model recorded yet.
    /// </summary>
    private static SessionDto CubeWorker() => new()
    {
        SessionId = "0da65999-7795-4abf-bd64-94be7a7438b1",
        DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
        Name = "Cube R-D Worker D - the ARN guard",
        Agent = "ClaudeCode",
        CurrentModel = null,
        ModelDisplay = new ModelDisplay
        {
            Kind = "notRecordedYet",
            Text = "no model yet",
            ModelId = null,
            Tooltip = "No model recorded yet.",
            IsAbsent = true,
        },
        RepoPath = "D:/ReposMindzie/worktrees/ns-cube-rd",
        MachineName = "SOREN_NORTH",
        MissionId = Guid.Parse("187f1fa3-bf91-4a13-8207-5aaa34def084"),
        MissionName = "New Studio Cube - the agent inside or outside",
        SessionRole = "Worker",
        ControllerSessionId = "9cec65de-6c1c-4079-b41b-45cc51ccd4f1",
        ParentSessionId = "9cec65de-6c1c-4079-b41b-45cc51ccd4f1",
        WorkflowRunId = Guid.Parse("28574ecd-0986-422b-b82e-01dc17af5744"),
        IsControlled = true,
        Status = "Running",
        ActivityState = "WaitingForInput",
        StateLabel = "Snoozed",
        TurnCount = 0,
        UncommittedCount = 1,
        ClaudeSessionId = "d7761529-aa4b-4fd5-9af4-17b89da315b3",
        ClaudeTranscriptPath = null,
        CreatedAt = new DateTime(2026, 9, 6, 17, 24, 39, DateTimeKind.Utc),
        SortOrder = 1,
    };

    [Fact]
    public void The_header_records_the_Director_the_machine_and_who_drove_it()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { LinuxArchitect() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        Assert.Equal("director-restart-2026-09-06", doc.Id);
        Assert.Equal("DevThrottle_1 restart", doc.Name);
        Assert.Equal(WorkspaceOrigins.Captured, doc.Origin);
        Assert.Equal("SOREN_NORTH", doc.Machine);
        Assert.Equal("6d4523e2-ed03-4ae6-ac1c-71d00a37bad1", doc.DirectorId);
        Assert.Equal("DevThrottle_1", doc.DirectorName);
        Assert.Equal("2.0.5", doc.DirectorVersionBefore);
        Assert.Equal(Now, doc.StartedAtUtc);
        Assert.Equal("3807b006-185a-419a-9897-c885455117ae", doc.DrivenBySessionId);
        Assert.Equal("first real run of the director-restart skill", doc.Reason);
    }

    [Fact]
    public void A_fresh_capture_is_draining_and_has_not_been_restarted()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { LinuxArchitect() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        // The version AFTER, the completion time and the restart record are written back later, by
        // whoever performs the restart. A capture that filled them in would be claiming an outcome.
        Assert.Equal(WorkspaceOutcomes.Draining, doc.Outcome);
        Assert.Null(doc.DirectorVersionAfter);
        Assert.Null(doc.CompletedAtUtc);
        Assert.Null(doc.RestartPerformed);
        Assert.Null(doc.RestoredBy);
        Assert.Empty(doc.RestoreAfterRestart);
        Assert.Empty(doc.OwnerQuestions);
    }

    [Fact]
    public void A_seat_carries_the_facts_the_hand_written_index_recorded()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { LinuxArchitect() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        var seat = Assert.Single(doc.Seats);
        Assert.Equal("5ff9ab8b-07d3-4b23-953b-6c853760b56c", seat.SessionId);
        Assert.Equal("Linux Support - Architect", seat.Name);
        Assert.Equal("ClaudeCode", seat.Agent);
        Assert.Equal("claude-opus-5", seat.Model);
        Assert.Equal(@"D:\ReposFred\devthrottle_internal", seat.RepoPath);
        Assert.Null(seat.Mission);
        Assert.Equal("Manager", seat.Role);
        Assert.Null(seat.ReportsTo);
        Assert.Null(seat.WorkflowRunId);
        Assert.Equal("Running", seat.StateAtDrain!.Status);
        Assert.Equal("WaitingForInput", seat.StateAtDrain.ActivityState);
        Assert.Equal("Needs you", seat.StateAtDrain.StateLabel);
        Assert.Equal(7, seat.StateAtDrain.TurnCount);
        Assert.Equal(5, seat.StateAtDrain.UncommittedCount);
        Assert.Equal("cea12a53-460d-4fb3-8133-fb87106cfd07", seat.ClaudeSessionId);
        Assert.Equal(new DateTime(2026, 9, 6, 15, 46, 59, DateTimeKind.Utc), seat.CreatedAt);
    }

    [Fact]
    public void A_controlled_worker_carries_its_mission_its_controller_and_its_workflow_run()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { CubeWorker() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        var seat = Assert.Single(doc.Seats);
        Assert.Equal("187f1fa3-bf91-4a13-8207-5aaa34def084", seat.Mission!.Id);
        Assert.Equal("New Studio Cube - the agent inside or outside", seat.Mission.Name);
        Assert.Equal("Worker", seat.Role);
        Assert.Equal("9cec65de-6c1c-4079-b41b-45cc51ccd4f1", seat.ReportsTo);
        Assert.Equal("9cec65de-6c1c-4079-b41b-45cc51ccd4f1", seat.ParentSessionId);
        Assert.Equal("28574ecd-0986-422b-b82e-01dc17af5744", seat.WorkflowRunId);
        Assert.Equal("Snoozed", seat.StateAtDrain!.StateLabel);
        Assert.Null(seat.ClaudeTranscriptPath);
    }

    [Fact]
    public void An_unrecorded_model_keeps_WHICH_absence_it_was()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { CubeWorker() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        // The two absences mean opposite things - not recorded YET, versus this agent can never report
        // one - so the seat keeps the folded verdict beside the null id rather than flattening both to
        // "no model".
        var seat = Assert.Single(doc.Seats);
        Assert.Null(seat.Model);
        Assert.NotNull(seat.ModelDisplay);
        Assert.Equal("notRecordedYet", seat.ModelDisplay!.Kind);
        Assert.Equal("no model yet", seat.ModelDisplay.Text);
        Assert.True(seat.ModelDisplay.IsAbsent);
    }

    [Fact]
    public void The_capture_makes_no_judgments()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { LinuxArchitect(), CubeWorker() }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        // Drain state and restore decision are read out of a HANDOVER by somebody who has read it. A
        // capture that guessed "drained" would be the one thing the drain skill forbids outright: an
        // entry nobody verified, on a record everybody trusts afterwards.
        foreach (var seat in doc.Seats)
        {
            Assert.Null(seat.DrainState);
            Assert.Null(seat.HandoverPath);
            Assert.Null(seat.ClosedAtUtc);
            Assert.Null(seat.RestoredSessionId);
            Assert.Equal(WorkspaceRestoreDecisions.Undecided, seat.Restore!.Decision);
            Assert.Null(seat.Restore.Command);
        }
    }

    [Fact]
    public void Seats_come_back_in_the_Directors_own_order()
    {
        var second = LinuxArchitect();
        second.SortOrder = 5;
        var first = CubeWorker();
        first.SortOrder = 2;

        var doc = WorkspaceCapture.Capture(
            Request(), new[] { second, first }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        Assert.Equal(new[] { first.SessionId, second.SessionId }, doc.Seats.Select(s => s.SessionId));
        Assert.Equal(new[] { 0, 1 }, doc.Seats.Select(s => s.SortOrder));
    }

    [Fact]
    public void The_machine_falls_back_to_what_the_sessions_report()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), new[] { LinuxArchitect() }, "DevThrottle_1", "2.0.5", machine: null, Now);
        Assert.Equal("SOREN_NORTH", doc.Machine);
    }

    [Fact]
    public void A_Director_with_no_sessions_captures_an_empty_workspace()
    {
        var doc = WorkspaceCapture.Capture(
            Request(), Array.Empty<SessionDto>(), "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);

        // Not an error: an empty Director is exactly what a finished drain leaves behind, and capturing
        // one has to produce a document rather than a failure.
        Assert.Empty(doc.Seats);
        Assert.Equal("SOREN_NORTH", doc.Machine);
    }

    [Fact]
    public void An_explicit_role_is_used_when_the_fleet_resolution_is_absent()
    {
        // A Director-local response cannot resolve a role at all - that needs the fleet view - so the
        // explicitly declared one is what is left, and an Architect can ONLY arrive that way.
        var s = LinuxArchitect();
        s.SessionRole = null;
        s.ExplicitRole = "Architect";

        var doc = WorkspaceCapture.Capture(
            Request(), new[] { s }, "DevThrottle_1", "2.0.5", "SOREN_NORTH", Now);
        Assert.Equal("Architect", Assert.Single(doc.Seats).Role);
    }
}
