using CcDirector.Core.Utilities;
using CcDirector.Engine.Jobs;
using CcDirector.Engine.Storage;

namespace CcDirector.Engine.Scheduling;

public sealed class JobExecutor
{
    private readonly EngineDatabase _db;

    public JobExecutor(EngineDatabase db)
    {
        _db = db;
    }

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

        try
        {
            var processJob = new ProcessJob(job.Name, job.Command, job.WorkingDir, timeoutSeconds,
                (pid, startedAtUtc) => _db.RecordRunChild(run.Id, pid, startedAtUtc));
            var result = await processJob.ExecuteAsync(cancellationToken);
            stopwatch.Stop();

            if (!result.ProcessStopped)
            {
                // Timed out, killed, and still not seen to exit: the command may be running. Keep the
                // claim open so nobody starts it again; once this Director is gone, cleanup releases
                // it only when the command process is proven gone too.
                FileLog.Write($"[JobExecutor] Job timed out and could not be proven stopped, claim kept: name={job.Name}, run={run.Id}");
                return run;
            }

            RecordResult(run, result, stopwatch.Elapsed);
            FileLog.Write($"[JobExecutor] Job completed: name={job.Name}, success={result.Success}, duration={run.DurationSeconds:F1}s");
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
            // Cancelled without proof the command stopped: the claim stays open (see above).
            FileLog.Write($"[JobExecutor] Job cancelled, command not proven stopped, claim kept: name={job.Name}, run={run.Id}");
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(run, stopwatch, ex.Message);
            FileLog.Write($"[JobExecutor] Job FAILED: name={job.Name}, error={ex.Message}");
        }

        var nextRun = CronHelper.GetNextOccurrence(job.Cron, DateTime.UtcNow);
        _db.CompleteRun(run, nextRun);

        return run;
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
