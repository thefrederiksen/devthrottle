using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The doorbell must ring an idle session while a background task or a Monitor runs, and must still defer a running
/// turn and the owner's unsent words (issues 3186 and 3289). The screens are real: Claude Code 2.1.283 in the Director's
/// own terminal on 27 September 2026, captured by the rig's --claude-doorbell-capture mode - see
/// TestData/doorbell/README.md. One is derived and says so: the idle footer with a stale "esc to interrupt", which the
/// current Claude Code no longer draws.
/// </summary>
public sealed class DoorbellIdleWithBackgroundTaskTests
{
    private static ScreenFrame Load(string name) => DoorbellCaptures.Load(name);

    private static DoorbellFacts Facts(ScreenFrame first, ScreenFrame second, bool working = false, TimeSpan? settled = null) => new(
        AgentKind.ClaudeCode,
        Exited: false,
        DirectorSaysWorking: working,
        HasTerminalGrid: true,
        ProductMayHaveLeftText: false,
        Frames: [first, second],
        DirectorSettledFor: settled);

    private static DoorbellFacts Facts(string capture, bool working = false, TimeSpan? settled = null) =>
        Facts(Load(capture), Load(capture), working, settled);

    private static readonly TimeSpan LongSettled = TimeSpan.FromMinutes(5);

    // ---------- Idle with a background task or a Monitor: rung ----------

    [Theory]
    [InlineData("claude-idle-background-task")]
    [InlineData("claude-idle-monitor")]
    public void Check_IdleWithBackgroundTaskRunning_Rings(string capture)
    {
        var verdict = DoorbellSafety.Check(Facts(capture, settled: TimeSpan.FromSeconds(5)));

        Assert.True(verdict.Ring, verdict.Detail);
    }

    [Fact]
    public void Check_StaleWorkingMarkerAfterTheDirectorSettled_Rings()
    {
        // The footer issue 3186 recorded on an idle session: "esc to interrupt" with the turn long over.
        var frame = Load("claude-idle-stale-working-marker");
        Assert.True(DoorbellSafety.ShowsWorking(frame.Rows));
        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));

        var verdict = DoorbellSafety.Check(Facts("claude-idle-stale-working-marker", settled: LongSettled));

        Assert.True(verdict.Ring, verdict.Detail);
    }

    [Fact]
    public void Check_StaleWorkingMarkerBeforeTheSettleTime_DefersAsWorking()
    {
        var verdict = DoorbellSafety.Check(Facts("claude-idle-stale-working-marker",
            settled: DoorbellSafety.SettledOutranksMarker - TimeSpan.FromSeconds(1)));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, verdict.Reason);
    }

    [Fact]
    public void Check_WorkingMarkerWithoutASettledDirector_DefersAsWorking()
    {
        // No turn-end signal at all: the marker is the only evidence, and it still defers, exactly as before.
        var verdict = DoorbellSafety.Check(Facts("claude-idle-stale-working-marker", settled: null));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, verdict.Reason);
    }

    [Fact]
    public void Check_StaleMarkerButTheTwoFramesDiffer_DefersAsWorking()
    {
        // The screen moved between the two looks: something is drawing, so the Director's settled state does not
        // outrank the marker.
        var first = Load("claude-idle-stale-working-marker");
        var second = DoorbellCaptures.WithRow(first, 33, "  a row that appeared between the two looks");

        var verdict = DoorbellSafety.Check(Facts(first, second, settled: LongSettled));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, verdict.Reason);
    }

    // ---------- A running turn with a background task: deferred ----------

    [Theory]
    [InlineData("claude-working-background-task")]
    [InlineData("claude-working-monitor")]
    public void Check_TurnRunningWhileBackgroundTaskRuns_DefersAsWorking(string capture)
    {
        // As captured: the Director said Working, and the footer said "esc to interrupt".
        var frame = Load(capture);
        Assert.True(DoorbellSafety.ShowsWorking(frame.Rows));

        var byDirector = DoorbellSafety.Check(Facts(capture, working: true));
        var byScreen = DoorbellSafety.Check(Facts(capture, working: false, settled: null));

        Assert.False(byDirector.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, byDirector.Reason);
        Assert.False(byScreen.Ring);
        Assert.Equal(FleetRingDeferReasons.Working, byScreen.Reason);
    }

    // ---------- The owner's words while a background task runs: deferred, whatever the Director says ----------

    [Theory]
    [InlineData("claude-owner-text-background-task")]
    [InlineData("claude-owner-text-monitor")]
    public void Check_OwnerTextWhileBackgroundTaskRuns_DefersAsComposerHoldsText(string capture)
    {
        var verdict = DoorbellSafety.Check(Facts(capture, settled: LongSettled));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    [Fact]
    public void Check_OwnerTextUnderAStaleMarker_DefersAsComposerHoldsText()
    {
        // Waiving the marker waives nothing else: the owner's draft still stops the ring.
        var stale = Load("claude-idle-stale-working-marker");
        var draft = DoorbellCaptures.WithRow(stale, 35, "❯ a sentence the owner has not sent") with { CursorCol = 35 };

        var verdict = DoorbellSafety.Check(Facts(draft, draft, settled: LongSettled));

        Assert.False(verdict.Ring);
        Assert.Equal(FleetRingDeferReasons.ComposerHoldsText, verdict.Reason);
    }

    // ---------- Claude Code's suggestion in an empty composer ----------

    [Fact]
    public void ReadComposer_SuggestionInAnEmptyComposer_IsEmpty()
    {
        var frame = Load("claude-idle-placeholder-fresh");
        Assert.Equal("❯ Try \"fix typecheck errors\"", frame.Rows[frame.CursorRow]);

        Assert.Equal(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
        Assert.True(DoorbellSafety.Check(Facts("claude-idle-placeholder-fresh")).Ring);
    }

    [Fact]
    public void ReadComposer_SuggestionShapeTypedByTheOwner_HoldsText()
    {
        // Typed, the same words leave the cursor at their end.
        var frame = Load("claude-idle-placeholder-fresh");
        var typed = frame with { CursorCol = frame.Rows[frame.CursorRow].Length };

        Assert.Equal(ComposerReading.HoldsText, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, typed));
    }

    [Fact]
    public void ReadComposer_SuggestionWithAHiddenCursor_IsNotEmpty()
    {
        var frame = Load("claude-idle-placeholder-fresh") with { CursorVisible = false };

        Assert.NotEqual(ComposerReading.Empty, DoorbellSafety.ReadComposer(AgentKind.ClaudeCode, frame));
    }

    [Theory]
    [InlineData("Try \"fix typecheck errors\"", true)]
    [InlineData("Try \"edit <filepath> to...\"", true)]
    [InlineData("Try \"\"", false)]
    [InlineData("Try this instead", false)]
    [InlineData("please try \"x\"", false)]
    public void IsClaudeSuggestion_OnlyTheCapturedShape(string row, bool expected)
    {
        Assert.Equal(expected, DoorbellSafety.IsClaudeSuggestion(row));
    }

    // ---------- Verifying the submit under a marker that was already up ----------

    [Fact]
    public void ShowsDoorbellSubmitted_MarkerAlreadyUpAndNoNewRow_IsFalse()
    {
        var stale = Load("claude-idle-stale-working-marker");
        var rowsBefore = DoorbellSafety.CountDoorbellRows(stale);

        Assert.True(DoorbellSafety.ShowsDoorbellSubmitted(AgentKind.ClaudeCode, stale, rowsBefore));
        Assert.False(DoorbellSafety.ShowsDoorbellSubmitted(AgentKind.ClaudeCode, stale, rowsBefore, markerWasUp: true));
    }

    [Fact]
    public void ShowsDoorbellSubmitted_MarkerAlreadyUpAndANewDoorbellRow_IsTrue()
    {
        var stale = Load("claude-idle-stale-working-marker");
        var rowsBefore = DoorbellSafety.CountDoorbellRows(stale);
        var after = DoorbellCaptures.WithRow(stale, 33, "❯ " + FleetDoorbellLine.For(1));

        Assert.True(DoorbellSafety.ShowsDoorbellSubmitted(AgentKind.ClaudeCode, after, rowsBefore, markerWasUp: true));
    }

    // ---------- The ringer, end to end, on the stale footer ----------

    [Fact]
    public async Task RingAsync_StaleMarkerOnASettledSession_TypesTheLineAndIsRung()
    {
        var stale = Load("claude-idle-stale-working-marker");
        var submitted = DoorbellCaptures.WithRow(stale, 33, "❯ " + FleetDoorbellLine.For(1));
        var target = new FleetDoorbellRingerTests.ScriptedTarget(AgentKind.ClaudeCode, stale, stale, stale, submitted)
        {
            DirectorSettledFor = LongSettled,
        };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, () => Task.CompletedTask);

        Assert.Equal(FleetRingOutcomes.Rung, answer.Outcome);
        Assert.Single(target.SentLines);
    }

    [Fact]
    public async Task RingAsync_StaleMarkerAndTheLineLeftWithoutATurn_IsNotCountedAsRung()
    {
        // The line left the composer but no doorbell row appeared: the marker that was already up must not be read
        // as the turn starting.
        var stale = Load("claude-idle-stale-working-marker");
        var target = new FleetDoorbellRingerTests.ScriptedTarget(AgentKind.ClaudeCode, stale, stale, stale, stale)
        {
            DirectorSettledFor = LongSettled,
        };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, () => Task.CompletedTask);

        Assert.Equal(FleetRingOutcomes.Deferred, answer.Outcome);
        Assert.Equal(FleetRingDeferReasons.NotSubmitted, answer.Reason);
    }

    [Fact]
    public async Task RingAsync_StaleMarkerBeforeTheSettleTime_TypesNothing()
    {
        var stale = Load("claude-idle-stale-working-marker");
        var target = new FleetDoorbellRingerTests.ScriptedTarget(AgentKind.ClaudeCode, stale, stale, stale)
        {
            DirectorSettledFor = TimeSpan.FromSeconds(3),
        };

        var answer = await FleetDoorbellRinger.RingAsync(target, 1, () => Task.CompletedTask);

        Assert.Equal(FleetRingDeferReasons.Working, answer.Reason);
        Assert.Empty(target.SentLines);
    }
}
