using System.Diagnostics;
using CcDirector.Engine.Jobs;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Review round 2 of the shared engine.db fix (devthrottle_internal#2311). EN-F3: a command whose record
/// fails is killed and the run ends only once it is gone. EN-F4: a run whose killed command was not seen
/// to exit is revisited every tick, and its claim released once the command is proven gone.
/// </summary>
public sealed class UnconfirmedStopTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _logPath;

    public UnconfirmedStopTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_unconfirmed_test_{id}.db");
        _logPath = Path.Combine(Path.GetTempPath(), $"engine_unconfirmed_log_{id}.txt");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _logPath })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort */ }
        }
    }

    /// <summary>Fake Director ids answer Running; a real process (a command) is probed for real.</summary>
    private EngineDatabase Director(int pid) =>
        new(_dbPath, new EngineRunOwner($"director-{pid}", Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc)),
            owner => owner.ProcessId is 1001 or 2002 ? OwnerLiveness.Running : ProcessOwnerLiveness.Probe(owner));

    private string LoopCommand() => OperatingSystem.IsWindows()
        ? $"for /L %i in (1,1,40) do @(echo tick>>\"{_logPath}\" & ping -n 2 127.0.0.1 >nul)"
        : $"for i in $(seq 1 40); do echo tick >> '{_logPath}'; sleep 1; done";

    private static OwnerLiveness Probe(int pid, DateTime startedAtUtc) =>
        ProcessOwnerLiveness.Probe(new EngineRunOwner("command", Environment.MachineName, pid, startedAtUtc));

    // -- EN-F3 --

    [Fact]
    public async Task RecordingTheCommandFails_TheCommandIsKilled_TheRunEndsWithoutMovingTheSchedule_AndNobodyStartsItWhileItLived()
    {
        var a = Director(1001);
        var b = Director(2002);
        var due = DateTime.UtcNow.AddSeconds(-1);
        var jobId = a.AddJob(new JobRecord { Name = "unrecorded", Cron = "0 0 31 2 *", Command = LoopCommand(), TimeoutSeconds = 120, NextRun = due });
        var job = a.GetJob("unrecorded")!;

        int? pid = null;
        DateTime started = default;
        var executor = new JobExecutor(a, JobExecutor.ProcessJobs, (_, p, s) =>
        {
            pid = p;
            started = s;
            throw new IOException("simulated: the database write failed");
        });
        var run = executor.TryClaim(job)!;

        var execution = executor.ExecuteClaimedAsync(job, run, CancellationToken.None);
        while (!execution.IsCompleted)
        {
            Assert.Null(b.TryClaimRun(jobId, DateTime.UtcNow));
            await Task.Delay(20);
        }
        await execution;

        Assert.NotNull(pid);
        Assert.Equal(OwnerLiveness.Gone, Probe(pid.Value, started));
        var ended = a.GetRun(run.Id)!;
        Assert.NotNull(ended.EndedAt);
        Assert.Equal(-1, ended.ExitCode);
        Assert.Contains("could not be recorded", ended.Stderr);
        Assert.Equal(due, a.GetJob("unrecorded")!.NextRun!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Equal(0, executor.UnconfirmedStopCount);
    }

    // -- EN-F4 --

    /// <summary>
    /// The first execution's command is killed and NOT seen to exit inside the confirmation window (a
    /// real process that exits about three seconds later). Later executions finish at once.
    /// </summary>
    private sealed class LateExitingThenQuickJobs
    {
        public int Executions;
        public Process? LateCommand;

        public IJob Create(JobRecord job, int timeoutSeconds, Action<int, DateTime> onStarted) =>
            new Job(this, onStarted);

        private sealed class Job(LateExitingThenQuickJobs owner, Action<int, DateTime> onStarted) : IJob
        {
            public string Name => "late-exit";

            public Task<JobResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref owner.Executions) > 1)
                    return Task.FromResult(new JobResult(true, "quick", ExitCode: 0));

                var info = OperatingSystem.IsWindows()
                    ? new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul")
                    : new ProcessStartInfo("/bin/sh", "-c \"sleep 3\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                owner.LateCommand = Process.Start(info)!;
                onStarted(owner.LateCommand.Id, owner.LateCommand.StartTime.ToUniversalTime());
                return Task.FromResult(new JobResult(false, "", "timed out", TimedOut: true, ProcessStopped: false));
            }
        }
    }

    [Fact]
    public async Task KilledCommandExitsAfterTheConfirmationWindow_TheClaimIsReleasedOnALaterTick_AndTheNextOccurrenceRuns()
    {
        var a = Director(1001);
        var jobId = a.AddJob(new JobRecord
        {
            Name = "late-exit", Cron = "0 0 31 2 *", Command = "unused", TimeoutSeconds = 1, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var jobs = new LateExitingThenQuickJobs();
        var executor = new JobExecutor(a, jobs.Create, a.RecordRunChild);
        using var scheduler = new Scheduler(a, executor, checkIntervalSeconds: 1, runRetentionDays: 30);
        scheduler.Start();

        try
        {
            var sw = Stopwatch.StartNew();
            while (jobs.LateCommand is null && sw.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(50);
            Assert.NotNull(jobs.LateCommand);

            // While the command lives the claim holds: one execution, the run open, the job unclaimable.
            await Task.Delay(1500);
            Assert.False(jobs.LateCommand.HasExited);
            Assert.Equal(1, jobs.Executions);
            var held = a.ListRuns(jobName: "late-exit").Single();
            Assert.Null(held.EndedAt);
            Assert.Null(a.TryClaimRun(jobId, DateTime.UtcNow));

            // After it exits, a tick releases the claim and the occurrence runs again.
            await jobs.LateCommand.WaitForExitAsync();
            sw.Restart();
            while (jobs.Executions < 2 && sw.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);

            Assert.Equal(2, jobs.Executions);
            var released = a.GetRun(held.Id)!;
            Assert.NotNull(released.EndedAt);
            Assert.True(released.TimedOut);
            Assert.Contains("exited only after the confirmation window", released.Stderr);
            Assert.Equal(0, executor.UnconfirmedStopCount);
        }
        finally
        {
            await scheduler.StopAsync(5);
            jobs.LateCommand?.Dispose();
        }
    }
}
