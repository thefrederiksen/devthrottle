using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests.Running;

/// <summary>
/// Which shifts a schedule works in, and the schedule screen's warning for a run that would still be going when its
/// shift ends (Factory Control, step 3). The rule: with history, warn when the typical run does not end before the
/// shift ends; with no history, warn inside the last 30 minutes of a shift.
/// </summary>
public sealed class ScheduleShiftsTests
{
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    // Sun 11 Oct 2026 12:00 UTC (08:00 in Toronto, daylight time): the next 7 days hold no clock change.
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static CronJobDto Daily(string cron, string id = "cj_a") => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = cron,
        TimeZoneId = "America/Toronto",
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static string? Warning(string cron, int? typicalMinutes, DateTime? now = null)
    {
        var fires = ScheduleShifts.NextFires(Daily(cron), now ?? Now);
        return ScheduleShifts.WarningFor(fires, Toronto, typicalMinutes is { } m ? TimeSpan.FromMinutes(m) : null);
    }

    // ---------- which shifts ----------

    [Fact]
    public void ShiftsOf_RunsAtSixAndEighteen_AreTheNightAndEveningShifts()
    {
        var shifts = ScheduleShifts.ShiftsOf(ScheduleShifts.NextFires(Daily("0 6,18 * * *"), Now), Toronto);

        Assert.Equal(new[] { WorkShifts.Night, WorkShifts.Evening }, shifts);
        Assert.Equal("Night and evening shifts", ScheduleShifts.Text(shifts));
    }

    [Fact]
    public void ShiftsOf_ARunAtExactlySixteen_IsTheEveningShift()
    {
        var shifts = ScheduleShifts.ShiftsOf(ScheduleShifts.NextFires(Daily("0 16 * * *"), Now), Toronto);

        Assert.Equal(new[] { WorkShifts.Evening }, shifts);
        Assert.Equal("Evening shift", ScheduleShifts.Text(shifts));
    }

    [Fact]
    public void Text_ThreeShiftsAndNone_ReadAsWords()
    {
        Assert.Equal("Night, morning and evening shifts", ScheduleShifts.Text(ScheduleShifts.Order));
        Assert.Equal("No run in the next 7 days", ScheduleShifts.Text(Array.Empty<string>()));
    }

    // ---------- the warning, with history ----------

    [Fact]
    public void WarningFor_TheTypicalRunEndsBeforeTheShiftEnds_IsNull()
    {
        Assert.Null(Warning("0 9 * * *", typicalMinutes: 120));
        // Ending exactly as the shift ends is not after it.
        Assert.Null(Warning("35 15 * * *", typicalMinutes: 25));
    }

    [Fact]
    public void WarningFor_TheTypicalRunEndsAfterTheShiftEnds_NamesTheRunTheShiftAndTheLength()
    {
        var warning = Warning("45 15 * * *", typicalMinutes: 25);

        Assert.NotNull(warning);
        Assert.StartsWith("Ends after its shift: the run on Sun 11 Oct at 15:45 starts in the morning shift", warning);
        Assert.Contains("usually takes about 25 min", warning);
        Assert.Contains("when the shift ends at 16:00", warning);
        Assert.Contains("7 of its 7 runs in the next 7 days do this.", warning);
    }

    [Fact]
    public void WarningFor_AShortRunLongBeforeTheEnd_ButALongTypical_StillWarns()
    {
        // Ten in the morning is six hours before the shift ends; a seven-hour run is not.
        Assert.NotNull(Warning("0 10 * * *", typicalMinutes: 7 * 60));
    }

    // ---------- the warning, with no history ----------

    [Fact]
    public void WarningFor_NoHistory_InsideTheLastThirtyMinutes_Warns()
    {
        var warning = Warning("45 15 * * *", typicalMinutes: null);

        Assert.NotNull(warning);
        Assert.StartsWith("Starts close to the end of its shift: the run on Sun 11 Oct at 15:45 starts in the last 30 minutes of the morning shift", warning);
        Assert.Contains("no finished run to measure yet", warning);
    }

    [Fact]
    public void WarningFor_NoHistory_ExactlyThirtyMinutesBefore_Warns_ThirtyOneDoesNot()
    {
        Assert.NotNull(Warning("30 15 * * *", typicalMinutes: null));
        Assert.Null(Warning("29 15 * * *", typicalMinutes: null));
    }

    [Fact]
    public void WarningFor_NoHistory_EarlyInTheShift_IsNull()
    {
        Assert.Null(Warning("0 9 * * *", typicalMinutes: null));
    }

    // ---------- Toronto's daylight-saving days ----------

    [Fact]
    public void WarningFor_SpringForwardDay_TheNightIsSevenHours_SoOnlyThatDaysRunEndsLate()
    {
        // Sun 8 Mar 2026 the clocks go from 02:00 to 03:00. A run at 01:00 that takes 6h 30m ends at 08:30 that
        // morning - after the night shift - though on the wall clock 01:00 + 6:30 is 07:30. Every other day it fits.
        var now = new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc);

        var warning = Warning("0 1 * * *", typicalMinutes: 6 * 60 + 30, now);

        Assert.NotNull(warning);
        Assert.StartsWith("Ends after its shift: the run on Sun 8 Mar at 01:00 starts in the night shift", warning);
        Assert.Contains("1 of its 7 runs in the next 7 days does this.", warning);
    }

    [Fact]
    public void WarningFor_FallBackDay_TheNightIsNineHours_SoAnEightAndAHalfHourRunFits()
    {
        // Sun 1 Nov 2026 the clocks go from 02:00 back to 01:00. A run at 00:00 (04:00 UTC) taking 8h 30m ends at
        // 12:30 UTC, which is 07:30 on the clock - inside the night shift, which ends at 08:00 (13:00 UTC).
        var fire = new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc);

        Assert.Null(ScheduleShifts.WarningFor(new[] { fire }, Toronto, TimeSpan.FromMinutes(8 * 60 + 30)));
        // The day before, the same run at 00:00 (04:00 UTC) ends at 08:30 on the clock: after the shift.
        var dayBefore = new DateTime(2026, 10, 31, 4, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(ScheduleShifts.WarningFor(new[] { dayBefore }, Toronto, TimeSpan.FromMinutes(8 * 60 + 30)));
    }

    [Fact]
    public void RuleText_StatesBothHalvesOfTheRule()
    {
        Assert.Contains("typical run", ScheduleShifts.RuleText);
        Assert.Contains("last 30 minutes of a shift", ScheduleShifts.RuleText);
    }
}
