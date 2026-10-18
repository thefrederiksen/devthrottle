using CcDirector.Engine.Events;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

[Collection("RealProcesses")]
public sealed class SchedulerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EngineDatabase _db;

    public SchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_sched_test_{Guid.NewGuid():N}.db");
        _db = new EngineDatabase(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); }
        catch (IOException) { /* Test temp file cleanup -- best effort */ }
    }

    [Fact]
    public async Task Start_CleansUpOrphanedRuns()
    {
        // An orphaned run (no EndedAt) left by a previous launch whose process is gone.
        var previousLaunch = new EngineRunOwner("director-a", Environment.MachineName, 4242, DateTime.UtcNow.AddHours(-1));
        var before = new EngineDatabase(_dbPath, previousLaunch, _ => OwnerLiveness.Running);
        var jobId = before.AddJob(new JobRecord { Name = "orphan", Cron = "0 0 31 2 *", Command = "echo test" });
        before.CreateRun(new RunRecord { JobId = jobId, JobName = "orphan", StartedAt = DateTime.UtcNow.AddMinutes(-1) });

        var db = new EngineDatabase(_dbPath,
            new EngineRunOwner("director-a", Environment.MachineName, 5151, DateTime.UtcNow),
            owner => owner.ProcessId == 4242 ? OwnerLiveness.Gone : OwnerLiveness.Running);
        var executor = new JobExecutor(db);
        using var scheduler = new Scheduler(db, executor, checkIntervalSeconds: 3600, runRetentionDays: 30);
        scheduler.Start();
        await scheduler.StopAsync(5);

        var runs = _db.ListRuns(jobName: "orphan");
        Assert.Single(runs);
        Assert.NotNull(runs[0].EndedAt);
        Assert.Equal(-1, runs[0].ExitCode);
        Assert.Equal("Interrupted by shutdown", runs[0].Stderr);
    }

    [Fact]
    public async Task Start_InitializesNextRunForJobs()
    {
        _db.AddJob(new JobRecord { Name = "init-next", Cron = "*/5 * * * *", Command = "echo test" });

        var executor = new JobExecutor(_db);
        using var scheduler = new Scheduler(_db, executor, checkIntervalSeconds: 3600, runRetentionDays: 30);
        scheduler.Start();
        await scheduler.StopAsync(5);

        var job = _db.GetJob("init-next");
        Assert.NotNull(job);
        Assert.NotNull(job.NextRun);
    }
}
