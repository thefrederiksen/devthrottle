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
/// Working/Starting -> blue, anything that means "your turn" -> red, gone -> gray. There
/// is no other colour algorithm (no buffer scan, no byte-burst heuristic, no turn-summary
/// voting) - those were removed.
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

    // ---------- End-to-end: the timer flip drives the badge ----------

    // ---------- Yellow overlay (Wingman auto-explain in flight) ----------
    // These tests use the in-process BufferOnlyBackend (never auto-exits) so the
    // Yellow rule is exercised without racing the cmd.exe spawn/exit lifecycle.

    [Fact]
    public void Yellow_only_when_wingman_enabled_and_explaining_and_at_turn_end()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            // Park at turn-end and flip IsExplaining (CreateBufferSession enabled Wingman).
            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.IsExplaining = true;
            Assert.Equal(StatusColor.Yellow, session.StatusColor);
            Assert.Equal("wingman is reading", session.LastStatusReason);

            session.IsExplaining = false;
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Yellow_does_not_apply_while_session_is_working()
    {
        // Yellow is the "your turn just ended, Wingman is reading" overlay. While the agent
        // is still producing bytes the dot must stay Blue even if IsExplaining is somehow
        // set (defensive: ProactiveExplainService only fires on WaitingForInput).
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

    [Fact]
    public void Yellow_suppressed_when_wingman_disabled()
    {
        // A WingmanEnabled=false session must never go Yellow -- it goes straight Blue->Red.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.WingmanEnabled = false;

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.IsExplaining = true;
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Yellow overlay (turn-brief pipeline, issue #192) ----------
    // The TurnBriefOrchestrator drives BriefingState around its read of a finished
    // turn. While Briefing the badge must be Yellow, not red "needs you" - until the
    // brief lands we do not know whether the session needs you.

    [Fact]
    public void Yellow_when_turn_brief_in_flight_at_turn_end()
    {
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.SetBriefingState(BriefingState.Briefing);
            Assert.Equal(StatusColor.Yellow, session.StatusColor);
            Assert.Equal("wingman is reading", session.LastStatusReason);

            // The brief lands: back to the activity-state verdict.
            session.SetBriefingState(BriefingState.Briefed);
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void TurnBrief_yellow_does_not_require_wingman_enabled()
    {
        // The TurnBriefOrchestrator briefs EVERY session, so the briefing yellow applies
        // regardless of WingmanEnabled (unlike the legacy IsExplaining overlay).
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.WingmanEnabled = false;

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.SetBriefingState(BriefingState.Briefing);
            Assert.Equal(StatusColor.Yellow, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void TurnBrief_yellow_does_not_apply_while_working()
    {
        // Watch-cancel: the user replied while the wingman was reading. The session is
        // Working again, so the dot must be Blue even while BriefingState is still
        // Briefing for a beat (defensive; the orchestrator cancels and resets to None).
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.SetBriefingState(BriefingState.Briefing);
            Assert.Equal(StatusColor.Yellow, session.StatusColor);

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    // ---------- Purple overlay (Wingman "running in background" verdict) ----------

    [Fact]
    public void Purple_when_background_running_and_at_turn_end()
    {
        // A session parked at WaitingForInput is normally red "needs you". When the Wingman
        // sets the background-running verdict, the badge becomes purple "running in background".
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            Assert.Equal(StatusColor.Red, session.StatusColor);

            session.SetBackgroundRunning(true, "build still running");
            Assert.Equal(StatusColor.Purple, session.StatusColor);
            Assert.Equal("build still running", session.LastStatusReason);

            // Clearing the verdict drops back to red "needs you".
            session.SetBackgroundRunning(false);
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Purple_released_when_output_resumes()
    {
        // The background-running overlay is released the instant real output resumes: the
        // Session clears IsBackgroundRunning on the transition off WaitingForInput, so a purple
        // session that starts producing bytes again goes blue, not purple.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.SetBackgroundRunning(true);
            Assert.Equal(StatusColor.Purple, session.StatusColor);

            session.ApplyTerminalActivityState(ActivityState.Working);
            Assert.False(session.IsBackgroundRunning);
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Purple_does_not_apply_while_session_is_working()
    {
        // Like Yellow, Purple is a turn-end overlay. While the agent is producing bytes the dot
        // must stay Blue even if the flag is somehow set (defensive).
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.Working);
            session.SetBackgroundRunning(true);
            Assert.Equal(StatusColor.Blue, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Yellow_takes_precedence_over_purple_while_explaining()
    {
        // While a briefing is in flight (IsExplaining) the transient Yellow "wingman is reading"
        // wins; the Purple verdict settles in once the briefing lifts.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.SetBackgroundRunning(true);
            session.IsExplaining = true;
            Assert.Equal(StatusColor.Yellow, session.StatusColor);

            session.IsExplaining = false;
            Assert.Equal(StatusColor.Purple, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void Purple_suppressed_when_wingman_disabled()
    {
        // A WingmanEnabled=false session never goes purple; it stays on the plain mapping (red).
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        var wingman = new SessionStatusWingman(manager);
        try
        {
            wingman.Start();
            var (session, _) = CreateBufferSession(manager);
            session.WingmanEnabled = false;

            session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
            session.SetBackgroundRunning(true);
            Assert.Equal(StatusColor.Red, session.StatusColor);
        }
        finally { wingman.Dispose(); manager.Dispose(); }
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

            var wingman = new SessionStatusWingman(manager);
            wingman.Start();
            try
            {
                // A freshly created session is born WaitingForInput ("your turn") since it
                // is literally sitting at Claude Code's prompt. The wingman maps that to red.
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
                // A brand-new session is born WaitingForInput, so the badge starts red.
                // Wingman is silenced separately (cached "brand new session" greeting +
                // ProactiveExplainService skipping IsBrandNew); no Opus call fires here.
                Assert.Equal(StatusColor.Red, session.StatusColor);
                Assert.Equal("needs you", session.LastStatusReason);
            }
            finally { wingman.Dispose(); }
        }
        finally { manager.Dispose(); }
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
