using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;

namespace CcDirector.Gateway.Running;

/// <summary>
/// EVERY SCHEDULED RUN THAT FAILS ON THE GATEWAY BECOMES AN ERROR REPORT UNDER ITS ACCOUNT (issue #4403).
///
/// A run fails along many paths - a fire that started nothing, a start whose outcome was never known and lapsed, a run
/// that did not report or ran past its shift, a check that no Director ran, a session that reported a problem - and each
/// is written by a different method of <see cref="CronRunHistoryStore"/>. What they share is that every one stamps the
/// run's result time when it records the failure. So instead of a hook in each path, which the next path added would
/// forget, this reads that one fact: on each <see cref="Scan"/> it asks the store for every failed run of every account
/// whose result was recorded since the last scan, and reports each through <see cref="GatewayAccountFailures"/>.
///
/// ONCE PER FAILURE. A run is reported once for each result time it carries, so a problem the session later adds its own
/// report to is told again, and nothing is told twice. The window reaches back <see cref="Overlap"/> before the last scan,
/// so a failure whose result time was taken just before a scan but committed just after it is still seen; the runs
/// already told inside that overlap are remembered, and forgotten once they fall out of it. A Gateway that restarts
/// starts from its own start time: what failed while it was down was recorded by the next Gateway's sweep, at that
/// Gateway's time, so it is seen then.
/// </summary>
public sealed class CronRunFailureReporter : IDisposable
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>At most this many failed runs are reported in one scan; the rest wait for the next. Well under the
    /// sink's <see cref="GatewayErrorSink.MaxPending"/>, so one scan can never fill the sink's table on its own. A burst of
    /// runs that failed for one reason folds into one row there anyway.</summary>
    internal const int MaxPerScan = 50;

    private readonly CronRunHistoryStore _runs;
    private readonly GatewayAccountFailures _failures;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<(string RunId, DateTime ResultUtc), DateTime> _told = new();
    private DateTime _since;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    public CronRunFailureReporter(CronRunHistoryStore runs, GatewayAccountFailures failures, Func<DateTime>? clock = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
        _clock = clock ?? (() => DateTime.UtcNow);
        _since = _clock();
    }

    /// <summary>Start the scan timer. Call once.</summary>
    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("the schedule failure reporter is already started");
        _loop = Task.Run(RunLoopAsync);
        FileLog.Write($"[CronRunFailureReporter] Start: reporting failed runs recorded from {_since:o}, every {ScanInterval.TotalSeconds:0}s");
    }

    /// <summary>
    /// Report every failed run recorded since the last scan that has not been told yet. Returns how many were reported.
    /// </summary>
    public int Scan()
    {
        lock (_lock)
        {
            var now = _clock();
            var from = _since - Overlap;
            // The page is widened by what was already told inside the window, so a window full of told runs - a burst of
            // missed runs recorded in one write all carry one result time - can never hide the ones not yet told.
            var toldInWindow = _told.Count(t => t.Value >= from);
            var limit = MaxPerScan + toldInWindow;
            var failed = _runs.FailedSince(from, limit);
            var reported = 0;
            var more = failed.Count >= limit;
            foreach (var f in failed)
            {
                var key = (f.Run.RunId, f.Run.ResultUtc!.Value);
                if (_told.ContainsKey(key)) continue;
                if (reported == MaxPerScan)
                {
                    more = true;
                    break;
                }
                _told[key] = key.Item2;
                _failures.ScheduleRunFailed(f);
                reported++;
            }

            // While a backlog remains the window stays where it is, and the told set keeps it from being told twice; once
            // it is drained the window moves on to now.
            if (!more) _since = now;
            var forgetBefore = _since - Overlap;
            foreach (var stale in _told.Where(t => t.Value < forgetBefore).Select(t => t.Key).ToList())
                _told.Remove(stale);

            if (reported > 0)
                FileLog.Write($"[CronRunFailureReporter] Scan: reported {reported} failed scheduled run(s) recorded since {from:o}{(more ? "; more are waiting for the next scan" : "")}");
            return reported;
        }
    }

    private async Task RunLoopAsync()
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                // One bad tick costs that tick, never the loop.
                try
                {
                    Scan();
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[CronRunFailureReporter] Scan FAILED this tick ({ex.GetType().Name}): {ex.Message}; the next tick tries again");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex)
        {
            FileLog.Write($"[CronRunFailureReporter] the scan loop ended with {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
        }
        _stop.Dispose();
        FileLog.Write("[CronRunFailureReporter] stopped");
    }
}
