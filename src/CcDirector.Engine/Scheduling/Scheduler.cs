using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Events;
using CcDirector.Engine.Storage;

namespace CcDirector.Engine.Scheduling;

public sealed class Scheduler : IDisposable
{
    private readonly EngineDatabase _db;
    private readonly JobExecutor _executor;
    private readonly int _checkIntervalSeconds;
    private readonly int _runRetentionDays;
    private readonly ConcurrentDictionary<int, byte> _runningJobs = new();
    private readonly SemaphoreSlim _concurrencyLimit = new(10, 10);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTime _lastPurge = DateTime.MinValue;

    public event Action<EngineEvent>? OnEvent;

    public int RunningJobCount => _runningJobs.Count;

    public Scheduler(EngineDatabase db, JobExecutor executor, int checkIntervalSeconds, int runRetentionDays)
    {
        _db = db;
        _executor = executor;
        _checkIntervalSeconds = checkIntervalSeconds;
        _runRetentionDays = runRetentionDays;
    }

    public void Start()
    {
        FileLog.Write("[Scheduler] Starting");

        _db.CleanupOrphanedRuns();
        InitializeNextRuns();

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => SchedulerLoop(_cts.Token));

        FileLog.Write("[Scheduler] Started");
    }

    public async Task StopAsync(int shutdownTimeoutSeconds)
    {
        FileLog.Write("[Scheduler] Stopping");

        if (_cts != null)
        {
            _cts.Cancel();

            if (_loopTask != null)
            {
                var completed = await Task.WhenAny(
                    _loopTask,
                    Task.Delay(TimeSpan.FromSeconds(shutdownTimeoutSeconds)));

                if (completed != _loopTask)
                    FileLog.Write("[Scheduler] Shutdown timed out, some jobs may still be running");
            }

            _cts.Dispose();
            _cts = null;
        }

        FileLog.Write("[Scheduler] Stopped");
    }

    private void InitializeNextRuns()
    {
        FileLog.Write("[Scheduler] InitializeNextRuns: calculating next_run for enabled jobs");

        var jobs = _db.ListJobs(includeDisabled: false);
        foreach (var job in jobs)
        {
            if (!job.NextRun.HasValue)
            {
                var nextRun = CronHelper.GetNextOccurrence(job.Cron, DateTime.UtcNow);
                _db.UpdateNextRun(job.Id, nextRun);
                FileLog.Write($"[Scheduler] Initialized next_run for {job.Name}: {nextRun}");
            }
        }
    }

    private async Task SchedulerLoop(CancellationToken ct)
    {
        FileLog.Write("[Scheduler] Loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                RunPurgeIfNeeded();

                // Another Director on the same engine.db may have died mid-run; its unfinished run
                // would hold that job's claim until something fails it, so look every tick, not
                // only at start. Runs of live owners are never touched.
                _db.CleanupOrphanedRuns();

                // This Director's own runs whose killed command was not seen to exit: release each
                // claim once its command is proven gone, so the job does not stay held forever.
                _executor.ReleaseConfirmedStops();

                // Candidates only: each one runs here only if this Director wins its claim.
                var dueJobs = _db.GetDueJobs();
                foreach (var job in dueJobs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (_runningJobs.ContainsKey(job.Id)) continue;

                    _ = RunJobAsync(job, ct);
                }

                // Sleep in 1-second intervals for responsive shutdown
                for (int i = 0; i < _checkIntervalSeconds && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Scheduler] Loop error: {ex.Message}");
                RaiseEvent(new EngineEvent(EngineEventType.Error, Message: ex.Message));

                // Brief pause before retrying
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        FileLog.Write("[Scheduler] Loop ended");
    }

    private async Task RunJobAsync(JobRecord job, CancellationToken ct)
    {
        if (!_runningJobs.TryAdd(job.Id, 0))
            return;

        await _concurrencyLimit.WaitAsync(ct);

        try
        {
            var claimed = _executor.TryClaim(job);
            if (claimed is null)
                return;

            RaiseEvent(new EngineEvent(EngineEventType.JobStarted, JobName: job.Name));

            var run = await _executor.ExecuteClaimedAsync(job, claimed, ct);

            var eventType = run.TimedOut ? EngineEventType.JobTimeout
                : run.ExitCode == 0 ? EngineEventType.JobCompleted
                : EngineEventType.JobFailed;

            RaiseEvent(new EngineEvent(eventType, JobName: job.Name, RunId: run.Id));
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[Scheduler] Job cancelled during shutdown: {job.Name}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Scheduler] RunJobAsync FAILED: {job.Name}, error={ex.Message}");
            RaiseEvent(new EngineEvent(EngineEventType.JobFailed, JobName: job.Name, Message: ex.Message));
        }
        finally
        {
            _runningJobs.TryRemove(job.Id, out _);
            _concurrencyLimit.Release();
        }
    }

    private void RunPurgeIfNeeded()
    {
        if ((DateTime.UtcNow - _lastPurge).TotalHours < 24) return;

        _lastPurge = DateTime.UtcNow;
        _db.CleanupOldRuns(_runRetentionDays);
    }

    private void RaiseEvent(EngineEvent e)
    {
        try
        {
            OnEvent?.Invoke(e);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Scheduler] Event handler error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _concurrencyLimit.Dispose();
    }
}
