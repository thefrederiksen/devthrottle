using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The <c>random</c> schedule kind (issue #3622): a pinned week, the statistical properties over ten years of
/// days, the human-rhythm shape, daylight-saving days, walking the plan with ComputeNextRunUtc, and validation.
/// </summary>
public sealed class RandomScheduleTests
{
    private const string OwnerSettings = "window=07:00-01:00 perDay=4 minGap=45 shape=human";
    private const string Toronto = "America/Toronto";

    private static readonly TimeZoneInfo Zone = CronSchedule.FindZone(Toronto)!;

    private readonly ITestOutputHelper _out;

    public RandomScheduleTests(ITestOutputHelper output) => _out = output;

    private static RandomScheduleSettings Settings(string text)
    {
        var (settings, error) = RandomSchedule.Parse(text);
        Assert.True(settings is not null, error);
        return settings!;
    }

    private static CronJobDto RandomJob(string id, string settings, string zone = Toronto) => new()
    {
        Id = id,
        Name = "random",
        ScheduleKind = CronSchedule.KindRandom,
        CronExpression = settings,
        TimeZoneId = zone,
        Target = new CronJobTarget { Machine = "workstation-A" },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    // ---- acceptance 1: a pinned week -----------------------------------------------------------------

    [Fact]
    public void PlanMinutes_FixedJobAndSettings_GivesThePinnedWeek()
    {
        var settings = Settings(OwnerSettings);
        var expected = new Dictionary<string, int[]>
        {
            ["2026-10-08"] = new[] { 426, 499, 568, 694, 927, 1028 },
            ["2026-10-09"] = new[] { 667, 1126 },
            ["2026-10-10"] = new[] { 518, 636, 1114 },
            ["2026-10-11"] = new[] { 548, 625, 705, 1013 },
            ["2026-10-12"] = new[] { 504, 956 },
            ["2026-10-13"] = new[] { 640, 712 },
            ["2026-10-14"] = new[] { 578, 1118 },
        };

        var actual = expected.Keys.ToDictionary(
            date => date, date => RandomSchedule.PlanMinutes("cj_reddit_example", DateOnly.Parse(date), settings, Zone).ToArray());
        foreach (var (date, minutes) in actual)
            _out.WriteLine($"[\"{date}\"] = new[] {{ {string.Join(", ", minutes)} }},");

        foreach (var (date, minutes) in expected)
            Assert.Equal(minutes, actual[date]);
    }

    [Fact]
    public void PlanUtc_SameInputs_SameAnswerEveryTime()
    {
        var settings = Settings(OwnerSettings);
        var zone = CronSchedule.FindZone(Toronto)!;
        var day = new DateOnly(2026, 10, 8);

        var first = RandomSchedule.PlanUtc("cj_a", day, settings, zone);
        var second = RandomSchedule.PlanUtc("cj_a", day, settings, zone);
        var otherJob = RandomSchedule.PlanUtc("cj_b", day, settings, zone);

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherJob);
    }

    // ---- acceptance 2: the properties over 3,650 days ------------------------------------------------

    [Fact]
    public void PlanMinutes_TenYears_MeanGapWindowAndHumanShapeHold()
    {
        var settings = Settings(OwnerSettings);
        var (count, minGap, perHour) = Simulate("cj_reddit_example", settings, 3650);

        var mean = count / 3650.0;
        _out.WriteLine($"mean={mean:0.000} minGap={minGap}");
        for (var h = 0; h < 24; h++)
            _out.WriteLine($"{h:00}: {perHour[h] / (double)count:0.000}");

        Assert.InRange(mean, settings.PerDay - 0.1, settings.PerDay + 0.1);
        Assert.True(minGap >= settings.MinGapMinutes, $"smallest gap {minGap} is under {settings.MinGapMinutes}");
        for (var h = 1; h < 7; h++)
            Assert.Equal(0, perHour[h]);   // 01:00 to 06:59 is outside the window

        var afternoon = perHour[16] + perHour[17] + perHour[18];
        var midday = perHour[12] + perHour[13] + perHour[14];
        Assert.True(afternoon >= 3 * midday, $"16-18 had {afternoon} fires against {midday} at 12-14");
    }

    [Fact]
    public void PlanMinutes_FlatShape_NoHourGetsMoreThanDoubleAnother()
    {
        var flat = string.Join(",", Enumerable.Repeat("1", 24));
        var settings = Settings($"window=07:00-01:00 perDay=4 minGap=45 shape={flat}");
        var (count, minGap, perHour) = Simulate("cj_flat", settings, 3650);

        var inWindow = Enumerable.Range(0, 24).Where(h => h >= 7 || h == 0).Select(h => perHour[h]).ToList();
        _out.WriteLine(string.Join(", ", inWindow));
        Assert.True(inWindow.Max() <= 2 * inWindow.Min(), $"max {inWindow.Max()} min {inWindow.Min()}");
        Assert.True(minGap >= 45);
        Assert.InRange(count / 3650.0, 3.9, 4.1);
    }

    private static (int Count, int MinGap, int[] PerHour) Simulate(string jobId, RandomScheduleSettings settings, int days)
    {
        var perHour = new int[24];
        var count = 0;
        var minGap = int.MaxValue;
        long? previous = null;
        var start = new DateOnly(2026, 10, 8);
        for (var d = 0; d < days; d++)
        {
            foreach (var minute in RandomSchedule.PlanMinutes(jobId, start.AddDays(d), settings, Zone))
            {
                Assert.InRange(minute, settings.WindowStartMinute, settings.WindowStartMinute + settings.WindowMinutes - 1);
                var absolute = (long)d * 1440 + minute;
                if (previous is not null)
                    minGap = Math.Min(minGap, (int)(absolute - previous.Value));
                previous = absolute;
                perHour[minute / 60 % 24]++;
                count++;
            }
        }
        return (count, minGap, perHour);
    }

    // ---- acceptance 3: daylight-saving days in America/Toronto ---------------------------------------

    [Theory]
    [InlineData("2026-03-08")]   // spring forward: 02:00 to 02:59 does not exist
    [InlineData("2026-11-01")]   // fall back: 01:00 to 01:59 happens twice
    public void PlanUtc_DaylightSavingDay_GapAndWindowHoldInUtc_NothingMoved_NothingTwice(string date)
    {
        // A window over the change, so the skipped and the repeated hour are both reachable.
        const string text = "window=00:00-06:00 perDay=4 minGap=15 shape=human";
        var settings = Settings(text);
        var day = DateOnly.Parse(date);

        var acrossTheChange = 0;
        for (var i = 0; i < 300; i++)
        {
            var id = $"cj_dst_{i}";
            var minutes = RandomSchedule.PlanMinutes(id, day, settings, Zone);
            var plan = RandomSchedule.PlanUtc(id, day, settings, Zone);
            AssertGapWindowAndNothingMoved(id, day, settings, minutes, plan, windowEnd: TimeSpan.FromHours(6));
            if (minutes.Any(m => m < 120) && minutes.Any(m => m >= 180))
                acrossTheChange++;

            // Walking the schedule fires each planned instant exactly once.
            var walked = Walk(RandomJob(id, text), plan[0].AddSeconds(-1), plan.Count);
            Assert.Equal(plan, walked);
        }
        Assert.True(acrossTheChange > 0, "no plan had fires on both sides of the changed hour, so the change was never exercised");
    }

    [Fact]
    public void PlanUtc_SpringForward_ReviewersGapInput_KeepsTheGapInUtc()
    {
        // Review of #3625, finding 1: this plan once held 02:12 local, moved to 03:00 and ten minutes from 03:10.
        var settings = Settings("window=00:00-06:00 perDay=4 minGap=15 shape=human");
        var day = new DateOnly(2026, 3, 8);

        var minutes = RandomSchedule.PlanMinutes("cj_dst_3", day, settings, Zone);
        var plan = RandomSchedule.PlanUtc("cj_dst_3", day, settings, Zone);

        _out.WriteLine(string.Join(", ", plan.Select(u => $"{u:HH:mm}Z")));
        AssertGapWindowAndNothingMoved("cj_dst_3", day, settings, minutes, plan, windowEnd: TimeSpan.FromHours(6));
        Assert.DoesNotContain(minutes, m => m / 60 == 2);
    }

    [Fact]
    public void PlanUtc_SpringForward_ReviewersWindowInput_StaysInsideTheWindow()
    {
        // Review of #3625, finding 1: this plan once chose 02:12, which was moved to 03:00, outside 00:00-02:30.
        var settings = Settings("window=00:00-02:30 perDay=1 minGap=10 shape=human");
        var day = new DateOnly(2026, 3, 8);

        var minutes = RandomSchedule.PlanMinutes("cj_out_0", day, settings, Zone);
        var plan = RandomSchedule.PlanUtc("cj_out_0", day, settings, Zone);

        AssertGapWindowAndNothingMoved("cj_out_0", day, settings, minutes, plan, windowEnd: TimeSpan.FromMinutes(150));
        Assert.All(minutes, m => Assert.True(m < 120, $"minute {m} is in the hour that does not exist"));
    }

    // Every fire is at least minGap after the one before IN UTC, inside the window on the local clock, at exactly the
    // local minute that was planned (nothing moved), and no instant twice.
    private static void AssertGapWindowAndNothingMoved(string id, DateOnly day, RandomScheduleSettings settings,
        IReadOnlyList<int> minutes, IReadOnlyList<DateTime> plan, TimeSpan windowEnd)
    {
        Assert.NotEmpty(plan);
        Assert.Equal(minutes.Count, plan.Count);
        for (var j = 0; j < plan.Count; j++)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(plan[j], Zone);
            Assert.Equal(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes[j]), local);
            Assert.True(local.TimeOfDay < windowEnd, $"{id}: {local:HH:mm} is outside the window");
            if (j > 0)
                Assert.True((plan[j] - plan[j - 1]).TotalMinutes >= settings.MinGapMinutes,
                    $"{id}: {plan[j - 1]:HH:mm}Z then {plan[j]:HH:mm}Z is under {settings.MinGapMinutes} minutes");
        }
    }

    [Fact]
    public void PlanUtc_FallBackHour_UsesTheEarlierInstant()
    {
        // 01:00 to 01:59 on 2026-11-01 happens twice, first in daylight time (05:00 UTC on) and again an hour
        // later in standard time; the plan uses the first.
        var settings = Settings("window=01:00-03:00 perDay=1 minGap=10 shape=human");
        var zone = CronSchedule.FindZone(Toronto)!;
        var day = new DateOnly(2026, 11, 1);
        var ambiguous = 0;
        for (var i = 0; i < 50; i++)
        {
            var minute = RandomSchedule.PlanMinutes($"cj_fb_{i}", day, settings, zone).Single();
            var utc = RandomSchedule.PlanUtc($"cj_fb_{i}", day, settings, zone).Single();
            if (minute >= 120)
                continue;
            ambiguous++;   // 01:00 to 01:59 local, the hour that happens twice
            Assert.Equal(new DateTime(2026, 11, 1, 5, minute - 60, 0, DateTimeKind.Utc), utc);
        }
        Assert.True(ambiguous > 0, "no job planned a fire in the repeated hour, so the rule was never exercised");
    }

    // ---- acceptance 4: ComputeNextRunUtc walks the plan ----------------------------------------------

    [Fact]
    public void ComputeNextRunUtc_OnItsOwnResult_WalksThePlanInOrder_AcrossMidnightAndDays()
    {
        var text = "window=18:00-03:00 perDay=4 minGap=15 shape=human";   // crosses midnight
        var job = RandomJob("cj_walk", text);
        var first = new DateOnly(2026, 10, 8);

        var expected = CronSchedule.RandomPlan(job, first, first.AddDays(9)).Select(p => p.Utc).ToList();
        var walked = Walk(job, expected[0].AddMinutes(-1), expected.Count);

        Assert.Equal(expected, walked);
        Assert.Contains(walked, utc => TimeZoneInfo.ConvertTimeFromUtc(utc, CronSchedule.FindZone(Toronto)!).Hour < 3);
    }

    [Fact]
    public void ComputeNextRunUtc_FromInsideTheWindow_ReturnsTheNextPlannedTimeNotTheFirst()
    {
        var job = RandomJob("cj_mid", OwnerSettings);
        var day = new DateOnly(2026, 10, 8);
        var plan = CronSchedule.RandomPlan(job, day, day).Select(p => p.Utc).ToList();
        Assert.True(plan.Count >= 2);

        var next = CronSchedule.ComputeNextRunUtc(job, plan[0]);   // strictly after the first

        Assert.Equal(plan[1], next);
    }

    private static List<DateTime> Walk(CronJobDto job, DateTime fromUtc, int steps)
    {
        var walked = new List<DateTime>();
        var at = fromUtc;
        for (var i = 0; i < steps; i++)
        {
            var next = CronSchedule.ComputeNextRunUtc(job, at);
            Assert.NotNull(next);
            walked.Add(next.Value);
            at = next.Value;
        }
        return walked;
    }

    // ---- acceptance 5: validation, one reason per failure --------------------------------------------

    [Theory]
    [InlineData("window=7:00-01:00 perDay=4 minGap=45", "window must be HH:mm-HH:mm")]
    [InlineData("window=07:00-25:00 perDay=4 minGap=45", "real times of day")]
    [InlineData("window=07:00-07:30 perDay=1 minGap=10", "at least 60 minutes")]
    [InlineData("window=07:00-01:00 perDay=0 minGap=45", "perDay must be a whole number from 1 to 24")]
    [InlineData("window=07:00-01:00 perDay=25 minGap=45", "perDay must be a whole number from 1 to 24")]
    [InlineData("window=07:00-01:00 perDay=4 minGap=5", "minGap must be a whole number of minutes from 10 to 240")]
    [InlineData("window=07:00-01:00 perDay=4 minGap=300", "minGap must be a whole number of minutes from 10 to 240")]
    [InlineData("window=07:00-09:00 perDay=4 minGap=45", "too short for 8 fires")]
    [InlineData("window=07:00-07:00 perDay=1 minGap=30", "between one day's window and the next")]
    [InlineData("perDay=4 minGap=45", "window is required")]
    [InlineData("window=07:00-01:00 minGap=45", "perDay is required")]
    [InlineData("window=07:00-01:00 perDay=4", "minGap is required")]
    [InlineData("window=07:00-01:00 perDay=4 minGap=45 shape=1,2,3", "24 comma-separated hourly weights")]
    [InlineData("window=07:00-01:00 perDay=4 minGap=45 colour=red", "unknown random setting 'colour'")]
    [InlineData("window=07:00-01:00 perDay=4 perDay=5 minGap=45", "given twice")]
    [InlineData("", "must hold the random settings")]
    public void Validate_BadRandomSettings_GivesItsOwnReason(string settings, string reason)
    {
        var (ok, error) = CronSchedule.Validate(RandomJob("", settings));

        Assert.False(ok);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Validate_HugeShapeWeight_IsRefused_SoTheWeightsCannotOverflow()
    {
        // Review of #3625, finding 4: 1e308 is finite, but four slots of it add up to infinity.
        var weights = string.Join(",", Enumerable.Repeat("1", 23).Prepend("1" + new string('0', 308)));
        var (ok, error) = CronSchedule.Validate(RandomJob("", $"window=00:00-02:00 perDay=1 minGap=10 shape={weights}"));

        Assert.False(ok);
        Assert.Contains("hour 00 must be a positive number no larger than 1000000", error);
        Assert.True(CronSchedule.Validate(RandomJob("", "window=00:00-02:00 perDay=1 minGap=10 shape=" +
            string.Join(",", Enumerable.Repeat("1", 23).Prepend("1000000")))).Ok);
    }

    [Fact]
    public void Validate_ZeroShapeWeight_IsRefused()
    {
        var weights = string.Join(",", Enumerable.Repeat("1", 23).Prepend("0"));
        var (ok, error) = CronSchedule.Validate(RandomJob("", $"window=07:00-01:00 perDay=4 minGap=45 shape={weights}"));

        Assert.False(ok);
        Assert.Contains("hour 00 must be a positive number", error);
    }

    [Fact]
    public void Validate_OwnerSettings_AndShapeDefaultsToHuman_Ok()
    {
        Assert.True(CronSchedule.Validate(RandomJob("", OwnerSettings)).Ok);
        var (settings, _) = RandomSchedule.Parse("window=07:00-01:00 perDay=4 minGap=45");
        Assert.NotNull(settings);
        Assert.True(settings.ShapeIsHuman);
        Assert.Equal(1080, settings.WindowMinutes);
        Assert.Equal(OwnerSettings, settings.ToText());
    }

    [Fact]
    public void Validate_UnknownKind_NamesAllThreeKinds()
    {
        var job = RandomJob("", OwnerSettings);
        job.ScheduleKind = "sometimes";

        var (_, error) = CronSchedule.Validate(job);

        Assert.Equal("scheduleKind must be 'recurring', 'oneOff' or 'random'", error);
    }

    // ---- the words, and the plan the route returns ---------------------------------------------------

    [Fact]
    public void Describe_RandomJob_SaysItInWords()
    {
        var job = RandomJob("cj_x", OwnerSettings);

        var text = FactoryScheduleText.Describe(job, CronSchedule.FindZone(Toronto)!);

        Assert.Equal("About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)", text);
    }

    [Fact]
    public void StampDisplay_RandomJob_GetsItsWordsAndTodaysRemainingTimes_OtherKindsAreLeftAlone()
    {
        var job = RandomJob("cj_reddit_example", OwnerSettings);
        var day = new DateOnly(2026, 10, 8);
        var all = CronSchedule.RandomPlan(job, day, day).Select(p => p.Utc).ToList();   // 07:06, 08:19, ...

        CronSchedule.StampDisplay(job, all[0]);   // the first has just happened

        Assert.Equal("About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)", job.ScheduleText);
        Assert.Equal(new[] { "08:19", "09:28", "11:34", "15:27", "17:08" }, job.RemainingToday);

        var cron = RandomJob("cj_cron", "");
        cron.ScheduleKind = CronSchedule.KindRecurring;
        cron.CronExpression = "0 7 * * *";
        CronSchedule.StampDisplay(cron, all[0]);
        Assert.Null(cron.ScheduleText);
        Assert.Null(cron.RemainingToday);
    }

    [Fact]
    public void StampDisplay_AfterMidnight_StillTodayIncludesLastNightsWindowsFireThatIsToday()
    {
        // Review of #3625, finding 3: last night's window plans 00:30 on the 9th; at 00:15 that is still to come today.
        var job = RandomJob("cj_late_52", OwnerSettings);
        Assert.Contains(1470, RandomSchedule.PlanMinutes("cj_late_52", new DateOnly(2026, 10, 8), Settings(OwnerSettings), Zone));
        var now = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 9, 0, 15, 0), Zone);

        CronSchedule.StampDisplay(job, now);

        Assert.NotNull(job.RemainingToday);
        Assert.Equal("00:30", job.RemainingToday[0]);
        var expected = CronSchedule.RandomPlan(job, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 9))
            .Where(p => p.Utc > now)
            .Select(p => TimeZoneInfo.ConvertTimeFromUtc(p.Utc, Zone))
            .Where(l => l.Date == new DateTime(2026, 10, 9))
            .Select(l => l.ToString("HH:mm"));
        Assert.Equal(expected, job.RemainingToday);
    }

    [Fact]
    public void BuildPlan_ListsOnlyFiresStillToCome_WithTheirWindowDateAndLocalTime()
    {
        var job = RandomJob("cj_reddit_example", OwnerSettings);
        var zone = CronSchedule.FindZone(Toronto)!;
        var day = new DateOnly(2026, 10, 8);
        var all = CronSchedule.RandomPlan(job, day, day).Select(p => p.Utc).ToList();
        var now = all[1];   // the first two have happened (the second exactly now)

        var plan = CronSchedule.BuildPlan(job, now, days: 1);

        Assert.Equal(all.Skip(2), plan.Fires.Select(f => f.Utc));
        Assert.All(plan.Fires, f => Assert.Equal("2026-10-08", f.WindowDate));
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(all[2], zone).ToString("yyyy-MM-dd HH:mm"), plan.Fires[0].Local);
        Assert.Equal("About 4 times a day at random, 07:00 to 01:00 (at least 45 min apart)", plan.Description);
    }
}
