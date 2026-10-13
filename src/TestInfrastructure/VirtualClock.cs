namespace CcDirector.TestInfrastructure;

/// <summary>
/// THE FAKE CLOCK THAT CANNOT MOVE WHILE THE WORK IT TIMES IS STILL RUNNING.
///
/// A test hands this clock to the code under test and to its test doubles, and <see cref="VirtualTime.RunAsync"/>
/// moves it. Three rules make the result depend on the code alone, never on how busy the machine is:
///
/// <list type="number">
/// <item>ONE THREAD MOVES THE CLOCK AND RUNS WHAT COMES DUE. A wait that comes due is continued by the thread that
/// moved the clock, before the clock moves again. Starting a wait never runs anything, on any thread. (The library
/// fake clock this replaced, <c>FakeTimeProvider</c>, lets ANY thread that starts a wait run every wait that is
/// due, and makes an advance from another thread return at once while it does. A spinner continuing on a test
/// runner thread therefore took over the send it was drawing for, and the advancing loop went on moving the clock
/// with nothing left to stop it: fake seconds passed while one file write was in flight.)</item>
/// <item>A WAIT IS CONTINUED WHERE IT WAS STARTED FROM, WITHOUT LEAVING THE THREAD. A wait started under a
/// synchronization context - a test double started from a test method, which runs under the test runner's - would
/// have its continuation queued to that context, to run whenever one of its threads is free. The clock remembers
/// the context a wait was started under and runs the wait's callback under the same one, so the continuation runs
/// inline. The test runner's threads being busy can no longer delay a spinner past a deadline.</item>
/// <item>THE CLOCK MOVES ONLY TO THE NEXT WAIT, AND NOT AT ALL WHEN NOTHING WAITS. When no wait is pending, the
/// work is running somewhere else or waits on something real; time is not what it is waiting for, so time stays
/// where it is until the work comes back to the clock or finishes.</item>
/// </list>
///
/// What the rules cannot see is work that left the thread by itself - a <c>Task.Run</c>, a continuation the pool
/// runs - while other waits are pending. That work shows itself the moment it starts a wait on this clock from a
/// thread that is not moving it, and the run then FAILS, every time and not only under load, naming where the wait
/// was started (<see cref="ThrowIfAWaitStartedOffTheMovingThread"/>).
///
/// THE LIMIT THAT IS LEFT, stated so nobody finds it the hard way: work that leaves the moving thread and never
/// comes back to the clock - it just finishes on a pool thread - cannot be seen at all. If a wait that repeats (a
/// spinner) is pending meanwhile, the clock is carried on at the speed of the processor until the pool gets to that
/// work. So the END of timed work must run inline too: a test that ends its work on a completion source makes a
/// plain one, never one with <c>RunContinuationsAsynchronously</c>. Three more things this clock does not do: an
/// await with <c>ConfigureAwait(false)</c> on a wait started under a synchronization context is queued to the pool
/// by the runtime (start such work with no context); a callback that BLOCKS its thread on a wait on this clock
/// waits for ever, because the blocked thread is the one that would move it; and a wait due at once runs at the
/// clock's next move, not inside the call that started it.
/// </summary>
public sealed class VirtualClock : TimeProvider
{
    /// <summary>Held by the one thread that is moving the clock and running what comes due.</summary>
    private readonly object _moving = new();

    /// <summary>Guards the time, the pending waits and the run's record.</summary>
    private readonly object _state = new();

    private readonly List<VirtualTimer> _pending = new();
    private DateTimeOffset _now;
    private long _order;
    private bool _running;
    private string? _startedOffTheMovingThread;
    private TaskCompletionSource _waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VirtualClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_state) return _now;
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new VirtualTimer(this, callback, state, SynchronizationContext.Current);
        Arm(timer, dueTime, period);
        return timer;
    }

    /// <summary>
    /// Move the clock forward by <paramref name="by"/>, running every wait that comes due on the way, in the order
    /// they come due, on the calling thread. For a test that moves the clock by hand; a test that waits for work to
    /// finish uses <see cref="VirtualTime.RunAsync"/>.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_moving)
        {
            var target = GetUtcNow() + by;
            while (RunNext(target)) { }
            lock (_state)
            {
                if (target > _now) _now = target;
            }
        }
    }

    /// <summary>Move the clock to the next pending wait and run it. False when nothing waits on the clock.</summary>
    internal bool RunNext() => RunNext(DateTimeOffset.MaxValue);

    private bool RunNext(DateTimeOffset notAfter)
    {
        lock (_moving)
        {
            VirtualTimer? next = null;
            lock (_state)
            {
                foreach (var timer in _pending)
                {
                    if (next is null || timer.Due < next.Due || (timer.Due == next.Due && timer.Order < next.Order))
                        next = timer;
                }
                if (next is null || next.Due > notAfter) return false;
                if (next.Due > _now) _now = next.Due;
                if (next.Period > TimeSpan.Zero)
                {
                    next.Due += next.Period;
                    next.Order = ++_order;
                }
                else
                {
                    _pending.Remove(next);
                }
            }
            next.Run();
            return true;
        }
    }

    /// <summary>Begin the work a run times, on the thread that will move the clock for it.</summary>
    internal Task Start(Func<Task> start)
    {
        lock (_moving) return start();
    }

    /// <summary>Mark a run as under way; the returned object ends it. One run at a time on one clock.</summary>
    internal IDisposable BeginRun()
    {
        lock (_state)
        {
            if (_running)
                throw new InvalidOperationException(
                    "This clock is already being moved by another VirtualTime run. Two runs moving one clock would each " +
                    "end the other's waits; await the first before starting the second.");
            _running = true;
            _startedOffTheMovingThread = null;
        }
        return new Run(this);
    }

    /// <summary>Completes the next time a wait is started on this clock. Take it BEFORE looking for a pending wait.</summary>
    internal Task NextWaitStarted
    {
        get { lock (_state) return _waitStarted.Task; }
    }

    /// <summary>
    /// Fail the run when, during it, a wait was started on this clock by a thread that was not moving it. That work
    /// left the moving thread by itself, so the clock was free to move while it ran: whatever the test decided, it
    /// decided on how long that work took on this machine.
    /// </summary>
    /// <param name="workFailure">The timed work's own failure, when it has already ended in one: kept as the inner
    /// exception, so the hop does not hide it.</param>
    internal void ThrowIfAWaitStartedOffTheMovingThread(Exception? workFailure = null)
    {
        string? where;
        lock (_state) where = _startedOffTheMovingThread;
        if (where is null) return;
        throw new InvalidOperationException(
            "A wait was started on the fake clock by a thread that was not moving it, while a VirtualTime run was under " +
            "way. The work being timed left the thread that moves the clock - a Task.Run, a continuation the thread " +
            "pool ran (a completion source made with RunContinuationsAsynchronously, a semaphore or gate that another " +
            "waiter held, an await with ConfigureAwait(false) on a wait started under a synchronization context), a " +
            "callback from a real timer or a real file read - so the clock could move while that work was still " +
            "running, and the test would then measure the machine. Keep the path on the moving thread: start delayed " +
            "work with a plain call instead of Task.Run, and complete what it awaits inline." +
            (workFailure is null ? "" : " The work itself also failed: " + workFailure.Message) +
            " The wait was started here:" + Environment.NewLine + where, workFailure);
    }

    private void Arm(VirtualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan) ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);
        TaskCompletionSource started;
        lock (_state)
        {
            _pending.Remove(timer);
            if (timer.Disposed || dueTime == Timeout.InfiniteTimeSpan) return;
            timer.Due = _now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            timer.Order = ++_order;
            _pending.Add(timer);
            if (_running && !Monitor.IsEntered(_moving))
                _startedOffTheMovingThread ??= Environment.StackTrace;
            started = _waitStarted;
            _waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        started.TrySetResult();
    }

    private void Remove(VirtualTimer timer)
    {
        lock (_state)
        {
            timer.Disposed = true;
            _pending.Remove(timer);
        }
    }

    private sealed class Run(VirtualClock clock) : IDisposable
    {
        public void Dispose()
        {
            lock (clock._state) clock._running = false;
        }
    }

    private sealed class VirtualTimer(VirtualClock clock, TimerCallback callback, object? state, SynchronizationContext? startedUnder) : ITimer
    {
        public DateTimeOffset Due;
        public TimeSpan Period;
        public long Order;
        public bool Disposed;

        /// <summary>
        /// Run the callback under the synchronization context the wait was started under. An await that captured
        /// that context continues inline when the task completes under the same one; under any other it is queued
        /// to the context, and runs whenever one of the context's threads is free.
        /// </summary>
        public void Run()
        {
            var before = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(startedUnder);
            try
            {
                callback(state);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(before);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._state)
            {
                if (Disposed) return false;
            }
            clock.Arm(this, dueTime, period);
            return true;
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
