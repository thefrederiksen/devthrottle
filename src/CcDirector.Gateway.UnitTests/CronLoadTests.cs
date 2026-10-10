using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The load strip's forecast (the owner, 2026-10-09): every active schedule's fires over the next 24 hours, each held
/// open for its own measured run length, as the most sessions open at once in each hour of each machine.
/// </summary>
public sealed class CronLoadTests
{
    // 00:10 in Toronto (daylight time, four hours behind), so the window is the local day 00:00 to 23:00.
    private static readonly DateTime Now = new(2026, 10, 9, 4, 10, 0, DateTimeKind.Utc);
    private const string Zone = "America/Toronto";

    private static CronJobDto Daily(string id, string cron, string machine = "SOREN_NORTH", bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Enabled = enabled,
        ScheduleKind = CronSchedule.KindRecurring,
        CronExpression = cron,
        TimeZoneId = Zone,
        Target = new CronJobTarget { Machine = machine },
        Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
    };

    private static Dictionary<string, TimeSpan> Lengths(params (string Id, int Minutes)[] lengths) =>
        lengths.ToDictionary(l => l.Id, l => TimeSpan.FromMinutes(l.Minutes));

    private static CronLoadHourDto Hour(CronMachineLoadDto machine, string label) =>
        machine.Hours.Single(h => h.Label == label);

    [Fact]
    public void Build_SessionsThatOverlap_AreCountedTogether_AndAnHourAboveCapacityIsOver()
    {
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"), Daily("c", "15 8 * * *") };

        var load = CronLoad.Build(jobs, Lengths(("a", 90), ("b", 90), ("c", 10)), Now, capacity: 2);

        var machine = Assert.Single(load.Machines);
        Assert.Equal(24, machine.Hours.Count);
        Assert.Equal("00:00", machine.Hours[0].Label);
        Assert.Equal(2, Hour(machine, "07:00").Concurrent);
        Assert.Equal(2, Hour(machine, "07:00").Starts);
        Assert.False(Hour(machine, "07:00").Over);
        // a and b run to 08:30, so c at 08:15 makes three.
        Assert.Equal(3, Hour(machine, "08:00").Concurrent);
        Assert.Equal(1, Hour(machine, "08:00").Starts);
        Assert.True(Hour(machine, "08:00").Over);
        Assert.Equal(new[] { "a", "b", "c" }, Hour(machine, "08:00").JobIds.OrderBy(j => j));
        Assert.Equal(0, Hour(machine, "09:00").Concurrent);
        Assert.Equal(3, machine.Peak);
        Assert.Equal(1, machine.HoursOver);
        Assert.Equal("peak 3 at 08:00 - 1 hour over 2 - quietest 00:00 (0 open)", machine.Summary);
        Assert.Empty(machine.EstimatedJobIds);
        Assert.Equal("", machine.EstimateNote);
    }

    [Fact]
    public void Build_EachHourSaysHowManyOfItsOpenSessionsAreFactoryOnes()
    {
        var factoryJob = Daily("factory", "0 7 * * *");
        factoryJob.Factory = "clickfunnels";
        factoryJob.Seat = "builder";
        var jobs = new[] { factoryJob, Daily("own-a", "0 7 * * *"), Daily("own-b", "30 9 * * *") };

        var machine = Assert.Single(CronLoad.Build(jobs, Lengths(("factory", 30), ("own-a", 30), ("own-b", 30)), Now, 6).Machines);

        Assert.Equal(2, Hour(machine, "07:00").Concurrent);
        Assert.Equal(1, Hour(machine, "07:00").FactoryConcurrent);
        Assert.Equal(1, Hour(machine, "09:00").Concurrent);
        Assert.Equal(0, Hour(machine, "09:00").FactoryConcurrent);
    }

    [Fact]
    public void Build_ARunEndingAsAnotherStarts_DoesNotOverlapIt()
    {
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 8 * * *") };

        var machine = Assert.Single(CronLoad.Build(jobs, Lengths(("a", 60), ("b", 60)), Now, capacity: 6).Machines);

        Assert.Equal(1, Hour(machine, "07:00").Concurrent);
        Assert.Equal(new[] { "a" }, Hour(machine, "07:00").JobIds);
        Assert.Equal(1, Hour(machine, "08:00").Concurrent);
        Assert.Equal(new[] { "b" }, Hour(machine, "08:00").JobIds);
    }

    [Fact]
    public void Build_ARunThatStartedBeforeTheWindow_StillHoldsItsSession()
    {
        // Last night's 23:00 run lasts three hours, so it is still open at 00:00 and 01:00.
        var machine = Assert.Single(CronLoad.Build(new[] { Daily("late", "0 23 * * *") }, Lengths(("late", 180)), Now, 6).Machines);

        Assert.Equal(1, Hour(machine, "00:00").Concurrent);
        Assert.Equal(0, Hour(machine, "00:00").Starts);
        Assert.Equal(1, Hour(machine, "01:00").Concurrent);
        Assert.Equal(0, Hour(machine, "02:00").Concurrent);
        Assert.Equal(1, Hour(machine, "23:00").Starts);
    }

    [Fact]
    public void Build_OnlyActiveSchedulesCount_AndAnUnmeasuredOneIsSaidToBeAGuess()
    {
        var paused = Daily("paused", "0 7 * * *", enabled: false);
        var spent = new CronJobDto
        {
            Id = "spent", Name = "spent", Enabled = false, ScheduleKind = CronSchedule.KindOneOff,
            RunAt = "2026-10-09T07:00:00", LastFiredUtc = Now.AddDays(-1), TimeZoneId = Zone,
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        };
        var oneOff = new CronJobDto
        {
            Id = "once", Name = "once", Enabled = true, ScheduleKind = CronSchedule.KindOneOff,
            RunAt = "2026-10-09T07:00:00", TimeZoneId = Zone,
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        };

        var machine = Assert.Single(CronLoad.Build(new[] { paused, spent, oneOff }, Lengths(), Now, 6).Machines);

        Assert.Equal(1, machine.Schedules);
        Assert.Equal(new[] { "once" }, Hour(machine, "07:00").JobIds);
        Assert.Equal(1, machine.Hours.Sum(h => h.Starts));
        // Thirty minutes, as a guess, and the forecast says so.
        Assert.Equal(new[] { "once" }, machine.EstimatedJobIds);
        Assert.Equal("1 schedule has never finished a run, so it is counted at 30 min", machine.EstimateNote);
        Assert.Equal(0, Hour(machine, "08:00").Concurrent);
    }

    [Fact]
    public void Build_EachMachineHasItsOwnStrip_BusiestFirst()
    {
        var jobs = new[]
        {
            Daily("quiet", "0 9 * * *", machine: "devlinux"),
            Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"),
        };

        var load = CronLoad.Build(jobs, Lengths(("quiet", 20), ("a", 20), ("b", 20)), Now, 6);

        Assert.Equal(new[] { "SOREN_NORTH", "devlinux" }, load.Machines.Select(m => m.Machine));
        Assert.Equal(2, load.Machines[0].Peak);
        Assert.Equal(1, load.Machines[1].Peak);
        Assert.Equal(6, load.Capacity);
        Assert.Equal("peak 1 at 09:00 - never over 6 - quietest 00:00 (0 open)", load.Machines[1].Summary);
    }

    [Fact]
    public void Build_AnActiveOneOffWhoseTimeHasPassed_OpensNow_BecauseTheEngineStillFiresIt()
    {
        var overdue = new CronJobDto
        {
            Id = "overdue", Name = "overdue", Enabled = true, ScheduleKind = CronSchedule.KindOneOff,
            RunAt = "2026-10-08T07:00:00", TimeZoneId = Zone,
            Target = new CronJobTarget { Machine = "SOREN_NORTH" },
            Action = new CronJobAction { RepoPath = @"D:\repo", Seed = "/help" },
        };

        var machine = Assert.Single(CronLoad.Build(new[] { overdue }, Lengths(("overdue", 20)), Now, 6).Machines);

        Assert.Equal(1, Hour(machine, "00:00").Starts);
        Assert.Equal(1, Hour(machine, "00:00").Concurrent);
        Assert.Equal(1, machine.Hours.Sum(h => h.Starts));
    }

    [Fact]
    public void Build_AnEveryMinuteScheduleWithALongRunLength_IsNeverCutShort()
    {
        // Sessions nobody closes: each lives three days, so the forecast reaches back three days of fires.
        var machine = Assert.Single(CronLoad.Build(new[] { Daily("minutely", "* * * * *") }, Lengths(("minutely", 3 * 24 * 60)), Now, 6).Machines);

        Assert.All(machine.Hours.Skip(1), h => Assert.Equal(60, h.Starts));
        // One fire a minute, each open three days: at any moment exactly three days of fires are open.
        Assert.Equal(3 * 24 * 60, Hour(machine, "01:00").Concurrent);
    }

    [Fact]
    public void Build_AScheduleInAZoneThisHostDoesNotKnow_IsNamedAndLeftOut_TheRestStillForecast()
    {
        var lost = Daily("lost", "0 7 * * *");
        lost.TimeZoneId = "Nowhere/Atlantis";

        var machine = Assert.Single(CronLoad.Build(new[] { lost, Daily("a", "0 7 * * *") }, Lengths(("a", 20)), Now, 6).Machines);

        Assert.Equal(new[] { "a" }, Hour(machine, "07:00").JobIds);
        Assert.Equal(new[] { "lost" }, machine.UnplacedJobIds);
        Assert.Equal("1 schedule is not counted: its time zone is not known on this host", machine.UnplacedNote);
        Assert.Equal(Zone, machine.TimeZoneId);
    }

    [Fact]
    public void WarningFor_AScheduleThatRunsInsideTheCrowd_NamesHowManyAreOpenAndTheQuietestHour()
    {
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"), Daily("new", "30 7 * * *") };

        Assert.Equal(
            "SOREN_NORTH will have 3 scheduled sessions open at once while this one runs from 07:30, over its capacity "
            + "of 2. The quietest hour is 00:00 (0 open) - see cc-devthrottle schedule load --machine SOREN_NORTH.",
            CronLoad.WarningFor(jobs, Lengths(("a", 90), ("b", 90), ("new", 20)), Now, 2, jobs[2]));
    }

    [Fact]
    public void WarningFor_AScheduleThatFits_IsNull_EvenWhenAnotherHourIsOver()
    {
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"), Daily("c", "0 7 * * *"), Daily("new", "0 13 * * *") };
        var lengths = Lengths(("a", 30), ("b", 30), ("c", 30), ("new", 30));

        Assert.True(CronLoad.Build(jobs, lengths, Now, 2).Machines[0].Hours.Single(h => h.Label == "07:00").Over);
        Assert.Null(CronLoad.WarningFor(jobs, lengths, Now, 2, jobs[3]));
        // A paused schedule starts nothing, so it is never warned about.
        var paused = Daily("paused", "0 7 * * *", enabled: false);
        Assert.Null(CronLoad.WarningFor(jobs.Append(paused), lengths, Now, 2, paused));
    }

    [Fact]
    public void WarningFor_ARunInTheCrowdedHourThatDoesNotOverlapTheCrowd_IsNotBlamedForIt()
    {
        // Three at 07:00 for 30 min fill the hour; the new one at 07:50 is alone by then.
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"), Daily("c", "0 7 * * *"), Daily("new", "50 7 * * *") };
        var lengths = Lengths(("a", 30), ("b", 30), ("c", 30), ("new", 20));

        Assert.True(CronLoad.Build(jobs, lengths, Now, 2).Machines[0].Hours.Single(h => h.Label == "07:00").Over);
        Assert.Null(CronLoad.WarningFor(jobs, lengths, Now, 2, jobs[3]));
    }

    [Fact]
    public void WarningFor_AScheduleWrittenJustAfterItsTimeToday_IsJudgedOnTomorrowsRunNotTheOneThatDidNotHappen()
    {
        // 07:20 in Toronto: the crowd at 07:00 is open, and a new 07:00 schedule's first run is tomorrow, outside the
        // forecast, so it adds nothing to today's 07:00.
        var at0720 = new DateTime(2026, 10, 9, 11, 20, 0, DateTimeKind.Utc);
        var jobs = new[] { Daily("a", "0 7 * * *"), Daily("b", "0 7 * * *"), Daily("new", "0 7 * * *") };
        var lengths = Lengths(("a", 30), ("b", 30), ("new", 30));

        Assert.Null(CronLoad.WarningFor(jobs, lengths, at0720, 2, jobs[2]));
    }

    [Fact]
    public void Build_AnEveryFifteenMinutesSchedule_StartsFourTimesAnHour()
    {
        var machine = Assert.Single(CronLoad.Build(new[] { Daily("often", "*/15 * * * *") }, Lengths(("often", 5)), Now, 6).Machines);

        Assert.All(machine.Hours.Skip(1), h => Assert.Equal(4, h.Starts));
        Assert.All(machine.Hours, h => Assert.Equal(1, h.Concurrent));
    }
}
