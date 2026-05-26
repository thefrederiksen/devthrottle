using CcDirector.Core.Utilities;

namespace CcDirector.Core.Scheduler;

/// <summary>
/// Single-leader election across all cc-director processes on this machine via a
/// Windows named kernel mutex (<c>Global\cc-director-scheduler</c> by default).
///
/// How it works:
///   * Each process opens (or creates) a handle to the named mutex on a dedicated
///     background thread, then attempts ownership with <c>WaitOne(0)</c>.
///   * Exactly one process can hold the mutex. That process becomes leader.
///   * Followers re-poll on a fixed interval (default 30s). When the previous
///     leader exits cleanly or crashes, the kernel releases the mutex and the
///     next polling follower acquires it. Crash-recovery produces an
///     <see cref="AbandonedMutexException"/>, which we treat as a successful
///     acquisition (the documented .NET pattern).
///
/// Threading note: a Windows kernel mutex is owned by a specific OS thread. Both
/// <c>WaitOne</c> and <c>ReleaseMutex</c> must run on the same thread. We park
/// all election state on a single dedicated background thread to satisfy this,
/// even while the leader is "idle" (just holding the mutex).
/// </summary>
public sealed class MutexLeaderElection : IDisposable
{
    public const string DefaultMutexName = @"Global\cc-director-scheduler";

    private readonly string _name;
    private readonly TimeSpan _followerPollInterval;
    private readonly ManualResetEventSlim _shutdown = new(false);

    private Thread? _thread;
    private Mutex? _mutex;
    private volatile bool _isLeader;
    private bool _disposed;

    public event Action<bool>? LeadershipChanged;

    public bool IsLeader => _isLeader;
    public string MutexName => _name;

    public MutexLeaderElection(string? name = null, TimeSpan? followerPollInterval = null)
    {
        _name = name ?? DefaultMutexName;
        _followerPollInterval = followerPollInterval ?? TimeSpan.FromSeconds(30);
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Scheduler-Leader-Election",
        };
        _thread.Start();
    }

    public void Stop()
    {
        SignalAndJoin();
    }

    private void SignalAndJoin()
    {
        if (_thread == null) return;
        // Set() on a disposed event throws. Swallow it: if Dispose has already
        // run, the thread has already exited; calling Stop afterwards is a
        // safe no-op.
        try { _shutdown.Set(); }
        catch (ObjectDisposedException) { return; }
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    private void Loop()
    {
        // This is a raw background thread; any exception that escapes it would be
        // unhandled and terminate the process. The inner loop already guards its
        // body, but this outer guard ensures even the loop condition or teardown
        // (e.g. a raced ObjectDisposedException) cannot crash the app.
        try
        {
            LoopCore();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LeaderElection] Loop terminated by unexpected error: {ex.Message}");
        }
    }

    private void LoopCore()
    {
        var pid = Environment.ProcessId;
        FileLog.Write($"[LeaderElection] Started pid={pid} mutex={_name}");

        bool followerLogged = false;

        while (!_shutdown.IsSet)
        {
            try
            {
                _mutex ??= new Mutex(initiallyOwned: false, _name);

                if (!_isLeader)
                {
                    bool acquired;
                    try
                    {
                        acquired = _mutex.WaitOne(0, exitContext: false);
                    }
                    catch (AbandonedMutexException)
                    {
                        // Previous owner died abnormally; we now own the mutex.
                        FileLog.Write($"[LeaderElection] Acquired mutex from abandoned previous owner pid={pid}");
                        acquired = true;
                    }

                    if (acquired)
                    {
                        _isLeader = true;
                        followerLogged = false;
                        FileLog.Write($"[LeaderElection] now scheduler leader pid={pid}");
                        SafeInvoke(true);
                    }
                    else if (!followerLogged)
                    {
                        FileLog.Write($"[LeaderElection] scheduler follower, polling for leadership pid={pid}");
                        followerLogged = true;
                    }
                }

                _shutdown.Wait(_isLeader ? TimeSpan.FromSeconds(60) : _followerPollInterval);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LeaderElection] Loop error: {ex.Message}");
                _shutdown.Wait(_followerPollInterval);
            }
        }

        if (_isLeader && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
                FileLog.Write($"[LeaderElection] stepping down pid={pid}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LeaderElection] ReleaseMutex error: {ex.Message}");
            }
            _isLeader = false;
            SafeInvoke(false);
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private void SafeInvoke(bool isLeader)
    {
        try { LeadershipChanged?.Invoke(isLeader); }
        catch (Exception ex) { FileLog.Write($"[LeaderElection] LeadershipChanged handler error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Join the loop thread BEFORE marking disposed and BEFORE disposing
        // the shutdown event, so the thread does not call Wait on a disposed
        // ManualResetEventSlim.
        SignalAndJoin();
        _disposed = true;
        _shutdown.Dispose();
    }
}
