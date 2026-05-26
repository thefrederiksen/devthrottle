using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Exhaustive unit coverage of the TERMINAL-ONLY finish-detection state machine
/// (docs/wingman/REDESIGN.md). CC Director is hook-free by design, so finish detection
/// rests entirely on the screen: declare a turn over only when the agent was seen working
/// and then parks for the confirm window. Fire once at a real turn-end, never mid-turn.
/// Pure and deterministic - an explicit clock, no timers.
/// </summary>
public sealed class FinishDetectorCoreTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(800);
    private static DateTime T(double seconds) => new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
    private static FinishDetectorCore New() => new(Window);

    [Fact]
    public void Worked_then_parked_fires_once_after_confirm_window()
    {
        var d = New();
        Assert.False(d.OnScreen(ScreenParkState.Working, T(1)));
        Assert.False(d.OnScreen(ScreenParkState.ParkedForInput, T(5)));   // just parked
        Assert.False(d.OnTick(T(5.5)));                                    // inside window
        Assert.True(d.OnTick(T(5.85)));                                    // window elapsed -> fire
        // Must not fire again while it stays parked.
        Assert.False(d.OnScreen(ScreenParkState.ParkedForInput, T(7)));
        Assert.False(d.OnTick(T(20)));
    }

    [Fact]
    public void Footer_less_spinner_does_not_fire_mid_turn()
    {
        // The trap silence-inference fell into: a quiet phase the screen reads as Unknown (no
        // "esc to interrupt", no idle footer) must NOT be declared finished.
        var d = New();
        d.OnScreen(ScreenParkState.Working, T(1));
        Assert.False(d.OnScreen(ScreenParkState.Unknown, T(6)));
        Assert.False(d.OnTick(T(30)));
        Assert.False(d.OnScreen(ScreenParkState.Unknown, T(60)));
    }

    [Fact]
    public void Permission_prompt_fires_as_finish_user_needed()
    {
        var d = New();
        d.OnScreen(ScreenParkState.Working, T(1));
        Assert.False(d.OnScreen(ScreenParkState.ParkedForPermission, T(3)));
        Assert.True(d.OnTick(T(3.85)));
    }

    [Fact]
    public void Idle_at_startup_with_no_prior_work_does_not_fire()
    {
        // Booting onto an already-idle session must not fabricate a turn-end - we never saw work.
        var d = New();
        Assert.False(d.OnScreen(ScreenParkState.ParkedForInput, T(0)));
        Assert.False(d.OnTick(T(5)));
        Assert.False(d.OnTick(T(60)));
    }

    [Fact]
    public void Brief_parked_flicker_then_resumed_work_does_not_fire()
    {
        var d = New();
        d.OnScreen(ScreenParkState.Working, T(1));
        Assert.False(d.OnScreen(ScreenParkState.ParkedForInput, T(2)));   // flicker
        Assert.False(d.OnScreen(ScreenParkState.Working, T(2.3)));        // back to work within window
        Assert.False(d.OnTick(T(3.5)));                                   // candidate cleared; no fire
    }

    [Fact]
    public void New_turn_after_a_finish_can_fire_again()
    {
        var d = New();
        d.OnScreen(ScreenParkState.Working, T(1));
        d.OnScreen(ScreenParkState.ParkedForInput, T(2));
        Assert.True(d.OnTick(T(2.85)));                                   // first finish

        d.OnScreen(ScreenParkState.Working, T(10));                       // second turn
        d.OnScreen(ScreenParkState.ParkedForInput, T(12));
        Assert.True(d.OnTick(T(12.85)));                                  // fires again
    }

    [Fact]
    public void Parked_without_tick_fires_on_next_parked_read_past_window()
    {
        // The fire can also come from a later OnScreen(parked) read, not only OnTick.
        var d = New();
        d.OnScreen(ScreenParkState.Working, T(1));
        Assert.False(d.OnScreen(ScreenParkState.ParkedForInput, T(5)));
        Assert.True(d.OnScreen(ScreenParkState.ParkedForInput, T(5.9)));  // still parked, window elapsed
    }
}
