using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="CronSchedule"/> (epic #479, #482): the single place schedule grammar,
/// time-zone resolution, and the recurring/one-off rules live. Covers validation of valid and
/// invalid definitions and the UTC next-occurrence computation for both schedule kinds.
/// </summary>
public sealed class CronScheduleTests
{
    private static CronJobDto ValidRecurring() => new()
    {
        Name = "nightly",
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = "0 0 * * *",
        TimeZoneId = "America/Chicago",
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/work-list run Tonight" },
    };

    [Fact]
    public void Validate_ValidRecurring_Ok()
    {
        var (ok, error) = CronSchedule.Validate(ValidRecurring());
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ValidOneOff_Ok()
    {
        var job = ValidRecurring();
        job.ScheduleKind = CronSchedule.KindOneOff;
        job.CronExpression = null;
        job.RunAt = "2026-06-17T00:00:00";

        var (ok, error) = CronSchedule.Validate(job);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_WorkListAction_NoSeed_Ok()
    {
        // A work-list job (#484) may omit the seed; the work-list name is the action.
        var job = ValidRecurring();
        job.Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "", WorkListName = "Tonight" };

        var (ok, error) = CronSchedule.Validate(job);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_NeitherSeedNorWorkList_Fails()
    {
        var job = ValidRecurring();
        job.Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "", WorkListName = null };

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_InvalidCronExpression_Fails()
    {
        var job = ValidRecurring();
        job.CronExpression = "not a cron";

        var (ok, error) = CronSchedule.Validate(job);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_RecurringWithoutExpression_Fails()
    {
        var job = ValidRecurring();
        job.CronExpression = null;

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_OneOffWithoutRunAt_Fails()
    {
        var job = ValidRecurring();
        job.ScheduleKind = CronSchedule.KindOneOff;
        job.CronExpression = null;
        job.RunAt = null;

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_UnknownTimeZone_Fails()
    {
        var job = ValidRecurring();
        job.TimeZoneId = "Mars/Olympus_Mons";

        var (ok, error) = CronSchedule.Validate(job);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_MissingName_Fails()
    {
        var job = ValidRecurring();
        job.Name = "  ";

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_MissingTargetMachine_Fails()
    {
        var job = ValidRecurring();
        job.Target = new CronJobTarget { Machine = "" };

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_MissingActionSeed_Fails()
    {
        var job = ValidRecurring();
        job.Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "" };

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void Validate_UnknownScheduleKind_Fails()
    {
        var job = ValidRecurring();
        job.ScheduleKind = "weeklyish";

        var (ok, _) = CronSchedule.Validate(job);
        Assert.False(ok);
    }

    [Fact]
    public void ComputeNextRunUtc_DailyMidnightChicago_IsNextLocalMidnightInUtc()
    {
        // Chicago is CDT (UTC-5) in June, so 00:00 local = 05:00 UTC. From 10:00 UTC on the 16th,
        // the next "0 0 * * *" occurrence is 00:00 CDT on the 17th = 05:00 UTC on the 17th.
        var from = new DateTime(2026, 6, 16, 10, 0, 0, DateTimeKind.Utc);

        var next = CronSchedule.ComputeNextRunUtc(ValidRecurring(), from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 6, 17, 5, 0, 0, DateTimeKind.Utc), next.Value);
    }

    [Fact]
    public void ComputeNextRunUtc_OneOff_ConvertsLocalRunAtToUtc()
    {
        var job = ValidRecurring();
        job.ScheduleKind = CronSchedule.KindOneOff;
        job.CronExpression = null;
        job.RunAt = "2026-06-17T00:00:00"; // wall clock in America/Chicago (CDT, UTC-5)

        var next = CronSchedule.ComputeNextRunUtc(job, new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 6, 17, 5, 0, 0, DateTimeKind.Utc), next.Value);
    }

    [Fact]
    public void ComputeNextRunUtc_InvalidJob_ReturnsNull()
    {
        var job = ValidRecurring();
        job.CronExpression = "garbage";

        Assert.Null(CronSchedule.ComputeNextRunUtc(job, DateTime.UtcNow));
    }
}
