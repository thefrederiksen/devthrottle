using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

public sealed class TerminalStateDetectorTests
{
    [Theory]
    [InlineData("working", ActivityState.Working)]
    [InlineData("waiting_for_permission", ActivityState.WaitingForPerm)]
    [InlineData("waiting_for_input", ActivityState.WaitingForInput)]
    [InlineData("idle", ActivityState.WaitingForInput)]
    [InlineData("cancelled", ActivityState.WaitingForInput)]
    [InlineData("unknown", ActivityState.WaitingForInput)]
    [InlineData("anything-unexpected", ActivityState.WaitingForInput)]
    public void MapVerdictToActivityState_mapsAsDesigned(string verdict, ActivityState expected)
    {
        Assert.Equal(expected, TerminalStateDetector.MapVerdictToActivityState(verdict));
    }

    [Fact]
    public void ScreenShowsWorkingFooter_true_when_interrupt_footer_visible()
    {
        // A working agent blocked on a quiet tool still shows the footer with no new bytes.
        var rows = new[]
        {
            "* Reading vault docs...",
            "",
            "> ",
            "  (esc to interrupt | ctrl+t to hide todos)",
        };
        Assert.True(TerminalStateDetector.ScreenShowsWorkingFooter(rows));
    }

    [Fact]
    public void ScreenShowsWorkingFooter_false_for_idle_prompt()
    {
        // Genuine turn-end: the footer is gone and the idle prompt is shown.
        var rows = new[]
        {
            "Done. Pushed commit abc123.",
            "",
            "> ",
            "  bypass permissions on (shift+tab to cycle)",
        };
        Assert.False(TerminalStateDetector.ScreenShowsWorkingFooter(rows));
    }

    [Fact]
    public void ScreenShowsWorkingFooter_true_for_empty_grid_never_fabricates_turn_end()
    {
        // No resolved screen (e.g. Embedded backend): we must not invent a turn-end we cannot see.
        Assert.True(TerminalStateDetector.ScreenShowsWorkingFooter(System.Array.Empty<string>()));
        Assert.True(TerminalStateDetector.ScreenShowsWorkingFooter(null!));
    }

    [Fact]
    public void WingmanLlmThrottle_blocks_a_second_call_within_the_window()
    {
        var sid = Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(sid));    // first call allowed
        Assert.False(WingmanLlmThrottle.TryAcquire(sid));   // immediate second blocked (< 5s)

        var other = Guid.NewGuid();
        Assert.True(WingmanLlmThrottle.TryAcquire(other));  // a different session is independent
    }
}
