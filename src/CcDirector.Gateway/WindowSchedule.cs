using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Contracts;
using Cronos;

namespace CcDirector.Gateway;

/// <summary>
/// The settings of a <c>window</c> schedule (the owner, 2026-10-09: "most jobs should stop naming a rigid time"):
/// run once a day at a minute the GATEWAY chooses inside a local window, spread by the machine's load, optionally
/// finishing by a deadline and starting a set time after another schedule.
///
/// Stored as text in the job's existing <c>CronExpression</c> column, like the random kind, so it needs no schema
/// change: <c>window=00:00-06:30 days=1-5 deadline=07:00 after=cj_abc gap=120 placed=03:40</c>. Every key but
/// <c>window</c> is optional, and <c>placed</c> is the Gateway's own: whatever a caller sends there is replaced
/// when the schedule is written (<see cref="WindowSchedule.Place"/>).
/// </summary>
public sealed record WindowScheduleSettings(
    int StartMinute,
    int WindowMinutes,
    string? Days,
    int? DeadlineMinute,
    string? AfterJobId,
    int? GapMinutes,
    int? PlacedMinute)
{
    /// <summary>The window's end as a local minute of the day (0-1439).</summary>
    public int EndMinute => (StartMinute + WindowMinutes) % WindowSchedule.MinutesPerDay;

    /// <summary>The settings written back as the stored text, in the canonical key order.</summary>
    public string ToText()
    {
        var text = new StringBuilder($"window={WindowSchedule.Hhmm(StartMinute)}-{WindowSchedule.Hhmm(EndMinute)}");
        if (Days is not null) text.Append(CultureInfo.InvariantCulture, $" days={Days}");
        if (DeadlineMinute is { } d) text.Append(CultureInfo.InvariantCulture, $" deadline={WindowSchedule.Hhmm(d)}");
        if (AfterJobId is not null) text.Append(CultureInfo.InvariantCulture, $" after={AfterJobId}");
        if (GapMinutes is { } g) text.Append(CultureInfo.InvariantCulture, $" gap={g}");
        if (PlacedMinute is { } p) text.Append(CultureInfo.InvariantCulture, $" placed={WindowSchedule.Hhmm(p)}");
        return text.ToString();
    }
}

/// <summary>
/// Parsing, describing and PLACING a <c>window</c> schedule.
///
/// The minute is chosen once, when the schedule is written, and then kept: the engine fires it daily at the placed
/// minute exactly like a fixed time, so "window 00:00-06:30, placed 03:40" stays true until the next write. A FIXED
/// schedule is never moved by any of this - only a window schedule's own placement changes, and only when it, or the
/// schedule it runs after, is written.
///
/// Placement tries every <see cref="StepMinutes"/> minutes from the window's opening to its latest start (the window's
/// end, or the deadline less the schedule's measured run length if that is earlier), keeps the minutes that satisfy
/// the after-constraint, and takes the one where the fewest other scheduled sessions on the machine are open while it
/// runs; then the fewest other starts within half an hour; then the one nearest the middle of the window.
/// </summary>
public static class WindowSchedule
{
    public const int MinutesPerDay = 1440;

    /// <summary>The spacing of the minutes placement considers.</summary>
    public const int StepMinutes = 5;

    /// <summary>The shortest window accepted: a window narrower than this is a fixed time and should say so.</summary>
    public const int MinWindowMinutes = 30;

    /// <summary>
    /// How far back the schedule a window runs after must have fired for the after-constraint to hold: the two belong
    /// to the same night or the same day, not to yesterday's run of the other one.
    /// </summary>
    public static readonly TimeSpan AfterReach = TimeSpan.FromHours(12);

    /// <summary>The largest gap accepted after another schedule - the reach, because a longer gap could never be met.</summary>
    public const int MaxGapMinutes = 12 * 60;

    private static readonly TimeSpan Crowd = TimeSpan.FromMinutes(30);

    private static readonly Regex RangePattern = new(@"^(\d{2}):(\d{2})-(\d{2}):(\d{2})$", RegexOptions.CultureInvariant);
    private static readonly Regex TimePattern = new(@"^(\d{2}):(\d{2})$", RegexOptions.CultureInvariant);
    private static readonly string[] Keys = { "window", "days", "deadline", "after", "gap", "placed" };

    /// <summary>Parse and validate the stored settings text, or say what is wrong with it in one sentence.</summary>
    public static (WindowScheduleSettings? Settings, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, "cronExpression must hold the window settings, e.g. 'window=00:00-06:30'");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1)
                return (null, $"window setting '{token}' is not key=value");
            var key = token[..eq];
            if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                return (null, $"unknown window setting '{key}'; the settings are {string.Join(", ", Keys)}");
            if (!values.TryAdd(key, token[(eq + 1)..]))
                return (null, $"window setting '{key}' is given twice");
        }

        if (!values.TryGetValue("window", out var windowText))
            return (null, "window is required for a window schedule, e.g. window=00:00-06:30");
        var range = RangePattern.Match(windowText);
        if (!range.Success || !TryMinute(range.Groups[1].Value, range.Groups[2].Value, out var start)
            || !TryMinute(range.Groups[3].Value, range.Groups[4].Value, out var end))
            return (null, $"window must be HH:mm-HH:mm with real times of day, not '{windowText}'");
        // An end at or before the start crosses midnight; an end equal to the start is a whole day.
        var windowMinutes = end > start ? end - start : end - start + MinutesPerDay;
        if (windowMinutes < MinWindowMinutes)
            return (null, $"window must be at least {MinWindowMinutes} minutes long, not {windowMinutes}; a narrower one is a fixed time");

        string? days = null;
        if (values.TryGetValue("days", out var daysText))
        {
            if (TryCron($"0 0 * * {daysText}") is null)
                return (null, $"days must be a cron day-of-week field, for example 1-5 or 0,6, not '{daysText}'");
            days = daysText;
        }

        int? deadline = null;
        if (values.TryGetValue("deadline", out var deadlineText))
        {
            if (!TryTime(deadlineText, out var d))
                return (null, $"deadline must be HH:mm, not '{deadlineText}'");
            deadline = d;
        }

        values.TryGetValue("after", out var after);
        int? gap = null;
        if (values.TryGetValue("gap", out var gapText))
        {
            if (after is null)
                return (null, "gap is the time after another schedule, so it needs after=<schedule id>");
            if (!int.TryParse(gapText, NumberStyles.None, CultureInfo.InvariantCulture, out var g) || g < 0 || g > MaxGapMinutes)
                return (null, $"gap must be a whole number of minutes from 0 to {MaxGapMinutes}, not '{gapText}'");
            gap = g;
        }

        int? placed = null;
        if (values.TryGetValue("placed", out var placedText))
        {
            if (!TryTime(placedText, out var p))
                return (null, $"placed must be HH:mm, not '{placedText}'");
            placed = p;
        }

        return (new WindowScheduleSettings(start, windowMinutes, days, deadline, after, gap, placed), null);
    }

    /// <summary>
    /// The schedule in words, for example "window 00:00-06:30, placed 03:40, done by 07:00, at least 2h 00m after
    /// Nightly backup". <paramref name="nameOf"/> names the schedule it runs after; its id is shown when it has none.
    /// </summary>
    public static string Describe(WindowScheduleSettings settings, Func<string, string?>? nameOf = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var text = new StringBuilder($"window {Hhmm(settings.StartMinute)}-{Hhmm(settings.EndMinute)}");
        text.Append(settings.PlacedMinute is { } p ? $", placed {Hhmm(p)}" : ", not placed yet");
        if (settings.Days is not null) text.Append(CultureInfo.InvariantCulture, $", days {settings.Days}");
        if (settings.DeadlineMinute is { } d) text.Append(CultureInfo.InvariantCulture, $", done by {Hhmm(d)}");
        if (settings.AfterJobId is { } after)
        {
            var name = nameOf?.Invoke(after) ?? after;
            text.Append(settings.GapMinutes is { } g
                ? $", at least {Running.CronRunEndingFold.Duration(TimeSpan.FromMinutes(g))} after {name}"
                : $", after {name} ends");
        }
        return text.ToString();
    }

    /// <summary>
    /// The next fire after <paramref name="fromUtc"/>: the placed minute in the next window that opens on an allowed
    /// day, or null when not placed. <c>days</c> is the day the WINDOW OPENS, as in placement, so a window from 22:00
    /// to 04:00 on weekdays placed at 01:00 fires early Tuesday to early Saturday, not Monday to Friday.
    /// </summary>
    public static DateTime? NextAfter(WindowScheduleSettings settings, TimeZoneInfo zone, DateTime fromUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.PlacedMinute is not { } placed)
            return null;
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(from, zone));
        // A placed minute before the opening minute is past midnight, in the window that opened the day before.
        var dayAfterOpening = placed < settings.StartMinute ? 1 : 0;
        for (var i = -1; i <= 8; i++)
        {
            var opening = today.AddDays(i);
            if (!OpensOn(settings, opening, zone))
                continue;
            var fire = Utc(opening.AddDays(dayAfterOpening), placed, zone);
            if (fire > from)
                return fire;
        }
        return null;
    }

    /// <summary>Whether the window opens on this local date, by its <c>days</c> field (every day when it has none).</summary>
    private static bool OpensOn(WindowScheduleSettings settings, DateOnly date, TimeZoneInfo zone)
    {
        if (settings.Days is null)
            return true;
        var days = TryCron($"0 0 * * {settings.Days}")!;
        var midnight = Utc(date, 0, zone);
        var next = days.GetNextOccurrence(midnight.AddTicks(-1), zone, inclusive: false);
        return next is not null && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(next.Value, zone)) == date;
    }

    /// <summary>
    /// Choose the minute for <paramref name="job"/> (a window schedule) against every other schedule in
    /// <paramref name="allJobs"/>, and return its settings with <c>placed</c> set - or the reason no minute in its
    /// window works. <paramref name="runLengths"/> are the measured run lengths (<see cref="CronLoad"/>).
    /// </summary>
    public static (WindowScheduleSettings? Placed, string? Error) Place(CronJobDto job, WindowScheduleSettings settings,
        IReadOnlyList<CronJobDto> allJobs, IReadOnlyDictionary<string, TimeSpan> runLengths, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(allJobs);
        ArgumentNullException.ThrowIfNull(runLengths);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var zone = CronSchedule.FindZone(job.TimeZoneId)
            ?? throw new ArgumentException($"job {job.Name} has an unknown timeZoneId: {job.TimeZoneId}", nameof(job));
        var length = LengthOf(job.Id, runLengths);

        CronJobDto? after = null;
        TimeSpan gap = TimeSpan.Zero;
        if (settings.AfterJobId is { } afterId)
        {
            after = allJobs.FirstOrDefault(j => j.Id == afterId);
            if (after is null)
                return (null, $"after={afterId}: there is no schedule with that id");
            if (after.Id == job.Id)
                return (null, "after= names this schedule itself");
            if (CronSchedule.LifecycleOf(after) != CronLifecycle.Active)
                return (null, $"after={afterId}: '{after.Name}' will not run (it is {CronSchedule.LifecycleOf(after)}), so there is nothing to run after");
            gap = settings.GapMinutes is { } g ? TimeSpan.FromMinutes(g) : LengthOf(after.Id, runLengths);
        }

        // The machine's other active schedules, held open for their own lengths. A window schedule among them counts
        // at the minute it is already placed at.
        var others = allJobs
            .Where(j => j.Id != job.Id
                && CronSchedule.LifecycleOf(j) == CronLifecycle.Active
                && string.Equals(j.Target.Machine, job.Target.Machine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var opening = NextOpening(settings, zone, now, length);
        if (opening is null)
            return (null, settings.DeadlineMinute is not null
                ? $"the window and the deadline leave no time to run a session of about {Running.CronRunEndingFold.Duration(length)}"
                : "the window never opens on the days given");
        var (open, latest) = opening.Value;

        var horizonFrom = open - TimeSpan.FromDays(1);
        var horizonTo = latest + length + TimeSpan.FromMinutes(1);
        var otherRuns = others
            .SelectMany(o =>
            {
                var l = LengthOf(o.Id, runLengths);
                return CronLoad.FiresBetween(o, horizonFrom - l, horizonTo, now).Select(s => (o.Id, Start: s, End: s + l));
            })
            .ToList();
        var afterFires = after is null
            ? new List<DateTime>()
            : CronLoad.FiresBetween(after, open - AfterReach, latest + TimeSpan.FromMinutes(1), now).ToList();

        var middle = open + (latest - open) / 2;
        (DateTime At, int Open, int Near, TimeSpan FromMiddle)? best = null;
        var rejectedByAfter = 0;
        for (var t = open; t <= latest; t = t.AddMinutes(StepMinutes))
        {
            if (t <= now)
                continue;
            if (after is not null)
            {
                var last = afterFires.Where(f => f <= t && f >= t - AfterReach).DefaultIfEmpty().Max();
                if (last == default || t - last < gap)
                {
                    rejectedByAfter++;
                    continue;
                }
            }
            var end = t + length;
            var overlapping = otherRuns.Where(r => r.Start < end && r.End > t).Select(r => (r.Id, r.Start, r.End)).ToList();
            var candidate = (At: t,
                Open: CronLoad.PeakOpen(overlapping, t, end),
                Near: otherRuns.Count(r => (r.Start - t).Duration() <= Crowd),
                FromMiddle: (t - middle).Duration());
            if (best is null || Better(candidate, best.Value))
                best = candidate;
        }

        if (best is null)
            return (null, after is not null && rejectedByAfter > 0
                ? $"no minute in the window is at least {Running.CronRunEndingFold.Duration(gap)} after '{after.Name}' fires; widen the window or move it later"
                : "no minute in the window is still to come");

        var local = TimeZoneInfo.ConvertTimeFromUtc(best.Value.At, zone);
        return (settings with { PlacedMinute = local.Hour * 60 + local.Minute }, null);
    }

    private static bool Better((DateTime At, int Open, int Near, TimeSpan FromMiddle) a,
        (DateTime At, int Open, int Near, TimeSpan FromMiddle) b)
    {
        if (a.Open != b.Open) return a.Open < b.Open;
        if (a.Near != b.Near) return a.Near < b.Near;
        if (a.FromMiddle != b.FromMiddle) return a.FromMiddle < b.FromMiddle;
        return a.At < b.At;
    }

    /// <summary>
    /// The next window - opening and latest start, in UTC - whose latest start is still to come, on an allowed day.
    /// The latest start is the window's end, or the deadline less the run length when that is earlier.
    /// </summary>
    private static (DateTime Open, DateTime Latest)? NextOpening(WindowScheduleSettings settings, TimeZoneInfo zone,
        DateTime now, TimeSpan length)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        for (var i = -1; i <= 8; i++)
        {
            var date = today.AddDays(i);
            if (!OpensOn(settings, date, zone))
                continue;
            var open = Utc(date, settings.StartMinute, zone);
            // The end by the WALL CLOCK, not by elapsed minutes: on the night the clock skips an hour, 00:00-06:30 still
            // ends at 06:30, so no minute after it can be chosen.
            // A window that reaches midnight or past it - 18:00-00:00 included - ends on the next date.
            var crosses = settings.StartMinute + settings.WindowMinutes >= MinutesPerDay;
            var latest = Utc(crosses ? date.AddDays(1) : date, settings.EndMinute, zone);
            if (settings.DeadlineMinute is { } deadlineMinute)
            {
                // The first time the deadline's clock time comes round after the window opens.
                var deadline = Utc(date, deadlineMinute, zone);
                if (deadline <= open) deadline = Utc(date.AddDays(1), deadlineMinute, zone);
                var lastStart = deadline - length;
                if (lastStart < latest) latest = lastStart;
            }
            if (latest < open)
                return null;
            if (latest > now)
                return (open, latest);
        }
        return null;
    }

    private static DateTime Utc(DateOnly date, int minute, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(minute / 60, minute % 60), DateTimeKind.Unspecified);
        // A local time skipped by a spring-forward change does not exist; the minute after the gap is what the clock shows.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static TimeSpan LengthOf(string jobId, IReadOnlyDictionary<string, TimeSpan> runLengths)
    {
        var length = runLengths.TryGetValue(jobId, out var measured) ? measured : CronLoad.UnmeasuredRunLength;
        return length < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : length;
    }

    internal static string Hhmm(int minute) =>
        $"{minute / 60:00}:{minute % 60:00}";

    private static bool TryTime(string text, out int minute)
    {
        minute = 0;
        var m = TimePattern.Match(text);
        return m.Success && TryMinute(m.Groups[1].Value, m.Groups[2].Value, out minute);
    }

    private static bool TryMinute(string hours, string minutes, out int minute)
    {
        var h = int.Parse(hours, CultureInfo.InvariantCulture);
        var m = int.Parse(minutes, CultureInfo.InvariantCulture);
        minute = h * 60 + m;
        return h <= 23 && m <= 59;
    }

    private static CronExpression? TryCron(string expression)
    {
        try
        {
            return CronExpression.Parse(expression, CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
