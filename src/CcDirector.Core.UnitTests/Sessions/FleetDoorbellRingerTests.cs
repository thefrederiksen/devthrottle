using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The doorbell's Director half, step by step, against a scripted session (the Message Load mission, slice 2
/// fix round). The screens are the real captures in TestData/doorbell.
/// </summary>
public sealed class FleetDoorbellRingerTests
{
    /// <summary>A session whose frames, state and send are scripted, and which records the order of every look.</summary>
    internal sealed class ScriptedTarget : IDoorbellTarget
    {
        private readonly Queue<ScreenFrame> _frames;
        private ScreenFrame _last;

        public ScriptedTarget(AgentKind agent, params ScreenFrame[] frames)
        {
            Agent = agent;
            _frames = new Queue<ScreenFrame>(frames);
            _last = frames[0];
        }

        public Guid Id { get; } = Guid.NewGuid();
        public AgentKind Agent { get; }
        public List<string> Events { get; } = new();
        public List<string> SentLines { get; } = new();

        /// <summary>The Director's working state on the Nth read (1-based); false when absent.</summary>
        public Func<int, bool> WorkingOnRead { get; set; } = _ => false;
        private int _workingReads;

        /// <summary>The exited state on the Nth read (1-based); false when absent.</summary>
        public Func<int, bool> ExitedOnRead { get; set; } = _ => false;
        private int _exitedReads;

        public bool Exited { get { Events.Add("exited?"); return ExitedOnRead(++_exitedReads); } }
        public bool DirectorSaysWorking { get { Events.Add("working?"); return WorkingOnRead(++_workingReads); } }
        public bool HasTerminalGrid { get; set; } = true;
        public bool ProductMayHaveLeftText => false;

        public ScreenFrame TakeFrame()
        {
            Events.Add("frame");
            if (_frames.Count > 0) _last = _frames.Dequeue();
            return _last;
        }

        /// <summary>What happens once the line is typed; by default the real rule - verified when the screen
        /// shows the turn at the first look, otherwise not.</summary>
        public Func<Func<bool>, Func<bool>, DoorbellSubmitOutcome> AfterTyping { get; set; } =
            (_, started) => started() ? DoorbellSubmitOutcome.Verified : DoorbellSubmitOutcome.NotVerified;

        /// <summary>Every event after the line was sent: a parked line may only be looked at, never written to.</summary>
        public IEnumerable<string> AfterSend => Events.Skip(Events.IndexOf("send") + 1);

        public Task<DoorbellSubmitOutcome> SubmitLineAsync(string line, Func<bool> mayTypeNow, Func<bool> composerShowsLine, Func<bool> turnStarted)
        {
            Events.Add("submit");
            if (!mayTypeNow()) return Task.FromResult(DoorbellSubmitOutcome.NotTyped);
            Events.Add("send");
            SentLines.Add(line);
            return Task.FromResult(AfterTyping(composerShowsLine, turnStarted));
        }
    }

    private static readonly Func<Task> NoPause = () => Task.CompletedTask;

    /// <summary>After the one send, the ringer only read the screen: nothing was typed, erased or pressed.</summary>
    private static void AssertOnlyLooked(ScriptedTarget target)
    {
        Assert.Single(target.SentLines);
        Assert.All(target.AfterSend, e => Assert.Equal("frame", e));
    }

    /// <summary>The answer is <c>deferred, parked</c> and the composer was left exactly as it was.</summary>
    private static void AssertParked(FleetRingResponse answer, ScriptedTarget target)
    {
        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("parked", answer.Reason);
        AssertOnlyLooked(target);
    }

    private static ScreenFrame Idle => DoorbellCaptures.Load("claude-idle-empty-after-turn");

    private const int PromptRow = 27;
    private const int TranscriptRow = 23;

    /// <summary>The idle screen with the composer holding <paramref name="text"/> and the cursor straight after it,
    /// as the terminal shows a typed line: the row trailing-trimmed, the cursor <paramref name="cursorPastText"/>
    /// columns beyond the last visible character.</summary>
    private static ScreenFrame Composer(string text, int cursorPastText = 0) => Parked(Idle, text, cursorPastText);

    private static ScreenFrame Parked(ScreenFrame on, string text, int cursorPastText = 0)
    {
        var row = "❯ " + text;
        return DoorbellCaptures.WithRow(on, PromptRow, row.TrimEnd()) with
        {
            CursorRow = PromptRow,
            CursorCol = row.TrimEnd().Length + cursorPastText,
            CursorVisible = true,
        };
    }

    /// <summary>The idle screen after the line was submitted: the transcript shows it, the composer holds
    /// <paramref name="composer"/> (empty by default).</summary>
    private static ScreenFrame Submitted(string line, string composer = "")
    {
        var f = DoorbellCaptures.WithRow(Idle, TranscriptRow, "❯ " + line);
        return composer.Length == 0 ? f : DoorbellCaptures.WithRow(f, PromptRow, "❯ " + composer);
    }

    // ---------- An unreadable screen gets one frame and no pause ----------

    /// <summary>A pause that counts how often the ringer waited between frames.</summary>
    private sealed class CountingPause
    {
        public int Calls { get; private set; }
        public Task Wait() { Calls++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task A_session_with_no_terminal_grid_is_deferred_as_unreadable_after_exactly_one_frame()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle) { HasTerminalGrid = false };
        var pause = new CountingPause();

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, pause.Wait);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("screen-unreadable", answer.Reason);
        Assert.Equal("the session has no rendered terminal to check", answer.Detail);
        Assert.Equal(1, target.Events.Count(e => e == "frame"));
        Assert.Equal(0, pause.Calls);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task A_first_frame_with_no_rows_is_deferred_as_unreadable_after_exactly_one_frame()
    {
        var empty = new ScreenFrame([], 0, 0, false);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, empty, Idle, Idle);
        var pause = new CountingPause();

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, pause.Wait);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("screen-unreadable", answer.Reason);
        Assert.Equal(1, target.Events.Count(e => e == "frame"));
        Assert.Equal(0, pause.Calls);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task An_exited_session_with_no_grid_is_still_deferred_as_exited()
    {
        // The check tests "exited" before "unreadable"; taking one frame must not change which answer wins.
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle) { HasTerminalGrid = false, ExitedOnRead = _ => true };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, new CountingPause().Wait);

        Assert.Equal("exited", answer.Reason);
        Assert.Equal(1, target.Events.Count(e => e == "frame"));
    }

    [Fact]
    public async Task A_readable_screen_still_gets_two_frames_with_the_pause_between_them()
    {
        // The one-frame shortcut is only for a screen the check cannot read. A readable one - here a working
        // screen, deferred on its frames - still takes the first frame, the pause, then the second frame.
        var working = DoorbellCaptures.Load("claude-working");
        var target = new ScriptedTarget(AgentKind.ClaudeCode, working, working);
        var pause = new CountingPause();

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, pause.Wait);

        Assert.Equal("working", answer.Reason);
        Assert.Equal(2, target.Events.Count(e => e == "frame"));
        Assert.Equal(1, pause.Calls);
    }

    // ---------- Ruling 1: the last look, immediately before the first byte ----------

    [Fact]
    public async Task A_turn_that_starts_after_the_second_frame_is_not_typed_into()
    {
        // The check approves two idle frames; by the third the agent is mid-turn.
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, DoorbellCaptures.Load("claude-working"));

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("working", answer.Reason);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task The_Director_turning_working_after_the_check_stops_the_send()
    {
        // The screen never moves (a static thinking screen with no marker); the Director's own state flips on
        // its second read, which is the last look.
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle) { WorkingOnRead = n => n >= 2 };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("working", answer.Reason);
        Assert.Contains("after the check", answer.Detail);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task A_keystroke_that_lands_after_the_check_stops_the_send()
    {
        var prompt = DoorbellCaptures.LastRowStartingWith(Idle, "❯");
        var typed = DoorbellCaptures.WithRow(Idle, prompt, "❯ h");
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, typed);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("working", answer.Reason);
        Assert.Contains("screen changed", answer.Detail);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task A_cursor_that_moved_after_the_check_stops_the_send()
    {
        var moved = Idle with { CursorCol = Idle.CursorCol + 1 };
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, moved);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task A_session_that_exits_after_the_check_is_never_typed_into()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle) { ExitedOnRead = n => n >= 2 };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("exited", answer.Reason);
        Assert.Empty(target.SentLines);
    }

    [Fact]
    public async Task An_unchanged_session_is_rung_and_the_last_look_comes_immediately_before_the_send()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Submitted(FleetDoorbellLine.For(2)));

        var answer = await FleetDoorbellRinger.RingAsync(target, 2, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        Assert.Equal([FleetDoorbellLine.For(2)], target.SentLines);
        // The check's two frames, then the last look (state and a third frame), then - with nothing between -
        // the send.
        var send = target.Events.IndexOf("send");
        Assert.Equal(["submit", "exited?", "working?", "frame", "send"], target.Events.Skip(send - 4).Take(5));
        Assert.Equal(3, target.Events.Take(send).Count(e => e == "frame"));
    }

    // ---------- Ruling 2: one Enter, and rung only when the submit was verified ----------

    private static readonly string Line = FleetDoorbellLine.For(1);

    [Fact]
    public async Task A_submit_the_screen_shows_is_rung()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Submitted(Line));

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        AssertOnlyLooked(target);
    }

    // ---------- Inspection 8, ruling 1: the doorbell never erases ----------

    [Fact]
    public async Task A_swallowed_Enter_leaves_our_line_parked_and_not_counted()
    {
        // The line sits in the composer after the one Enter; nothing is pressed again and nothing is erased.
        var parked = Composer(Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, parked, parked, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Theory]
    [MemberData(nameof(OwnerWhitespace))]
    public async Task A_trimmed_owner_character_that_did_not_move_the_cursor_is_left_parked_and_never_erased(string added)
    {
        // Inspection 8, finding 1: the owner's character is in the composer, the grid trimmed it, and the cursor
        // did not advance. The frame is identical to the bare line's, so no frame can tell them apart - which is
        // why nothing is ever erased.
        var indistinguishable = Composer(Line + added, cursorPastText: 0);
        Assert.True(FleetDoorbellRinger.SameFrame(Composer(Line), indistinguishable));
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, indistinguishable, indistinguishable, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task A_second_ring_on_a_parked_line_is_deferred_as_composer_text()
    {
        var parked = Composer(Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, parked, parked, parked);

        var answer = await FleetDoorbellRinger.RingAsync(target, 2, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("composer-holds-text", answer.Reason);
        Assert.Empty(target.SentLines);
        Assert.DoesNotContain("submit", target.Events);
    }

    /// <summary>The idle screen with the composer block one row taller: the prompt row holds
    /// <paramref name="first"/>, the continuation row holds <paramref name="second"/>, and the cursor sits
    /// <paramref name="cursorPastText"/> columns after the continuation row's last visible character.</summary>
    private static ScreenFrame Wrapped(string first, string second, int cursorPastText = 0)
    {
        var wrapped = DoorbellCaptures.WithRow(
            DoorbellCaptures.WithRow(Idle, PromptRow - 1, ("❯ " + first).TrimEnd()), PromptRow, second.TrimEnd());
        // Row 26 is the upper rule in the capture, so the wrapped block is rebuilt one row higher.
        wrapped = DoorbellCaptures.WithRow(wrapped, PromptRow - 2, new string('─', 120));
        return wrapped with { CursorRow = PromptRow, CursorCol = second.TrimEnd().Length + cursorPastText, CursorVisible = true };
    }

    [Fact]
    public async Task A_line_that_wrapped_in_the_composer_is_left_parked()
    {
        var wrapped = Wrapped(Line[..40], "  " + Line[40..]);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, wrapped, wrapped, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task A_line_word_wrapped_at_a_space_is_left_parked()
    {
        // A word wrap consumes the space it breaks at: the upper row ends before it, the lower row starts after it.
        var at = Line.IndexOf(' ', 40);
        var wrapped = Wrapped(Line[..at], "  " + Line[(at + 1)..]);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, wrapped, wrapped, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    public static TheoryData<string> OwnerWhitespace => new() { " ", "\t", "\u00A0" };

    [Theory]
    [MemberData(nameof(OwnerWhitespace))]
    public async Task A_line_the_owner_extended_by_one_whitespace_character_is_left_parked(string added)
    {
        // Inspection 5, finding 1. The terminal trims the row, so the row reads as the bare line; the cursor one
        // column further right is what the owner's character left behind.
        var extended = Composer(Line + added, cursorPastText: 1);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, extended, extended, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Theory]
    [MemberData(nameof(OwnerWhitespace))]
    public async Task A_line_the_owner_extended_by_one_whitespace_character_is_left_parked_when_the_row_keeps_it(string added)
    {
        // The same, with the character still on the row and the cursor after it.
        var row = "❯ " + Line + added;
        var extended = DoorbellCaptures.WithRow(Idle, PromptRow, row) with
        {
            CursorRow = PromptRow, CursorCol = row.Length, CursorVisible = true,
        };
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, extended, extended, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Theory]
    [MemberData(nameof(OwnerWhitespace))]
    public async Task A_wrapped_line_the_owner_extended_by_one_whitespace_character_is_left_parked(string added)
    {
        var extended = Wrapped(Line[..40], "  " + Line[40..] + added, cursorPastText: 1);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, extended, extended, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task A_space_the_owner_put_inside_the_line_is_left_parked()
    {
        // The squeezed comparison this replaces read "doorbell]  1" as the line.
        var inner = Line.Replace("] 1", "]  1", StringComparison.Ordinal);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Composer(inner), Composer(inner), Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task A_new_line_the_owner_added_after_the_line_is_left_parked()
    {
        // Shift+Enter after the parked line: an empty continuation row holding the cursor.
        var withBreak = Wrapped(Line, "") with { CursorCol = 2 };
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, withBreak, withBreak, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task A_line_the_interface_cleared_without_a_turn_is_not_counted_and_nothing_is_erased()
    {
        // After the Enter the composer is empty but the transcript shows no new doorbell and nothing is working.
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("not-submitted", answer.Reason);
        AssertOnlyLooked(target);
    }

    [Fact]
    public async Task Owner_text_typed_after_an_unsubmitted_line_is_left_untouched()
    {
        var both = Composer(Line + " and the owner's words");
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, both);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task Owner_text_typed_after_a_submitted_line_does_not_hide_the_submit()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Submitted(Line, composer: "hello"));

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        AssertOnlyLooked(target);
    }

    [Fact]
    public async Task A_line_still_in_the_composer_is_parked_even_with_an_older_doorbell_in_the_transcript()
    {
        // An earlier doorbell is on screen before this ring; this one is still parked. The count must not be
        // fooled by the old row, and the parked one does not count because it is in the composer.
        var before = Submitted(Line);
        var parked = Parked(before, Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, before, before, before, parked, parked, before);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        AssertParked(answer, target);
    }

    [Fact]
    public async Task An_older_doorbell_already_on_screen_does_not_prove_a_discarded_line_was_submitted()
    {
        // The transcript already shows an earlier doorbell. This line is typed, Enter pressed, and the interface
        // throws it away: the screen after is the screen before. One doorbell row is not "one more".
        var before = Submitted(Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, before, before, before, before);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("not-submitted", answer.Reason);
    }

    [Fact]
    public async Task A_screen_showing_the_working_marker_after_the_Enter_is_a_submit()
    {
        var working = DoorbellCaptures.WithRow(Idle, PromptRow + 2, "  ⏵⏵ bypass permissions on (shift+tab to cycle) · esc to interrupt");
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, working);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
    }

    [Fact]
    public void The_unverified_reasons_are_the_literals_the_Gateway_reads()
    {
        Assert.Equal("not-submitted", FleetRingDeferReasons.NotSubmitted);
        Assert.Equal("parked", FleetRingDeferReasons.Parked);
    }
}
