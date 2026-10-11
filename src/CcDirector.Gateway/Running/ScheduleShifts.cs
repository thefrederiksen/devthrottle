using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// WHICH SHIFTS A SCHEDULE WORKS IN, AND WHETHER ITS RUNS FIT THEM (Factory Control, step 3). Folded here once and
/// stamped onto the schedule and the factory's Seats tab; the screens render the words as given.
///
/// The shifts themselves are <see cref="WorkShifts"/> - this class never counts hours. They are counted in the
/// account's display time zone, the owner's default; nothing about shifts is stored, per factory or per schedule.
///
/// THE WARNING RULE (stated again on screen in <see cref="RuleText"/>): a run belongs to the shift it starts in, and a
/// run still going when that shift ends is a problem. So a schedule is warned about when, over the next
/// <see cref="Lookahead"/>, any of its runs would end after its shift ends:
///   - with history: the run starts at its fire and lasts the schedule's TYPICAL length - the middle length of its
///     recent finished runs (<see cref="CronRunRecordReader.RunLengthsOf"/>) - and it is late when that end is after
///     the shift's end. Real time, not wall-clock time: a night shift on a daylight-saving day is seven or nine hours.
///   - with no history (no finished run to measure): it is late when it starts inside the last
///     <see cref="NoHistoryMargin"/> of its shift.
/// </summary>
public static class ScheduleShifts
{
    /// <summary>How far ahead a schedule's runs are read to find its shifts and to check them.</summary>
    public static readonly TimeSpan Lookahead = TimeSpan.FromDays(7);

    /// <summary>With no finished run to measure, a run starting this close to its shift's end is warned about.</summary>
    public static readonly TimeSpan NoHistoryMargin = TimeSpan.FromMinutes(30);

    /// <summary>The rule, in the words the schedule screen shows under a warning.</summary>
    public const string RuleText =
        "The rule: a run belongs to the shift it starts in, and a run still going when that shift ends is a problem. "
        + "This warns when the schedule's typical run - the middle length of its recent finished runs - would not end "
        + "before its shift ends. A schedule with no finished run yet is warned about when it starts in the last "
        + "30 minutes of a shift.";

    /// <summary>The shifts in the order a day has them.</summary>
    public static readonly IReadOnlyList<string> Order = new[] { WorkShifts.Night, WorkShifts.Morning, WorkShifts.Evening };

    /// <summary>A shift's heading, for example "Night shift, 00:00-08:00".</summary>
    public static string Heading(string shift) => shift switch
    {
        WorkShifts.Night => "Night shift, 00:00-08:00",
        WorkShifts.Morning => "Morning shift, 08:00-16:00",
        WorkShifts.Evening => "Evening shift, 16:00-24:00",
        _ => throw new ArgumentOutOfRangeException(nameof(shift), shift, "not a shift"),
    };

    /// <summary>The schedule's runs in the next <see cref="Lookahead"/>, from <paramref name="nowUtc"/>.</summary>
    public static IReadOnlyList<DateTime> NextFires(CronJobDto job, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        return CronLoad.FiresBetween(job, nowUtc, nowUtc + Lookahead, nowUtc).ToList();
    }

    /// <summary>The shifts these runs start in, each once, in the order a day has them.</summary>
    public static IReadOnlyList<string> ShiftsOf(IEnumerable<DateTime> fires, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(fires);
        var names = fires.Select(f => WorkShifts.ShiftOf(f, zone).Name).ToHashSet(StringComparer.Ordinal);
        return Order.Where(names.Contains).ToList();
    }

    /// <summary>
    /// The shifts in words: "Morning shift", "Night and evening shifts", "Night, morning and evening shifts", or
    /// "No run in the next 7 days".
    /// </summary>
    public static string Text(IReadOnlyList<string> shifts)
    {
        ArgumentNullException.ThrowIfNull(shifts);
        return shifts.Count switch
        {
            0 => "No run in the next 7 days",
            1 => $"{Capital(shifts[0])} shift",
            2 => $"{Capital(shifts[0])} and {shifts[1]} shifts",
            _ => $"{Capital(string.Join(", ", shifts.Take(shifts.Count - 1)))} and {shifts[^1]} shifts",
        };
    }

    /// <summary>
    /// The warning for these runs, or null when every one ends inside its shift (see the class comment for the rule).
    /// <paramref name="typical"/> is the schedule's typical run length, or null when it has no finished run.
    /// </summary>
    public static string? WarningFor(IReadOnlyList<DateTime> fires, TimeZoneInfo zone, TimeSpan? typical)
    {
        ArgumentNullException.ThrowIfNull(fires);
        ArgumentNullException.ThrowIfNull(zone);
        var late = fires
            .Select(f => (Fire: f, Shift: WorkShifts.ShiftOf(f, zone)))
            .Where(r => typical is { } t ? r.Fire + t > r.Shift.EndUtc : r.Shift.EndUtc - r.Fire <= NoHistoryMargin)
            .ToList();
        if (late.Count == 0)
            return null;

        var (fire, shift) = late[0];
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(fire, DateTimeKind.Utc), zone);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(shift.EndUtc, DateTimeKind.Utc), zone);
        var when = startLocal.ToString("ddd d MMM 'at' HH:mm", CultureInfo.InvariantCulture);
        var end = endLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        var count = late.Count == 1
            ? $"1 of its {fires.Count} runs in the next 7 days does this."
            : $"{late.Count} of its {fires.Count} runs in the next 7 days do this.";
        var text = typical is { } length
            ? $"Ends after its shift: the run on {when} starts in the {shift.Name} shift and usually takes about "
              + $"{CronRunEndingFold.Duration(length)}, so it would still be going when the shift ends at {end}. {count}"
            : $"Starts close to the end of its shift: the run on {when} starts in the last 30 minutes of the {shift.Name} "
              + $"shift, which ends at {end}, and this schedule has no finished run to measure yet. {count}";
        FileLog.Write($"[ScheduleShifts] WarningFor: late={late.Count}, runs={fires.Count}, typical={(typical?.ToString() ?? "none")}");
        return text;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
