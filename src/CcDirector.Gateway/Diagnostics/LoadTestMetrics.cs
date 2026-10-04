using System.Diagnostics;

namespace CcDirector.Gateway.Diagnostics;

/// <summary>
/// Stage 0 of the Gateway load-test plan (devthrottle_internal issue #1173, mission 05): the numbers that
/// did not exist and without which a load test is blind on exactly the thing it expects to break. The
/// read-model review says the hot cost is database work and N+1 reads held under process-wide locks, the
/// five-second display sweep has no overlap guard, and cross-tenant harm comes from shared locks. This
/// class measures those claims so the load-test harness can confirm or refute them with numbers:
///
///   * how long callers WAIT to enter the snooze registry's process-wide gate (the shared-lock claim);
///   * how many database reads the snooze registry performs (the N+1 claim - divide by roster requests);
///   * how long one <c>StampFleetRolesAndFold</c> pass takes (the fold cost);
///   * whether two display sweeps were ever in flight at once, and how many ticks the overlap guard skipped
///     because a pass was already running (the overlap-guard claim, and the guard's own effect);
///   * how many Director stream connections are held and how many pushes are in flight (the socket and
///     ingress-pressure numbers);
///   * server-side <c>GET /sessions</c> latency, beside what the load driver measures from outside.
///
/// MEASUREMENT ONLY, still: nothing here changes behavior, guards anything, or caches a read. The first two
/// fixes the numbers called for have since landed in the code they measure - one set-based snooze read per
/// fold (<c>SnoozeRegistry.HoldSnapshotFor</c>) and the sweep's overlap guard
/// (<c>GatewayHost.SweepDisplayStateTick</c>) - and this class stayed exactly as it was, because an
/// instrument that is edited alongside the thing it measures cannot be used to compare before with after.
/// The one addition is <see cref="SweepSkipped"/>, which counts what the guard now prevents; every counter
/// the 31 July baseline reported still means what it meant then. Everything is a lock-free counter or
/// a fixed-bucket histogram (a handful of interlocked adds per observation), cheap enough to stay on
/// permanently. Deliberately NO FileLog entry/exit logging in this class: these methods run millions of
/// times per minute under load, and a log line per observation would make the instrument the bottleneck
/// it exists to find.
///
/// Read it at <c>GET /diag/loadmetrics</c>; pass <c>?reset=true</c> to also start a fresh window, so each
/// load step reads its own numbers rather than a mix with the previous step's.
/// </summary>
public static class LoadTestMetrics
{
    /// <summary>Time callers spent waiting to ENTER the snooze registry's process-wide gate on the hot
    /// read path (HoldStateFor / IsExpired / SnoozeUntilFor). Near zero uncontended; the read-model
    /// review predicts this is what grows first under multi-tenant load.</summary>
    public static readonly DurationHistogram SnoozeLockWaitMs = new();

    /// <summary>Duration of one whole <c>StampFleetRolesAndFold</c> pass (roles + hold + color/label/triage
    /// over the stamped set, including its per-session snooze reads).</summary>
    public static readonly DurationHistogram FoldDurationMs = new();

    /// <summary>Server-side duration of <c>GET /sessions</c>, recorded by the access-log middleware. The
    /// outside-the-process twin of the load driver's own latency measurement.</summary>
    public static readonly DurationHistogram RosterDurationMs = new();

    /// <summary>Duration of one display-state sweep tick (all tenants), measured around the whole
    /// <c>ForEachTenant(Sweep)</c> pass in the five-second timer.</summary>
    public static readonly DurationHistogram SweepDurationMs = new();

    /// <summary>Duration of one DirectorHub push handler (PushSnapshot or PushDelta), which runs the
    /// store apply AND the synchronous fold observers inline on the hub invocation.</summary>
    public static readonly DurationHistogram HubPushDurationMs = new();

    private static long _snoozeDbReads;
    private static long _deviceCredentialLookups;
    private static long _rosterRequests;
    private static long _rosterNonSuccess;
    private static long _sweepTicks;
    private static long _sweepOverlaps;
    private static long _sweepSkipped;
    private static int _sweepInFlight;
    private static int _hubConnections;
    private static long _hubConnectionsTotal;
    private static long _hubPushEvents;
    private static int _hubPushInFlight;

    /// <summary>One database read performed by the snooze registry (each is one context + one query under
    /// the gate). Divided by <see cref="RosterRequestObserved"/>'s count, this is the N+1 measurement.</summary>
    public static void SnoozeDbReadObserved() => Interlocked.Increment(ref _snoozeDbReads);

    /// <summary>One per-request device-credential lookup by key hash (the uncached auth read).</summary>
    public static void DeviceCredentialLookupObserved() => Interlocked.Increment(ref _deviceCredentialLookups);

    /// <summary>One finished <c>GET /sessions</c> request, with its server-side elapsed time and status.</summary>
    public static void RosterRequestObserved(TimeSpan elapsed, int statusCode)
    {
        Interlocked.Increment(ref _rosterRequests);
        if (statusCode >= 400)
            Interlocked.Increment(ref _rosterNonSuccess);
        RosterDurationMs.Record(elapsed);
    }

    /// <summary>A display sweep tick is starting and is about to RUN. Returns the timestamp to hand to
    /// <see cref="SweepFinished"/>. If another tick is still in flight, the overlap the read-model review
    /// predicted has actually happened, and it is counted here - this class never prevents it.
    ///
    /// The overlap GUARD now lives at the sweep itself (<c>GatewayHost.SweepDisplayStateTick</c>), so in the
    /// ordinary case this counter stays at zero and <see cref="SweepSkipped"/> carries the ticks that did not
    /// run. The counter is deliberately kept rather than removed: it is the instrument that measured the
    /// defect (91 of 98 ticks on 31 July), and a zero from an instrument that can no longer report anything
    /// else would be worthless as evidence that the guard holds.</summary>
    public static long SweepStarting()
    {
        if (Interlocked.Increment(ref _sweepInFlight) > 1)
            Interlocked.Increment(ref _sweepOverlaps);
        Interlocked.Increment(ref _sweepTicks);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// A display sweep tick fired and was SKIPPED because a pass was already running. Counted as a tick (the
    /// timer did fire) AND as a skip, so <c>sweepTicks</c> keeps the meaning it had in the 31 July baseline -
    /// how many times the timer fired - and <c>sweepTicks - sweepSkipped</c> is how many actually ran.
    ///
    /// This exists so the guard cannot make its own effect invisible. Without it, a working guard and a dead
    /// instrument produce the same reading: zero overlaps.
    /// </summary>
    public static void SweepSkipped()
    {
        Interlocked.Increment(ref _sweepTicks);
        Interlocked.Increment(ref _sweepSkipped);
    }

    /// <summary>The matching end of <see cref="SweepStarting"/>. Call from a finally block.</summary>
    public static void SweepFinished(long startTimestamp)
    {
        SweepDurationMs.Record(Stopwatch.GetElapsedTime(startTimestamp));
        Interlocked.Decrement(ref _sweepInFlight);
    }

    /// <summary>A DirectorHub connection opened.</summary>
    public static void HubConnectionOpened()
    {
        Interlocked.Increment(ref _hubConnections);
        Interlocked.Increment(ref _hubConnectionsTotal);
    }

    /// <summary>A DirectorHub connection closed.</summary>
    public static void HubConnectionClosed() => Interlocked.Decrement(ref _hubConnections);

    /// <summary>A hub push handler (PushSnapshot/PushDelta) is starting. Returns the timestamp to hand to
    /// <see cref="HubPushFinished"/>. The in-flight count is the ingress-pressure gauge: SignalR queues
    /// per-connection, so a growing in-flight count means handlers are not keeping up with arrivals.</summary>
    public static long HubPushStarting()
    {
        Interlocked.Increment(ref _hubPushEvents);
        Interlocked.Increment(ref _hubPushInFlight);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>The matching end of <see cref="HubPushStarting"/>. Call from a finally block.</summary>
    public static void HubPushFinished(long startTimestamp)
    {
        HubPushDurationMs.Record(Stopwatch.GetElapsedTime(startTimestamp));
        Interlocked.Decrement(ref _hubPushInFlight);
    }

    /// <summary>
    /// The full snapshot the <c>/diag/loadmetrics</c> endpoint serves: every counter and histogram above,
    /// plus standard process numbers (CPU, working set, GC, thread pool) read live. With
    /// <paramref name="reset"/> the histograms and counters start a fresh window AFTER being read, so a
    /// load step can scrape its own numbers; the gauges (in-flight, connections) and process numbers are
    /// instantaneous and never reset.
    /// </summary>
    public static object Snapshot(bool reset)
    {
        var process = Process.GetCurrentProcess();
        // Read BEFORE anything resets: a failure here must not throw away a load step's histograms and counters.
        var memory = MemorySnapshot(process);
        var snapshot = new
        {
            capturedAtUtc = DateTime.UtcNow,
            snoozeLockWaitMs = SnoozeLockWaitMs.Snapshot(reset),
            foldDurationMs = FoldDurationMs.Snapshot(reset),
            rosterDurationMs = RosterDurationMs.Snapshot(reset),
            sweepDurationMs = SweepDurationMs.Snapshot(reset),
            hubPushDurationMs = HubPushDurationMs.Snapshot(reset),
            counters = new
            {
                snoozeDbReads = reset ? Interlocked.Exchange(ref _snoozeDbReads, 0) : Interlocked.Read(ref _snoozeDbReads),
                deviceCredentialLookups = reset ? Interlocked.Exchange(ref _deviceCredentialLookups, 0) : Interlocked.Read(ref _deviceCredentialLookups),
                rosterRequests = reset ? Interlocked.Exchange(ref _rosterRequests, 0) : Interlocked.Read(ref _rosterRequests),
                rosterNonSuccess = reset ? Interlocked.Exchange(ref _rosterNonSuccess, 0) : Interlocked.Read(ref _rosterNonSuccess),
                sweepTicks = reset ? Interlocked.Exchange(ref _sweepTicks, 0) : Interlocked.Read(ref _sweepTicks),
                sweepOverlaps = reset ? Interlocked.Exchange(ref _sweepOverlaps, 0) : Interlocked.Read(ref _sweepOverlaps),
                sweepSkipped = reset ? Interlocked.Exchange(ref _sweepSkipped, 0) : Interlocked.Read(ref _sweepSkipped),
                hubPushEvents = reset ? Interlocked.Exchange(ref _hubPushEvents, 0) : Interlocked.Read(ref _hubPushEvents),
                hubConnectionsTotal = reset ? Interlocked.Exchange(ref _hubConnectionsTotal, 0) : Interlocked.Read(ref _hubConnectionsTotal),
            },
            gauges = new
            {
                hubConnections = Volatile.Read(ref _hubConnections),
                hubPushInFlight = Volatile.Read(ref _hubPushInFlight),
                sweepInFlight = Volatile.Read(ref _sweepInFlight),
            },
            process = new
            {
                cpuTotalSeconds = process.TotalProcessorTime.TotalSeconds,
                workingSetBytes = process.WorkingSet64,
                gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                gcGen0 = GC.CollectionCount(0),
                gcGen1 = GC.CollectionCount(1),
                gcGen2 = GC.CollectionCount(2),
                gcTotalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                threadPoolThreads = ThreadPool.ThreadCount,
                threadPoolPendingWorkItems = ThreadPool.PendingWorkItemCount,
                processorCount = Environment.ProcessorCount,
            },
            memory,
        };
        return snapshot;
    }

    /// <summary>
    /// Where the process's memory is, so a working set far above the managed heap can be explained (Money Saver,
    /// 4 October 2026: 476 MB working set against an 87 MB heap). Three layers, each a subset of the next:
    /// <list type="bullet">
    /// <item>the managed heap as of the last collection, and how much of it is free fragments;</item>
    /// <item>what the garbage collector has COMMITTED - memory it holds for the heap whether or not anything lives
    /// in it, which a runtime setting (for example GCConserveMemory) can make it hand back;</item>
    /// <item>everything else in the process (native libraries, the runtime, compiled code).</item>
    /// </list>
    /// On Linux the kernel's own split of the resident memory is added: VmRSS (all resident memory, the working
    /// set), anonymous (heap and native allocations), file-backed (mapped assemblies and libraries, which the
    /// kernel can drop and reload) and shared. That file exists only on Linux; elsewhere the field is null,
    /// because there is nothing to read. On Linux RssAnon, not privateBytes, is the resident private figure:
    /// .NET reports privateBytes there from the process's virtual data size, which can sit far above the
    /// working set, so the "each a subset of the next" reading holds for privateBytes on Windows only.
    /// containerMemoryInUseBytes and memoryLimitBytes describe the whole machine or container the process runs
    /// in, as the garbage collector sees it - not memory this process holds.
    /// The garbage collector's configuration is included so the settings in effect are read, not assumed.
    /// </summary>
    internal static object MemorySnapshot(Process process)
    {
        var info = GC.GetGCMemoryInfo();
        return new
        {
            gcHeapAfterLastCollectionBytes = info.HeapSizeBytes,
            gcFragmentedBytes = info.FragmentedBytes,
            gcCommittedBytes = info.TotalCommittedBytes,
            containerMemoryInUseBytes = info.MemoryLoadBytes,
            memoryLimitBytes = info.TotalAvailableMemoryBytes,
            gcHighMemoryLoadThresholdBytes = info.HighMemoryLoadThresholdBytes,
            gcIsServer = System.Runtime.GCSettings.IsServerGC,
            gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
            gcConfiguration = GC.GetConfigurationVariables()
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            privateBytes = process.PrivateMemorySize64,
            workingSetBytes = process.WorkingSet64,
            linuxResident = File.Exists(ProcSelfStatus) ? ReadLinuxResident(File.ReadAllLines(ProcSelfStatus)) : null,
        };
    }

    private const string ProcSelfStatus = "/proc/self/status";

    /// <summary>The VmRSS, RssAnon, RssFile and RssShmem lines of /proc/self/status, in bytes (the file reports kB).
    /// A line the kernel does not print is absent from the result rather than reported as zero.</summary>
    internal static Dictionary<string, long> ReadLinuxResident(IEnumerable<string> statusLines)
    {
        var wanted = new[] { "RssAnon", "RssFile", "RssShmem", "VmRSS" };
        var result = new Dictionary<string, long>();
        foreach (var line in statusLines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            if (Array.IndexOf(wanted, name) < 0) continue;
            var parts = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[1] != "kB" || !long.TryParse(parts[0], out var kb))
                throw new FormatException($"/proc/self/status line not in the 'N kB' form: '{line}'");
            result[name + "Bytes"] = kb * 1024;
        }
        return result;
    }
}

/// <summary>
/// A fixed-bucket duration histogram: lock-free to record (three interlocked adds), approximate to read.
/// Percentiles are reported as the upper bound of the bucket the quantile falls in, which is accurate to
/// the bucket granularity below - ample for finding a ceiling, where the question is "did p95 cross 300
/// milliseconds", not "was it 301 or 304". Reset swaps the whole window atomically by reference, so a
/// racing record can at worst land in the window that was just read - one sample, not a corruption.
/// </summary>
public sealed class DurationHistogram
{
    /// <summary>Bucket upper bounds in milliseconds. The last bucket is unbounded.</summary>
    private static readonly double[] BoundsMs =
    {
        0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 300, 500, 800, 1000, 2500, 5000, 10000, 30000,
    };

    private sealed class Window
    {
        public readonly long[] Buckets = new long[BoundsMs.Length + 1];
        public long Count;
        /// <summary>Sum in microseconds, so an interlocked long carries fractional milliseconds.</summary>
        public long SumMicroseconds;
        public long MaxMicroseconds;
    }

    private Window _window = new();

    /// <summary>Record one duration.</summary>
    public void Record(TimeSpan elapsed)
    {
        var window = Volatile.Read(ref _window);
        var ms = elapsed.TotalMilliseconds;
        var micro = (long)(ms * 1000.0);
        var bucket = 0;
        while (bucket < BoundsMs.Length && ms > BoundsMs[bucket])
            bucket++;
        Interlocked.Increment(ref window.Buckets[bucket]);
        Interlocked.Increment(ref window.Count);
        Interlocked.Add(ref window.SumMicroseconds, micro);
        // Lock-free max: retry until our value is no longer larger than the stored one.
        long seen;
        while (micro > (seen = Interlocked.Read(ref window.MaxMicroseconds)))
            if (Interlocked.CompareExchange(ref window.MaxMicroseconds, micro, seen) == seen)
                break;
    }

    /// <summary>Record the time elapsed since a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public void RecordSince(long startTimestamp) => Record(Stopwatch.GetElapsedTime(startTimestamp));

    /// <summary>Read the window: count, mean, max, and approximate p50/p95/p99. With
    /// <paramref name="reset"/> a fresh window starts after the read.</summary>
    public object Snapshot(bool reset)
    {
        var window = reset ? Interlocked.Exchange(ref _window, new Window()) : Volatile.Read(ref _window);
        var count = Interlocked.Read(ref window.Count);
        var sumMicro = Interlocked.Read(ref window.SumMicroseconds);
        var maxMicro = Interlocked.Read(ref window.MaxMicroseconds);
        return new
        {
            count,
            meanMs = count == 0 ? 0 : sumMicro / 1000.0 / count,
            maxMs = maxMicro / 1000.0,
            p50Ms = Quantile(window, count, 0.50),
            p95Ms = Quantile(window, count, 0.95),
            p99Ms = Quantile(window, count, 0.99),
        };
    }

    private static double Quantile(Window window, long count, double quantile)
    {
        if (count == 0) return 0;
        var rank = (long)Math.Ceiling(quantile * count);
        long cumulative = 0;
        for (var i = 0; i < window.Buckets.Length; i++)
        {
            cumulative += Interlocked.Read(ref window.Buckets[i]);
            if (cumulative >= rank)
                return i < BoundsMs.Length ? BoundsMs[i] : BoundsMs[^1];
        }
        return BoundsMs[^1];
    }
}
