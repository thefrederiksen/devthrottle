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
[Collection("RealProcesses")]
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

    private static OwnerLiveness Probe(int pid, DateTime startedAtUtc) =>
        ProcessOwnerLiveness.Probe(new EngineRunOwner("command", Environment.MachineName, pid, startedAtUtc));

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
}
