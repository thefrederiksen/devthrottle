using System.Globalization;

namespace CcDirector.Gateway.Running;

/// <summary>One shift: its name, and when it starts and ends (UTC).</summary>
/// <param name="Name"><c>night</c>, <c>morning</c> or <c>evening</c>.</param>
/// <param name="StartUtc">The first instant of the shift.</param>
/// <param name="EndUtc">The first instant AFTER the shift - the moment a run of it still going is past its shift.</param>
/// <param name="Text">The shift in words in the account's time zone, for example "night shift, Sat 11 Oct, 00:00-08:00".</param>
public sealed record WorkShift(string Name, DateTime StartUtc, DateTime EndUtc, string Text);

/// <summary>
/// THE SHIFTS (Factory Control, the owner's ruling of 10 October 2026): every factory works in three shifts - night
/// 00:00-08:00, morning 08:00-16:00, evening 16:00-24:00 - counted in the account's display time zone. A run belongs
/// to the shift it STARTED in, and a run still going when that shift ends is a problem.
///
/// This is the one place the shifts are worked out. Every later reader - the factory page, the 08:00 email, the
/// schedule screen's warning - asks here rather than counting hours itself.
///
/// DAYLIGHT SAVING. The boundaries are wall-clock times, so a shift that spans a change is an hour shorter or longer
/// in real time (the night of a spring-forward day in Toronto is seven hours, of a fall-back day nine). A boundary
/// that falls inside a spring-forward gap - not in Toronto, whose clocks change at 02:00, but in a zone that changes
/// at midnight - is the first instant after the gap, which is when that wall-clock time is first reached. A boundary
/// that falls in a fall-back overlap is the later of its two instants (the standard-time one).
/// </summary>
public static class WorkShifts
{
    /// <summary>The night shift, 00:00-08:00.</summary>
    public const string Night = "night";

    /// <summary>The morning shift, 08:00-16:00.</summary>
    public const string Morning = "morning";

    /// <summary>The evening shift, 16:00-24:00.</summary>
    public const string Evening = "evening";

    /// <summary>How long each shift is on the wall clock.</summary>
    public const int ShiftHours = 8;

    /// <summary>The shift that holds <paramref name="utc"/> in <paramref name="zone"/>.</summary>
    public static WorkShift ShiftOf(DateTime utc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        var startHour = local.Hour / ShiftHours * ShiftHours;
        var name = startHour switch
        {
            0 => Night,
            8 => Morning,
            _ => Evening,
        };
        var startLocal = local.Date.AddHours(startHour);
        var endLocal = startLocal.AddHours(ShiftHours);
        var text = string.Format(CultureInfo.InvariantCulture, "{0} shift, {1}, {2:00}:00-{3:00}:00",
            name, local.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture), startHour, startHour + ShiftHours);
        return new WorkShift(name, ToUtc(startLocal, zone), ToUtc(endLocal, zone), text);
    }

    /// <summary>The end of the shift that holds <paramref name="utc"/>: the first instant after it.</summary>
    public static DateTime EndOfShift(DateTime utc, TimeZoneInfo zone) => ShiftOf(utc, zone).EndUtc;

    // A wall-clock boundary to its instant. In a spring-forward gap the wall-clock time never happens, so the boundary
    // is the first instant after the gap; in a fall-back overlap ConvertTimeToUtc takes the standard-time instant.
    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall))
            wall = wall.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }
}
