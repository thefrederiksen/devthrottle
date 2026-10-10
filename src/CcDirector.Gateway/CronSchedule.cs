using System.Globalization;
using Cronos;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// Schedule validation and next-occurrence computation for cron jobs (epic #479, part 1 = #482).
/// This is the single place the cron-expression grammar, the time-zone resolution, and the
/// recurring/one-off rules live, so the REST surface (user-facing 400s) and the store (recompute
/// on load) agree. Cronos parses the standard 5-field expression and computes DST-correct UTC
/// occurrences; one-off times are a wall-clock timestamp in the job's zone converted to UTC.
/// </summary>
public static class CronSchedule
{
    /// <summary>A recurring job; <see cref="CronJobDto.CronExpression"/> drives it.</summary>
    public const string KindRecurring = "recurring";

    /// <summary>A one-off job; <see cref="CronJobDto.RunAt"/> drives it, then it auto-disables (part 2).</summary>
    public const string KindOneOff = "oneOff";

    /// <summary>
    /// A random job (issue #3622): about N fires a day at random times inside a daily local window, its settings
    /// held as text in <see cref="CronJobDto.CronExpression"/> (see <see cref="RandomSchedule"/>). The engine
    /// treats it as recurring in every respect: it advances after each fire and a missed fire is not replayed.
    /// </summary>
    public const string KindRandom = "random";

    /// <summary>
    /// A window job (the owner, 2026-10-09): once a day at a minute the Gateway chooses inside a local window, spread
    /// by the machine's load, optionally by a deadline and after another schedule. Its settings are text in
    /// <see cref="CronJobDto.CronExpression"/> (see <see cref="WindowSchedule"/>), and the engine treats it as
    /// recurring: it fires at the placed minute and advances to the next allowed day.
    /// </summary>
    public const string KindWindow = "window";

    /// <summary>
    /// Validate a job's definition (required fields + schedule grammar + time zone). Returns
    /// (true, null) when the job is well-formed, otherwise (false, reason) with a single
    /// human-readable reason suitable for a 400 response. Does not mutate the job.
    /// </summary>
    public static (bool Ok, string? Error) Validate(CronJobDto job)
    {
        if (job is null)
            return (false, "job body is required");
        if (string.IsNullOrWhiteSpace(job.Name))
            return (false, "name is required");
        if (string.IsNullOrWhiteSpace(job.TimeZoneId))
            return (false, "timeZoneId is required");
        if (TryFindTimeZone(job.TimeZoneId) is null)
            return (false, $"unknown timeZoneId: {job.TimeZoneId}");
        if (job.Target is null || string.IsNullOrWhiteSpace(job.Target.Machine))
            return (false, "target.machine is required");
        if (job.Action is null || string.IsNullOrWhiteSpace(job.Action.RepoPath))
            return (false, "action.repoPath is required");
        // The action is EITHER a seed (a prompt/skill) OR a work-list drain (#484). At least one is
        // required; a work-list job may omit the seed.
        if (string.IsNullOrWhiteSpace(job.Action.Seed) && string.IsNullOrWhiteSpace(job.Action.WorkListName))
            return (false, "action requires either a seed or a workListName");

        if (IsRecurring(job.ScheduleKind))
        {
            if (string.IsNullOrWhiteSpace(job.CronExpression))
                return (false, "cronExpression is required for a recurring job");
            if (TryParseCron(job.CronExpression) is null)
                return (false, $"invalid cron expression: {job.CronExpression}");
            return (true, null);
        }

        if (IsOneOff(job.ScheduleKind))
        {
            if (string.IsNullOrWhiteSpace(job.RunAt))
                return (false, "runAt is required for a one-off job");
            if (TryParseLocal(job.RunAt) is null)
                return (false, $"runAt is not a parseable timestamp: {job.RunAt}");
            return (true, null);
        }

        if (IsRandom(job.ScheduleKind))
        {
            var (settings, error) = RandomSchedule.Parse(job.CronExpression);
            return settings is null ? (false, error) : (true, null);
        }

        if (IsWindow(job.ScheduleKind))
        {
            var (settings, error) = WindowSchedule.Parse(job.CronExpression);
            return settings is null ? (false, error) : (true, null);
        }

        return (false, $"scheduleKind must be '{KindRecurring}', '{KindOneOff}', '{KindRandom}' or '{KindWindow}'");
    }

    /// <summary>
    /// Compute the next UTC instant the job is due relative to <paramref name="fromUtc"/>, or null
    /// when none exists. A recurring job uses the next cron occurrence after the instant; a one-off
    /// returns its single run time in UTC (which may already be in the past - the firing engine in
    /// part 2 decides catch-up). Returns null for an invalid job rather than throwing, so a load of
    /// a hand-edited file degrades to "no next run" instead of crashing the Gateway.
    /// </summary>
    public static DateTime? ComputeNextRunUtc(CronJobDto job, DateTime fromUtc)
    {
        var (ok, _) = Validate(job);
        if (!ok)
            return null;

        var zone = TryFindTimeZone(job.TimeZoneId);
        if (zone is null)
            return null;

        if (IsRecurring(job.ScheduleKind))
        {
            var expr = TryParseCron(job.CronExpression);
            if (expr is null)
                return null;
            var fromUtcKind = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            return expr.GetNextOccurrence(fromUtcKind, zone, inclusive: false);
        }

        if (IsRandom(job.ScheduleKind))
        {
            var (settings, _) = RandomSchedule.Parse(job.CronExpression);
            if (settings is null)
                return null;
            return RandomSchedule.NextAfter(job.Id, settings, zone, fromUtc);
        }

        if (IsWindow(job.ScheduleKind))
        {
            // The minute the Gateway placed it at, on the next allowed day; none until it has been placed.
            var (settings, _) = WindowSchedule.Parse(job.CronExpression);
            return settings is null ? null : WindowSchedule.NextAfter(settings, zone, fromUtc);
        }

        // One-off: the RunAt wall-clock time in the job's zone, converted to UTC.
        var local = TryParseLocal(job.RunAt);
        if (local is null)
            return null;
        var unspecified = DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    /// <summary>
    /// Format a UTC instant as a short wall-clock label ("yyyy-MM-dd HH:mm") in the job's own time
    /// zone, for naming the session a fire spawns after the schedule and when it ran (e.g.
    /// "Daily Error Triage - 2026-07-24 05:00"). A job's time zone is validated at create time
    /// (<see cref="Validate"/>), so it resolves here; a job whose stored id no longer resolves on this
    /// host labels in UTC so a display string is always produced - the label is cosmetic and must
    /// never throw and break a fire.
    /// </summary>
    public static string LocalRunLabel(CronJobDto job, DateTime utc)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        var zone = TryFindTimeZone(job.TimeZoneId);
        var utcKind = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = zone is null ? utcKind : TimeZoneInfo.ConvertTimeFromUtc(utcKind, zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static bool IsRecurring(string? kind) =>
        string.Equals(kind?.Trim(), KindRecurring, StringComparison.OrdinalIgnoreCase);

    internal static bool IsOneOff(string? kind) =>
        string.Equals(kind?.Trim(), KindOneOff, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the kind is <see cref="KindWindow"/>, ignoring case and surrounding spaces.</summary>
    public static bool IsWindow(string? kind) =>
        string.Equals(kind?.Trim(), KindWindow, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the kind is <see cref="KindRandom"/>, ignoring case and surrounding spaces.</summary>
    public static bool IsRandom(string? kind) =>
        string.Equals(kind?.Trim(), KindRandom, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The planned fires of a random job for the windows opening on the local dates <paramref name="fromDate"/>
    /// through <paramref name="toDate"/>, each with the local date its window opened. Throws when the job is
    /// not a valid random job - the caller checks the kind first.
    /// </summary>
    public static IReadOnlyList<(DateOnly WindowDate, DateTime Utc)> RandomPlan(
        CronJobDto job, DateOnly fromDate, DateOnly toDate)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!IsRandom(job.ScheduleKind))
            throw new ArgumentException($"job {job.Id} is a {job.ScheduleKind} schedule, not random", nameof(job));
        var (settings, error) = RandomSchedule.Parse(job.CronExpression);
        if (settings is null)
            throw new ArgumentException($"job {job.Id} has invalid random settings: {error}", nameof(job));
        var zone = TryFindTimeZone(job.TimeZoneId)
            ?? throw new ArgumentException($"job {job.Id} has an unknown timeZoneId: {job.TimeZoneId}", nameof(job));

        var plan = new List<(DateOnly, DateTime)>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
            foreach (var utc in RandomSchedule.PlanUtc(job.Id, d, settings, zone))
                plan.Add((d, utc));
        return plan;
    }

    /// <summary>The job's zone, or null when the system does not know its id.</summary>
    public static TimeZoneInfo? FindZone(string? id) => TryFindTimeZone(id);

    /// <summary>
    /// Stamp a job's display fields as of <paramref name="nowUtc"/>, so a client shows them as given: every job gets
    /// its <see cref="CronJobDto.Lifecycle"/>, and a random job also gets <see cref="CronJobDto.ScheduleText"/> and
    /// <see cref="CronJobDto.RemainingToday"/>. A random job whose stored settings no longer validate gets only its
    /// lifecycle. Returns the same job.
    /// </summary>
    public static CronJobDto StampDisplay(CronJobDto job, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.Lifecycle = LifecycleOf(job);
        if (IsWindow(job.ScheduleKind) && Validate(job).Ok)
        {
            // The schedule it runs after is named by the list route, which holds every schedule; here it is its id.
            job.ScheduleText = WindowSchedule.Describe(WindowSchedule.Parse(job.CronExpression).Settings!);
            return job;
        }
        if (!IsRandom(job.ScheduleKind) || !Validate(job).Ok)
            return job;
        var plan = BuildPlan(job, nowUtc, days: 1);
        job.ScheduleText = plan.Description;
        // "Today" is the local calendar date of the fire, not the date its window opened: at 00:15 a fire at 00:30
        // from last night's window is still today, and one after tonight's midnight is not.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), TryFindTimeZone(job.TimeZoneId)!));
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        job.RemainingToday = plan.Fires.Where(f => f.Local.StartsWith(todayText, StringComparison.Ordinal))
            .Select(f => f.Local[11..]).ToList();
        return job;
    }

    /// <summary>
    /// Whether the job will still run (<see cref="CronLifecycle"/>). A job that is switched on is active - including
    /// a one-off whose time has passed but has not fired yet, because the engine still fires it. A switched-off
    /// one-off that has fired is spent: the engine switches a one-off off as it fires it. One switched off by hand
    /// before it ever fired is off. Any other switched-off job repeats, so it is paused rather than finished.
    /// </summary>
    public static string LifecycleOf(CronJobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Enabled)
            return CronLifecycle.Active;
        if (IsOneOff(job.ScheduleKind))
            return job.LastFiredUtc is null ? CronLifecycle.Off : CronLifecycle.Spent;
        return CronLifecycle.Paused;
    }

    /// <summary>The most windows <see cref="BuildPlan"/> will list.</summary>
    public const int MaxPlanDays = 14;

    /// <summary>
    /// The plan of a random job as the plan route returns it: every fire after <paramref name="nowUtc"/> in the
    /// windows opening yesterday (what remains of one crossing midnight), today and the next
    /// <paramref name="days"/> - 1 local dates. The caller checks the kind and the day count first.
    /// </summary>
    public static CronPlanDto BuildPlan(CronJobDto job, DateTime nowUtc, int days)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (days < 1 || days > MaxPlanDays)
            throw new ArgumentOutOfRangeException(nameof(days), days, $"days must be 1 to {MaxPlanDays}");
        var zone = TryFindTimeZone(job.TimeZoneId)
            ?? throw new ArgumentException($"job {job.Id} has an unknown timeZoneId: {job.TimeZoneId}", nameof(job));
        var (settings, error) = RandomSchedule.Parse(job.CronExpression);
        if (settings is null)
            throw new ArgumentException($"job {job.Id} has invalid random settings: {error}", nameof(job));

        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var plan = new CronPlanDto
        {
            Id = job.Id,
            TimeZoneId = job.TimeZoneId,
            Description = RandomSchedule.Describe(settings),
            Days = days,
            GeneratedUtc = now,
        };
        foreach (var (windowDate, utc) in RandomPlan(job, today.AddDays(-1), today.AddDays(days - 1)))
        {
            if (utc <= now)
                continue;
            plan.Fires.Add(new CronPlanFire
            {
                WindowDate = windowDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Utc = utc,
                Local = LocalRunLabel(job, utc),
            });
        }
        return plan;
    }

    /// <summary>Parse a standard 5-field cron expression, or null if it is not valid.</summary>
    private static CronExpression? TryParseCron(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;
        try
        {
            return CronExpression.Parse(expression, CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    /// <summary>Resolve an IANA/Windows time-zone id, or null if the system does not know it.</summary>
    private static TimeZoneInfo? TryFindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Parse a wall-clock timestamp (no offset assumed), or null if it does not parse.</summary>
    private static DateTime? TryParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
