using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Storage;

public sealed class EngineDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly EngineDatabase _db;

    public EngineDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_test_{Guid.NewGuid():N}.db");
        _db = new EngineDatabase(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); }
        catch (IOException) { /* Test temp file cleanup -- best effort */ }
    }

    // -- Job CRUD --

    [Fact]
    public void AddJob_ReturnsId()
    {
        var id = _db.AddJob(MakeJob("test-job"));
        Assert.True(id > 0);
    }

    [Fact]
    public void GetJob_ReturnsAddedJob()
    {
        _db.AddJob(MakeJob("my-job", cron: "*/5 * * * *"));

        var job = _db.GetJob("my-job");

        Assert.NotNull(job);
        Assert.Equal("my-job", job.Name);
        Assert.Equal("*/5 * * * *", job.Cron);
        Assert.Equal("echo hello", job.Command);
        Assert.True(job.Enabled);
    }

    [Fact]
    public void GetJob_NonExistent_ReturnsNull()
    {
        var job = _db.GetJob("does-not-exist");
        Assert.Null(job);
    }

    [Fact]
    public void GetJobById_ReturnsCorrectJob()
    {
        var id = _db.AddJob(MakeJob("by-id-job"));

        var job = _db.GetJobById(id);

        Assert.NotNull(job);
        Assert.Equal("by-id-job", job.Name);
        Assert.Equal(id, job.Id);
    }

    [Fact]
    public void ListJobs_ExcludesDisabledByDefault()
    {
        _db.AddJob(MakeJob("enabled-job"));
        _db.AddJob(MakeJob("disabled-job", enabled: false));

        var jobs = _db.ListJobs();

        Assert.Single(jobs);
        Assert.Equal("enabled-job", jobs[0].Name);
    }

    [Fact]
    public void ListJobs_IncludeDisabled()
    {
        _db.AddJob(MakeJob("a"));
        _db.AddJob(MakeJob("b", enabled: false));

        var jobs = _db.ListJobs(includeDisabled: true);

        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public void ListJobs_FilterByTag()
    {
        _db.AddJob(MakeJob("tagged", tags: "email,important"));
        _db.AddJob(MakeJob("untagged"));

        var jobs = _db.ListJobs(tag: "email");

        Assert.Single(jobs);
        Assert.Equal("tagged", jobs[0].Name);
    }

    [Fact]
    public void UpdateJob_ModifiesFields()
    {
        var id = _db.AddJob(MakeJob("update-me"));
        var job = _db.GetJobById(id);
        Assert.NotNull(job);

        job.Command = "echo updated";
        job.TimeoutSeconds = 600;
        _db.UpdateJob(job);

        var updated = _db.GetJobById(id);
        Assert.NotNull(updated);
        Assert.Equal("echo updated", updated.Command);
        Assert.Equal(600, updated.TimeoutSeconds);
    }

    [Fact]
    public void DeleteJob_RemovesJob()
    {
        _db.AddJob(MakeJob("delete-me"));

        var result = _db.DeleteJob("delete-me");

        Assert.True(result);
        Assert.Null(_db.GetJob("delete-me"));
    }

    [Fact]
    public void DeleteJob_NonExistent_ReturnsFalse()
    {
        var result = _db.DeleteJob("nope");
        Assert.False(result);
    }

    [Fact]
    public void SetJobEnabled_TogglesEnabled()
    {
        _db.AddJob(MakeJob("toggle-me"));

        _db.SetJobEnabled("toggle-me", false);
        var disabled = _db.GetJob("toggle-me");
        Assert.NotNull(disabled);
        Assert.False(disabled.Enabled);

        _db.SetJobEnabled("toggle-me", true);
        var enabled = _db.GetJob("toggle-me");
        Assert.NotNull(enabled);
        Assert.True(enabled.Enabled);
    }

    // -- Due Jobs --

    [Fact]
    public void GetDueJobs_ReturnsPastNextRun()
    {
        var job = MakeJob("due-job");
        job.NextRun = DateTime.UtcNow.AddMinutes(-1);
        _db.AddJob(job);

        var due = _db.GetDueJobs();

        Assert.Single(due);
        Assert.Equal("due-job", due[0].Name);
    }

    [Fact]
    public void GetDueJobs_ExcludesFutureNextRun()
    {
        var job = MakeJob("future-job");
        job.NextRun = DateTime.UtcNow.AddHours(1);
        _db.AddJob(job);

        var due = _db.GetDueJobs();

        Assert.Empty(due);
    }

    [Fact]
    public void GetDueJobs_ExcludesDisabled()
    {
        var job = MakeJob("disabled-due", enabled: false);
        job.NextRun = DateTime.UtcNow.AddMinutes(-1);
        _db.AddJob(job);

        var due = _db.GetDueJobs();

        Assert.Empty(due);
    }

    [Fact]
    public void GetDueJobs_ExcludesNullNextRun()
    {
        _db.AddJob(MakeJob("no-schedule"));

        var due = _db.GetDueJobs();

        Assert.Empty(due);
    }

    // -- Runs --

    [Fact]
    public void CreateRun_ReturnsId()
    {
        var jobId = _db.AddJob(MakeJob("run-job"));
        var run = new RunRecord { JobId = jobId, JobName = "run-job", StartedAt = DateTime.UtcNow };

        var runId = _db.CreateRun(run);

        Assert.True(runId > 0);
    }

    [Fact]
    public void UpdateRun_SetsCompletionFields()
    {
        var jobId = _db.AddJob(MakeJob("complete-job"));
        var run = new RunRecord { JobId = jobId, JobName = "complete-job", StartedAt = DateTime.UtcNow };
        run.Id = _db.CreateRun(run);

        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;
        run.Stdout = "output";
        run.DurationSeconds = 1.5;
        _db.UpdateRun(run);

        var loaded = _db.GetRun(run.Id);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.EndedAt);
        Assert.Equal(0, loaded.ExitCode);
        Assert.Equal("output", loaded.Stdout);
        Assert.Equal(1.5, loaded.DurationSeconds);
    }

    [Fact]
    public void ListRuns_ReturnsInDescOrder()
    {
        var jobId = _db.AddJob(MakeJob("list-runs-job"));

        var run1 = new RunRecord { JobId = jobId, JobName = "list-runs-job", StartedAt = DateTime.UtcNow.AddMinutes(-2) };
        run1.Id = _db.CreateRun(run1);

        var run2 = new RunRecord { JobId = jobId, JobName = "list-runs-job", StartedAt = DateTime.UtcNow.AddMinutes(-1) };
        run2.Id = _db.CreateRun(run2);

        var runs = _db.ListRuns();

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].StartedAt > runs[1].StartedAt);
    }

    [Fact]
    public void ListRuns_FilterByJobName()
    {
        var jobId1 = _db.AddJob(MakeJob("job-a"));
        var jobId2 = _db.AddJob(MakeJob("job-b"));

        _db.CreateRun(new RunRecord { JobId = jobId1, JobName = "job-a", StartedAt = DateTime.UtcNow });
        _db.CreateRun(new RunRecord { JobId = jobId2, JobName = "job-b", StartedAt = DateTime.UtcNow });

        var runs = _db.ListRuns(jobName: "job-a");

        Assert.Single(runs);
        Assert.Equal("job-a", runs[0].JobName);
    }

    // -- Cleanup --

    [Fact]
    public void CleanupOrphanedRuns_MarksOpenRunsOfAGoneOwnerAsFailed()
    {
        // A previous launch of this Director: a process that is no longer running.
        var previousLaunch = new EngineRunOwner("director-a", Environment.MachineName, 4242, DateTime.UtcNow.AddHours(-1));
        var before = new EngineDatabase(_dbPath, previousLaunch, _ => OwnerLiveness.Running);
        var jobId = before.AddJob(MakeJob("orphan-job"));
        var run = new RunRecord { JobId = jobId, JobName = "orphan-job", StartedAt = DateTime.UtcNow };
        run.Id = before.CreateRun(run);

        var now = new EngineDatabase(_dbPath,
            new EngineRunOwner("director-a", Environment.MachineName, 5151, DateTime.UtcNow),
            owner => owner.ProcessId == 4242 ? OwnerLiveness.Gone : OwnerLiveness.Running);
        var count = now.CleanupOrphanedRuns();

        Assert.Equal(1, count);

        var cleaned = _db.GetRun(run.Id);
        Assert.NotNull(cleaned);
        Assert.NotNull(cleaned.EndedAt);
        Assert.Equal(-1, cleaned.ExitCode);
        Assert.Equal("Interrupted by shutdown", cleaned.Stderr);
    }

    [Fact]
    public void CleanupOldRuns_PurgesOldRecords()
    {
        var jobId = _db.AddJob(MakeJob("purge-job"));

        // Create an old run (40 days ago) and a recent run
        var oldRun = new RunRecord
        {
            JobId = jobId, JobName = "purge-job",
            StartedAt = DateTime.UtcNow.AddDays(-40),
            EndedAt = DateTime.UtcNow.AddDays(-40),
            ExitCode = 0, DurationSeconds = 1
        };
        oldRun.Id = _db.CreateRun(oldRun);
        _db.UpdateRun(oldRun);

        var newRun = new RunRecord
        {
            JobId = jobId, JobName = "purge-job",
            StartedAt = DateTime.UtcNow.AddDays(-1),
            EndedAt = DateTime.UtcNow.AddDays(-1),
            ExitCode = 0, DurationSeconds = 1
        };
        newRun.Id = _db.CreateRun(newRun);
        _db.UpdateRun(newRun);

        var purged = _db.CleanupOldRuns(30);

        Assert.Equal(1, purged);
        Assert.Null(_db.GetRun(oldRun.Id));
        Assert.NotNull(_db.GetRun(newRun.Id));
    }

    // -- Helpers --

    private static JobRecord MakeJob(string name, string cron = "* * * * *", bool enabled = true, string? tags = null)
    {
        return new JobRecord
        {
            Name = name,
            Cron = cron,
            Command = "echo hello",
            Enabled = enabled,
            TimeoutSeconds = 300,
            Tags = tags
        };
    }
}
