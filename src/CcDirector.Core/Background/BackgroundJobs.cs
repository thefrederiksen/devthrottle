using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Background;

/// <summary>The four tiers of docs/BackgroundWork.md, plus the two kinds that need no cadence.</summary>
public enum BackgroundJobTier
{
    /// <summary>A file or directory changed; recompute only that. Never a timer.</summary>
    OnChange,
    /// <summary>Runs while a screen is open and effectively visible; stops when it closes.</summary>
    OnView,
    /// <summary>A real timer, in minutes, for things with no change signal.</summary>
    SlowAndSteady,
    /// <summary>Leaves the machine to answer a question only a person cares about: on demand, cached.</summary>
    NeverOnATimer,
    /// <summary>A debounce or a one-shot delay that fires once after an event.</summary>
    Once,
    /// <summary>A loop that reads a child process's output as it arrives and ends when the process does.</summary>
    WhileAProcessRuns,
}

/// <summary>
/// What a recurring job declares when it registers: the row of docs/BackgroundWork.md that describes
/// it, in code. <paramref name="Cadence"/> is the least time allowed between two runs - null for a
/// job that only runs when something changed. <paramref name="IsOn"/> is the off switch: a job whose
/// switch answers false is skipped and the skip is counted, never run.
/// </summary>
public sealed record BackgroundJobSpec(
    string Name,
    BackgroundJobTier Tier,
    TimeSpan? Cadence,
    string OffSwitch,
    Func<bool>? IsOn = null)
{
    /// <summary>The most runs an hour the cadence allows, or null for a job with no cadence.</summary>
    public int? CeilingPerHour => Cadence is { } c && c > TimeSpan.Zero
        ? (int)Math.Ceiling(TimeSpan.FromHours(1) / c)
        : null;
}

/// <summary>
/// One job's state, as the Background work page and the log read it. A job kind may have several
/// instances at once (one deletion reaper per session manager, one heartbeat per host); the
/// registry's snapshot folds them into one row per name, with <paramref name="Instances"/> saying
/// how many, and the counts summed. The ceiling is per instance, so a kind with three instances
/// is over its ceiling when its runs exceed three times the ceiling.
/// </summary>
public sealed record BackgroundJobSnapshot(
    string Name,
    BackgroundJobTier Tier,
    TimeSpan? Cadence,
    int? CeilingPerHour,
    DateTime? LastRunUtc,
    TimeSpan? LastDuration,
    int RunsInLastHour,
    int SkippedOff,
    int SkippedTooSoon,
    int Failures,
    bool Running,
    int Instances = 1)
{
    /// <summary>
    /// True only when this row shows MORE runs than its cadence can explain, fencepost included.
    ///
    /// <para>The <c>+ 1</c> is the fencepost and it is not slack (issue #4852). A trailing hour that is
    /// closed at both ends holds one more timestamp than it has intervals, and on a real clock whether
    /// the oldest run falls inside or outside the horizon is decided by timer lateness and by the rate
    /// of the wall clock against the monotonic clock the timer schedules on. Comparing against
    /// <c>ceiling</c> alone therefore marked every healthy job on a fixed cadence as over its ceiling,
    /// on every run, for as long as the Director lived - sixteen of them at once on one machine. An
    /// indicator that is red for everything cannot report the runaway it exists to catch.</para>
    ///
    /// <para>The bound is EXACT, not approximate, because the trailing hour counts the times runs
    /// STARTED. A cadence bounds the fires, so an hour holds at most <c>ceiling + 1</c> of them. Were
    /// the queue to hold END times instead it could hold one more, because an end is a fire shifted by
    /// a varying duration - see the comment where a run is enqueued.</para>
    ///
    /// <para>It is per instance, so a folded row of three instances may legitimately show
    /// <c>3 * (ceiling + 1)</c>. This must agree with the log line in <c>BackgroundJob.RunAsync</c>.</para>
    /// </summary>
    public bool OverCeiling => CeilingPerHour is { } ceiling && RunsInLastHour > (ceiling + 1) * Math.Max(1, Instances);
}

/// <summary>
/// The one place every recurring job registers, so "off the window's thread, parallelism-limited,
/// no faster than its cadence, stops when its switch is off, counted" is a property of the scheduler
/// rather than a promise in every class.
///
/// On 22 September 2026 one Director ran 808,945 git commands in fourteen hours because a loop fed
/// itself and nothing counted it. A job registered here cannot run faster than its cadence, cannot
/// run while its switch is off, cannot overlap itself, runs on the pool with at most
/// <see cref="Parallelism"/> jobs at once, and is counted - so a job over its ceiling is visible
/// in <see cref="Snapshot"/> and in the log before it is visible in a CPU graph.
///
/// A job's WORK is still its own class; this owns only when it runs. The register at
/// docs/BackgroundWork.md and its test keep the set of timers in the code in step with the rows;
/// a job that has moved here has no timer of its own and a site count of 0 in that register.
/// </summary>
public sealed class BackgroundJobs
{
    /// <summary>The process-wide registry. Tests build their own.</summary>
    public static BackgroundJobs Default { get; } = new();

    /// <summary>How many jobs may run at once.</summary>
    public int Parallelism { get; }

    private readonly SemaphoreSlim _slots;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<BackgroundJob, byte> _jobs = new();

    public BackgroundJobs(int parallelism = 4, Func<DateTime>? nowUtc = null)
    {
        if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
        Parallelism = parallelism;
        _slots = new SemaphoreSlim(parallelism, parallelism);
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Register a job. The name is the job's row in docs/BackgroundWork.md; a kind that exists once
    /// per session manager or per host registers once per instance under the same name, and the
    /// snapshot folds the instances into one row. The job does nothing until it is triggered or its
    /// timer is started.
    /// </summary>
    public BackgroundJob Register(BackgroundJobSpec spec, Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(spec.Name)) throw new ArgumentException("a job needs a name", nameof(spec));

        var job = new BackgroundJob(this, spec, run);
        _jobs.TryAdd(job, 0);
        FileLog.Write($"[BackgroundJobs] registered '{spec.Name}': {spec.Tier}, cadence={(spec.Cadence is { } c ? c.ToString() : "on change")}, off switch: {spec.OffSwitch}");
        return job;
    }

    /// <summary>One row per job name, instances folded, for the Background work page and for tests.</summary>
    public IReadOnlyList<BackgroundJobSnapshot> Snapshot() =>
        _jobs.Keys.Select(j => j.Snapshot())
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.First() : g.First() with
            {
                Instances = g.Count(),
                LastRunUtc = g.Max(s => s.LastRunUtc),
                LastDuration = g.OrderByDescending(s => s.LastRunUtc).First().LastDuration,
                RunsInLastHour = g.Sum(s => s.RunsInLastHour),
                SkippedOff = g.Sum(s => s.SkippedOff),
                SkippedTooSoon = g.Sum(s => s.SkippedTooSoon),
                Failures = g.Sum(s => s.Failures),
                Running = g.Any(s => s.Running),
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    internal void Forget(BackgroundJob job) => _jobs.TryRemove(job, out _);
    internal Func<DateTime> NowUtc => _nowUtc;
    internal SemaphoreSlim Slots => _slots;
}

/// <summary>
/// One registered job. <see cref="Trigger"/> asks for a run now (an on-change job's event, or a
/// timer's tick); <see cref="StartTimer"/> arms the cadence for a slow-and-steady job. A run asked
/// for while one is in progress is remembered and one trailing run follows, so nothing asked for is
/// lost and nothing runs twice for the same reason.
/// </summary>
public sealed class BackgroundJob : IDisposable
{
    public BackgroundJobSpec Spec { get; }

    private readonly BackgroundJobs _owner;
    private readonly Func<CancellationToken, Task> _run;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<DateTime> _runsLastHour = new();
    private Timer? _timer;
    private Task? _current;
    private bool _running;
    private bool _askedAgain;
    private DateTime? _lastRunUtc;
    private TimeSpan? _lastDuration;
    private int _skippedOff, _skippedTooSoon, _failures;
    private bool _disposed;

    internal BackgroundJob(BackgroundJobs owner, BackgroundJobSpec spec, Func<CancellationToken, Task> run)
    {
        _owner = owner;
        Spec = spec;
        _run = run;
    }

    /// <summary>
    /// Arm the cadence: the job runs every <see cref="BackgroundJobSpec.Cadence"/>, first after
    /// <paramref name="firstAfter"/> (the cadence itself when null; zero for at once). Only a job
    /// with a cadence can be armed.
    /// </summary>
    public void StartTimer(TimeSpan? firstAfter = null)
    {
        if (Spec.Cadence is not { } cadence)
            throw new InvalidOperationException($"'{Spec.Name}' has no cadence and cannot run on a timer - it runs when triggered");
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new Timer(_ => Trigger(fromTimer: true), null, firstAfter ?? cadence, cadence);
        }
    }

    /// <summary>Ask for a run now. Returns true when a run was started or queued, false when it was skipped.</summary>
    public bool Trigger() => Trigger(fromTimer: false);

    private bool Trigger(bool fromTimer)
    {
        lock (_gate)
        {
            if (_disposed) return false;

            if (Spec.IsOn is { } isOn && !SafeIsOn(isOn))
            {
                _skippedOff++;
                return false;
            }

            // A trigger that is not the timer's own tick may not beat the cadence. Ten percent of
            // slack covers a timer's jitter without letting a burst of triggers double the rate.
            if (!fromTimer && Spec.Cadence is { } cadence && _lastRunUtc is { } last
                && _owner.NowUtc() - last < cadence * 0.9)
            {
                _skippedTooSoon++;
                return false;
            }

            if (_running)
            {
                _askedAgain = true;
                return true;
            }
            _running = true;
            _current = RunAsync();
        }
        return true;
    }

    private bool SafeIsOn(Func<bool> isOn)
    {
        try { return isOn(); }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundJobs] '{Spec.Name}' off switch FAILED ({ex.Message}) - treated as off");
            return false;
        }
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        var started = _owner.NowUtc();
        try
        {
            await _owner.Slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Run(() => _run(ct), ct).ConfigureAwait(false);
            }
            finally
            {
                _owner.Slots.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed mid-run: a clean end.
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            FileLog.Write($"[BackgroundJobs] '{Spec.Name}' FAILED: {ex.Message}");
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                var ended = _owner.NowUtc();
                _lastRunUtc = ended;
                _lastDuration = ended - started;
                // THE QUEUE HOLDS START TIMES, NOT END TIMES, and that is what makes the
                // `ceiling + 1` bound in OverCeiling provably tight rather than nearly tight
                // (issue #4852, third option). A cadence bounds the FIRES, so a trailing hour can
                // hold at most `ceiling + 1` of them. End times are fires shifted by a varying
                // duration, so a window over ENDED times can hold one more post than any window
                // over fires - reachable when a run lasts nearly a whole cadence, which is
                // pathological but not impossible. Counting starts removes the case rather than
                // documenting it. `started` is read before the slot wait, so it tracks the fire.
                _runsLastHour.Enqueue(started);
                TrimLocked(ended);
                _running = false;
                again = _askedAgain;
                _askedAgain = false;

                // TOLERATE THE FENCEPOST (issue #4852). A closed trailing hour holds one more
                // timestamp than it has intervals, and on a real clock which side of the horizon the
                // oldest run falls is decided by timer lateness and by the wall clock's rate against
                // the monotonic clock the timer schedules on - not by anything the job did. So a job
                // running exactly at its cadence legitimately shows either `ceiling` or `ceiling + 1`
                // runs, and only MORE than that is evidence of a runaway. See OverCeiling, which must
                // agree with this line.
                if (Spec.CeilingPerHour is { } ceiling && _runsLastHour.Count > ceiling + 1)
                    FileLog.Write($"[BackgroundJobs] '{Spec.Name}' OVER ITS CEILING: {_runsLastHour.Count} runs in the last hour, allowed {ceiling}");
            }
            if (again)
                Trigger(fromTimer: false);
        }
    }

    /// <summary>
    /// Drop the runs that have left the trailing hour, leaving the window HALF-OPEN:
    /// <c>(now - 1h, now]</c>. A run exactly an hour old is out.
    ///
    /// <para>THIS IS A TIDY-UP, NOT THE FIX FOR #4852, and the distinction matters because the first
    /// draft of that fix claimed otherwise. Half-open makes the count mean the same thing
    /// <see cref="BackgroundJobSpec.CeilingPerHour"/> means - a job on an IDEAL clock running exactly on
    /// cadence yields exactly the ceiling - which is worth having for the number the Background work
    /// page displays.</para>
    ///
    /// <para>It fixes almost nothing on a real machine, and it was measured rather than reasoned about.
    /// This comparison and the previous one (<c>&lt; horizon</c>) differ only when a run's timestamp
    /// equals the horizon to the tick, which on a clock read from <c>DateTime.UtcNow</c> with
    /// millisecond timer jitter effectively never happens. What actually decides whether the hour holds
    /// <c>ceiling</c> or <c>ceiling + 1</c> timestamps is whether the span of the last <c>ceiling</c>
    /// intervals measures shorter or longer than an hour on the WALL clock - set by timer lateness, run
    /// duration, and the wall clock's rate against the monotonic clock the timer schedules on - and
    /// that is identical under both comparisons. Simulated over three hours of a two-second job: with a
    /// wall clock 1.68 parts per million slow, both the old and the new comparison alarm on every run.
    /// The fix is the tolerance in <see cref="BackgroundJobSnapshot.OverCeiling"/> and in the log line
    /// in <c>RunAsync</c>; this only makes the displayed count honest.</para>
    /// </summary>
    private void TrimLocked(DateTime now)
    {
        var horizon = now - TimeSpan.FromHours(1);
        while (_runsLastHour.Count > 0 && _runsLastHour.Peek() <= horizon)
            _runsLastHour.Dequeue();
    }

    public BackgroundJobSnapshot Snapshot()
    {
        lock (_gate)
        {
            TrimLocked(_owner.NowUtc());
            return new BackgroundJobSnapshot(
                Spec.Name, Spec.Tier, Spec.Cadence, Spec.CeilingPerHour,
                _lastRunUtc, _lastDuration, _runsLastHour.Count,
                _skippedOff, _skippedTooSoon, _failures, _running);
        }
    }

    /// <summary>
    /// TEST-ONLY. The run CURRENTLY in flight, so a test can await a run to COMPLETION - body,
    /// accounting and all - with no sleep and no clock read.
    ///
    /// <para>This exists because the accounting a test needs to observe (the run is enqueued into the
    /// trailing-hour window, the window is trimmed, <c>Running</c> clears) happens in
    /// <c>RunAsync</c>'s <c>finally</c>, AFTER the job body has returned. So signalling from the body
    /// is not enough, and the alternative - polling <c>Snapshot()</c> behind a timeout - is exactly
    /// the machine-dependent wait that <c>GateTestsDoNotDependOnTheMachineTests</c> refuses, and
    /// rightly: it decides pass or fail on how loaded the machine is. Awaiting this task is
    /// deterministic. <see cref="StopAsync"/> is not a substitute - it disposes the job, so it cannot
    /// be used between two runs.</para>
    ///
    /// <para>IT IS NOT "the run the last Trigger started", and a caller must not read it as that. A
    /// <see cref="Trigger()"/> that arrives while a run is in flight does not start one - it sets the
    /// ask-again flag and leaves <c>_current</c> alone - so reading this straight after such a trigger
    /// hands back the PREVIOUS run. A test must therefore establish that the job is idle before it
    /// triggers; the helper in BackgroundJobsTests asserts exactly that, so a test which triggers into
    /// a run in flight fails on the assertion rather than silently awaiting the wrong task.</para>
    /// </summary>
    internal Task? InFlightRunForTest
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// <see cref="Dispose"/>, then wait for a run that was in flight to finish winding down. For an
    /// owner whose teardown must not overlap the job's last run - the turn pusher's sweep reads a
    /// stream client its host nulls at the top of its own stop - so "stopped" means stopped.
    /// </summary>
    public async Task StopAsync()
    {
        Dispose();
        Task? current;
        lock (_gate) current = _current;
        if (current is not null)
        {
            try { await current.ConfigureAwait(false); }
            catch (Exception) { /* the run's own failure was logged where it happened */ }
        }
    }

    /// <summary>Stop the timer, cancel a run in progress, and leave the registry.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _stop.Cancel();
        _owner.Forget(this);
        FileLog.Write($"[BackgroundJobs] '{Spec.Name}' stopped");
    }
}
