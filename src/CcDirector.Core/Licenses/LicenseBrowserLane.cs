using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching phase 6 (Track C): keeps the daily approver check out of the way of sign-ins and approvals.
///
/// A sign-in, an approval and a check all drive this machine's browser profiles through browser-harness, and two of
/// them on one profile at once collide (seen live: a daemon left attached to a profile made the next run fail). So a
/// check runs only while no sign-in and no approval is running, and a sign-in or approval that starts while a check is
/// running waits for that check to end.
///
/// WHY THIS LANE AND NOT THE SIGN-IN COORDINATOR'S ONE-AT-A-TIME LOCK OR THE KEY STORE LOCK. The key store lock is
/// about the key store, which a check never touches. The coordinator's lock is held for the whole of a sign-in,
/// including the wait for another machine's approval - and the approving machine can be THIS machine (a Director that
/// approves its own renewal). An approval that had to take that lock would wait for the sign-in that is waiting for it:
/// a deadlock until the sign-in's deadline. So sign-ins and approvals never wait for each other here; only a check
/// waits for them, and only a RUNNING check makes them wait. A check that is waiting holds nothing back, so a
/// sign-in that needs an approval from this machine always gets it.
///
/// WAITING WORK GOES FIRST, AND WAITS ONLY UNTIL IT IS TOO LATE (review of 4123). A sign-in or approval that is
/// waiting stops the NEXT check from starting, so a run of many checks lets it in after the one running now, never
/// after the whole run. And it waits only as long as its caller says it may still start: a sign-in or approval that
/// would start after the Gateway stopped listening for it is never started at all, because an approval pressed for a
/// sign-in that was already abandoned is spent for nothing.
/// </summary>
public sealed class LicenseBrowserLane
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _working;
    private int _waitingWork;
    private bool _checking;
    private TaskCompletionSource _changed = NewSignal();

    /// <param name="delay">Test seam: how the longest wait for a running check passes.</param>
    public LicenseBrowserLane(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
    }

    /// <summary>How many sign-ins and approvals are running now.</summary>
    public int Working
    {
        get { lock (_gate) return _working; }
    }

    /// <summary>Whether a check is running now.</summary>
    public bool Checking
    {
        get { lock (_gate) return _checking; }
    }

    /// <summary>How many sign-ins and approvals are waiting for a running check to end.</summary>
    public int WaitingWork
    {
        get { lock (_gate) return _waitingWork; }
    }

    /// <summary>
    /// Enters for a sign-in or an approval: at once, unless a check is running, and then when it ends - but never after
    /// <paramref name="longestWait"/>, when it answers null and the caller must not start. Any number may be in at
    /// once. Dispose the answer to leave. While it waits, no further check starts.
    /// </summary>
    /// <param name="longestWait">How long the work may wait and still start in time; zero or less gives up at once
    /// when a check is running.</param>
    public async Task<IDisposable?> EnterWorkAsync(string what, TimeSpan longestWait, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            if (!_checking)
            {
                _working++;
                return new Leaver(this, check: false);
            }
            _waitingWork++;
            wait = _changed.Task;
        }
        FileLog.Write($"[LicenseBrowserLane] EnterWorkAsync: {what} waits for the approver check that is running, at most {longestWait.TotalSeconds:0} seconds");
        CancellationTokenSource? stopTimer = null;
        try
        {
            // Inside the try: a timer that could not be started must still stop this wait counting, or no check would
            // ever run again.
            stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tooLate = longestWait > TimeSpan.Zero ? _delay(longestWait, stopTimer.Token) : Task.CompletedTask;
            while (true)
            {
                var first = await Task.WhenAny(wait, tooLate);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    // Looked at before the timer: work whose check ended in the same moment as its wait still starts.
                    if (!_checking)
                    {
                        _waitingWork--;
                        _working++;
                        return new Leaver(this, check: false);
                    }
                    if (first == tooLate)
                    {
                        _waitingWork--;
                        FileLog.Write($"[LicenseBrowserLane] EnterWorkAsync: {what} gave up, the approver check was still running when it could no longer start in time");
                        Signal();
                        return null;
                    }
                    wait = _changed.Task;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _waitingWork--;
                Signal();
            }
            throw;
        }
        finally
        {
            stopTimer?.Cancel();
            stopTimer?.Dispose();
        }
    }

    /// <summary>Enters for one approver check: when no sign-in, no approval and no other check is running, and no
    /// sign-in or approval is waiting to. Dispose the answer to leave.</summary>
    public async Task<IDisposable> EnterCheckAsync(CancellationToken cancellationToken)
    {
        var logged = false;
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_checking && _working == 0 && _waitingWork == 0)
                {
                    _checking = true;
                    return new Leaver(this, check: true);
                }
                wait = _changed.Task;
            }
            if (!logged)
            {
                FileLog.Write("[LicenseBrowserLane] EnterCheckAsync: the approver check waits for a sign-in or approval to finish");
                logged = true;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private void Leave(bool check)
    {
        lock (_gate)
        {
            if (check) _checking = false;
            else _working--;
            Signal();
        }
    }

    /// <summary>Wakes every waiter to look again. Called under the gate; the waiters run on their own, not here.</summary>
    private void Signal()
    {
        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Leaver : IDisposable
    {
        private readonly LicenseBrowserLane _lane;
        private readonly bool _check;
        private int _left;

        public Leaver(LicenseBrowserLane lane, bool check)
        {
            _lane = lane;
            _check = check;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) _lane.Leave(_check);
        }
    }
}
