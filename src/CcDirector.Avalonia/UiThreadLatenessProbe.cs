namespace CcDirector.Avalonia;

/// <summary>
/// Measures how late the screen thread is (terminal slowdown plan, step 1).
///
/// WHY. The owner feels the Director slow to a crawl, and nothing in the log could show it: every other
/// line says what ran, none says how long the screen thread was unable to run. A timer at background
/// priority fires only when the screen thread has nothing more urgent to do, so the gap between its ticks,
/// minus the interval it asked for, is how long the screen thread was busy. This class turns those gaps into
/// log lines: <c>[UiThread] late by N ms</c> when a tick arrives more than <see cref="LateThresholdMs"/> late,
/// and one summary line a minute with the count, the worst and the total.
///
/// It keeps no clock of its own - the caller passes the time in milliseconds - so the rule and the summary are
/// tested without a window or a real timer. Cheap by design: one subtraction and one comparison per tick.
/// </summary>
internal sealed class UiThreadLatenessProbe
{
    /// <summary>The interval the probe's timer asks for.</summary>
    public const int IntervalMs = 250;

    /// <summary>A tick later than this past its interval is reported.</summary>
    public const int LateThresholdMs = 100;

    /// <summary>At most one "late by" line per this many milliseconds, so a thread that stalls again and
    /// again cannot flood the log. The summary still counts every late tick.</summary>
    public const int LateLineMinGapMs = 1000;

    /// <summary>How often the summary line is written.</summary>
    public const int SummaryEveryMs = 60_000;

    private readonly Action<string> _log;
    private long? _lastTickMs;
    private long _summaryStartMs;
    private long? _lastLateLineMs;
    private int _lateCount;
    private long _worstLateMs;
    private long _totalLateMs;
    private int _suppressedLines;

    public UiThreadLatenessProbe(Action<string> log, long nowMs)
    {
        _log = log;
        _summaryStartMs = nowMs;
    }

    /// <summary>How late a tick is, in milliseconds: the time since the previous tick minus the interval
    /// asked for, and never below zero.</summary>
    internal static long LatenessMs(long sincePreviousTickMs, long expectedIntervalMs) =>
        Math.Max(0, sincePreviousTickMs - expectedIntervalMs);

    /// <summary>True when a tick is late enough to report.</summary>
    internal static bool IsLate(long latenessMs) => latenessMs > LateThresholdMs;

    /// <summary>Record one tick of the probe's timer at <paramref name="nowMs"/>.</summary>
    public void Tick(long nowMs)
    {
        if (_lastTickMs is { } last)
        {
            var late = LatenessMs(nowMs - last, IntervalMs);
            if (IsLate(late))
            {
                _lateCount++;
                _totalLateMs += late;
                if (late > _worstLateMs) _worstLateMs = late;

                if (_lastLateLineMs is not { } lastLine || nowMs - lastLine >= LateLineMinGapMs)
                {
                    var suppressed = _suppressedLines > 0 ? $", {_suppressedLines} more since the last line" : "";
                    _log($"[UiThread] late by {late} ms{suppressed}");
                    _lastLateLineMs = nowMs;
                    _suppressedLines = 0;
                }
                else
                {
                    _suppressedLines++;
                }
            }
        }
        _lastTickMs = nowMs;

        if (nowMs - _summaryStartMs >= SummaryEveryMs)
        {
            // Written every minute even when nothing was late, so a quiet log proves the probe ran rather
            // than leaving it indistinguishable from a probe that never started.
            _log($"[UiThread] summary: late={_lateCount}, worstMs={_worstLateMs}, totalLateMs={_totalLateMs}, " +
                 $"overMs={nowMs - _summaryStartMs}");
            _summaryStartMs = nowMs;
            _lateCount = 0;
            _worstLateMs = 0;
            _totalLateMs = 0;
        }
    }
}
