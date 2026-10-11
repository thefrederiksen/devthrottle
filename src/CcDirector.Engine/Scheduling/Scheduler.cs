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
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<int, byte> _runningJobs = new();
    private readonly SemaphoreSlim _concurrencyLimit = new(10, 10);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTime _lastPurge = DateTime.MinValue;

    public event Action<EngineEvent>? OnEvent;

    public int RunningJobCount => _runningJobs.Count;

    /// <summary>How many ticks have run their pass (cleanup, due jobs claimed and started). A test that moves the
    /// loop's clock reads this to know the loop woke, instead of guessing from the absence of an effect.</summary>
    internal int TicksRun => Volatile.Read(ref _ticksRun);
    private int _ticksRun;

    /// <summary>The running loop, for a test to see that a stop ended it rather than timed out waiting for it.</summary>
    internal Task? LoopTask => _loopTask;

    /// <param name="clock">The clock the loop waits on and the purge is judged by. The product passes nothing and
    /// gets the system clock; a test passes its own so a wait is a fact it controls, not seconds it sits out.</param>
    public Scheduler(EngineDatabase db, JobExecutor executor, int checkIntervalSeconds, int runRetentionDays,
        TimeProvider? clock = null)
    {
        _db = db;
        _executor = executor;
        _checkIntervalSeconds = checkIntervalSeconds;
        _runRetentionDays = runRetentionDays;
        _clock = clock ?? TimeProvider.System;
    }

    public void Start()
    {
        FileLog.Write("[Scheduler] Starting");

        _db.CleanupOrphanedRuns(_clock.GetUtcNow().UtcDateTime, _executor.IsInFlight);
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
                var nextRun = CronHelper.GetNextOccurrence(job.Cron, _clock.GetUtcNow().UtcDateTime);
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
                // The loop does not wait for the jobs a tick started: they run beside the next ticks,
                // and the tick's own bookkeeping (_runningJobs) keeps one job from being started twice.
                _ = Tick(ct);

                await Task.Delay(TimeSpan.FromSeconds(_checkIntervalSeconds), _clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Scheduler] Loop ERROR: {ex.Message}");
                RaiseEvent(new EngineEvent(EngineEventType.Error, Message: ex.Message));

                // Brief pause before retrying
                try { await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        FileLog.Write("[Scheduler] Loop ended");
    }

    /// <summary>
    /// One pass of the scheduler, exactly what the running loop does once per interval: purge old runs when
    /// due, release the open runs the database proves abandoned, and start every due job this Director wins
    /// the claim for. The returned task completes when every job this tick started has finished, so a
    /// caller that is not the loop - a test driving two Directors on one database - can run ticks in a
    /// chosen order and read the database between them instead of waiting out timers. A job already running
    /// from an earlier tick is not started again.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await Tick(ct);
    }

    private Task Tick(CancellationToken ct)
    {
        RunPurgeIfNeeded();

        // Every tick, from the database: another Director's runs left by a death, and this
        // Director's own open runs it is not executing (a killed command not seen to exit,
        // or a run left by an engine restarted in this process). Runs of live owners, and
        // runs being executed here, are never touched.
        _db.CleanupOrphanedRuns(_clock.GetUtcNow().UtcDateTime, _executor.IsInFlight);

        // Candidates only: each one runs here only if this Director wins its claim.
        var started = new List<Task>();
        foreach (var job in _db.GetDueJobs())
        {
            if (ct.IsCancellationRequested) break;
            if (_runningJobs.ContainsKey(job.Id)) continue;

            started.Add(RunJobAsync(job, ct));
        }
        Interlocked.Increment(ref _ticksRun);
        return Task.WhenAll(started);
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
            // not-an-error: shutdown cancels the running jobs
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
        var now = _clock.GetUtcNow().UtcDateTime;
        if ((now - _lastPurge).TotalHours < 24) return;

        _lastPurge = now;
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
            FileLog.Write($"[Scheduler] Event handler ERROR: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _concurrencyLimit.Dispose();
    }
}
