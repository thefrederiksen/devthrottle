using CcDirector.Core.Utilities;
using CcDirector.Engine.Dispatcher;
using CcDirector.Engine.Events;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;

namespace CcDirector.Engine;

public sealed class EngineHost : IDisposable
{
    private readonly EngineOptions _options;
    private EngineDatabase? _db;
    private Scheduler? _scheduler;
    private CommunicationDispatcher? _dispatcher;
    private Task? _dispatcherInitTask;
    private DateTime _startedAt;

    public bool IsRunning { get; private set; }

    public event Action<EngineEvent>? OnEvent;

    public EngineHost(EngineOptions options)
    {
        _options = options;
    }

    public void Start()
    {
        FileLog.Write("[EngineHost] Starting engine");

        _db = new EngineDatabase(_options.DatabasePath);
        var executor = new JobExecutor(_db);
        _scheduler = new Scheduler(_db, executor, _options.CheckIntervalSeconds, _options.RunRetentionDays);
        _scheduler.OnEvent += e =>
        {
            try { OnEvent?.Invoke(e); }
            catch (Exception ex) { FileLog.Write($"[EngineHost] Event handler error: {ex.Message}"); }
        };

        _scheduler.Start();

        // Start communication dispatcher if configured. Email tool discovery spawns each
        // cc-* tool (cc-gmail, cc-outlook) with "accounts list --json" -- each probe is a
        // Python interpreter startup plus auth checks (~1-3s per tool) and they run
        // sequentially. Run discovery on the background so it doesn't block app startup;
        // the dispatcher comes online once discovery finishes.
        if (!string.IsNullOrEmpty(_options.CommunicationsDbPath))
        {
            FileLog.Write($"[EngineHost] Scheduling background communication dispatcher init: db={_options.CommunicationsDbPath}");
            _dispatcherInitTask = Task.Run(InitializeDispatcherAsync);
        }
        else
        {
            FileLog.Write("[EngineHost] Communication dispatcher not configured (no CommunicationsDbPath)");
        }

        _startedAt = DateTime.UtcNow;
        IsRunning = true;

        RaiseEvent(new EngineEvent(EngineEventType.EngineStarted, Message: "Engine started"));
        FileLog.Write("[EngineHost] Engine started");
    }

    public async Task StopAsync()
    {
        FileLog.Write("[EngineHost] Stopping engine");
        RaiseEvent(new EngineEvent(EngineEventType.EngineStopping, Message: "Engine stopping"));

        // Setting IsRunning=false first so the deferred dispatcher init bails out without
        // starting a brand-new dispatcher we'd then immediately need to stop.
        IsRunning = false;

        if (_dispatcherInitTask is { } initTask)
        {
            try
            {
                await initTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                FileLog.Write("[EngineHost] Deferred dispatcher init did not complete within 2s during shutdown");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[EngineHost] Deferred dispatcher init error during shutdown: {ex.Message}");
            }
        }

        _dispatcher?.Stop();

        if (_scheduler != null)
            await _scheduler.StopAsync(_options.ShutdownTimeoutSeconds);

        RaiseEvent(new EngineEvent(EngineEventType.EngineStopped, Message: "Engine stopped"));
        FileLog.Write("[EngineHost] Engine stopped");
    }

    private async Task InitializeDispatcherAsync()
    {
        try
        {
            var routingTable = await EmailToolDiscovery.DiscoverAsync(
                _options.BinDirectory, _options.EmailToolNames);
            FileLog.Write($"[EngineHost] Discovered {routingTable.Count} email routes");

            if (!IsRunning)
            {
                FileLog.Write("[EngineHost] InitializeDispatcherAsync: engine no longer running, skipping dispatcher start");
                return;
            }

            var dispatcher = new CommunicationDispatcher(
                _options.CommunicationsDbPath,
                routingTable,
                _options.DispatcherPollIntervalSeconds);
            dispatcher.OnEvent += e =>
            {
                try { OnEvent?.Invoke(e); }
                catch (Exception ex) { FileLog.Write($"[EngineHost] Event handler error: {ex.Message}"); }
            };
            dispatcher.Start();
            _dispatcher = dispatcher;
            FileLog.Write("[EngineHost] Communication dispatcher started (deferred)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[EngineHost] InitializeDispatcherAsync FAILED: {ex.Message}");
        }
    }

    public EngineStatus GetStatus()
    {
        if (_db == null)
        {
            return new EngineStatus
            {
                IsRunning = false,
                StartedAt = DateTime.MinValue
            };
        }

        var allJobs = _db.ListJobs(includeDisabled: true);
        var enabledJobs = allJobs.Where(j => j.Enabled).ToList();

        var nextRun = enabledJobs
            .Where(j => j.NextRun.HasValue)
            .Select(j => j.NextRun.GetValueOrDefault())
            .OrderBy(t => t)
            .Cast<DateTime?>()
            .FirstOrDefault();

        var today = DateTime.UtcNow.Date;
        var todayRuns = _db.ListRuns(limit: 1000)
            .Where(r => r.StartedAt.Date == today)
            .ToList();

        return new EngineStatus
        {
            IsRunning = IsRunning,
            StartedAt = _startedAt,
            TotalJobs = allJobs.Count,
            EnabledJobs = enabledJobs.Count,
            RunningJobs = _scheduler?.RunningJobCount ?? 0,
            NextScheduledRun = nextRun,
            RunsToday = todayRuns.Count,
            FailedRunsToday = todayRuns.Count(r => r.ExitCode != 0 || r.TimedOut)
        };
    }

    public EngineDatabase? Database => _db;

    private void RaiseEvent(EngineEvent e)
    {
        try { OnEvent?.Invoke(e); }
        catch (Exception ex) { FileLog.Write($"[EngineHost] Event handler error: {ex.Message}"); }
    }

    public void Dispose()
    {
        _dispatcher?.Dispose();
        _scheduler?.Dispose();
    }
}
