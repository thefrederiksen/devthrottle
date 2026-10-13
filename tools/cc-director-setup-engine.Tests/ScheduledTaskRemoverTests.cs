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
}
