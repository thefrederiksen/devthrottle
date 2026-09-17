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

        public Func<string, Task> OnSend { get; set; } = _ => Task.CompletedTask;

        public async Task SendLineAsync(string line)
        {
            Events.Add("send");
            SentLines.Add(line);
            await OnSend(line);
        }
    }

    private static readonly Func<Task> NoPause = () => Task.CompletedTask;

    private static ScreenFrame Idle => DoorbellCaptures.Load("claude-idle-empty-after-turn");

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
        var target = new ScriptedTarget(AgentKind.ClaudeCode, Idle, Idle, Idle);

        var answer = await FleetDoorbellRinger.RingAsync(target, 2, NoPause);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        Assert.Equal([FleetDoorbellLine.For(2)], target.SentLines);
        // The check's two frames, then the last look (state and a third frame), then - with nothing between -
        // the send.
        var send = target.Events.IndexOf("send");
        Assert.Equal(["exited?", "working?", "frame"], target.Events.Skip(send - 3).Take(3));
        Assert.Equal(3, target.Events.Take(send).Count(e => e == "frame"));
    }
}
