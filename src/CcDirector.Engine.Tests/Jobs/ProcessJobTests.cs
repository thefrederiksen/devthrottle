using CcDirector.Engine.Jobs;
using Xunit;

namespace CcDirector.Engine.Tests.Jobs;

/// <summary>
/// The process half the factory triggers borrow: the exit code, the standard error kept apart from the output, the
/// timeout, and the shell chosen for the operating system.
/// </summary>
public sealed class ProcessJobTests
{
    [Fact]
    public async Task ExecuteAsync_ACommandThatExitsThree_ReportsExitCodeThree()
    {
        var result = await new ProcessJob("exit-three", "exit 3", Path.GetTempPath(), 30).ExecuteAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_ASuccessfulCommand_ReportsExitZeroAndItsOutput()
    {
        var result = await new ProcessJob("echo", "echo hello", Path.GetTempPath(), 30).ExecuteAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.Output.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_StandardError_IsKeptApartFromTheOutput()
    {
        var result = await new ProcessJob("stderr", "echo oops 1>&2", Path.GetTempPath(), 30).ExecuteAsync(CancellationToken.None);

        Assert.Equal("oops", result.ErrorOutput.Trim());
        Assert.Equal("", result.Output.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_PastItsTimeout_IsKilled_AndHasNoExitCode()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 20 127.0.0.1 >nul" : "sleep 20";
        var result = await new ProcessJob("slow", slow, Path.GetTempPath(), 1).ExecuteAsync(CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledByTheCaller_KillsTheCommand_AndSaysItStopped()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        int? childPid = null;
        DateTime childStarted = default;
        using var cts = new CancellationTokenSource();
        var job = new ProcessJob("cancel-me", slow, Path.GetTempPath(), 120, (pid, started) =>
        {
            childPid = pid;
            childStarted = started;
        });

        var execution = job.ExecuteAsync(cts.Token);
        await Task.Delay(1000);
        Assert.NotNull(childPid);
        cts.Cancel();

        var cancelled = await Assert.ThrowsAsync<JobCancelledException>(() => execution);
        Assert.True(cancelled.ProcessStopped);
        var command = new CcDirector.Engine.Storage.EngineRunOwner("test", Environment.MachineName, childPid.Value, childStarted);
        Assert.Equal(CcDirector.Engine.Storage.OwnerLiveness.Gone, CcDirector.Engine.Storage.ProcessOwnerLiveness.Probe(command));
    }

    [Fact]
    public async Task ExecuteAsync_RecordingTheStartedCommandFails_TheCommandIsKilled()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        int? childPid = null;
        DateTime childStarted = default;
        var job = new ProcessJob("unrecorded", slow, Path.GetTempPath(), 120, (pid, started) =>
        {
            childPid = pid;
            childStarted = started;
            throw new IOException("simulated: the database write failed");
        });

        Exception? thrown = null;
        try { await job.ExecuteAsync(CancellationToken.None); }
        catch (Exception ex) { thrown = ex; }

        Assert.NotNull(thrown);
        Assert.NotNull(childPid);
        var command = new CcDirector.Engine.Storage.EngineRunOwner("test", Environment.MachineName, childPid.Value, childStarted);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (CcDirector.Engine.Storage.ProcessOwnerLiveness.Probe(command) != CcDirector.Engine.Storage.OwnerLiveness.Gone
               && sw.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(100);
        Assert.True(CcDirector.Engine.Storage.ProcessOwnerLiveness.Probe(command) == CcDirector.Engine.Storage.OwnerLiveness.Gone,
            "the command whose record failed was left running");
    }

    [Fact]
    public void ShellFor_UsesTheShellOfThisOperatingSystem()
    {
        var info = ProcessJob.ShellFor("echo hi");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", info.FileName);
            Assert.Equal("/c echo hi", info.Arguments);
        }
        else
        {
            Assert.Equal("/bin/sh", info.FileName);
            Assert.Equal(new[] { "-c", "echo hi" }, info.ArgumentList);
        }
    }
}
