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
        public bool HasTerminalGrid => true;
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

        public List<int> Erased { get; } = new();

        public Task<DoorbellSubmitOutcome> SubmitLineAsync(string line, Func<bool> mayTypeNow, Func<bool> composerShowsLine, Func<bool> turnStarted)
        {
            Events.Add("submit");
            if (!mayTypeNow()) return Task.FromResult(DoorbellSubmitOutcome.NotTyped);
            Events.Add("send");
            SentLines.Add(line);
            return Task.FromResult(AfterTyping(composerShowsLine, turnStarted));
        }

        public Task EraseAsync(int characters)
        {
            Events.Add("erase");
            Erased.Add(characters);
            return Task.CompletedTask;
        }
    }

    private static readonly Func<Task> NoPause = () => Task.CompletedTask;

    private static ScreenFrame Idle => DoorbellCaptures.Load("claude-idle-empty-after-turn");

    private const int PromptRow = 27;
    private const int TranscriptRow = 23;

    /// <summary>The idle screen with the composer holding <paramref name="text"/>.</summary>
    private static ScreenFrame Composer(string text) => DoorbellCaptures.WithRow(Idle, PromptRow, "❯ " + text);

    /// <summary>The idle screen after the line was submitted: the transcript shows it, the composer holds
    /// <paramref name="composer"/> (empty by default).</summary>
    private static ScreenFrame Submitted(string line, string composer = "")
    {
        var f = DoorbellCaptures.WithRow(Idle, TranscriptRow, "❯ " + line);
        return composer.Length == 0 ? f : DoorbellCaptures.WithRow(f, PromptRow, "❯ " + composer);
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
        Assert.Empty(target.Erased);
    }

    [Fact]
    public async Task A_swallowed_Enter_leaves_our_line_which_is_erased_and_not_counted()
    {
        // The line sits in the composer after the one Enter; nothing is pressed again. It is exactly our line,
        // so the Director takes it back, and the composer then reads empty.
        var parked = Composer(Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, parked, parked, Idle)
        {
            AfterTyping = (_, started) => started() ? DoorbellSubmitOutcome.Verified : DoorbellSubmitOutcome.NotVerified,
        };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("not-submitted", answer.Reason);
        Assert.Equal([Line.Length], target.Erased);
    }

    [Fact]
    public async Task A_line_that_wrapped_in_the_composer_is_still_recognised_as_ours()
    {
        var wrapped = DoorbellCaptures.WithRow(
            DoorbellCaptures.WithRow(Idle, PromptRow - 1, "❯ " + Line[..40]), PromptRow, "  " + Line[40..]);
        // Row 26 is the upper rule in the capture, so the wrapped block is rebuilt one row higher.
        wrapped = DoorbellCaptures.WithRow(wrapped, PromptRow - 2, new string('─', 120));
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, wrapped, wrapped, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal("not-submitted", answer.Reason);
        Assert.Equal([Line.Length], target.Erased);
    }

    [Fact]
    public async Task A_line_the_interface_cleared_without_a_turn_is_not_counted_and_nothing_is_erased()
    {
        // After the Enter the composer is empty but the transcript shows no new doorbell and nothing is working.
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("not-submitted", answer.Reason);
        Assert.Empty(target.Erased);
    }

    [Fact]
    public async Task Owner_text_typed_after_an_unsubmitted_line_is_left_untouched()
    {
        var both = Composer(Line + " and the owner's words");
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, both);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal("composer-holds-text", answer.Reason);
        Assert.Empty(target.Erased);
    }

    [Fact]
    public async Task Owner_text_typed_after_a_submitted_line_does_not_hide_the_submit()
    {
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, Submitted(Line, composer: "hello"));

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        Assert.Empty(target.Erased);
    }

    [Fact]
    public async Task A_line_still_in_the_composer_is_not_a_submit_even_with_an_older_doorbell_in_the_transcript()
    {
        // An earlier doorbell is on screen before this ring; this one is still parked. The count must not be
        // fooled by the old row, and the parked one does not count because it is in the composer.
        var before = Submitted(Line);
        var parked = DoorbellCaptures.WithRow(before, PromptRow, "❯ " + Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, before, before, before, parked, parked, before);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal("not-submitted", answer.Reason);
        Assert.Equal([Line.Length], target.Erased);
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
    public async Task An_erase_that_does_not_empty_the_composer_is_reported_as_text()
    {
        var parked = Composer(Line);
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle, parked, parked);

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, NoPause);

        Assert.Equal("composer-holds-text", answer.Reason);
        Assert.Equal([Line.Length], target.Erased);
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
    public void The_not_submitted_reason_is_the_literal_the_Gateway_reads()
    {
        Assert.Equal("not-submitted", FleetRingDeferReasons.NotSubmitted);
    }
}
