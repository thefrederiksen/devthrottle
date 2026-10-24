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
/// already told inside that overlap are remembered, and forgotten once they fall out of it.
///
/// TOLD MEANS THE SINK TOOK IT. A run is marked told only when <see cref="GatewayErrorSink"/> accepted its report. A
/// report the sink dropped because its table was full leaves the run untold and the window where it is, so the next
/// scan tries it again.
///
/// A RESTART LOSES NOTHING IT CAN STILL SEE. Each report carries its run and result time as its correlation id
/// (<see cref="GatewayAccountFailures.RunCorrelation"/>). A new reporter starts <see cref="RestartLookback"/> back and
/// first reads which of those failures the store already holds, so a failure the last Gateway recorded but never got
/// into the store - accepted by its sink and lost with the process - is reported now, and one that did get there is not
/// told twice. A failure older than the lookback that never reached the store is not recovered.
/// </summary>
public sealed class CronRunFailureReporter : IDisposable
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RestartLookback = TimeSpan.FromHours(6);
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>At most this many failed runs are reported in one scan; the rest wait for the next. Well under the
    /// sink's <see cref="GatewayErrorSink.MaxPending"/>, so one scan can never fill the sink's table on its own. A burst of
    /// runs that failed for one reason folds into one row there anyway.</summary>
    internal const int MaxPerScan = 50;

    private readonly CronRunHistoryStore _runs;
    private readonly GatewayAccountFailures _failures;
    private readonly Func<DateTime> _clock;
    private readonly Func<DateTime, IReadOnlySet<string>> _alreadyStored;
    private bool _reconciled;
    private readonly object _lock = new();
    private readonly Dictionary<(string RunId, DateTime ResultUtc), DateTime> _told = new();
    private DateTime _since;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    /// <param name="alreadyStored">The correlation ids of the schedule failures the error store holds from the given time
    /// on - what a restarted reporter must not tell again. REQUIRED, so a wiring line that forgot it fails to compile
    /// rather than reporting every recent failure again on every start.</param>
    public CronRunFailureReporter(CronRunHistoryStore runs, GatewayAccountFailures failures,
        Func<DateTime, IReadOnlySet<string>> alreadyStored, Func<DateTime>? clock = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
        _alreadyStored = alreadyStored ?? throw new ArgumentNullException(nameof(alreadyStored));
        _clock = clock ?? (() => DateTime.UtcNow);
        _since = _clock() - RestartLookback;
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
            // Once, on the first scan: what the store already holds from the lookback was told by an earlier Gateway.
            var stored = _reconciled ? null : _alreadyStored(from);
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
                if (stored is not null && stored.Contains(GatewayAccountFailures.RunCorrelation(key.RunId, key.Item2)))
                {
                    _told[key] = key.Item2;
                    continue;
                }
                if (reported == MaxPerScan)
                {
                    more = true;
                    break;
                }
                if (!_failures.ScheduleRunFailed(f))
                {
                    // Not taken: left untold, and the window stays, so the next scan tries it again.
                    more = true;
                    break;
                }
                _told[key] = key.Item2;
                reported++;
            }
            _reconciled = true;

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

    /// <summary>
    /// The correlation ids of every schedule failure the error store holds from <paramref name="sinceUtc"/> to
    /// <paramref name="nowUtc"/> - what a restarted reporter must not tell again. Read an hour at a time, because one read
    /// returns at most <see cref="ErrorReportStore.MaxLimit"/> rows and the store keeps up to
    /// <see cref="GatewayErrorSink.MaxStoredPerHour"/> Gateway rows an hour; an hour that still fills a read is logged, as
    /// its older failures may then be told once more (told twice, never lost).
    /// </summary>
    internal static IReadOnlySet<string> StoredScheduleFailures(ErrorReportStore store, DateTime sinceUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var from = sinceUtc; from < nowUtc; from += TimeSpan.FromHours(1))
        {
            var to = from + TimeSpan.FromHours(1) < nowUtc ? from + TimeSpan.FromHours(1) : nowUtc.AddMinutes(5);
            var page = store.Query(new ErrorReportQuery(from, to,
                Component: CcDirector.Core.ErrorReports.ErrorReportLimits.Gateway, Limit: ErrorReportStore.MaxLimit));
            if (page.TotalMatched > page.Records.Count)
                FileLog.Write($"[CronRunFailureReporter] StoredScheduleFailures: {page.TotalMatched} Gateway rows from {from:o}, more than one read returns; some failures of that hour may be reported again");
            foreach (var r in page.Records)
                if (r.Source == GatewayAccountFailures.ScheduleSource && r.CorrelationId is { Length: > 0 } id)
                    ids.Add(id);
        }
        FileLog.Write($"[CronRunFailureReporter] StoredScheduleFailures: {ids.Count} schedule failure(s) already stored since {sinceUtc:o}");
        return ids;
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
