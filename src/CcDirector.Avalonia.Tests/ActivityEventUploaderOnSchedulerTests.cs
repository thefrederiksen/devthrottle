using CcDirector.ControlApi;
using CcDirector.Core.Activity;
using CcDirector.Core.Background;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The activity event outbox drain is a job on the scheduler (docs/BackgroundWork.md): registered
/// under its register name with the thirty-second cadence it always had, gone when disposed. Lives
/// here because this is the test project that can see the control plane. A private registry is
/// injected so the process-wide one stays empty.
/// </summary>
public sealed class ActivityEventUploaderOnSchedulerTests
{
    [Fact]
    public void TheOutboxDrain_RegistersUnderItsRegisterName_WithItsThirtySecondCadence_AndLeavesWhenDisposed()
    {
        var jobs = new BackgroundJobs();
        var outbox = new ActivityEventOutbox(Path.Combine(Path.GetTempPath(), "ccd-outbox-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl"));
        var uploader = new ActivityEventUploader(() => null, outbox, jobs);

        uploader.Start();

        var row = Assert.Single(jobs.Snapshot());
        Assert.Equal("Activity event outbox", row.Name);
        Assert.Equal(BackgroundJobTier.SlowAndSteady, row.Tier);
        Assert.Equal(ActivityEventUploader.Interval, row.Cadence);
        Assert.Equal(120, row.CeilingPerHour);

        uploader.Dispose();
        Assert.Empty(jobs.Snapshot());
    }
}
