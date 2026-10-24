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
/// A RESTART LOSES NOTHING. Each report carries its run and result time as its correlation id
/// (<see cref="GatewayAccountFailures.RunCorrelation"/>), and after every scan that drained its backlog the reporter
/// writes a CHECKPOINT - the time it had read up to - to a file on the durable storage root. A new reporter starts from
/// that checkpoint less <see cref="CheckpointSlack"/> (time enough for the last process's sink to have written what it
/// accepted), however long the Gateway was down, and first reads which of those failures the store already holds. So a
/// failure the last Gateway recorded but never got into the store - accepted by its sink and lost with the process - is
/// reported now, and one that did get there is not told twice. With no checkpoint yet (the first start with this
/// reporter) it starts <see cref="RestartLookback"/> back. It never reaches past what the store keeps.
/// </summary>
public sealed class CronRunFailureReporter : IDisposable
{
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RestartLookback = TimeSpan.FromHours(6);
    internal static readonly TimeSpan CheckpointSlack = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MaxLookback = TimeSpan.FromDays(ErrorReportStore.RetentionDays);
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>At most this many failed runs are reported in one scan; the rest wait for the next. Well under the
    /// sink's <see cref="GatewayErrorSink.MaxPending"/>, so one scan can never fill the sink's table on its own. A burst of
    /// runs that failed for one reason folds into one row there anyway.</summary>
    internal const int MaxPerScan = 50;

    private readonly CronRunHistoryStore _runs;
    private readonly GatewayAccountFailures _failures;
    private readonly Func<DateTime> _clock;
    private readonly Func<DateTime, IReadOnlySet<string>> _alreadyStored;
    // What the store already held when this reporter started, kept until the backlog from the lookback is drained -
    // not just for the first scan, because a backlog longer than one page is read over several.
    private IReadOnlySet<string>? _stored;
    private bool _reconciled;
    private readonly object _lock = new();
    private readonly Dictionary<(string RunId, DateTime ResultUtc), DateTime> _told = new();
    private DateTime _since;
    private readonly string _checkpointPath;

    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _disposed;

    /// <param name="alreadyStored">The correlation ids of the schedule failures the error store holds from the given time
    /// on - what a restarted reporter must not tell again. REQUIRED, so a wiring line that forgot it fails to compile
    /// rather than reporting every recent failure again on every start.</param>
    /// <param name="checkpointPath">The file the checkpoint is kept in, on durable storage. REQUIRED for the same reason.</param>
    public CronRunFailureReporter(CronRunHistoryStore runs, GatewayAccountFailures failures,
        Func<DateTime, IReadOnlySet<string>> alreadyStored, string checkpointPath, Func<DateTime>? clock = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
        _alreadyStored = alreadyStored ?? throw new ArgumentNullException(nameof(alreadyStored));
        if (string.IsNullOrWhiteSpace(checkpointPath)) throw new ArgumentException("a checkpoint path is required", nameof(checkpointPath));
        _checkpointPath = checkpointPath;
        _clock = clock ?? (() => DateTime.UtcNow);
        var now = _clock();
        var checkpoint = ReadCheckpoint(checkpointPath);
        _since = checkpoint is { } c ? c - CheckpointSlack : now - RestartLookback;
        if (_since < now - MaxLookback) _since = now - MaxLookback;
        FileLog.Write($"[CronRunFailureReporter] reading failed runs from {_since:o} ({(checkpoint is null ? "no checkpoint yet" : $"checkpoint {checkpoint:o}")})");
    }

    /// <summary>The checkpoint in <paramref name="path"/>, or null when there is none. A file that is there and cannot be
    /// read as a time is a fault, and fails loud.</summary>
    internal static DateTime? ReadCheckpoint(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Trim();
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            throw new InvalidOperationException($"the schedule failure checkpoint {path} does not hold a time ('{text}')");
        return DateTime.SpecifyKind(at, DateTimeKind.Utc);
    }

    private void WriteCheckpoint(DateTime at)
    {
        var dir = Path.GetDirectoryName(_checkpointPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _checkpointPath + ".tmp";
        File.WriteAllText(temp, at.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temp, _checkpointPath, overwrite: true);
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
            // Read once, on the first scan: what the store already holds from the lookback was told by an earlier Gateway.
            if (!_reconciled)
            {
                _stored = _alreadyStored(from);
                _reconciled = true;
            }
            var stored = _stored;
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
            // The lookback's backlog is drained: everything the store held has been read past, so the set is let go, and
            // how far this reporter has read is kept for the next process.
            if (!more)
            {
                _stored = null;
                WriteCheckpoint(now);
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

    /// <summary>
    /// The correlation ids of every schedule failure the error store holds from <paramref name="sinceUtc"/> to
    /// <paramref name="nowUtc"/> - what a restarted reporter must not tell again. Read an hour at a time, and any part that
    /// holds more than one read returns (<see cref="ErrorReportStore.MaxLimit"/>; the store keeps up to
    /// <see cref="GatewayErrorSink.MaxStoredPerHour"/> Gateway rows an hour) is read again in halves.
    /// </summary>
    internal static IReadOnlySet<string> StoredScheduleFailures(ErrorReportStore store, DateTime sinceUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(store);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var end = nowUtc.AddMinutes(5);
        for (var from = sinceUtc; from < end; from += TimeSpan.FromHours(1))
        {
            var to = from + TimeSpan.FromHours(1) < end ? from + TimeSpan.FromHours(1) : end;
            ReadStored(store, from, to, ids);
        }
        FileLog.Write($"[CronRunFailureReporter] StoredScheduleFailures: {ids.Count} schedule failure(s) already stored since {sinceUtc:o}");
        return ids;
    }

    // One read returns at most ErrorReportStore.MaxLimit rows; a window that holds more is read in halves until each fits.
    private static void ReadStored(ErrorReportStore store, DateTime from, DateTime to, HashSet<string> ids)
    {
        var page = store.Query(new ErrorReportQuery(from, to,
            Component: CcDirector.Core.ErrorReports.ErrorReportLimits.Gateway, Limit: ErrorReportStore.MaxLimit));
        if (page.TotalMatched > page.Records.Count)
        {
            if (to - from > TimeSpan.FromSeconds(1))
            {
                var middle = from + TimeSpan.FromTicks((to - from).Ticks / 2);
                ReadStored(store, from, middle, ids);
                ReadStored(store, middle, to, ids);
                return;
            }
            FileLog.Write($"[CronRunFailureReporter] StoredScheduleFailures: {page.TotalMatched} Gateway rows inside one second at {from:o}, more than one read returns; some may be reported again");
        }
        foreach (var r in page.Records)
            if (r.Source == GatewayAccountFailures.ScheduleSource && r.CorrelationId is { Length: > 0 } id)
                ids.Add(id);
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
