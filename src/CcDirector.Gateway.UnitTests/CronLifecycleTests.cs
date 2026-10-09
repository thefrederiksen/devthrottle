using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The lifecycle verdict the Gateway stamps on every schedule it lists (<see cref="CronJobDto.Lifecycle"/>). The
/// Schedule page shows only active schedules by default, so a one-off that fired weeks ago must never read as live,
/// and a switched-off recurring schedule must read as paused rather than finished.
/// </summary>
public sealed class CronLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static CronJobDto Job(string kind, bool enabled, DateTime? lastFiredUtc = null) => new()
    {
        Id = "cj_" + kind,
        Name = kind,
        Enabled = enabled,
        ScheduleKind = kind,
        CronExpression = kind == CronSchedule.KindRecurring ? "0 7 * * *"
            : kind == CronSchedule.KindRandom ? "window=07:00-01:00 perDay=4 minGap=45" : null,
        RunAt = kind == CronSchedule.KindOneOff ? "2026-08-03T10:30:00" : null,
        TimeZoneId = "America/Toronto",
        LastFiredUtc = lastFiredUtc,
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    [Theory]
    [InlineData(CronSchedule.KindRecurring)]
    [InlineData(CronSchedule.KindRandom)]
    public void StampDisplay_RepeatingScheduleSwitchedOn_IsActive(string kind)
    {
        var job = CronSchedule.StampDisplay(Job(kind, enabled: true, lastFiredUtc: Now.AddDays(-1)), Now);

        Assert.Equal(CronLifecycle.Active, job.Lifecycle);
    }

    [Theory]
    [InlineData(CronSchedule.KindRecurring)]
    [InlineData(CronSchedule.KindRandom)]
    public void StampDisplay_RepeatingScheduleSwitchedOff_IsPausedNotFinished(string kind)
    {
        var job = CronSchedule.StampDisplay(Job(kind, enabled: false, lastFiredUtc: Now.AddDays(-30)), Now);

        Assert.Equal(CronLifecycle.Paused, job.Lifecycle);
    }

    [Fact]
    public void StampDisplay_OneOffThatFiredAndSwitchedItselfOff_IsSpent()
    {
        // Exactly what the engine leaves behind: a one-off fires once and switches itself off.
        var job = CronSchedule.StampDisplay(Job(CronSchedule.KindOneOff, enabled: false, lastFiredUtc: Now.AddDays(-66)), Now);

        Assert.Equal(CronLifecycle.Spent, job.Lifecycle);
    }

    [Fact]
    public void StampDisplay_OneOffSwitchedOffBeforeItEverFired_IsOff()
    {
        var job = CronSchedule.StampDisplay(Job(CronSchedule.KindOneOff, enabled: false), Now);

        Assert.Equal(CronLifecycle.Off, job.Lifecycle);
    }

    [Fact]
    public void StampDisplay_OneOffSwitchedOnWhoseTimeHasPassed_IsStillActiveBecauseTheEngineWillFireIt()
    {
        // Its run time (2026-08-03) is behind Now, but it has not fired and is switched on: the engine fires it on
        // its next pass, so hiding it in Historical would hide a run that is about to happen.
        var job = CronSchedule.StampDisplay(Job(CronSchedule.KindOneOff, enabled: true), Now);

        Assert.Equal(CronLifecycle.Active, job.Lifecycle);
    }

    [Fact]
    public void StampDisplay_OneOffStillAheadThatWasRunByHand_IsStillActive()
    {
        // Run now records a fire but leaves the schedule switched on, so the one-off still runs at its time.
        var job = Job(CronSchedule.KindOneOff, enabled: true, lastFiredUtc: Now.AddHours(-1));
        job.RunAt = "2026-12-01T09:00:00";

        Assert.Equal(CronLifecycle.Active, CronSchedule.StampDisplay(job, Now).Lifecycle);
    }

    [Fact]
    public void StampDisplay_RandomScheduleWithBrokenSettings_StillGetsItsLifecycle()
    {
        var job = Job(CronSchedule.KindRandom, enabled: false);
        job.CronExpression = "not settings";

        CronSchedule.StampDisplay(job, Now);

        Assert.Equal(CronLifecycle.Paused, job.Lifecycle);
        Assert.Null(job.ScheduleText);
    }
}
