using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// In-process stub backend that provides a real CircularTerminalBuffer but never
/// spawns a process and never auto-exits. Used where a session must stay alive long
/// enough for assertions to run -- the real ConPty backend (cmd.exe) terminates almost
/// immediately, which puts the session into ActivityState.Exited.
/// </summary>
internal sealed class BufferOnlyBackend : ISessionBackend
{
    public int ProcessId => 0;
    public string Status => "Buffer-only";
    public bool IsRunning => true;
    public bool HasExited => false;
    public CircularTerminalBuffer? Buffer { get; } = new CircularTerminalBuffer(65536);

#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
    public void Write(byte[] data) => Buffer?.Write(data);
    public Task SendTextAsync(string text) => Task.CompletedTask;
    public Task SendEnterAsync() => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>
/// Tests for <see cref="SessionStatusWingman"/>, the sole writer of
/// <see cref="Session.StatusColor"/>. The badge is a direct mapping from ActivityState:
/// Working/Starting -> blue, anything that means "your turn" -> red, gone -> gray. The one
/// overlay on the activity mapping (besides the Wingman's yellow/purple) is green "ready":
/// a brand-new session parked at its prompt before its first turn shows green, not red.
/// There is no other colour algorithm (no buffer scan, no byte-burst heuristic, no
/// turn-summary voting) - those were removed.
/// </summary>
public sealed class SessionStatusWingmanTests
{
    // ---------- The one state -> colour mapping ----------

    [Fact]
    public void New_session_maps_to_blue_session_created()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.Starting, isNew: true);
        Assert.Equal(StatusColor.Blue, color);
        Assert.Equal("session created", reason);
    }

    [Fact]
    public void Working_maps_to_blue()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.Working, isNew: false);
        Assert.Equal(StatusColor.Blue, color);
        Assert.Equal("working", reason);
    }

    [Fact]
    public void WaitingForInput_maps_to_red_needs_you()
    {
        // The timer's only "not working" state: silence past QuietThreshold -> needs you.
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.WaitingForInput, isNew: false);
        Assert.Equal(StatusColor.Red, color);
        Assert.Equal("needs you", reason);
    }

    [Fact]
    public void WaitingForPerm_maps_to_red_needs_you()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.WaitingForPerm, isNew: false);
        Assert.Equal(StatusColor.Red, color);
        Assert.Equal("needs you", reason);
    }

    [Fact]
    public void Idle_maps_to_red_needs_you()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.Idle, isNew: false);
        Assert.Equal(StatusColor.Red, color);
        Assert.Equal("needs you", reason);
    }

    [Fact]
    public void Exited_maps_to_unknown_with_reason()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.Exited, isNew: false);
        Assert.Equal(StatusColor.Unknown, color);
        Assert.Equal("exited", reason);
    }

    [Fact]
    public void Restored_session_starting_is_blue()
    {
        var (color, reason) = SessionStatusWingman.ColorFromActivityState(ActivityState.Starting, isNew: false);
        Assert.Equal(StatusColor.Blue, color);
        Assert.Equal("starting", reason);
    }

    // ---------- The voice-mode colour rule lived here, and is DELETED ----------
    // Five tests exercised SessionStatusWingman.VoiceColorFor. The method had ZERO production callers -
    // these tests were its only callers - and it carried the very bug the live Gateway rule had already
    // fixed: it held yellow while `!voiceAudioReady`, and a text-to-speech failure produces no audio, so
    // the session wedged yellow forever. SessionOrdering.IsVoicePreparing gates on VoiceGenerating ALONE
    // precisely to give the rule a terminal exit.
    //
    // So these tests were GREEN and they asserted the wedge: `VoiceMode_waiting_and_not_ready_is_yellow`
    // pinned "no audio yet -> yellow" as correct behaviour. They are deleted with the method rather than
    // corrected - there is nothing left to test, and the live rule is tested at SessionOrderingTests.
    // (docs/new_architecture/sessions.html - the traps.)

    // ---------- End-to-end: the timer flip drives the badge ----------

    // ---------- Former overlays are GONE (Phase 2.3): the Director maps ActivityState ONLY ----------
    // The transcribing (orange), wingman-reading (yellow: briefing + auto-explain), background-running
    // (purple), brand-new "ready" (green), and controlled-sub-agent (slate/supporting) overlays moved OUT
    // of the Director. The Director still reports the raw facts (IsTranscribing / IsExplaining /
    // BriefingState / IsBackgroundRunning / IsControlled / IsBrandNew) so a Gateway can fold them, but it
    // no longer turns any of them into a color: the standalone-desktop badge is purely blue (working) /
    // red (needs you) / gray (exited). These tests guard that each former-overlay flag no longer repaints
    // the Director color. They use the in-process BufferOnlyBackend (never auto-exits).

    [Fact]
    public void IsExplaining_no_longer_repaints_the_director_color()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.IsBrandNew = false;

            // Park at a red turn-end, then toggle the auto-explain flag: the color must NOT move to yellow.
            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.IsExplaining = true;
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.IsExplaining = false;
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void IsExplaining_while_working_stays_blue()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.Working);
            session.IsExplaining = true;
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Former transcribing (orange) overlay: no longer a Director color ----------

    [Fact]
    public void IsTranscribing_no_longer_repaints_the_director_color()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.IsBrandNew = false;

            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            // The Gateway now folds IsTranscribing into orange; the Director stays on its activity color.
            session.IsTranscribing = true;
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.IsTranscribing = false;
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void IsTranscribing_while_working_stays_blue()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, session.StatusColor);

            session.IsTranscribing = true;
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Former turn-brief (yellow) overlay: no longer a Director color ----------

    [Fact]
    public void BriefingState_no_longer_repaints_the_director_color()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.IsBrandNew = false;

            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            // The Gateway now folds BriefingState==Briefing into yellow; the Director stays activity-only.
            session.SetBriefingState(BriefingState.Briefing);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.SetBriefingState(BriefingState.Briefed);
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Former background-running (purple) overlay: no longer a Director color ----------

    [Fact]
    public void IsBackgroundRunning_no_longer_repaints_the_director_color()
    {
        // A session parked at WaitingForInput is red "needs you". Setting the background-running
        // verdict no longer paints purple on the Director; the Gateway folds that from the raw fact.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.IsBrandNew = false;

            session.ApplyTerminalActivityState(ActivityState.Working);
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.SetBackgroundRunning(true, "build still running");
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.SetBackgroundRunning(false);
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Former controlled-sub-agent (supporting/slate) overlay: no longer a Director color ----------
    // A controlled sub-agent (issue #815) used to recede to slate "Supporting" while its controller drove
    // it. That overlay moved to the Gateway (it folds IsControlled + ControllerSessionId). The Director now
    // paints a controlled sub-agent by its own plain activity color, and no longer repaints children when
    // their controller exits (the RecomputeControlledChildren color machinery is gone). The raw facts
    // IsControlled / ControllerSessionId are still set on the Session for the Gateway to fold.

    [Fact]
    public void Controlled_subagent_while_working_is_plain_blue_not_supporting()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (controller, _) = CreateBufferSession(manager);   // the controlling session, alive
            var (child, _) = CreateBufferSession(manager);
            child.ControllerSessionId = controller.Id;
            Assert.True(child.IsControlled); // the raw fact the Gateway folds is still set
            child.IsBrandNew = false;

            child.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, child.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Controlled_subagent_waiting_is_plain_red()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (controller, _) = CreateBufferSession(manager);
            var (child, _) = CreateBufferSession(manager);
            child.ControllerSessionId = controller.Id;
            child.IsBrandNew = false;

            child.ApplyTerminalActivityState(ActivityState.Working);
            child.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, child.StatusColor);
            Assert.Equal("needs you", child.LastStatusReason);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Controller_exit_does_not_repaint_the_child()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (controller, _) = CreateBufferSession(manager);
            var (child, _) = CreateBufferSession(manager);
            child.ControllerSessionId = controller.Id;
            child.IsBrandNew = false;

            // The child paints by its own activity color, and stays there when its controller exits
            // (no Director-side child repaint anymore - the Gateway owns the supporting fold).
            child.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, child.StatusColor);

            controller.ApplyTerminalActivityState(ActivityState.Exited);
            Assert.Equal(StatusColor.Blue, child.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Uncontrolled_session_working_is_blue()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            Assert.False(session.IsControlled);

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void CreateSession_stamps_ControllerSessionId_and_IsControlled()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var controllerId = Guid.NewGuid();
            var session = manager.CreateSession(
                Path.GetTempPath(), AgentKind.ClaudeCode, null, SessionBackendType.ConPty,
                resumeSessionId: null, controllerSessionId: controllerId);

            Assert.Equal(controllerId, session.ControllerSessionId);
            Assert.True(session.IsControlled);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void Wingman_paints_blue_on_working_and_red_on_waiting_for_input()
    {
        // The whole detection algorithm, exercised through the public state writer the
        // TerminalStateDetector uses: bytes -> Working -> blue; QuietThreshold of silence
        // -> WaitingForInput -> red ("needs you").
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var session = manager.CreateSession(Path.GetTempPath());
            // Past the first turn: red "needs you" at a turn-end only applies once the
            // session is no longer brand-new (a brand-new session is green "ready").
            session.IsBrandNew = false;

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, session.StatusColor);
            Assert.Equal("working", session.LastStatusReason);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Session model writes ----------

    [Fact]
    public void Session_starts_blue_at_construction()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            Assert.Equal(StatusColor.Blue, session.StatusColor);
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

            session.SetStatusColor(StatusColor.Red, "needs you");
            Assert.Equal("blue->red:needs you", captured);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void WingmanEventLog_records_each_color_change_newest_first()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            session.SetStatusColor(StatusColor.Blue, "working");
            session.SetStatusColor(StatusColor.Red, "needs you");
            session.SetStatusColor(StatusColor.Blue, "working again");

            var events = session.RecentWingmanEvents;
            Assert.NotEmpty(events);
            Assert.Equal("blue", events[0].NewColor);
            Assert.Equal("red", events[1].NewColor);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void ClearWingmanContext_clears_status_events_and_replay_buffer()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var (session, _) = CreateBufferSession(manager);
            session.SetStatusColor(StatusColor.Red, "needs you before /clear");
            session.Buffer!.Write(new byte[] { 1, 2, 3, 4 });
            Assert.NotEmpty(session.RecentWingmanEvents);
            Assert.NotEmpty(session.Buffer!.DumpAll());

            session.ClearWingmanContext();

            Assert.Empty(session.RecentWingmanEvents);
            Assert.Empty(session.Buffer!.DumpAll());
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void WingmanEventLog_caps_at_50_entries()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            for (int i = 0; i < 80; i++)
            {
                var c = (i % 2 == 0) ? StatusColor.Blue : StatusColor.Red;
                session.SetStatusColor(c, $"tick {i}");
            }
            Assert.Equal(50, session.RecentWingmanEvents.Count);
            Assert.Equal("tick 79", session.RecentWingmanEvents[0].Reason);
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
            session.SetStatusColor(StatusColor.Blue, "session created");
            Assert.Equal(0, fires);

            // Different reason fires even if the color is the same.
            session.SetStatusColor(StatusColor.Blue, "working");
            Assert.Equal(1, fires);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SnapshotScreenRows_returns_the_rendered_grid_text()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            if (session.Buffer is null) return; // no grid (Embedded backend); skip
            session.Buffer.Write(System.Text.Encoding.UTF8.GetBytes("HELLO_GRID_MARKER_42"));

            var rows = session.SnapshotScreenRows();

            Assert.NotEmpty(rows);
            Assert.Contains(rows, r => r.Contains("HELLO_GRID_MARKER_42"));
        }
        finally { manager.Dispose(); }
    }

    // ---------- Wingman lifecycle ----------

    [Fact]
    public void Wingman_Start_sets_existing_sessions_to_color_matching_their_state()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            // Wingman.Start wires sessions restored from persistence on Director boot.
            // Restored sessions already have history, so they are not brand-new: a turn-end
            // maps to red "needs you" (a brand-new session would be green "ready").
            session.IsBrandNew = false;

            var wingman = new SessionStatusWingman(manager);
            wingman.Start();
            try
            {
                // The session is parked WaitingForInput ("your turn"); the wingman maps that
                // to red because it is no longer brand-new.
                Assert.Equal(StatusColor.Red, session.StatusColor);
            }
            finally { wingman.Dispose(); }
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void Wingman_pill_goes_gray_on_real_session_end()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var session = manager.CreateSession(Path.GetTempPath());
            // A real session end is surfaced by the detector as the Exited state; the
            // wingman maps that to gray ("unknown" colour).
            session.ApplyTerminalActivityState(ActivityState.Exited);
            Assert.Equal(StatusColor.Unknown, session.StatusColor);
            Assert.Equal(ActivityState.Exited, session.ActivityState);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Wingman_OnSessionCreated_writes_red_needs_you()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var wingman = new SessionStatusWingman(manager);
            wingman.Start();
            try
            {
                var session = manager.CreateSession(Path.GetTempPath());
                // Phase 2.3: the brand-new "green ready" overlay is gone from the Director. A brand-new
                // session is born WaitingForInput, which the dumb standalone map paints red "needs you".
                // The Gateway still folds a green "ready" for brand-new sessions from the IsBrandNew raw
                // fact, which the Session below still reports.
                Assert.Equal(StatusColor.Red, session.StatusColor);
                Assert.Equal("needs you", session.LastStatusReason);
                Assert.True(session.IsBrandNew); // the raw fact the Gateway folds is still set
            }
            finally { wingman.Dispose(); }
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void Brand_new_session_is_red_then_blue_then_red_across_first_turn()
    {
        // Reduced standalone lifecycle (no green "ready" overlay): red "needs you" on startup ->
        // blue "working" once the user submits (which clears IsBrandNew) -> red "needs you" when the
        // first turn ends.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);

            // First submit clears IsBrandNew and drives Working.
            session.SendInput(new byte[] { 0x0A });
            Assert.False(session.IsBrandNew);
            Assert.Equal(StatusColor.Blue, session.StatusColor);

            // Turn ends: red "needs you".
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);
            Assert.Equal("needs you", session.LastStatusReason);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Session teardown: the class must not outlive the sessions it watched ----------
    //
    // This class subscribed to OnSessionCreated and never to OnSessionRemoved, so both of its
    // per-session dictionaries grew for the life of the Director. The activity-handler entry is a
    // closure that CAPTURES the Session, so each dead entry rooted the whole Session - its backend,
    // its 2 MB terminal buffer, and both AnsiParsers with 5,000 scrollback rows each. A heap dump of
    // a Director at 58 hours uptime found 146 Sessions retained for 9 live: ~1.7 GB, the bulk of the
    // process heap. Its siblings (TerminalStateDetector, TransientErrorAutoResume) always hooked
    // removal and sat correctly at the live count.
    //
    // These assert the COUNT, not that a teardown method ran. A test that only checked "the removal
    // handler fired" would pass against a handler that removed from the wrong dictionary.

    [Fact]
    public void Removing_a_session_releases_both_per_session_entries()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            Assert.Equal(1, wingman.TrackedHandlerCount);
            Assert.Equal(1, wingman.TrackedWatcherCount);

            manager.RemoveSession(session.Id);

            Assert.Equal(0, wingman.TrackedHandlerCount);
            Assert.Equal(0, wingman.TrackedWatcherCount);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Tracked_state_matches_the_live_session_count_across_churn()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();

            // Churn many sessions through, keeping only the last three alive. Before the fix this
            // ended at 12 tracked handlers for 3 live sessions - the leak in miniature.
            var live = new List<Session>();
            for (int i = 0; i < 12; i++)
            {
                var (s, _) = CreateBufferSession(manager);
                live.Add(s);
                if (live.Count > 3)
                {
                    var doomed = live[0];
                    live.RemoveAt(0);
                    manager.RemoveSession(doomed.Id);
                }
            }

            Assert.Equal(3, manager.ListSessions().Count);
            Assert.Equal(3, wingman.TrackedHandlerCount);
            Assert.Equal(3, wingman.TrackedWatcherCount);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Dispose_clears_entries_for_sessions_that_already_ended()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            Assert.Equal(1, wingman.TrackedHandlerCount);

            // Dispose() used to walk ListSessions() - the LIVE sessions - which is precisely the set
            // that does NOT need clearing. A session removed first was therefore unreachable from
            // the teardown loop and survived a full Director shutdown.
            manager.RemoveSession(session.Id);
            wingman.Dispose();

            Assert.Equal(0, wingman.TrackedHandlerCount);
            Assert.Equal(0, wingman.TrackedWatcherCount);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Prompt-injection watcher (end-to-end via real buffer) ----------

    private static (Session session, BufferOnlyBackend backend) CreateBufferSession(SessionManager manager)
    {
        var backend = new BufferOnlyBackend();
        var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        // These tests exercise the Wingman status overlays (Yellow/Purple), so opt the
        // session into the Wingman experience. The new-session default is OFF; the
        // "suppressed when disabled" cases flip it back to false explicitly.
        session.WingmanEnabled = true;
        return (session, backend);
    }

    // Rewritten deterministically for #264. The old version wrote bytes and slept 1500ms,
    // relying on the byte-arrival debounce to fire one scan that happened to read a resolved
    // grid - it raced both the grid resolution and (when asserting PendingPromptText) the
    // session's own source="user" write, so it was [Fact(Skip)]. This version:
    //   1. Drives the internal PromptInjectionWatcher directly (reachable via InternalsVisibleTo)
    //      so there is no spurious byte-arrival scan to race the explicit one.
    //   2. CONFIRMS the grid is resolved BEFORE triggering: it asserts the same extractor the
    //      watcher uses already yields the expected text from the snapshotted grid+cursor. The
    //      single scan therefore reads a grid that is known to produce a push - no nondeterminism.
    //   3. Fires exactly ONE scan via the existing RequestImmediateScan() seam, and waits on the
    //      "wingman"-source event through a TaskCompletionSource (signalled by the push, not a
    //      fixed Task.Delay-then-assert). The safety timeout only guards against a hang.
    //   4. Asserts on the captured "wingman"-source value, NOT PendingPromptText (which the
    //      session also writes with source="user", the original race).
    [Fact]
    public async Task PromptInjectionWatcher_pushes_extracted_text_via_wingman_source()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        // BufferOnlyBackend gives a real grid-backed buffer that never auto-exits, so the
        // session stays alive and the snapshot is stable for the whole test.
        var (session, _) = CreateBufferSession(manager);
        var buffer = session.Buffer;
        Assert.NotNull(buffer);

        const string expected = "commit the cc-playwright changes too";

        // CRLF: a real PTY resets the column on CR. The grid-aware extractor reads the
        // resolved grid, so the mode line must land at column 0.
        var frame =
            "\r\n\r\n" +
            "> commit the cc-playwright changes too\r\n" +
            "  >> bypass permissions on (shift+tab to cycle)\r\n";
        buffer!.Write(System.Text.Encoding.UTF8.GetBytes(frame));

        // CONFIRM the grid is resolved BEFORE we trigger the scan. This is the crux of the
        // determinism fix: we assert that the exact inputs the watcher's tick will read
        // (the snapshotted rows + cursor) already extract to the expected text. If this
        // holds, the single scan below CANNOT read a not-yet-yielding grid.
        var (rows, cursorRow, cursorCol) = session.SnapshotScreenRowsWithCursor();
        var extractedNow = PromptInputLineExtractor.ExtractUserAuthoredInput(rows, cursorRow, cursorCol);
        Assert.Equal(expected, extractedNow);

        // Capture the "wingman"-source push via a TaskCompletionSource so we await the actual
        // event rather than sleeping a fixed interval. We deliberately do NOT assert on
        // PendingPromptText: the session also writes it with source="user", which raced the
        // original assertion.
        var pushed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPendingPromptTextChanged += (text, source) =>
        {
            if (source == "wingman")
                pushed.TrySetResult(text);
        };

        var watcher = new PromptInjectionWatcher(session, buffer);
        try
        {
            watcher.Start();

            // Drive exactly one scan against the now-confirmed-resolved grid.
            watcher.RequestImmediateScan();

            // Wait for the push event itself. The timeout is only a hang guard; on success
            // the await completes the instant the scan fires, with no fixed delay.
            var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(completed == pushed.Task, "Timed out waiting for the wingman-source push.");

            Assert.Equal(expected, await pushed.Task);
        }
        finally { watcher.Dispose(); manager.Dispose(); }
    }

    // ---------- Brand-new session gate ----------

    [Fact]
    public void New_session_is_brand_new_until_user_submits()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            Assert.True(session.IsBrandNew);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task SendTextAsync_clears_IsBrandNew()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var (session, _) = CreateBufferSession(manager);
            Assert.True(session.IsBrandNew);
            await session.SendTextAsync("hello");
            Assert.False(session.IsBrandNew);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SendInput_with_submit_byte_clears_IsBrandNew()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var (session, _) = CreateBufferSession(manager);
            Assert.True(session.IsBrandNew);
            // Submit byte (LF) flips the gate.
            session.SendInput(new byte[] { 0x0A });
            Assert.False(session.IsBrandNew);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void SendInput_without_submit_byte_keeps_IsBrandNew()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var (session, _) = CreateBufferSession(manager);
            // A bare keystroke (single 'a') is the user composing - not a submitted turn.
            session.SendInput(new byte[] { (byte)'a' });
            Assert.True(session.IsBrandNew);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void Wingman_seeds_brand_new_session_with_canned_explain()
    {
        // SessionStatusWingman.WireSession populates CachedExplainText with a canned
        // greeting on new sessions so the Wingman tab has content immediately, with no
        // Opus call.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var session = manager.CreateSession(Path.GetTempPath());
            Assert.NotNull(session.CachedExplainText);
            Assert.Contains("brand new session", session.CachedExplainText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("system", session.CachedExplainModel);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void SetCachedExplain_fires_OnCachedExplainChanged()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateSession(Path.GetTempPath());
            int fires = 0;
            session.OnCachedExplainChanged += () => fires++;

            session.SetCachedExplain("hello", "opus");
            Assert.Equal(1, fires);
            Assert.Equal("hello", session.CachedExplainText);

            // Empty/whitespace input is ignored and does not fire.
            session.SetCachedExplain("", "opus");
            session.SetCachedExplain("   ", "opus");
            Assert.Equal(1, fires);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task PromptInjectionWatcher_does_not_double_push_same_text()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var session = manager.CreateSession(Path.GetTempPath());
            if (session.Buffer is null) return;

            int pushCount = 0;
            session.OnPendingPromptTextChanged += (_, source) =>
            {
                if (source == "wingman") pushCount++;
            };

            var frame =
                "\r\n\r\n" +
                "> commit the cc-playwright changes too\r\n" +
                "  >> bypass permissions on (shift+tab to cycle)\r\n";
            session.Buffer.Write(System.Text.Encoding.UTF8.GetBytes(frame));
            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            // Append unrelated noise; the frame at the tail is unchanged.
            session.Buffer.Write(System.Text.Encoding.UTF8.GetBytes(
                "some background log line\r\n" + frame));
            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            Assert.Equal(1, pushCount);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }
}
