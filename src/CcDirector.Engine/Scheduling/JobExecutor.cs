using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Engine.Jobs;
using CcDirector.Engine.Storage;

namespace CcDirector.Engine.Scheduling;

public sealed class JobExecutor
{
    /// <summary>Builds the command for a claimed run: (job, the run's stored timeout, called with the started process's id and start time).</summary>
    internal delegate IJob CreateJob(JobRecord job, int timeoutSeconds, Action<int, DateTime> onStarted);

    internal static readonly CreateJob ProcessJobs = (job, timeoutSeconds, onStarted) =>
        new ProcessJob(job.Name, job.Command, job.WorkingDir, timeoutSeconds, onStarted);

    private readonly EngineDatabase _db;
    private readonly CreateJob _createJob;
    private readonly Action<int, int, DateTime> _recordChild;

    /// <summary>
    /// Runs whose command was killed but not seen to exit. Their claims stay open, and every scheduler
    /// tick (<see cref="ReleaseConfirmedStops"/>) asks whether the command is gone yet.
    /// </summary>
    private readonly ConcurrentDictionary<int, UnconfirmedStop> _unconfirmed = new();

    private sealed record UnconfirmedStop(RunRecord Run, EngineRunOwner Command, string Reason, bool TimedOut, DateTime StartedAt);

    public JobExecutor(EngineDatabase db)
        : this(db, ProcessJobs, db.RecordRunChild)
    {
    }

    /// <param name="recordChild">Records a run's command process: (run id, process id, process start time).</param>
    internal JobExecutor(EngineDatabase db, CreateJob createJob, Action<int, int, DateTime> recordChild)
    {
        _db = db;
        _createJob = createJob;
        _recordChild = recordChild;
    }

    /// <summary>How many runs hold their claim while waiting to see a killed command exit.</summary>
    public int UnconfirmedStopCount => _unconfirmed.Count;

    /// <summary>
    /// Claims a due job for this Director. Null means it is not ours to run: not due any more, or
    /// another Director on the same engine.db claimed this occurrence first.
    /// </summary>
    public RunRecord? TryClaim(JobRecord job)
    {
        var run = _db.TryClaimRun(job.Id, DateTime.UtcNow);
        if (run is null)
            FileLog.Write($"[JobExecutor] Not claimed (not due, or another Director holds it): id={job.Id}, name={job.Name}");
        return run;
    }

    /// <summary>Runs a job whose run this Director has claimed with <see cref="TryClaim"/>.</summary>
    public async Task<RunRecord> ExecuteClaimedAsync(JobRecord job, RunRecord run, CancellationToken cancellationToken)
    {
        FileLog.Write($"[JobExecutor] Starting job: id={job.Id}, name={job.Name}, run={run.Id}");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // The timeout the run was claimed with, never the job's current one: the job may be edited
        // while it runs, and other Directors judge this run by the value stored on it.
        var timeoutSeconds = run.TimeoutSeconds
            ?? throw new InvalidOperationException($"Run {run.Id} has no timeout; runs are started with TryClaim");

        // Known in memory before the record is written, so a command whose record fails can still be watched.
        EngineRunOwner? command = null;

        try
        {
            var processJob = _createJob(job, timeoutSeconds, (pid, startedAtUtc) =>
            {
                command = _db.Owner with { ProcessId = pid, ProcessStartedAtUtc = startedAtUtc };
                _recordChild(run.Id, pid, startedAtUtc);
            });
            var result = await processJob.ExecuteAsync(cancellationToken);
            stopwatch.Stop();

            if (!result.ProcessStopped)
            {
                // Timed out, killed, and still not seen to exit: the command may be running. Keep the
                // claim open so nobody starts it again, and watch for it to exit.
                Watch(run, command, $"Timed out after {timeoutSeconds} seconds; the killed command exited only after the confirmation window.", timedOut: true);
                return run;
            }

            RecordResult(run, result, stopwatch.Elapsed);
            FileLog.Write($"[JobExecutor] Job completed: name={job.Name}, success={result.Success}, duration={run.DurationSeconds:F1}s");
        }
        catch (CommandNotRecordedException ex)
        {
            // The command started but could not be recorded, and was killed. The run ends exactly as a
            // cancellation does - without moving next_run - and only once the command is proven gone.
            var reason = $"The command could not be recorded, so it was killed: {ex.InnerException?.Message}";
            if (ex.ProcessStopped)
            {
                RecordFailure(run, stopwatch, reason);
                _db.EndRunKeepingSchedule(run);
                FileLog.Write($"[JobExecutor] Command not recorded, killed and stopped: name={job.Name}, run={run.Id}");
            }
            else
            {
                var unrecorded = ex.ProcessStartedAtUtc is { } started
                    ? _db.Owner with { ProcessId = ex.ProcessId, ProcessStartedAtUtc = started }
                    : null;
                Watch(run, unrecorded, reason, timedOut: false);
            }
            return run;
        }
        catch (JobCancelledException ex) when (ex.ProcessStopped)
        {
            // Shutdown, and the command's process tree was killed and seen to exit. The run ends but
            // next_run stays where it is, so the occurrence runs again - after, never alongside.
            RecordFailure(run, stopwatch, "Cancelled: the command's process tree was killed");
            _db.EndRunKeepingSchedule(run);
            FileLog.Write($"[JobExecutor] Job cancelled, command stopped: name={job.Name}");
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancelled without proof the command stopped: the claim stays open and is watched.
            Watch(run, command, "Cancelled: the killed command exited only after the confirmation window.", timedOut: false);
            throw;
        }
        catch (Exception ex)
        {
            if (command is not null && _db.ProbeOwner(command) != OwnerLiveness.Gone)
            {
                // Failed after the command started, and it is not proven gone: never release a claim
                // over a command that may still be running.
                Watch(run, command, $"Failed while the command ran: {ex.Message}", timedOut: false);
                return run;
            }

            RecordFailure(run, stopwatch, ex.Message);
            FileLog.Write($"[JobExecutor] Job FAILED: name={job.Name}, error={ex.Message}");
        }

        var nextRun = CronHelper.GetNextOccurrence(job.Cron, DateTime.UtcNow);
        _db.CompleteRun(run, nextRun);

        return run;
    }

    /// <summary>
    /// Called every scheduler tick. For each run whose killed command was not seen to exit, asks
    /// whether that command is gone now; when it is, ends the run and releases the claim exactly as a
    /// confirmed cancellation (next_run is not moved, so the occurrence runs again). A command still
    /// running, or one that cannot be decided, keeps its claim. Returns how many were released.
    /// </summary>
    public int ReleaseConfirmedStops()
    {
        var released = 0;
        foreach (var (runId, stop) in _unconfirmed)
        {
            if (_db.ProbeOwner(stop.Command) != OwnerLiveness.Gone)
                continue;

            var run = stop.Run;
            run.EndedAt = DateTime.UtcNow;
            run.ExitCode = -1;
            run.TimedOut = stop.TimedOut;
            run.Stderr = stop.Reason;
            run.DurationSeconds = (run.EndedAt.Value - stop.StartedAt).TotalSeconds;
            _db.EndRunKeepingSchedule(run);
            _unconfirmed.TryRemove(runId, out _);
            released++;
            FileLog.Write($"[JobExecutor] ReleaseConfirmedStops: command pid={stop.Command.ProcessId} is gone, run={runId} ended and its claim released");
        }
        return released;
    }

    private void Watch(RunRecord run, EngineRunOwner? command, string reason, bool timedOut)
    {
        if (command is null)
        {
            // Nothing identifies the process, so it can never be proven gone from here. The claim
            // stays open until this Director exits; then cleanup releases it (owner gone, no command
            // recorded).
            FileLog.Write($"[JobExecutor] Command not proven stopped and not identifiable, claim kept until this Director exits: run={run.Id}");
            return;
        }

        _unconfirmed[run.Id] = new UnconfirmedStop(run, command, reason, timedOut, DateTime.UtcNow);
        FileLog.Write($"[JobExecutor] Command not proven stopped, claim kept and watched: run={run.Id}, pid={command.ProcessId}");
    }

    private static void RecordResult(RunRecord run, JobResult result, TimeSpan elapsed)
    {
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = result.Success ? 0 : 1;
        run.Stdout = result.Output;
        run.Stderr = result.Error;
        run.TimedOut = result.TimedOut;
        run.DurationSeconds = elapsed.TotalSeconds;
    }

    private static void RecordFailure(RunRecord run, System.Diagnostics.Stopwatch stopwatch, string error)
    {
        stopwatch.Stop();
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = -1;
        run.Stderr = error;
        run.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
    }
}
