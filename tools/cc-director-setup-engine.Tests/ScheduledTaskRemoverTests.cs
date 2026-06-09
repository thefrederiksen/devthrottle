using System.Diagnostics;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Scheduled-task removal (issue #257): owned tasks removed if present, absent tasks skipped
/// (idempotent, Assumption 4). The OS call is behind a seam, so the policy is tested without
/// real tasks or elevation.
/// </summary>
public class ScheduledTaskRemoverTests
{
    [Fact]
    public void TaskNames_AreTheTwoLaunchHelpers()
    {
        Assert.Contains("cc-director-launch", ScheduledTaskRemover.TaskNames);
        Assert.Contains("cc-director-gateway-launch", ScheduledTaskRemover.TaskNames);
        Assert.Equal(2, ScheduledTaskRemover.TaskNames.Count);
    }

    [Fact]
    public void RemoveAll_PresentTask_IsRemoved()
    {
        var results = ScheduledTaskRemover.RemoveAll(name =>
            new ScheduledTaskResult(name, Present: true, Removed: true, Error: null));

        Assert.All(results, r => Assert.True(r.Removed));
        Assert.Equal(ScheduledTaskRemover.TaskNames.Count, results.Count);
    }

    [Fact]
    public void RemoveAll_AbsentTask_IsSkipped_NotErrored()
    {
        var results = ScheduledTaskRemover.RemoveAll(name =>
            new ScheduledTaskResult(name, Present: false, Removed: false, Error: null));

        Assert.All(results, r =>
        {
            Assert.False(r.Present);
            Assert.False(r.Removed);
            Assert.Null(r.Error);
        });
    }

    [Fact]
    public void RemoveAll_DeleteFailure_SurfacesError()
    {
        var results = ScheduledTaskRemover.RemoveAll(name =>
            new ScheduledTaskResult(name, Present: true, Removed: false, Error: "access denied"));

        Assert.All(results, r =>
        {
            Assert.True(r.Present);
            Assert.False(r.Removed);
            Assert.Equal("access denied", r.Error);
        });
    }

    /// <summary>
    /// Exercises the REAL schtasks-backed DefaultRunner against a uniquely-named throwaway task
    /// (never one of CC Director's real task names), proving the OS path the seam tests stub out.
    /// Non-elevated create/delete of a per-user task works on Windows; skipped off Windows.
    /// </summary>
    [Fact]
    public void DefaultRunner_RemovesARealThrowawayTask_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var name = "cc-director-sandbox-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, RunSchtasks($"/Create /SC ONCE /TN \"{name}\" /TR \"cmd.exe /c echo hi\" /ST 23:59 /F"));
        try
        {
            var r = ScheduledTaskRemover.DefaultRunner(name);
            Assert.True(r.Present);
            Assert.True(r.Removed);
            Assert.Null(r.Error);

            // Idempotent: removing an absent task is a clean skip.
            var again = ScheduledTaskRemover.DefaultRunner(name);
            Assert.False(again.Present);
        }
        finally
        {
            RunSchtasks($"/Delete /TN \"{name}\" /F"); // best effort if the assertion path failed
        }
    }

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return -1;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
