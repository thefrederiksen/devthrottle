using CcDirector.Core.Utilities;

namespace CcDirector.Core.Scheduler;

/// <summary>
/// Top-level scheduler singleton hosted inside the Avalonia app.
///
/// Combines:
///   * <see cref="MutexLeaderElection"/> -- so only one Director instance runs
///     the scheduler at a time when the user has several open.
///   * <see cref="CommQueueScheduler"/> -- the tick body that scans the
///     communications.db queue and invokes registered runners.
///   * A periodic timer that ticks the queue scheduler while this instance is
///     leader. Followers run the same timer but short-circuit immediately.
///
/// Lifecycle:
///   * Constructed in App startup, runners registered, then <see cref="Start"/>.
///   * <see cref="Stop"/> releases the leader mutex (via the election thread)
///     and disposes the timer. Called from App.OnShutdown.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly MutexLeaderElection _election;
    private readonly CommQueueScheduler _queueScheduler;
    private readonly LeaderIdentityStore? _leaderIdentityStore;
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _firstTickDelay;
    private readonly string? _runnersConfigPath;
    private readonly object _reloadGate = new();

    private Timer? _tickTimer;
    private FileSystemWatcher? _runnersWatcher;
    private Timer? _reloadDebounceTimer;
    private int _ticking;
    private int _started;

    public SchedulerService(
        TimeSpan? tickInterval = null,
        string? commQueueDbPath = null,
        string? mutexName = null,
        TimeSpan? followerPollInterval = null,
        TimeSpan? firstTickDelay = null,
        string? statePath = null,
        string? leaderIdentityPath = null,
        string? runnersConfigPath = null)
    {
        _tickInterval = tickInterval ?? TimeSpan.FromMinutes(5);
        _firstTickDelay = firstTickDelay ?? TimeSpan.FromSeconds(10);
        _runnersConfigPath = runnersConfigPath;
        _queueScheduler = new CommQueueScheduler(commQueueDbPath, statePath);
        _election = new MutexLeaderElection(mutexName, followerPollInterval);

        if (!string.IsNullOrEmpty(leaderIdentityPath))
        {
            _leaderIdentityStore = new LeaderIdentityStore(leaderIdentityPath);
            _election.LeadershipChanged += OnLeadershipChanged;
        }
    }

    private void OnLeadershipChanged(bool isLeader)
    {
        if (_leaderIdentityStore == null) return;
        if (isLeader) _leaderIdentityStore.Write();
        else _leaderIdentityStore.Delete();
    }

    /// <summary>Returns the current leader's identity (PID, exe name, since-when).
    /// Returns null if the file is missing, malformed, or the recorded PID is
    /// no longer alive. Safe to call from any process, including followers.</summary>
    public LeaderIdentityStore.IdentityRecord? GetLeaderIdentity() => _leaderIdentityStore?.Read();

    public CommQueueScheduler Queue => _queueScheduler;
    public bool IsLeader => _election.IsLeader;
    public string MutexName => _election.MutexName;
    public TimeSpan TickInterval => _tickInterval;

    public void RegisterRunner(RunnerRegistration runner) => _queueScheduler.RegisterRunner(runner);

    public IReadOnlyList<CommQueueScheduler.RunnerSnapshot> GetRunnerSnapshot() => _queueScheduler.GetSnapshot();

    /// <summary>Manual fire. Returns a failure result if this Director is not
    /// the scheduler leader (we don't want followers double-firing runners on
    /// the user's behalf while the leader is also running them).</summary>
    public CommQueueScheduler.RunNowResult RunNow(string runnerName)
    {
        if (!IsLeader)
        {
            return new CommQueueScheduler.RunNowResult(false,
                "This Director is not the scheduler leader; switch to the leader window to fire runners.");
        }
        return _queueScheduler.RunNow(runnerName);
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        FileLog.Write(
            $"[SchedulerService] Start pid={Environment.ProcessId} " +
            $"tickInterval={_tickInterval} runners={_queueScheduler.Runners.Count}");

        // Restore persisted LastFiredAt before starting the tick loop so a
        // recently-fired runner does not double-fire on restart.
        _queueScheduler.LoadPersistedState();

        _election.Start();

        // The timer fires every tick interval regardless of leader status; the
        // tick callback short-circuits on followers. This is cheaper than
        // coupling timer start/stop to leadership events and avoids missing a
        // tick during handoff.
        _tickTimer = new Timer(OnTick, null, _firstTickDelay, _tickInterval);

        if (!string.IsNullOrEmpty(_runnersConfigPath))
        {
            StartRunnersConfigWatcher();
        }
    }

    private void StartRunnersConfigWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_runnersConfigPath);
            var file = Path.GetFileName(_runnersConfigPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
            Directory.CreateDirectory(dir);

            _runnersWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
                             | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _runnersWatcher.Changed += OnRunnersFileChanged;
            _runnersWatcher.Created += OnRunnersFileChanged;
            _runnersWatcher.Renamed += OnRunnersFileChanged;

            FileLog.Write($"[SchedulerService] Hot-reload watcher armed on {_runnersConfigPath}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SchedulerService] Failed to start runners watcher: {ex.Message}");
        }
    }

    private void OnRunnersFileChanged(object? sender, FileSystemEventArgs e)
    {
        // Editors fire multiple events for a single save (write, rename, etc.).
        // Debounce so we reload at most once per ~500ms of quiet.
        lock (_reloadGate)
        {
            _reloadDebounceTimer?.Dispose();
            _reloadDebounceTimer = new Timer(_ => DoReload(), null,
                TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private void DoReload()
    {
        if (string.IsNullOrEmpty(_runnersConfigPath)) return;
        try
        {
            FileLog.Write($"[SchedulerService] Hot-reloading runners from {_runnersConfigPath}");
            var newRunners = RunnersConfig.LoadOrSeed(_runnersConfigPath);
            _queueScheduler.ReloadRunners(newRunners);
            // Re-apply persisted state for newly added runners (existing runners
            // keep their in-memory state -- LoadPersistedState skips entries
            // whose LastFiredAt is already populated).
            _queueScheduler.LoadPersistedState();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SchedulerService] Hot-reload FAILED: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;

        FileLog.Write($"[SchedulerService] Stop pid={Environment.ProcessId}");
        _tickTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _tickTimer?.Dispose();
        _tickTimer = null;

        if (_runnersWatcher != null)
        {
            try
            {
                _runnersWatcher.EnableRaisingEvents = false;
                _runnersWatcher.Dispose();
            }
            catch (Exception ex) { FileLog.Write($"[SchedulerService] runners watcher dispose error: {ex.Message}"); }
            _runnersWatcher = null;
        }
        lock (_reloadGate)
        {
            _reloadDebounceTimer?.Dispose();
            _reloadDebounceTimer = null;
        }

        _election.Stop();
    }

    private void OnTick(object? state)
    {
        // Skip if a previous tick is still running (DB read on slow disk, etc).
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            if (!_election.IsLeader) return;
            _queueScheduler.Tick(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SchedulerService] Tick error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    public void Dispose()
    {
        Stop();
        _election.Dispose();
    }
}
