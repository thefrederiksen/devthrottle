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
        if (job.Target is null || string.IsNullOrWhiteSpace(job.Target.DirectorId))
            return (false, "target.directorId is required");
        if (job.Action is null || string.IsNullOrWhiteSpace(job.Action.RepoPath))
            return (false, "action.repoPath is required");
        if (job.Action is null || string.IsNullOrWhiteSpace(job.Action.Seed))
            return (false, "action.seed is required");

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

        return (false, $"scheduleKind must be '{KindRecurring}' or '{KindOneOff}'");
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

        // One-off: the RunAt wall-clock time in the job's zone, converted to UTC.
        var local = TryParseLocal(job.RunAt);
        if (local is null)
            return null;
        var unspecified = DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static bool IsRecurring(string? kind) =>
        string.Equals(kind?.Trim(), KindRecurring, StringComparison.OrdinalIgnoreCase);

    private static bool IsOneOff(string? kind) =>
        string.Equals(kind?.Trim(), KindOneOff, StringComparison.OrdinalIgnoreCase);

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
