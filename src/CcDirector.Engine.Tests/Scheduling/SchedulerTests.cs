using CcDirector.Engine.Events;
using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

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
        // Create an orphaned run (no EndedAt)
        var jobId = _db.AddJob(new JobRecord { Name = "orphan", Cron = "0 0 31 2 *", Command = "echo test" });
        _db.CreateRun(new RunRecord { JobId = jobId, JobName = "orphan", StartedAt = DateTime.UtcNow.AddMinutes(-30) });

        var executor = new JobExecutor(_db);
        using var scheduler = new Scheduler(_db, executor, checkIntervalSeconds: 3600, runRetentionDays: 30);
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

    [Fact]
    public async Task Scheduler_RaiseEvents()
    {
        // Create a job due immediately with a fast command
        var job = new JobRecord
        {
            Name = "event-job",
            Cron = "0 0 31 2 *",  // Feb 31 = never (we manually set next_run)
            Command = "echo event-test",
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        };
        _db.AddJob(job);

        var events = new List<EngineEvent>();
        var executor = new JobExecutor(_db);
        using var scheduler = new Scheduler(_db, executor, checkIntervalSeconds: 1, runRetentionDays: 30);
        scheduler.OnEvent += e => events.Add(e);

        scheduler.Start();

        // Poll for the start+complete events instead of a fixed sleep: a hard 3s delay flakes
        // under CI load when the 1s-interval tick + subprocess run + completion take longer.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool Both() =>
            events.Any(e => e.Type == EngineEventType.JobStarted && e.JobName == "event-job") &&
            events.Any(e => e.Type == EngineEventType.JobCompleted && e.JobName == "event-job");
        while (!Both() && sw.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(100);

        await scheduler.StopAsync(5);

        Assert.Contains(events, e => e.Type == EngineEventType.JobStarted && e.JobName == "event-job");
        Assert.Contains(events, e => e.Type == EngineEventType.JobCompleted && e.JobName == "event-job");
    }
}
