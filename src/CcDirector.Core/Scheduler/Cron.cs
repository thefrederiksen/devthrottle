namespace CcDirector.Core.Scheduler;

/// <summary>
/// A single firing rule. <see cref="ShouldFire"/> returns true when the scheduler
/// should consider firing the associated runner at <paramref name="now"/>, given
/// the time the runner most recently fired.
///
/// `lastFiredAt = DateTime.MinValue` means "never fired" -- the first eligible
/// tick after process start should be allowed to fire.
/// </summary>
public abstract class Schedule
{
    public abstract bool ShouldFire(DateTime lastFiredAt, DateTime now);

    /// <summary>Human-readable rule shown in the Scheduler UI.</summary>
    public abstract string Describe();
}

/// <summary>
/// Minimal cron-ish factory. Three patterns supported intentionally:
///   * <see cref="Daily"/> -- once per day at HH:mm.
///   * <see cref="Weekdays"/> -- Mon-Fri at HH:mm.
///   * <see cref="EveryMinutes"/> -- fixed interval, useful for tests.
///
/// Anything more elaborate, write a Schedule subclass. We intentionally do
/// not parse cron strings -- the readability cost on the few real schedules
/// we use is not worth it.
/// </summary>
public static class Cron
{
    public static Schedule Daily(string timeOfDay)
    {
        return new DailySchedule(TimeOnly.Parse(timeOfDay));
    }

    public static Schedule Weekdays(string timeOfDay)
    {
        return new WeekdaysSchedule(TimeOnly.Parse(timeOfDay));
    }

    public static Schedule EveryMinutes(int minutes)
    {
        if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        return new EveryMinutesSchedule(TimeSpan.FromMinutes(minutes));
    }

    private sealed class DailySchedule : Schedule
    {
        private readonly TimeOnly _time;
        public DailySchedule(TimeOnly time) { _time = time; }

        public override bool ShouldFire(DateTime lastFiredAt, DateTime now)
        {
            var todayFireTime = now.Date + _time.ToTimeSpan();
            if (now < todayFireTime) return false;
            return lastFiredAt < todayFireTime;
        }

        public override string Describe() => $"Daily at {_time:HH:mm}";
    }

    private sealed class WeekdaysSchedule : Schedule
    {
        private readonly TimeOnly _time;
        public WeekdaysSchedule(TimeOnly time) { _time = time; }

        public override bool ShouldFire(DateTime lastFiredAt, DateTime now)
        {
            if (now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday)
                return false;
            var todayFireTime = now.Date + _time.ToTimeSpan();
            if (now < todayFireTime) return false;
            return lastFiredAt < todayFireTime;
        }

        public override string Describe() => $"Weekdays at {_time:HH:mm}";
    }

    private sealed class EveryMinutesSchedule : Schedule
    {
        private readonly TimeSpan _interval;
        public EveryMinutesSchedule(TimeSpan interval) { _interval = interval; }

        public override bool ShouldFire(DateTime lastFiredAt, DateTime now)
        {
            return now - lastFiredAt >= _interval;
        }

        public override string Describe() => $"Every {_interval.TotalMinutes:F0} min";
    }
}
