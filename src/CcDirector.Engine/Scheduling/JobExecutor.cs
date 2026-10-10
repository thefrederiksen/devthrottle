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
    /// The jobs this executor is claiming or executing right now. It is the ONLY thing kept in memory:
    /// every other decision about an open run is read from the database (see
    /// <see cref="EngineDatabase.CleanupOrphanedRuns(DateTime, Func{int, bool})"/>), so an open run this
    /// process owns but is not executing - a killed command not seen to exit, or a run left by an engine
    /// restarted in this process - is judged from its row on every tick.
    /// </summary>
    private readonly ConcurrentDictionary<int, byte> _inFlightJobs = new();

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

    /// <summary>Whether this executor is claiming or executing the job right now.</summary>
    public bool IsInFlight(int jobId) => _inFlightJobs.ContainsKey(jobId);

    /// <summary>
    /// Claims a due job for this Director. Null means it is not ours to run: not due any more, or
    /// another Director on the same engine.db claimed this occurrence first.
    /// </summary>
    public RunRecord? TryClaim(JobRecord job)
    {
        // In flight BEFORE the row exists, so a tick's cleanup never judges a run this executor is
        // about to execute.
        _inFlightJobs[job.Id] = 0;
        var run = _db.TryClaimRun(job.Id, DateTime.UtcNow);
        if (run is null)
        {
            _inFlightJobs.TryRemove(job.Id, out _);
            FileLog.Write($"[JobExecutor] Not claimed (not due, or another Director holds it): id={job.Id}, name={job.Name}");
        }
        return run;
    }

    /// <summary>Runs a job whose run this Director has claimed with <see cref="TryClaim"/>.</summary>
    public async Task<RunRecord> ExecuteClaimedAsync(JobRecord job, RunRecord run, CancellationToken cancellationToken)
    {
        _inFlightJobs[job.Id] = 0;
        try
        {
            return await ExecuteClaimedCoreAsync(job, run, cancellationToken);
        }
        finally
        {
            // From here on an open run is judged from its row on every tick.
            _inFlightJobs.TryRemove(job.Id, out _);
        }
    }

    private async Task<RunRecord> ExecuteClaimedCoreAsync(JobRecord job, RunRecord run, CancellationToken cancellationToken)
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
            // Before the process exists: from now on, losing the command's identity holds the claim
            // to the deadline instead of releasing it.
            _db.MarkCommandStarting(run.Id);

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
                KeepClaim(run, "timed out, killed, not seen to exit");
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
                FileLog.Write($"[JobExecutor] Command not recorded, killed and stopped FAILED: name={job.Name}, run={run.Id}");
            }
            else
            {
                KeepClaim(run, "not recorded, killed, not seen to exit");
            }
            return run;
        }
        catch (JobCancelledException ex) when (ex.ProcessStopped)
        {
            // Shutdown, and the command's process tree was killed and seen to exit. The run ends but
            // next_run stays where it is, so the occurrence runs again - after, never alongside.
            RecordFailure(run, stopwatch, "Cancelled: the command's process tree was killed");
            _db.EndRunKeepingSchedule(run);
            // not-an-error: the job was cancelled on purpose
            FileLog.Write($"[JobExecutor] Job cancelled, command stopped: name={job.Name}");
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancelled without proof the command stopped: the claim stays open.
            KeepClaim(run, "cancelled, killed, not seen to exit");
            throw;
        }
        catch (Exception ex)
        {
            if (command is not null && _db.ProbeOwner(command) != OwnerLiveness.Gone)
            {
                // Failed after the command started, and it is not proven gone: never release a claim
                // over a command that may still be running.
                KeepClaim(run, $"failed while the command ran ({ex.Message}), not proven gone");
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
    /// Leaves the run open: its command may still be running. Nothing here remembers it - once this
    /// execution returns, every tick judges the run from its row (a recorded command proven gone ends it
    /// at once; one that cannot be proven gone ends it at the stored deadline plus the margin).
    /// </summary>
    private static void KeepClaim(RunRecord run, string why)
    {
        FileLog.Write($"[JobExecutor] Claim kept open, judged from the database every tick: run={run.Id}, {why}");
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
