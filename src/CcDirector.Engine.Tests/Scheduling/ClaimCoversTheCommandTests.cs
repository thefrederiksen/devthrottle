using CcDirector.Engine.Scheduling;
using CcDirector.Engine.Storage;
using Xunit;

namespace CcDirector.Engine.Tests.Scheduling;

/// <summary>
/// Review round 1 of the shared engine.db fix (devthrottle_internal#2311). EN-F1: a claim must cover the
/// COMMAND, not only the Director - a command that outlives a cancellation or its Director keeps the
/// claim. EN-F2: a run is judged by the timeout it was claimed with, a live owner is never aged out,
/// and a revoked owner's late completion changes nothing. These use only API that existed at the
/// reviewed head 3823c55c5, so this file runs red against it.
/// </summary>
public sealed class ClaimCoversTheCommandTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _logPath;

    public ClaimCoversTheCommandTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"engine_claimcmd_test_{id}.db");
        _logPath = Path.Combine(Path.GetTempPath(), $"engine_claimcmd_log_{id}.txt");
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _logPath })
        {
            try { File.Delete(path); }
            catch (IOException) { /* Test temp file cleanup -- best effort; a surviving command may hold it */ }
        }
    }

    private static EngineRunOwner Owner(int pid) =>
        new($"director-{pid}", Environment.MachineName, pid, new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>The listed fake Director ids answer as given; any other id (a real command process) is probed for real.</summary>
    private EngineDatabase Director(int pid, int[]? running = null, int[]? gone = null) =>
        new(_dbPath, Owner(pid), owner =>
            running?.Contains(owner.ProcessId) == true ? OwnerLiveness.Running
            : gone?.Contains(owner.ProcessId) == true ? OwnerLiveness.Gone
            : ProcessOwnerLiveness.Probe(owner));

    /// <summary>A command that writes "{marker}-start" once, then "{marker}-tick" about once a second for 40 seconds.</summary>
    private string LoopCommand(string marker) => OperatingSystem.IsWindows()
        ? $"echo {marker}-start>>\"{_logPath}\" & for /L %i in (1,1,40) do @(echo {marker}-tick>>\"{_logPath}\" & ping -n 2 127.0.0.1 >nul)"
        : $"echo {marker}-start >> '{_logPath}'; for i in $(seq 1 40); do echo {marker}-tick >> '{_logPath}'; sleep 1; done";

    private List<string> ReadLog()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(_logPath)) return [];
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task WaitForLine(string line, TimeSpan limit)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ReadLog().Contains(line))
        {
            Assert.True(sw.Elapsed < limit, $"'{line}' never appeared in the command log");
            await Task.Delay(100);
        }
    }

    // -- EN-F1 --

    [Fact]
    public async Task CancelledMidRun_TheCommandStops_AndTheOtherSchedulerDoesNotStartItWhileItLives()
    {
        var a = Director(1001, running: [1001, 2002]);
        var b = Director(2002, running: [1001, 2002]);
        a.AddJob(new JobRecord
        {
            Name = "long-job", Cron = "0 0 31 2 *", Command = LoopCommand("first"),
            TimeoutSeconds = 120, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });

        using var schedulerA = new Scheduler(a, new JobExecutor(a), checkIntervalSeconds: 1, runRetentionDays: 30);
        schedulerA.Start();
        await WaitForLine("first-start", TimeSpan.FromSeconds(20));

        // Any later execution of this job is told apart by its marker.
        var job = a.GetJob("long-job")!;
        job.Command = LoopCommand("second");
        a.UpdateJob(job);

        // Director A shuts down mid-run, and Director B is running on the same file.
        await schedulerA.StopAsync(15);
        using var schedulerB = new Scheduler(b, new JobExecutor(b), checkIntervalSeconds: 1, runRetentionDays: 30);
        schedulerB.Start();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var log = ReadLog();
            var firstTicksAfterStop = log.Count(l => l == "first-tick");
            await Task.Delay(TimeSpan.FromSeconds(3));
            log = ReadLog();

            Assert.True(log.Count(l => l == "first-tick") == firstTicksAfterStop,
                "the cancelled command kept running after its Director shut down");

            var secondStart = log.IndexOf("second-start");
            if (secondStart >= 0)
            {
                Assert.True(log.Skip(secondStart).All(l => l != "first-tick"),
                    "Director B started the occurrence while Director A's command was still running");
            }
        }
        finally
        {
            await schedulerB.StopAsync(15);
        }
    }

    [Fact]
    public async Task DirectorGoneWithItsCommandStillAlive_NoSecondStart()
    {
        // Director 4242 claims and starts the command, then dies; its command keeps running.
        var dead = Director(4242);
        var jobId = dead.AddJob(new JobRecord
        {
            Name = "orphan-command", Cron = "0 0 31 2 *", Command = LoopCommand("first"),
            TimeoutSeconds = 120, NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var job = dead.GetJob("orphan-command")!;
        var executor = new JobExecutor(dead);
        var run = executor.TryClaim(job)!;
        using var cts = new CancellationTokenSource();
        var execution = executor.ExecuteClaimedAsync(job, run, cts.Token);

        try
        {
            await WaitForLine("first-start", TimeSpan.FromSeconds(20));
            await Task.Delay(300);

            // Director B starts: 4242 is proven gone, the command process is real and alive.
            var b = Director(2002, running: [2002], gone: [4242]);
            b.CleanupOrphanedRuns();

            Assert.Null(b.GetRun(run.Id)!.EndedAt);
            Assert.Null(b.TryClaimRun(jobId, DateTime.UtcNow));
        }
        finally
        {
            cts.Cancel();
            try { await execution; }
            catch (OperationCanceledException) { /* the test's own cancellation */ }
        }
    }

    // -- EN-F2 --

    [Fact]
    public void JobEditedToAShorterTimeoutMidRun_UndecidableOwner_IsJudgedByTheTimeoutItWasClaimedWith()
    {
        var owner = Director(1001);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "edited-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 3600,
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var started = DateTime.UtcNow;
        var run = owner.TryClaimRun(jobId, started)!;

        var job = owner.GetJob("edited-job")!;
        job.TimeoutSeconds = 1;
        owner.UpdateJob(job);

        var other = new EngineDatabase(_dbPath, Owner(2002), _ => OwnerLiveness.Unknown);

        Assert.Equal(0, other.CleanupOrphanedRuns(started.AddSeconds(400)));
        Assert.Null(other.GetRun(run.Id)!.EndedAt);
        Assert.Equal(1, other.CleanupOrphanedRuns(started.AddSeconds(3600) + EngineDatabase.AbandonedRunGrace + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LiveOwner_IsNeverAgedOutByAnotherDirector()
    {
        var owner = Director(1001);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "live-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 60,
            NextRun = DateTime.UtcNow.AddSeconds(-1)
        });
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;

        var other = new EngineDatabase(_dbPath, Owner(2002), _ => OwnerLiveness.Running);

        Assert.Equal(0, other.CleanupOrphanedRuns(DateTime.UtcNow.AddDays(30)));
        Assert.Null(other.GetRun(run.Id)!.EndedAt);
    }

    [Fact]
    public void RevokedOwnersLateCompletion_ChangesNeitherTheVerdictNorTheSchedule()
    {
        var owner = Director(4242);
        var due = DateTime.UtcNow.AddSeconds(-1);
        var jobId = owner.AddJob(new JobRecord
        {
            Name = "revoked-job", Cron = "0 0 31 2 *", Command = "echo x", TimeoutSeconds = 60, NextRun = due
        });
        var run = owner.TryClaimRun(jobId, DateTime.UtcNow)!;

        // Another Director proves 4242 gone and releases the claim.
        var other = Director(2002, running: [2002], gone: [4242]);
        Assert.Equal(1, other.CleanupOrphanedRuns());

        // 4242 was not gone after all, and reports its result late.
        run.EndedAt = DateTime.UtcNow;
        run.ExitCode = 0;
        run.Stdout = "late";
        owner.CompleteRun(run, DateTime.UtcNow.AddHours(1));

        var loaded = other.GetRun(run.Id)!;
        Assert.Equal(-1, loaded.ExitCode);
        Assert.Equal(EngineDatabase.InterruptedRunMessage, loaded.Stderr);
        Assert.Null(loaded.Stdout);
        Assert.Equal(due, other.GetJob("revoked-job")!.NextRun!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }
}
