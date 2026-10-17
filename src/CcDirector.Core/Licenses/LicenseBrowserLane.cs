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
/// </summary>
public sealed class LicenseBrowserLane
{
    private readonly object _gate = new();
    private int _working;
    private bool _checking;
    private TaskCompletionSource _changed = NewSignal();

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

    /// <summary>Enters for a sign-in or an approval: at once, unless a check is running, and then when it ends. Any
    /// number may be in at once. Dispose the answer to leave.</summary>
    public async Task<IDisposable> EnterWorkAsync(string what, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_checking)
                {
                    _working++;
                    return new Leaver(this, check: false);
                }
                wait = _changed.Task;
            }
            FileLog.Write($"[LicenseBrowserLane] EnterWorkAsync: {what} waits for the approver check that is running");
            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Enters for one approver check: when no sign-in, no approval and no other check is running. Dispose the
    /// answer to leave.</summary>
    public async Task<IDisposable> EnterCheckAsync(CancellationToken cancellationToken)
    {
        var logged = false;
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (!_checking && _working == 0)
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
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (check) _checking = false;
            else _working--;
            changed = _changed;
            _changed = NewSignal();
        }
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
