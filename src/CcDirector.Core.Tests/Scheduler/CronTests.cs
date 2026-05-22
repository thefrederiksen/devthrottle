using CcDirector.Core.Scheduler;
using Xunit;

namespace CcDirector.Core.Tests.Scheduler;

public class CronTests
{
    [Fact]
    public void Daily_NeverFired_BeforeScheduledTime_DoesNotFire()
    {
        var schedule = Cron.Daily("08:00");
        var now = new DateTime(2026, 5, 22, 7, 30, 0);

        Assert.False(schedule.ShouldFire(DateTime.MinValue, now));
    }

    [Fact]
    public void Daily_NeverFired_AfterScheduledTime_Fires()
    {
        var schedule = Cron.Daily("08:00");
        var now = new DateTime(2026, 5, 22, 8, 30, 0);

        Assert.True(schedule.ShouldFire(DateTime.MinValue, now));
    }

    [Fact]
    public void Daily_AlreadyFiredToday_DoesNotFireAgain()
    {
        var schedule = Cron.Daily("08:00");
        var firedAt = new DateTime(2026, 5, 22, 8, 15, 0);
        var now = new DateTime(2026, 5, 22, 15, 0, 0);

        Assert.False(schedule.ShouldFire(firedAt, now));
    }

    [Fact]
    public void Daily_FiredYesterday_FiresAgainToday()
    {
        var schedule = Cron.Daily("08:00");
        var firedYesterday = new DateTime(2026, 5, 21, 8, 15, 0);
        var now = new DateTime(2026, 5, 22, 8, 30, 0);

        Assert.True(schedule.ShouldFire(firedYesterday, now));
    }

    [Theory]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Weekdays_OnWeekend_DoesNotFire(DayOfWeek dow)
    {
        var schedule = Cron.Weekdays("08:00");
        // Find a date that lands on the requested weekday.
        var d = new DateTime(2026, 5, 23); // 2026-05-23 is Saturday
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        var now = d.AddHours(9);

        Assert.False(schedule.ShouldFire(DateTime.MinValue, now));
    }

    [Fact]
    public void Weekdays_OnMondayAfter8_Fires()
    {
        var schedule = Cron.Weekdays("08:00");
        var monday = new DateTime(2026, 5, 25, 8, 30, 0); // 2026-05-25 is Monday
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        Assert.True(schedule.ShouldFire(DateTime.MinValue, monday));
    }

    [Fact]
    public void EveryMinutes_RespectsInterval()
    {
        var schedule = Cron.EveryMinutes(15);
        var now = new DateTime(2026, 5, 22, 10, 0, 0);

        Assert.True(schedule.ShouldFire(DateTime.MinValue, now));
        Assert.True(schedule.ShouldFire(now.AddMinutes(-15), now));
        Assert.False(schedule.ShouldFire(now.AddMinutes(-10), now));
    }

    [Fact]
    public void EveryMinutes_ZeroOrNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cron.EveryMinutes(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cron.EveryMinutes(-1));
    }
}
