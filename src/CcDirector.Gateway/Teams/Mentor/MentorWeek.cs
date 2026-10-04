using System.Globalization;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>
/// One ISO week - Monday to Sunday - as the Mentor's page names it, <c>YYYY-Www</c> (devthrottle_internal#2305). The
/// week is a calendar week in the TEAM's own time zone: <see cref="UtcBounds"/> turns it into the instant it began
/// and the instant it ended there.
/// </summary>
public readonly record struct MentorWeek(int Year, int Week)
{
    private static readonly Regex Shape = new(@"^(\d{4})-W(\d{2})$", RegexOptions.CultureInvariant);

    /// <summary>The week's Monday.</summary>
    public DateOnly Start => DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday));

    /// <summary>The week's Sunday.</summary>
    public DateOnly End => Start.AddDays(6);

    /// <summary>The week before this one.</summary>
    public MentorWeek Previous => Of(Start.AddDays(-7));

    /// <summary><c>YYYY-Www</c>, for example <c>2026-W40</c>.</summary>
    public override string ToString() => $"{Year:D4}-W{Week:D2}";

    /// <summary>The week a calendar day falls in.</summary>
    public static MentorWeek Of(DateOnly day)
    {
        var dt = day.ToDateTime(TimeOnly.MinValue);
        return new MentorWeek(ISOWeek.GetYear(dt), ISOWeek.GetWeekOfYear(dt));
    }

    /// <summary>
    /// Parse <c>YYYY-Www</c>. Null for anything else, including a week number the year does not have (week 53 of a
    /// 52-week year).
    /// </summary>
    public static MentorWeek? TryParse(string? value)
    {
        if (value is null) return null;
        var m = Shape.Match(value.Trim());
        if (!m.Success) return null;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var week = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (year < 2000 || year > 9998 || week < 1 || week > ISOWeek.GetWeeksInYear(year)) return null;
        return new MentorWeek(year, week);
    }

    /// <summary>
    /// The instant the week began (its Monday 00:00 in <paramref name="zone"/>) and the instant it ended (the next
    /// Monday 00:00 there), both UTC. The end is exclusive. Correct across a change of clocks, because each edge is
    /// converted on its own.
    /// </summary>
    public (DateTime FromUtc, DateTime ToUtc) UtcBounds(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (LocalMidnightToUtc(Start, zone), LocalMidnightToUtc(Start.AddDays(7), zone));
    }

    /// <summary>
    /// The most recent week that has closed at <paramref name="nowUtc"/> in <paramref name="zone"/>: the week before
    /// the one the team's clock is in now.
    /// </summary>
    public static MentorWeek LastClosed(DateTime nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        return Of(DateOnly.FromDateTime(local)).Previous;
    }

    private static DateTime LocalMidnightToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        // A zone whose clocks jump at midnight has no local 00:00 that day; the week then begins at the first
        // instant that exists, one hour on.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
