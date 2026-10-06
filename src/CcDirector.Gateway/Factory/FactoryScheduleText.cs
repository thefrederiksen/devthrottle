using System.Globalization;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// When a seat runs, in the owner's words (Factories screen mission, phase B): "Daily 06:15", "06:00 and 18:00",
/// "Wednesday 05:30", "Weekdays 07:00". Folded here once so the Cockpit and the phone never read a cron expression.
///
/// The shapes a factory's schedules actually use are named. Any other expression is shown AS IT IS
/// ("Cron 0 */2 1 * *") rather than guessed at: a wrong sentence about when an agent runs is worse than an exact
/// one in a less friendly form.
/// </summary>
public static class FactoryScheduleText
{
    private static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    /// <summary>
    /// One schedule in words. A schedule in another time zone than the account's says which; a paused one says so.
    /// </summary>
    public static string Describe(CronJobDto job, TimeZoneInfo accountZone)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(accountZone);
        string text;
        if (string.Equals(job.ScheduleKind, CronSchedule.KindOneOff, StringComparison.Ordinal))
            text = string.IsNullOrWhiteSpace(job.RunAt) ? "Once" : $"Once, {OneOffText(job.RunAt)}";
        else
            text = Cron(job.CronExpression);

        if (!string.IsNullOrWhiteSpace(job.TimeZoneId) && !SameZone(job.TimeZoneId, accountZone))
            text += $" ({job.TimeZoneId})";
        if (!job.Enabled)
            text += " (paused)";
        return text;
    }

    /// <summary>A five-field cron expression in words, or the expression itself when it is not a named shape.</summary>
    public static string Cron(string? expression)
    {
        var raw = (expression ?? "").Trim();
        var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return Exact(raw);
        var (minute, hour, dom, month, dow) = (f[0], f[1], f[2], f[3], f[4]);
        if (dom != "*" || month != "*") return Exact(raw);

        // Every N minutes, hourly, every N hours.
        if (hour == "*" && dow == "*")
        {
            if (minute.StartsWith("*/", StringComparison.Ordinal) && Int(minute[2..]) is { } every and > 0)
                return every == 1 ? "Every minute" : $"Every {every} minutes";
            if (Int(minute) is { } m0 and >= 0 and < 60) return $"Hourly at :{m0:00}";
            return Exact(raw);
        }
        if (hour.StartsWith("*/", StringComparison.Ordinal) && dow == "*" && Int(minute) is { } m1 and >= 0 and < 60
            && Int(hour[2..]) is { } hours and > 0)
            return hours == 1 ? $"Hourly at :{m1:00}" : $"Every {hours} hours at :{m1:00}";

        // Fixed times of day, on every day or on named days.
        if (Int(minute) is not { } m || m < 0 || m > 59) return Exact(raw);
        var hourList = List(hour);
        if (hourList is null || hourList.Count == 0 || hourList.Any(h => h < 0 || h > 23)) return Exact(raw);
        var times = Join(hourList.OrderBy(h => h).Select(h => $"{h:00}:{m:00}").ToList());

        if (dow == "*") return hourList.Count == 1 ? $"Daily {times}" : times;
        var days = Days(dow);
        return days is null ? Exact(raw) : $"{days} {times}";
    }

    // The days part: "Wednesday", "Monday and Thursday", "Weekdays", "Weekends". Null when it is not a named shape.
    private static string? Days(string dow)
    {
        if (dow is "1-5" or "MON-FRI" or "mon-fri") return "Weekdays";
        var list = List(dow, allowNames: true);
        if (list is null || list.Count == 0 || list.Any(d => d < 0 || d > 7)) return null;
        var set = list.Select(d => d % 7).Distinct().OrderBy(d => d).ToList();
        if (set.SequenceEqual(new[] { 0, 6 })) return "Weekends";
        if (set.SequenceEqual(new[] { 1, 2, 3, 4, 5 })) return "Weekdays";
        if (set.Count == 7) return "Daily";
        // Monday first, the way a week is read.
        return Join(set.OrderBy(d => d == 0 ? 7 : d).Select(d => DayNames[d]).ToList());
    }

    private static List<int>? List(string field, bool allowNames = false)
    {
        var result = new List<int>();
        foreach (var part in field.Split(','))
        {
            if (Int(part) is { } n) { result.Add(n); continue; }
            if (allowNames && DayIndex(part) is { } d) { result.Add(d); continue; }
            return null;
        }
        return result;
    }

    private static int? DayIndex(string name)
    {
        var i = Array.FindIndex(DayNames, d => d[..3].Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : i;
    }

    private static int? Int(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string Exact(string raw) => raw.Length == 0 ? "No schedule expression" : $"Cron {raw}";

    // A one-off's wall-clock time in its own zone, as stored ("2026-10-08T05:30" -> "8 Oct 05:30").
    private static string OneOffText(string runAt) =>
        DateTime.TryParse(runAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)
            : runAt;

    private static bool SameZone(string id, TimeZoneInfo zone)
    {
        if (string.Equals(id, zone.Id, StringComparison.OrdinalIgnoreCase)) return true;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var other) && other.HasSameRules(zone);
    }
}
