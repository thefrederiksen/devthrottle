namespace CcDirectorClient.Voice;

/// <summary>
/// The retry/backoff knobs for <see cref="VoiceTurnRunner"/>, with the clock and the inter-poll
/// delay injected so the loop is unit tested off-device with instant (no real wall-clock) waits.
/// In production the defaults apply: ~1.5s steady poll cadence, backoff growing to ~5s while a
/// run of polls is failing, an overall deadline aligned with the Gateway's ~10-minute job TTL,
/// and a small bounded submit-retry budget.
/// </summary>
public sealed class VoiceTurnRetryPolicy
{
    /// <summary>Steady poll cadence while the turn is progressing (matches the prior 1.5s).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(1_500);

    /// <summary>First backoff step after a transient failure.</summary>
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(1_500);

    /// <summary>Backoff ceiling - it never waits longer than this between retries (~5s).</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromMilliseconds(5_000);

    /// <summary>Overall deadline for the poll loop, aligned with the Gateway job TTL (~10 min).</summary>
    public TimeSpan OverallDeadline { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How many times to attempt the submit before surfacing a failure (1 initial + retries).</summary>
    public int SubmitAttempts { get; init; } = 4;

    /// <summary>The clock. Injected so tests advance time without waiting.</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The wait primitive. Injected so tests run instantly instead of sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; }
        = (d, ct) => Task.Delay(d, ct);

    public static VoiceTurnRetryPolicy Default => new();

    /// <summary>
    /// The backoff for the Nth consecutive transient failure: linear growth off
    /// <see cref="BackoffBase"/> capped at <see cref="BackoffMax"/> (e.g. 1.5s, 3s, 4.5s, 5s...).
    /// Kept simple and bounded - no jitter - so the cadence is predictable and testable.
    /// </summary>
    public TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures < 1) consecutiveFailures = 1;
        var ms = BackoffBase.TotalMilliseconds * consecutiveFailures;
        if (ms > BackoffMax.TotalMilliseconds) ms = BackoffMax.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }
}
