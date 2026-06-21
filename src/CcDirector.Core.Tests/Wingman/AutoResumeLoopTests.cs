using CcDirector.Core.Configuration;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Issue #476: the auto-resume scheduler's decision core. Driven with an injected clock and a
/// fixed config so the cadence, recovery, give-up, and OFF rules are asserted deterministically
/// with no real timers. This is where the scheduler acceptance criteria are proven:
///   * fires the first continue after the first-retry delay, then on the interval,
///   * stops on recovery (error gone),
///   * stops at the give-up bound (max attempts OR max elapsed),
///   * zero retries when the setting is OFF.
/// </summary>
public sealed class AutoResumeLoopTests
{
    private DateTime _now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    private AutoResumeLoop MakeLoop(AutoResumeConfig cfg)
        => new(() => cfg, () => _now);

    private static AutoResumeConfig On(int first = 60, int interval = 300, int maxAttempts = 12, int maxElapsed = 120)
        => new(Enabled: true, FirstRetrySeconds: first, IntervalSeconds: interval,
               MaxAttempts: maxAttempts, MaxElapsedMinutes: maxElapsed);

    private void Advance(TimeSpan by) => _now += by;

    [Fact]
    public void OnScreenScan_TransientDetectedWhileOn_ArmsFirstRetry()
    {
        var loop = MakeLoop(On(first: 60));

        var step = loop.OnScreenScan(hasTransientError: true);

        Assert.Equal(AutoResumeKind.ArmFirstRetry, step.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), step.Delay);
        Assert.True(loop.IsArmed);
    }

    [Fact]
    public void OnScreenScan_TransientWhileOff_DoesNothing()
    {
        var loop = MakeLoop(On() with { Enabled = false });

        var step = loop.OnScreenScan(hasTransientError: true);

        Assert.Equal(AutoResumeKind.None, step.Kind);
        Assert.False(loop.IsArmed);
    }

    [Fact]
    public void OnRetryDue_OffMidLoop_StopsWithZeroFurtherContinues()
    {
        // Arm while ON, then flip OFF before the retry fires: the loop must not auto-continue.
        var cfg = On();
        var loop = new AutoResumeLoop(() => cfg, () => _now);
        Assert.Equal(AutoResumeKind.ArmFirstRetry, loop.OnScreenScan(true).Kind);

        cfg = cfg with { Enabled = false };
        var step = loop.OnRetryDue(hasTransientError: true);

        Assert.Equal(AutoResumeKind.None, step.Kind);
        Assert.False(loop.IsArmed);
    }

    [Fact]
    public void OnRetryDue_StillTransient_SendsContinueAndReArmsForInterval()
    {
        var loop = MakeLoop(On(first: 60, interval: 300));
        loop.OnScreenScan(true); // arm

        var step = loop.OnRetryDue(hasTransientError: true);

        Assert.Equal(AutoResumeKind.Continue, step.Kind);
        Assert.Equal(1, step.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(300), step.Delay); // re-arm on the steady interval
        Assert.Equal(1, loop.Attempts);
    }

    [Fact]
    public void OnRetryDue_RepeatsOnIntervalWhileErrorPersists()
    {
        var loop = MakeLoop(On(first: 60, interval: 300, maxAttempts: 12));
        loop.OnScreenScan(true);

        for (int expected = 1; expected <= 5; expected++)
        {
            var step = loop.OnRetryDue(hasTransientError: true);
            Assert.Equal(AutoResumeKind.Continue, step.Kind);
            Assert.Equal(expected, step.Attempt);
            Advance(TimeSpan.FromSeconds(300));
        }

        Assert.Equal(5, loop.Attempts);
    }

    [Fact]
    public void OnScreenScan_ErrorCleared_RecoversAndStops()
    {
        var loop = MakeLoop(On());
        loop.OnScreenScan(true);
        loop.OnRetryDue(true); // one continue

        var step = loop.OnScreenScan(hasTransientError: false);

        Assert.Equal(AutoResumeKind.Recovered, step.Kind);
        Assert.Equal(1, step.Attempt);
        Assert.False(loop.IsArmed);

        // After recovery, a later retry-due fires nothing.
        Assert.Equal(AutoResumeKind.None, loop.OnRetryDue(false).Kind);
    }

    [Fact]
    public void OnRetryDue_ErrorClearedAtFireTime_Recovers()
    {
        var loop = MakeLoop(On());
        loop.OnScreenScan(true);

        var step = loop.OnRetryDue(hasTransientError: false);

        Assert.Equal(AutoResumeKind.Recovered, step.Kind);
        Assert.False(loop.IsArmed);
    }

    [Fact]
    public void OnRetryDue_MaxAttemptsReached_GivesUp()
    {
        var loop = MakeLoop(On(first: 60, interval: 300, maxAttempts: 3));
        loop.OnScreenScan(true);

        Assert.Equal(1, loop.OnRetryDue(true).Attempt); // attempt 1
        Assert.Equal(2, loop.OnRetryDue(true).Attempt); // attempt 2
        Assert.Equal(3, loop.OnRetryDue(true).Attempt); // attempt 3 (== max)

        // Fourth retry: attempts (3) >= maxAttempts (3) => give up.
        var step = loop.OnRetryDue(true);
        Assert.Equal(AutoResumeKind.GiveUp, step.Kind);
        Assert.Equal(3, step.Attempt);
        Assert.False(loop.IsArmed);
    }

    [Fact]
    public void OnRetryDue_MaxElapsedReached_GivesUpBeforeMaxAttempts()
    {
        var loop = MakeLoop(On(first: 60, interval: 300, maxAttempts: 100, maxElapsed: 10));
        loop.OnScreenScan(true); // _firstDetectedUtc = now

        Assert.Equal(AutoResumeKind.Continue, loop.OnRetryDue(true).Kind); // attempt 1, well inside 10min

        Advance(TimeSpan.FromMinutes(11)); // now past max elapsed
        var step = loop.OnRetryDue(true);

        Assert.Equal(AutoResumeKind.GiveUp, step.Kind);
        Assert.True(step.Elapsed >= TimeSpan.FromMinutes(10));
        Assert.False(loop.IsArmed);
    }

    [Fact]
    public void OnScreenScan_AlreadyArmed_DoesNotReArm()
    {
        var loop = MakeLoop(On());
        Assert.Equal(AutoResumeKind.ArmFirstRetry, loop.OnScreenScan(true).Kind);

        // A second scan while still showing the error must not re-arm (which would reset the
        // cadence and the elapsed clock).
        var step = loop.OnScreenScan(true);
        Assert.Equal(AutoResumeKind.None, step.Kind);
        Assert.True(loop.IsArmed);
    }
}
