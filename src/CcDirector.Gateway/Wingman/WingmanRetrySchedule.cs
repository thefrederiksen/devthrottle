namespace CcDirector.Gateway.Wingman;

/// <summary>
/// THE ONE RETRY SCHEDULE THE WINGMAN HAS (mission "Wingman error and retry", owner ruling 2026-09-19): three
/// tries one minute apart, three tries five minutes apart, two tries thirty minutes apart, then stop. Eight
/// retries over about an hour and twenty minutes, for every kind of failure alike.
///
/// IT HOLDS NO STATE AND STARTS NO TIMER. Where a stop is on the schedule is written on the thing that failed -
/// the stored reading carries <c>RetriesMade</c> and <c>NextRetryAtUtc</c> - and the idle sweep, which comes
/// past about every 45 seconds, is the clock that asks "is a retry due". So a Gateway restart forgets nothing,
/// and what a card says is booked is exactly what the record says is booked.
///
/// It replaces the voice path's own ten, thirty and ninety second ladder. Two schedules for one failure was part
/// of how a card came to promise an attempt nobody had booked.
/// </summary>
public static class WingmanRetrySchedule
{
    /// <summary>The wait before each retry, measured from the attempt before it. The length IS the cap.</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays = new[]
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30),
    };

    /// <summary>How many retries one stop gets.</summary>
    public static int Total => Delays.Count;

    /// <summary>
    /// When the next retry is due after an attempt that failed at <paramref name="failedAtUtc"/>, or null when
    /// the schedule is used up and nothing more is booked.
    /// </summary>
    /// <param name="retriesMade">How many scheduled retries this stop has already spent. Zero after the first
    /// failure.</param>
    /// <param name="providerWait">The wait a rate limit named, when it named one. The retry is the LATER of the
    /// schedule and that wait: a provider that asked for ten minutes is not called again in one.</param>
    public static DateTime? NextRetryAtUtc(int retriesMade, DateTime failedAtUtc, TimeSpan? providerWait = null)
    {
        if (retriesMade < 0) throw new ArgumentOutOfRangeException(nameof(retriesMade));
        if (retriesMade >= Total) return null;
        var wait = Delays[retriesMade];
        if (providerWait is { } named && named > wait) wait = named;
        return failedAtUtc + wait;
    }

    /// <summary>Is the booked retry due? False when nothing is booked.</summary>
    public static bool IsDue(DateTime? nextRetryAtUtc, DateTime nowUtc)
        => nextRetryAtUtc is { } due && due <= nowUtc;
}
