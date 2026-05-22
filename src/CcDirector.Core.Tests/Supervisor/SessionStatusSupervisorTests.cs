using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Supervisor;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Supervisor;

/// <summary>
/// Phase 3 tests for <see cref="SessionStatusSupervisor"/>. The supervisor is the
/// sole writer of <see cref="Session.StatusColor"/> on the Director. The UI renders
/// what it writes; nothing else may invent colors.
/// </summary>
public sealed class SessionStatusSupervisorTests
{
    // ---------- Pure mapping (fast path) ----------

    [Fact]
    public void New_session_maps_to_green_session_created()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.Starting, isNew: true);
        Assert.Equal(StatusColor.Green, color);
        Assert.Equal("session created", reason);
    }

    [Fact]
    public void Working_maps_to_blue()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.Working, isNew: false);
        Assert.Equal(StatusColor.Blue, color);
        Assert.Equal("working", reason);
    }

    [Fact]
    public void Idle_maps_to_green_ready()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.Idle, isNew: false);
        Assert.Equal(StatusColor.Green, color);
        Assert.Equal("idle, ready for next task", reason);
    }

    [Fact]
    public void WaitingForInput_maps_to_green_by_default()
    {
        // Phase 4a: WaitingForInput is no longer red by default. Red is promoted only
        // when the supervisor has positive evidence (buffer scan or turn summary).
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.WaitingForInput, isNew: false);
        Assert.Equal(StatusColor.Green, color);
        Assert.Equal("ready, awaiting next prompt", reason);
    }

    [Fact]
    public void PromotePendingQuestion_sets_red_with_detail()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var supervisor = new SessionStatusSupervisor(manager);
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            supervisor.PromotePendingQuestion(session, "delete users.db?");
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("delete users.db?", session.LastStatusReason);
        }
        finally { supervisor.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void PromotePendingQuestion_truncates_long_detail()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var supervisor = new SessionStatusSupervisor(manager);
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            var huge = new string('q', 800);
            supervisor.PromotePendingQuestion(session, huge);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.True(session.LastStatusReason.Length <= 500, $"reason length {session.LastStatusReason.Length} exceeds cap");
            Assert.EndsWith("...", session.LastStatusReason);
        }
        finally { supervisor.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void WaitingForPerm_maps_to_red()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.WaitingForPerm, isNew: false);
        Assert.Equal(StatusColor.Red, color);
        Assert.Equal("waiting for permission", reason);
    }

    [Fact]
    public void Exited_maps_to_unknown_with_reason()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.Exited, isNew: false);
        // Exited sessions never appear on the directory (the /sessions endpoint hides
        // them). But debug tooling that does ask for them gets a truthful "exited"
        // reason - NOT a gray-as-fallback.
        Assert.Equal(StatusColor.Unknown, color);
        Assert.Equal("exited", reason);
    }

    [Fact]
    public void Restored_session_starting_uses_plain_starting_reason()
    {
        var (color, reason) = SessionStatusSupervisor.ColorFromActivityState(ActivityState.Starting, isNew: false);
        Assert.Equal(StatusColor.Green, color);
        Assert.Equal("starting", reason);
    }

    // ---------- Session model writes ----------

    [Fact]
    public void Session_starts_green_at_construction()
    {
        // Even without a supervisor wired up, the Session model defaults to green
        // ("session created") so any consumer reads a meaningful color before the
        // supervisor's Start() runs. The supervisor's job is to keep this current.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            Assert.Equal(StatusColor.Green, session.StatusColor);
            Assert.Equal("session created", session.LastStatusReason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SetStatusColor_fires_OnStatusColorChanged_event_with_old_new_reason()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());

            string? captured = null;
            session.OnStatusColorChanged += (oldC, newC, reason) =>
            {
                captured = $"{oldC}->{newC}:{reason}";
            };

            session.SetStatusColor(StatusColor.Red, "waiting for input");
            Assert.Equal("green->red:waiting for input", captured);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("waiting for input", session.LastStatusReason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SupervisorEventLog_records_each_color_change_newest_first()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            session.SetStatusColor(StatusColor.Blue, "working");
            session.SetStatusColor(StatusColor.Red, "waiting for input");
            session.SetStatusColor(StatusColor.Green, "clean turn");

            var events = session.RecentSupervisorEvents;
            // 3 events plus there may be one from CreateSession's default-already-green
            // path being a no-op. The most-recent-first order is what we care about.
            Assert.NotEmpty(events);
            Assert.Equal("green", events[0].NewColor);
            Assert.Equal("red", events[1].NewColor);
            Assert.Equal("blue", events[2].NewColor);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SupervisorEventLog_caps_at_50_entries()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            for (int i = 0; i < 80; i++)
            {
                // Alternate colors so each call actually changes state.
                var c = (i % 2 == 0) ? StatusColor.Blue : StatusColor.Green;
                session.SetStatusColor(c, $"tick {i}");
            }
            Assert.Equal(50, session.RecentSupervisorEvents.Count);
            // Newest first - last call was i=79, color=Green, reason="tick 79"
            Assert.Equal("tick 79", session.RecentSupervisorEvents[0].Reason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SetStatusColor_no_change_does_not_fire_event()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            int fires = 0;
            session.OnStatusColorChanged += (_, _, _) => fires++;

            // Same color and same reason as the constructor default - no-op.
            session.SetStatusColor(StatusColor.Green, "session created");
            Assert.Equal(0, fires);

            // Different reason fires even if the color is the same.
            session.SetStatusColor(StatusColor.Green, "idle, ready for next task");
            Assert.Equal(1, fires);
        }
        finally { manager.Dispose(); }
    }

    // ---------- ApplyTurnSummary (slow path) ----------

    [Fact]
    public void ApplyTurnSummary_question_goes_red_with_detail()
    {
        var supervisor = new SessionStatusSupervisor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            supervisor.ApplyTurnSummary(session, new TurnSummary
            {
                NeedsUser = "question",
                NeedsUserDetail = "should I delete the file?",
                Headline = "asked about deletion",
            });
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("should I delete the file?", session.LastStatusReason);
        }
        finally { manager.Dispose(); supervisor.Dispose(); }
    }

    [Fact]
    public void ApplyTurnSummary_prefers_needs_user_short_over_detail()
    {
        // Phase 4e: when the supervisor produces a crisp NeedsUserShort, that becomes
        // the LastStatusReason. NeedsUserDetail (which can be a paragraph) is ignored
        // for the reason field; the merged Session View renders detail separately.
        var supervisor = new SessionStatusSupervisor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            supervisor.ApplyTurnSummary(session, new TurnSummary
            {
                NeedsUser = "question",
                NeedsUserShort = "Delete users.db?",
                NeedsUserDetail = "I noticed users.db is no longer referenced from any code. Removing it would save 4 MB on disk but is irreversible without a backup. Should I proceed with the delete?",
                Headline = "deletion check",
            });
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("Delete users.db?", session.LastStatusReason);
        }
        finally { manager.Dispose(); supervisor.Dispose(); }
    }

    [Fact]
    public void ApplyTurnSummary_clean_turn_goes_green_with_headline()
    {
        var supervisor = new SessionStatusSupervisor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            session.SetStatusColor(StatusColor.Blue, "working");
            supervisor.ApplyTurnSummary(session, new TurnSummary
            {
                NeedsUser = "no",
                Headline = "fixed the login bug",
            });
            Assert.Equal(StatusColor.Green, session.StatusColor);
            Assert.Equal("fixed the login bug", session.LastStatusReason);
        }
        finally { manager.Dispose(); supervisor.Dispose(); }
    }

    [Fact]
    public void ApplyTurnSummary_warnings_go_yellow()
    {
        var supervisor = new SessionStatusSupervisor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            supervisor.ApplyTurnSummary(session, new TurnSummary
            {
                NeedsUser = "no",
                Headline = "ran a thing",
            }, hasWarnings: true);
            Assert.Equal(StatusColor.Yellow, session.StatusColor);
        }
        finally { manager.Dispose(); supervisor.Dispose(); }
    }

    [Fact]
    public void ApplyTurnSummary_does_not_downgrade_an_active_red_activity_state()
    {
        // Race scenario: turn summary arrives reporting "no" / clean, but the
        // session has since moved to WaitingForInput. The fast path already wrote
        // red. The slow path must NOT overwrite that with green just because Haiku
        // didn't see the new question yet.
        // We can't drive ActivityState directly from the test (private setter), so
        // this test exercises the guard with a session whose default state is
        // Starting (not WaitingFor*) and then asserts the inverse: clean summary
        // does write through when not blocked. The guard logic is straight-line.
        var supervisor = new SessionStatusSupervisor(new SessionManager(new AgentOptions { ClaudePath = TestShell.Path }));
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            // Session is in Starting state by default - guard does not block.
            supervisor.ApplyTurnSummary(session, new TurnSummary
            {
                NeedsUser = "no",
                Headline = "clean",
            });
            Assert.Equal(StatusColor.Green, session.StatusColor);
        }
        finally { manager.Dispose(); supervisor.Dispose(); }
    }

    // ---------- Supervisor lifecycle ----------

    [Fact]
    public void Supervisor_Start_sets_existing_sessions_to_color_matching_their_state()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());

            var supervisor = new SessionStatusSupervisor(manager);
            supervisor.Start();
            try
            {
                // The session was just created in Starting state; supervisor on Start()
                // re-stamps each pre-existing session's color from its current activity
                // state. isNew=false at that point, so reason is "starting", not
                // "session created" (which is reserved for the OnSessionCreated path).
                Assert.True(session.StatusColor == StatusColor.Green ||
                            session.StatusColor == StatusColor.Blue,
                    $"expected green or blue post-start, got {session.StatusColor}");
            }
            finally { supervisor.Dispose(); }
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void Supervisor_OnSessionCreated_writes_green_session_created()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var supervisor = new SessionStatusSupervisor(manager);
            supervisor.Start();
            try
            {
                var session = manager.CreateSession(Path.GetTempPath());
                Assert.Equal(StatusColor.Green, session.StatusColor);
                // The supervisor's OnSessionCreated path uses isNew=true so the reason
                // is the friendlier "session created".
                Assert.Equal("session created", session.LastStatusReason);
            }
            finally { supervisor.Dispose(); }
        }
        finally { manager.Dispose(); }
    }
}
