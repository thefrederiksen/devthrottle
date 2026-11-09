using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Gateway.Diagnostics;

/// <summary>
/// Writes one compact load line to the Gateway's own log every <see cref="Interval"/> for the first
/// <see cref="Window"/> after the process starts, then stops for good.
///
/// WHY (owner decision, 9 November 2026: "measure first"). Hosted deploys lost from 2 to 115 seconds of service,
/// rising with load. The likely causes - worker threads blocked behind synchronous database work while every
/// Director reconnects at once, and a database pool too small for the burst - could not be proved, because
/// nothing recorded the thread pool or the database pool during a deploy. The deploy workflow cannot read
/// <c>/diag/loadmetrics</c> (it holds no Gateway credential, and the route is not opened for it), so the Gateway
/// records the numbers itself, into the log that now lives on the shared drive and survives the deploy. Every
/// line carries a UTC timestamp, so it lines up with the watch job's outage record afterwards.
///
/// WHAT A LINE HOLDS: the thread pool (threads, queued work items, completed work items), the Director hub
/// (connections, pushes in flight), the roster request count and failures, processor time and working set,
/// and, for each Npgsql connection pool, connections in use and idle, the pool's maximum, requests WAITING
/// for a connection, connection timeouts, and commands executing. The database pool numbers come from Npgsql's
/// own metrics, which <c>/diag/loadmetrics</c> does not carry. A pool is named by a short hash of its Npgsql
/// pool name, because that name is the connection string without its password and still names the database
/// host and user.
///
/// MEASUREMENT ONLY. Nothing here changes how the Gateway serves. It runs on its OWN thread rather than the
/// thread pool, so a starved thread pool - one of the causes it exists to prove - cannot silence it. A gap
/// between two lines is evidence too: it means the log writer or the process itself stalled.
///
/// COST: one line of a few hundred bytes every two seconds, 300 lines per container start, then nothing.
/// </summary>
public sealed class StartupLoadSampler : IDisposable
{
    /// <summary>Time between two lines.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>How long after start the sampler keeps writing. A deploy's hand-over and reconnect burst is
    /// inside the first few minutes; the watch job watches for ten.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The name of the meter Npgsql publishes its connection-pool metrics on.</summary>
    public const string NpgsqlMeterName = "Npgsql";

    /// <summary>The prefix of every line this writes, for a reader to grep.</summary>
    public const string LinePrefix = "[StartupLoad]";

    // Instruments Npgsql reports by observation (read on demand): the last value seen is the current value.
    private readonly ConcurrentDictionary<(string Instrument, string Pool, string State), double> _observed = new();
    // Instruments Npgsql reports by adding (up-down counters and counters): the running sum is the current value,
    // which is right because the listener starts before the Gateway opens any connection.
    private readonly ConcurrentDictionary<(string Instrument, string Pool, string State), double> _summed = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, byte> _listened = new(StringComparer.Ordinal);

    /// <summary>The Npgsql instruments this sampler is listening to, by name. A name the line reads that is not
    /// here would print "-" forever, so the database test checks every one is.</summary>
    internal IReadOnlyCollection<string> ListenedInstruments => _listened.Keys.ToList();

    /// <summary>Start listening to Npgsql's meter. Construct before the Gateway opens a database connection.</summary>
    public StartupLoadSampler() : this(listenToNpgsql: true)
    {
    }

    /// <summary>
    /// TEST seam: <paramref name="listenToNpgsql"/> false builds a sampler that hears only what a test feeds it
    /// through <see cref="Record(string, bool, double, ReadOnlySpan{KeyValuePair{string, object?}})"/>. The Npgsql
    /// meter is process-wide, so a listening sampler in a test would also see every pool another test created.
    /// </summary>
    internal StartupLoadSampler(bool listenToNpgsql)
    {
        if (!listenToNpgsql)
            return;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == NpgsqlMeterName && instrument.Name.StartsWith("db.client.", StringComparison.Ordinal))
            {
                _listened[instrument.Name] = 0;
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    /// <summary>
    /// Start the sampler on its own background thread, writing through <paramref name="write"/> (the Gateway
    /// passes <c>FileLog.Write</c>). Returns the thread, which ends by itself after <see cref="Window"/>.
    /// </summary>
    public static Thread StartOnOwnThread(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var sampler = new StartupLoadSampler();
        var clock = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            using (sampler)
                sampler.Run(() => clock.Elapsed, delay => { Thread.Sleep(delay); return true; }, write, () => DateTime.UtcNow);
        })
        {
            IsBackground = true,
            Name = "StartupLoadSampler",
        };
        thread.Start();
        return thread;
    }

    /// <summary>
    /// The sampling loop: a line at 0, 2, 4 ... seconds until <see cref="Window"/> has passed, then one closing
    /// line, then return. The schedule is anchored to the start, so a slow sample does not push every later
    /// one back. <paramref name="wait"/> returning false stops the loop early. Returns the number of samples
    /// written. Every dependency is passed in, so the loop is tested without a real clock or a real wait.
    /// </summary>
    internal int Run(Func<TimeSpan> elapsed, Func<TimeSpan, bool> wait, Action<string> write, Func<DateTime> utcNow)
    {
        write($"{LinePrefix} started: one line every {Interval.TotalSeconds:0}s for {Window.TotalMinutes:0} minutes");
        var samples = 0;
        while (true)
        {
            var now = elapsed();
            if (now >= Window) break;
            write(FormatLine(now, utcNow()));
            samples++;

            var delay = Interval * samples - elapsed();
            if (delay > TimeSpan.Zero && !wait(delay)) break;
        }
        write($"{LinePrefix} finished: {samples} samples over {elapsed().TotalSeconds:0}s; no more lines from this process");
        return samples;
    }

    /// <summary>One sample, as one log line.</summary>
    internal string FormatLine(TimeSpan sinceStart, DateTime utcNow)
    {
        _listener.RecordObservableInstruments();
        var live = LoadTestMetrics.ReadLive();
        using var process = Process.GetCurrentProcess();

        var sb = new StringBuilder(LinePrefix);
        sb.Append(CultureInfo.InvariantCulture, $" utc={utcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
        sb.Append(CultureInfo.InvariantCulture, $" t={sinceStart.TotalSeconds:0.0}s");
        sb.Append(CultureInfo.InvariantCulture, $" threads={ThreadPool.ThreadCount}");
        sb.Append(CultureInfo.InvariantCulture, $" queued={ThreadPool.PendingWorkItemCount}");
        sb.Append(CultureInfo.InvariantCulture, $" completed={ThreadPool.CompletedWorkItemCount}");
        sb.Append(CultureInfo.InvariantCulture, $" hub={live.HubConnections}");
        sb.Append(CultureInfo.InvariantCulture, $" hubTotal={live.HubConnectionsTotal}");
        sb.Append(CultureInfo.InvariantCulture, $" pushInFlight={live.HubPushInFlight}");
        sb.Append(CultureInfo.InvariantCulture, $" pushes={live.HubPushEvents}");
        sb.Append(CultureInfo.InvariantCulture, $" sweepInFlight={live.SweepInFlight}");
        sb.Append(CultureInfo.InvariantCulture, $" roster={live.RosterRequests}");
        sb.Append(CultureInfo.InvariantCulture, $" rosterFailed={live.RosterNonSuccess}");
        sb.Append(CultureInfo.InvariantCulture, $" cpu={process.TotalProcessorTime.TotalSeconds:0.0}s");
        sb.Append(CultureInfo.InvariantCulture, $" workingSetMb={process.WorkingSet64 / (1024 * 1024)}");
        AppendDatabasePools(sb);
        return sb.ToString();
    }

    /// <summary>
    /// The database pools, one group per pool: <c>db[label] used= idle= max= waiting= timeouts= executing=</c>.
    /// "No database pool yet" is said out loud, so an empty group is never mistaken for a dead instrument. A value
    /// is "-" when that instrument has reported nothing for the pool yet: Npgsql reports waiting requests,
    /// timeouts and executing commands only as they change, so "-" there means none since the process started.
    /// </summary>
    internal void AppendDatabasePools(StringBuilder sb)
    {
        var pools = _observed.Keys.Select(k => k.Pool)
            .Concat(_summed.Keys.Select(k => k.Pool))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (pools.Count == 0)
        {
            sb.Append(" db=none-yet");
            return;
        }

        foreach (var pool in pools)
        {
            sb.Append(CultureInfo.InvariantCulture, $" db[{PoolLabel(pool)}]");
            sb.Append(CultureInfo.InvariantCulture, $" used={Value("db.client.connections.usage", pool, "used")}");
            sb.Append(CultureInfo.InvariantCulture, $" idle={Value("db.client.connections.usage", pool, "idle")}");
            sb.Append(CultureInfo.InvariantCulture, $" max={Value("db.client.connections.max", pool, "")}");
            sb.Append(CultureInfo.InvariantCulture, $" waiting={Value("db.client.connections.pending_requests", pool, "")}");
            sb.Append(CultureInfo.InvariantCulture, $" timeouts={Value("db.client.connections.timeouts", pool, "")}");
            sb.Append(CultureInfo.InvariantCulture, $" executing={Value("db.client.commands.executing", pool, "")}");
        }
    }

    /// <summary>A short, stable, non-reversible label for an Npgsql pool name (which names the database host).</summary>
    internal static string PoolLabel(string poolName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(poolName));
        return Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
    }

    /// <summary>Feed one measurement in, exactly as the listener does. Internal so a test can drive the
    /// bookkeeping without a database.</summary>
    internal void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => Record(instrument.Name, instrument.IsObservable, value, tags);

    internal void Record(string instrumentName, bool observable, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var pool = "";
        var state = "";
        foreach (var tag in tags)
        {
            if (tag.Key == "pool.name") pool = tag.Value?.ToString() ?? "";
            else if (tag.Key == "state") state = tag.Value?.ToString() ?? "";
        }
        // Histograms (durations, create times) are not counts; they are left out.
        if (instrumentName.EndsWith(".duration", StringComparison.Ordinal) || instrumentName.EndsWith(".create_time", StringComparison.Ordinal))
            return;

        var key = (instrumentName, pool, state);
        if (observable)
            _observed[key] = value;
        else
            _summed.AddOrUpdate(key, value, (_, existing) => existing + value);
    }

    private string Value(string instrument, string pool, string state)
    {
        var key = (instrument, pool, state);
        if (_observed.TryGetValue(key, out var observed)) return observed.ToString("0", CultureInfo.InvariantCulture);
        if (_summed.TryGetValue(key, out var summed)) return summed.ToString("0", CultureInfo.InvariantCulture);
        return "-";
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}
