using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Two Directors on ONE engine.db (devthrottle_internal#2311, live proof F6). When CC_VAULT_PATH is
/// set at the user level every Director on the machine opens the same engine.db, so the engine must
/// be safe there: each due occurrence runs exactly once, and one Director starting never fails
/// another live Director's run. In the double-run test each simulated Director has its own owner
/// identity, as two real Director processes do.
/// </summary>
[Collection("RealProcesses")]
public sealed class SharedEngineDatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public SharedEngineDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_shared_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort */ }
        }
    }

    [Fact]
    public void SecondDirectorStarting_LeavesTheFirstDirectorsRunInProgressOpen()
    {
        var first = new EngineDatabase(_dbPath);
        var jobId = first.AddJob(new JobRecord { Name = "long-job", Cron = "0 0 31 2 *", Command = "echo long" });
        var run = new RunRecord { JobId = jobId, JobName = "long-job", StartedAt = DateTime.UtcNow };
        run.Id = first.CreateRun(run);

        // A second Director opens the same file and runs its start-up cleanup.
        var second = new EngineDatabase(_dbPath);
        var failed = second.CleanupOrphanedRuns();

        Assert.Equal(0, failed);
        var loaded = first.GetRun(run.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.EndedAt);
        Assert.Null(loaded.ExitCode);
    }
}
