using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Window schedules (the owner, 2026-10-09: "most jobs should stop naming a rigid time"): the Gateway chooses the minute
/// inside a window, spread by the machine's load, by a deadline, and a set time after another schedule. A fixed
/// schedule is never moved.
/// </summary>
public sealed class WindowScheduleTests
{
    // 00:10 in Toronto (daylight time, four hours behind), a Friday.
    private static readonly DateTime Now = new(2026, 10, 9, 4, 10, 0, DateTimeKind.Utc);
    private const string Zone = "America/Toronto";
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById(Zone);

    private static CronJobDto Window(string id, string settings, string machine = "SOREN_NORTH") => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        ScheduleKind = CronSchedule.KindWindow,
        CronExpression = settings,
        TimeZoneId = Zone,
        Target = new CronJobTarget { Machine = machine },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static CronJobDto Fixed(string id, string cron) => new()
    {
        Id = id,
        Name = id,
        Enabled = true,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = cron,
        TimeZoneId = Zone,
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static Dictionary<string, TimeSpan> Lengths(params (string Id, int Minutes)[] lengths) =>
        lengths.ToDictionary(l => l.Id, l => TimeSpan.FromMinutes(l.Minutes));

    /// <summary>Place the job against the others and store the placement on it, as the create route does.</summary>
    private static string Place(CronJobDto job, IReadOnlyList<CronJobDto> all, Dictionary<string, TimeSpan> lengths)
    {
        var settings = WindowSchedule.Parse(job.CronExpression).Settings!;
        var (placed, error) = WindowSchedule.Place(job, settings, all, lengths, Now);
        Assert.True(placed is not null, error);
        job.CronExpression = placed!.ToText();
        return WindowSchedule.Hhmm(placed.PlacedMinute!.Value);
    }

    private static int Minute(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[3..]);

    // ---- the owner's example ----------------------------------------------------------------------------------

    [Fact]
    public void Place_TheOwnersExample_OneAboutTwoAndOneAboutFour_StayAtLeastTwoHoursApart()
    {
        // "one job at about 02:00, another at about 04:00, and they must stay apart".
        var first = Window("first", "window=01:00-03:00");
        var second = Window("second", "window=03:00-06:00 after=first gap=120");
        var all = new List<CronJobDto> { first, second };
        var lengths = Lengths(("first", 30), ("second", 30));

        var firstAt = Place(first, all, lengths);
        var secondAt = Place(second, all, lengths);

        Assert.Equal("02:00", firstAt);
        Assert.True(Minute(secondAt) - Minute(firstAt) >= 120, $"{firstAt} and {secondAt} are under two hours apart");
        Assert.Equal("04:30", secondAt);

        // Move the first later, and the second has to follow to keep the two hours.
        first.CronExpression = "window=03:00-04:00";
        var movedFirst = Place(first, all, lengths);
        var movedSecond = Place(second, all, lengths);
        Assert.Equal("03:30", movedFirst);
        Assert.True(Minute(movedSecond) - Minute(movedFirst) >= 120, $"{movedFirst} and {movedSecond} are under two hours apart");
    }

    [Fact]
    public void Place_AfterWithoutAGap_WaitsForTheOtherToFinish()
    {
        var first = Window("first", "window=01:00-03:00");
        var second = Window("second", "window=01:00-06:00 after=first");
        var all = new List<CronJobDto> { first, second };
        var lengths = Lengths(("first", 90), ("second", 20));

        var firstAt = Place(first, all, lengths);
        var secondAt = Place(second, all, lengths);

        Assert.True(Minute(secondAt) >= Minute(firstAt) + 90, $"{secondAt} starts before {firstAt} plus its 90 minutes");
    }

    [Fact]
    public void Place_AfterASchedulesThatNeverFiresInReach_SaysWhy()
    {
        var late = Fixed("late", "0 5 * * *");
        var early = Window("early", "window=00:30-02:00 after=late gap=120");

        var (placed, error) = WindowSchedule.Place(early, WindowSchedule.Parse(early.CronExpression).Settings!,
            new List<CronJobDto> { late, early }, Lengths(), Now);

        Assert.Null(placed);
        Assert.Contains("no minute in the window is at least 2h 00m after 'late' fires", error);
    }

    // ---- the load --------------------------------------------------------------------------------------------

    [Fact]
    public void Place_StaysOutOfACrowd_AndNeverMovesTheFixedSchedules()
    {
        var crowd = new[] { Fixed("a", "0 3 * * *"), Fixed("b", "0 3 * * *"), Fixed("c", "0 3 * * *") };
        var job = Window("job", "window=02:00-05:00");
        var all = crowd.Append(job).ToList();

        var at = Place(job, all, Lengths(("a", 60), ("b", 60), ("c", 60), ("job", 30)));

        // Nothing open from 04:00, nobody starting within half an hour, and as near the middle (03:30) as that allows.
        Assert.Equal("04:00", at);
        Assert.All(crowd, f => Assert.Equal("0 3 * * *", f.CronExpression));
    }

    [Fact]
    public void PlaceAgain_AWindowThatWasNeverPlaced_GetsAMinute_AndThenANextRun()
    {
        // A window schedule created switched off is never placed; a factory's Restore switches it on through this.
        var job = Window("restored", "window=02:00-05:00");
        Assert.Null(CronSchedule.ComputeNextRunUtc(job, Now));

        var (expression, error) = WindowSchedule.PlaceAgain(job, new[] { job }, Lengths(("restored", 30)), Now);

        Assert.Null(error);
        Assert.Contains("placed=", expression);
        job.CronExpression = expression;
        Assert.NotNull(CronSchedule.ComputeNextRunUtc(job, Now));
    }

    [Fact]
    public void PlaceAgain_AWindowWithNoWorkableMinute_SaysWhy()
    {
        var job = Window("tight", "window=02:00-02:30 deadline=02:40");

        var (expression, error) = WindowSchedule.PlaceAgain(job, new[] { job }, Lengths(("tight", 90)), Now);

        Assert.Null(expression);
        Assert.Contains("deadline", error);
    }

    [Fact]
    public void DeadlineFor_IsTheFirstDeadlineAfterTheRun_AndNullWithoutOne()
    {
        var job = Window("late", "window=23:00-02:00 deadline=03:00 placed=23:30");
        // 23:30 on 9 October, Toronto time, is 03:30 UTC on the 10th; the deadline is 03:00 on the 10th, 07:00 UTC.
        var run = new DateTime(2026, 10, 10, 3, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc), WindowSchedule.DeadlineFor(job, run));
        Assert.Null(WindowSchedule.DeadlineFor(Window("open", "window=23:00-02:00 placed=23:30"), run));
        Assert.Null(WindowSchedule.DeadlineFor(Fixed("fixed", "0 7 * * *"), run));
    }

    [Fact]
    public void Place_TwoWindowsOnOneMachine_DoNotStartTogether()
    {
        var one = Window("one", "window=01:00-05:00");
        var two = Window("two", "window=01:00-05:00");
        var all = new List<CronJobDto> { one, two };
        var lengths = Lengths(("one", 20), ("two", 20));

        var oneAt = Place(one, all, lengths);
        var twoAt = Place(two, all, lengths);

        Assert.Equal("03:00", oneAt);
        Assert.True(Math.Abs(Minute(twoAt) - Minute(oneAt)) > 30, $"{oneAt} and {twoAt} start within half an hour");
    }

    [Fact]
    public void Place_AWindowOnAnotherMachine_IsNotInTheWay()
    {
        var elsewhere = Window("elsewhere", "window=01:00-05:00", machine: "devlinux");
        var all = new List<CronJobDto> { elsewhere };
        Place(elsewhere, all, Lengths());
        var here = Window("here", "window=01:00-05:00");
        all.Add(here);

        Assert.Equal("03:00", Place(here, all, Lengths()));
    }

    // ---- the deadline ----------------------------------------------------------------------------------------

    [Fact]
    public void Place_ADeadline_LeavesTheRunTimeToFinish()
    {
        var job = Window("job", "window=05:00-08:00 deadline=07:00");

        var at = Place(job, new List<CronJobDto> { job }, Lengths(("job", 60)));

        // The latest start is 06:00, so the middle of what is left is 05:30.
        Assert.Equal("05:30", at);
        Assert.True(Minute(at) + 60 <= Minute("07:00"));
    }

    [Fact]
    public void Place_ADeadlineThatLeavesNoTime_SaysSo()
    {
        var job = Window("job", "window=06:30-08:00 deadline=07:00");

        var (placed, error) = WindowSchedule.Place(job, WindowSchedule.Parse(job.CronExpression).Settings!,
            new List<CronJobDto> { job }, Lengths(("job", 60)), Now);

        Assert.Null(placed);
        Assert.Equal("the window and the deadline leave no time to run a session of about 1h 00m", error);
    }

    // ---- firing ----------------------------------------------------------------------------------------------

    [Fact]
    public void ComputeNextRunUtc_APlacedWindow_FiresAtItsMinuteOnTheNextAllowedDay()
    {
        // Friday 00:10; weekdays only, placed 03:40 - so Friday 03:40, then Monday 03:40.
        var job = Window("job", "window=00:00-06:30 days=1-5 placed=03:40");

        var next = CronSchedule.ComputeNextRunUtc(job, Now)!.Value;
        var after = CronSchedule.ComputeNextRunUtc(job, next)!.Value;

        Assert.Equal(new DateTime(2026, 10, 9, 3, 40, 0), TimeZoneInfo.ConvertTimeFromUtc(next, Toronto));
        Assert.Equal(new DateTime(2026, 10, 12, 3, 40, 0), TimeZoneInfo.ConvertTimeFromUtc(after, Toronto));
    }

    [Fact]
    public void ComputeNextRunUtc_AWindowAcrossMidnightOnWeekdays_FiresInTheWindowsThatOpenOnWeekdays()
    {
        // Weekday windows from 22:00 to 04:00, placed at 01:00: Thursday night's run is early Friday, Friday night's is
        // early Saturday, and the next is Monday night's, early Tuesday - never early Monday, from Sunday night.
        var job = Window("job", "window=22:00-04:00 days=1-5 placed=01:00");

        var fires = new List<DateTime>();
        var from = Now.AddHours(-1);
        for (var i = 0; i < 3; i++)
        {
            from = CronSchedule.ComputeNextRunUtc(job, from)!.Value;
            fires.Add(TimeZoneInfo.ConvertTimeFromUtc(from, Toronto));
        }

        Assert.Equal(new[]
        {
            new DateTime(2026, 10, 9, 1, 0, 0), new DateTime(2026, 10, 10, 1, 0, 0), new DateTime(2026, 10, 13, 1, 0, 0),
        }, fires);
    }

    [Fact]
    public void Place_OnTheNightTheClockSkipsAnHour_NeverChoosesAMinuteAfterTheWindowsEnd()
    {
        // Saturday 13 March 2027, 20:00 in Toronto; the clocks go forward at 02:00. The machine is full from 00:00 to
        // 06:00, so the quiet minutes are at the very end of the window - which still ends at 06:30 by the clock.
        var saturdayEvening = new DateTime(2027, 3, 14, 1, 0, 0, DateTimeKind.Utc);
        var busy = Fixed("busy", "0 0 * * *");
        var job = Window("job", "window=00:00-06:30");
        var settings = WindowSchedule.Parse(job.CronExpression).Settings!;

        var (placed, error) = WindowSchedule.Place(job, settings, new List<CronJobDto> { busy, job },
            Lengths(("busy", 360), ("job", 20)), saturdayEvening);

        Assert.True(placed is not null, error);
        Assert.InRange(placed!.PlacedMinute!.Value, 0, Minute("06:30"));
    }

    [Fact]
    public void ComputeNextRunUtc_AWindowNotPlacedYet_HasNoNextRun()
    {
        Assert.Null(CronSchedule.ComputeNextRunUtc(Window("job", "window=00:00-06:30"), Now));
    }

    [Fact]
    public void Load_CountsAWindowScheduleAtItsPlacedMinute()
    {
        var job = Window("job", "window=00:00-06:30 placed=03:40");

        var machine = Assert.Single(CronLoad.Build(new[] { job }, Lengths(("job", 30)), Now, 6).Machines);

        Assert.Equal(new[] { "job" }, machine.Hours.Single(h => h.Label == "03:00").JobIds);
    }

    // ---- the settings ----------------------------------------------------------------------------------------

    [Fact]
    public void Parse_TheFullSettings_RoundTripInTheCanonicalOrder()
    {
        var (settings, error) = WindowSchedule.Parse("placed=03:40 gap=120 after=cj_a deadline=07:00 days=1-5 window=00:00-06:30");

        Assert.Null(error);
        Assert.Equal("window=00:00-06:30 days=1-5 deadline=07:00 after=cj_a gap=120 placed=03:40", settings!.ToText());
        Assert.Equal("window 00:00-06:30, placed 03:40, days 1-5, done by 07:00, at least 2h 00m after Nightly backup",
            WindowSchedule.Describe(settings, id => id == "cj_a" ? "Nightly backup" : null));
    }

    [Theory]
    [InlineData("", "cronExpression must hold the window settings, e.g. 'window=00:00-06:30'")]
    [InlineData("deadline=07:00", "window is required for a window schedule, e.g. window=00:00-06:30")]
    [InlineData("window=01:00-01:20", "window must be at least 30 minutes long, not 20; a narrower one is a fixed time")]
    [InlineData("window=25:00-03:00", "window must be HH:mm-HH:mm with real times of day, not '25:00-03:00'")]
    [InlineData("window=00:00-06:30 gap=60", "gap is the time after another schedule, so it needs after=<schedule id>")]
    [InlineData("window=00:00-06:30 after=cj_a gap=800", "gap must be a whole number of minutes from 0 to 720, not '800'")]
    [InlineData("window=00:00-06:30 days=funday", "days must be a cron day-of-week field, for example 1-5 or 0,6, not 'funday'")]
    [InlineData("window=00:00-06:30 when=soon", "unknown window setting 'when'; the settings are window, days, deadline, after, gap, placed")]
    public void Parse_ABadSetting_SaysWhatIsWrong(string text, string expected)
    {
        Assert.Equal(expected, WindowSchedule.Parse(text).Error);
    }

    [Fact]
    public void Validate_AcceptsTheWindowKind()
    {
        Assert.Equal((true, (string?)null), CronSchedule.Validate(Window("job", "window=00:00-06:30")));
    }
}
