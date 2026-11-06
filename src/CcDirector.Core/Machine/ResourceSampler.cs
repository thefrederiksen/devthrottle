using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// The Director's resource sampler (the Resource Monitor mission, issue #2811): reads the probe every
/// <see cref="ResourceSampling.Interval"/>, keeps the last hour in a ring buffer, folds the history into a
/// verdict and raises a crossing when a resource moves across a limit. <see cref="IResourceSampler"/> is the
/// contract; this class keeps it.
///
/// THE VERDICT IS HANDED IN, NOT WORKED OUT HERE. The limits and the 60-second rule belong to
/// <c>ResourcePressure.Evaluate</c> in Core. The sampler takes that function as a constructor argument so a
/// test can drive every verdict it wants, and so the sampler cannot grow an opinion of its own about what a
/// figure means.
///
/// WHAT IT COSTS. One probe read, one copy of at most 720 references and one evaluation every five seconds,
/// on a thread-pool timer. No subprocess, and nothing on the user-interface thread. The mission's budget is
/// under 0.5 per cent of one core over ten minutes, and the live load harness measures it.
/// </summary>
public sealed class ResourceSampler : IResourceSampler
{
    private readonly IResourceProbe _probe;
    private readonly Func<IReadOnlyList<TimelineSample>, ResourcePressureVerdict> _evaluate;
    private readonly TimeProvider _time;
    private readonly ResourceCrossingDetector _detector = new();
    private readonly object _gate = new();
    private readonly TimelineSample[] _ring = new TimelineSample[ResourceSampling.HistoryCapacity];
    private readonly long _timelineOrigin;

    private int _ringStart;
    private int _ringCount;
    private ResourceSamplerState _current = ResourceSamplerState.NotYetSampled;
    private long _latestTimestamp;
    private ITimer? _timer;
    private bool _disposed;
    private readonly object _tickLock = new();

    /// <summary>Creates the sampler. It reads nothing until <see cref="Start"/>.</summary>
    /// <param name="probe">The machine's probe.</param>
    /// <param name="evaluate">
    /// Folds the history, oldest first, into a verdict - <see cref="ResourcePressure.Evaluate"/>. Each sample travels
    /// with its place on this sampler's monotonic timeline, so the fold measures its windows without ever
    /// subtracting two wall-clock stamps.
    /// </param>
    /// <param name="time">The clock for the timer and for ageing the latest sample.</param>
    public ResourceSampler(
        IResourceProbe probe,
        Func<IReadOnlyList<TimelineSample>, ResourcePressureVerdict> evaluate,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(time);
        _probe = probe;
        _evaluate = evaluate;
        _time = time;
        _timelineOrigin = time.GetTimestamp();
    }

    /// <inheritdoc />
    public ResourceSamplerState Current
    {
        get { lock (_gate) return _current; }
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourceSample> History
    {
        get
        {
            lock (_gate)
            {
                var copy = new ResourceSample[_ringCount];
                for (var i = 0; i < _ringCount; i++)
                    copy[i] = _ring[(_ringStart + i) % _ring.Length].Sample;
                return copy;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan? LatestAge
    {
        get
        {
            lock (_gate)
            {
                if (_current.Latest is null) return null;
                return _time.GetElapsedTime(_latestTimestamp);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ResourceSamplerState>? Sampled;

    /// <inheritdoc />
    public event EventHandler<ResourceCrossingEventArgs>? Crossed;

    /// <inheritdoc />
    public void Start()
    {
        FileLog.Write($"[ResourceSampler] Start: interval={ResourceSampling.Interval.TotalSeconds}s, capacity={ResourceSampling.HistoryCapacity}");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
                throw new InvalidOperationException("The resource sampler has already been started.");
            _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, ResourceSampling.Interval);
        }
    }

    /// <summary>
    /// One read: probe, record, fold, raise. Public so a test can step the sampler without a timer; the timer
    /// calls exactly this. Reads are serialised, so a slow probe on a struggling machine never runs two at once.
    /// </summary>
    public void Tick()
    {
        if (!Monitor.TryEnter(_tickLock))
        {
            // The previous read is still running - a probe slower than five seconds on a struggling machine.
            // Skipping is honest: the next sample is simply later, and the 60-second rule measures held time.
            FileLog.Write("[ResourceSampler] Tick: skipped, the previous read has not finished");
            return;
        }

        try
        {
            TickOnce();
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    private void TickOnce()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        ResourceSample sample;
        try
        {
            sample = _probe.Read();
        }
        catch (Exception ex)
        {
            // The probe contract says Read never throws, so this is a defect in a probe. Skip this read and
            // keep the timer, so one broken read does not end the meter for the rest of the Director's life.
            FileLog.Write($"[ResourceSampler] Tick FAILED: the probe threw, which its contract forbids; this read is skipped: {ex}");
            return;
        }

        ResourceSamplerState state;
        IReadOnlyList<ResourceCrossing> crossings;
        lock (_gate)
        {
            if (_disposed) return;
            _latestTimestamp = _time.GetTimestamp();
            Append(new TimelineSample(sample, _time.GetElapsedTime(_timelineOrigin, _latestTimestamp)));
            var verdict = _evaluate(CopyHistory());
            state = new ResourceSamplerState(sample, verdict);
            _current = state;
            crossings = _detector.Observe(verdict, sample.TakenAtUtc);
        }

        Raise(Sampled, state, nameof(Sampled));
        foreach (var crossing in crossings)
            Raise(Crossed, new ResourceCrossingEventArgs(crossing, state), nameof(Crossed));
    }

    /// <summary>
    /// Stops the timer AND WAITS FOR A READ ALREADY RUNNING TO FINISH, so when this returns no handler of
    /// <see cref="Sampled"/> or <see cref="Crossed"/> is running or will run again. That is what lets an owner
    /// dispose what those handlers write to - the 24-hour recording is not safe to write and dispose on two
    /// threads, and the Director shuts down on a different thread from the timer.
    /// </summary>
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();

        FileLog.Write("[ResourceSampler] Dispose: timer stopped, waiting for any read in progress");
        lock (_tickLock)
        {
            // Nothing to do: taking the lock is the wait. A read that starts after this sees _disposed and
            // returns before raising anything.
        }
        FileLog.Write("[ResourceSampler] Dispose: stopped");
    }

    // The buffer is bounded by COUNT, never by comparing timestamps: a wall clock that jumps cannot empty it
    // or let it grow.
    private void Append(TimelineSample sample)
    {
        if (_ringCount < _ring.Length)
        {
            _ring[(_ringStart + _ringCount) % _ring.Length] = sample;
            _ringCount++;
            return;
        }

        _ring[_ringStart] = sample;
        _ringStart = (_ringStart + 1) % _ring.Length;
    }

    private TimelineSample[] CopyHistory()
    {
        var copy = new TimelineSample[_ringCount];
        for (var i = 0; i < _ringCount; i++)
            copy[i] = _ring[(_ringStart + i) % _ring.Length];
        return copy;
    }

    private void Raise<T>(EventHandler<T>? handlers, T args, string eventName)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)handler).Invoke(this, args);
            }
            catch (Exception ex)
            {
                // No subscriber can stall the sampler: a handler that throws is logged and the next one runs.
                FileLog.Write($"[ResourceSampler] {eventName} handler FAILED: {ex}");
            }
        }
    }
}
