using CcDirector.Core.Configuration;

namespace CcDirector.Core.Wingman;

/// <summary>
/// The pure decision core of <see cref="TransientErrorAutoResume"/> (issue #476), factored out of
/// the timer plumbing so the cadence, recovery, and give-up rules are deterministically testable
/// with an injected clock - no real timers, no flakiness.
///
/// It is a small state machine driven by two events:
///   * <see cref="OnScreenScan"/>(hasTransientError) - a debounced look at the terminal content.
///   * <see cref="OnRetryDue"/>(hasTransientError)    - the retry timer elapsed.
///
/// Each returns an <see cref="AutoResumeStep"/> telling the caller what to physically do
/// (arm a timer for a delay, send an auto-continue, flag give-up, or nothing). The loop never
/// touches a <see cref="CcDirector.Core.Sessions.Session"/> or a timer itself, so it carries no
/// side effects and is trivial to assert against.
///
/// Config is read once per decision via the supplied provider, so a toggle or interval change
/// takes effect on the next event without a restart. When <see cref="AutoResumeConfig.Enabled"/>
/// is false EVERY decision is <see cref="AutoResumeStep.None"/> - zero retries, the OFF guarantee.
/// </summary>
public sealed class AutoResumeLoop
{
    private readonly Func<AutoResumeConfig> _configProvider;
    private readonly Func<DateTime> _clock;

    private bool _armed;
    private int _attempts;
    private DateTime _firstDetectedUtc;

    public AutoResumeLoop(Func<AutoResumeConfig> configProvider, Func<DateTime>? clock = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True while a transient error is being actively auto-resumed.</summary>
    public bool IsArmed => _armed;

    /// <summary>Auto-continue attempts sent in the current armed window.</summary>
    public int Attempts => _attempts;

    /// <summary>
    /// A debounced screen scan settled. <paramref name="hasTransientError"/> is the result of
    /// <see cref="TransientErrorSignatures.IsRetryableTransient"/> on the resolved grid.
    /// </summary>
    public AutoResumeStep OnScreenScan(bool hasTransientError)
    {
        var cfg = _configProvider();
        if (!cfg.Enabled)
        {
            // OFF: never arm, and clear any prior armed state defensively.
            Reset();
            return AutoResumeStep.None;
        }

        if (hasTransientError)
        {
            if (_armed)
                return AutoResumeStep.None; // already on cadence; the retry timer owns it
            // Arm: schedule the FIRST continue after the (shorter) first-retry delay.
            _armed = true;
            _attempts = 0;
            _firstDetectedUtc = _clock();
            return AutoResumeStep.ArmFirstRetry(cfg.FirstRetryDelay);
        }

        // No transient error on screen.
        if (_armed)
        {
            // We were auto-resuming and the error is gone => recovery.
            var attempts = _attempts;
            Reset();
            return AutoResumeStep.Recovered(attempts);
        }
        return AutoResumeStep.None;
    }

    /// <summary>
    /// The retry timer elapsed. <paramref name="hasTransientError"/> is a FRESH scan taken at fire
    /// time - we only auto-continue if the error is still present.
    /// </summary>
    public AutoResumeStep OnRetryDue(bool hasTransientError)
    {
        var cfg = _configProvider();
        if (!cfg.Enabled)
        {
            Reset();
            return AutoResumeStep.None;
        }

        if (!_armed)
            return AutoResumeStep.None;

        if (!hasTransientError)
        {
            var attempts = _attempts;
            Reset();
            return AutoResumeStep.Recovered(attempts);
        }

        // Give-up bound: max attempts OR max elapsed wall-clock, whichever first.
        var elapsed = _clock() - _firstDetectedUtc;
        if (_attempts >= cfg.MaxAttempts || elapsed >= cfg.MaxElapsed)
        {
            var attempts = _attempts;
            Reset();
            return AutoResumeStep.GiveUp(attempts, elapsed);
        }

        // Auto-continue, then re-arm for the steady interval.
        _attempts++;
        return AutoResumeStep.Continue(_attempts, cfg.MaxAttempts, cfg.Interval);
    }

    private void Reset()
    {
        _armed = false;
        _attempts = 0;
    }
}

/// <summary>What the caller must physically do after an <see cref="AutoResumeLoop"/> decision.</summary>
public readonly struct AutoResumeStep
{
    public AutoResumeKind Kind { get; }

    /// <summary>Delay to arm the retry timer for (ArmFirstRetry / Continue).</summary>
    public TimeSpan Delay { get; }

    /// <summary>Attempt number just performed (Continue) or reached (Recovered / GiveUp).</summary>
    public int Attempt { get; }

    /// <summary>The configured max attempts (Continue), for the audit/log line.</summary>
    public int MaxAttempts { get; }

    /// <summary>Wall-clock elapsed since first detection (GiveUp).</summary>
    public TimeSpan Elapsed { get; }

    private AutoResumeStep(AutoResumeKind kind, TimeSpan delay, int attempt, int maxAttempts, TimeSpan elapsed)
    {
        Kind = kind;
        Delay = delay;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
        Elapsed = elapsed;
    }

    /// <summary>Do nothing.</summary>
    public static readonly AutoResumeStep None =
        new(AutoResumeKind.None, TimeSpan.Zero, 0, 0, TimeSpan.Zero);

    /// <summary>Arm the retry timer for the first (shorter) delay.</summary>
    public static AutoResumeStep ArmFirstRetry(TimeSpan delay) =>
        new(AutoResumeKind.ArmFirstRetry, delay, 0, 0, TimeSpan.Zero);

    /// <summary>Send the continue nudge (attempt N) and re-arm for <paramref name="interval"/>.</summary>
    public static AutoResumeStep Continue(int attempt, int maxAttempts, TimeSpan interval) =>
        new(AutoResumeKind.Continue, interval, attempt, maxAttempts, TimeSpan.Zero);

    /// <summary>Stop the loop: output resumed.</summary>
    public static AutoResumeStep Recovered(int attempts) =>
        new(AutoResumeKind.Recovered, TimeSpan.Zero, attempts, 0, TimeSpan.Zero);

    /// <summary>Stop the loop and flag the session needs-you: the give-up bound was hit.</summary>
    public static AutoResumeStep GiveUp(int attempts, TimeSpan elapsed) =>
        new(AutoResumeKind.GiveUp, TimeSpan.Zero, attempts, 0, elapsed);
}

/// <summary>The kind of action an <see cref="AutoResumeStep"/> calls for.</summary>
public enum AutoResumeKind
{
    None,
    ArmFirstRetry,
    Continue,
    Recovered,
    GiveUp,
}
