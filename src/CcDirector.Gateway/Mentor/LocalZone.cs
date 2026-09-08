using System.Globalization;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// One account's time zone, with the conversions the reference performs through Python's <c>zoneinfo</c>.
///
/// <see cref="ToUtc"/> reproduces <c>datetime(..., tzinfo=zone)</c> with <c>fold=0</c>: a wall time that
/// happens twice (the autumn overlap) is its FIRST occurrence, the one with the larger offset; a wall time
/// that never happens (the spring gap) takes the offset in force before the transition. .NET would throw on
/// the second and pick the later occurrence for the first, and either would move a week boundary.
/// </summary>
public sealed class LocalZone
{
    public TimeZoneInfo Zone { get; }

    /// <summary>The IANA name the reference prints (<c>time zone: America/Toronto</c>).</summary>
    public string Name { get; }

    public LocalZone(TimeZoneInfo zone, string name)
    {
        Zone = zone;
        Name = name;
    }

    /// <summary>Resolve an IANA id. A name the system does not know is refused naming it.</summary>
    public static LocalZone FromIana(string ianaId)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException error)
        {
            throw new MentorDataException("Time zone '" + ianaId + "' is not known to this system: " + error.Message
                + ". The mentor needs the IANA id the account chose; install the ICU time zone data or set the account's zone.");
        }
        return new LocalZone(zone, ianaId);
    }

    public DateTime ToLocal(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            throw new MentorDataException("ToLocal needs a UTC instant; got a DateTime of kind " + utc.Kind + ".");
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, Zone), DateTimeKind.Unspecified);
    }

    /// <summary>The reference's minute stamp: <c>when.astimezone(zone).strftime("%Y-%m-%d %H:%M")</c>.</summary>
    public string Stamp(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The reference's local day: <c>moment.strftime("%Y-%m-%d")</c>.</summary>
    public string Day(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A wall-clock time in this zone as a UTC instant, with Python's fold=0 rule for the overlap and the gap.</summary>
    public DateTime ToUtc(DateTime wall)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (Zone.IsAmbiguousTime(wall))
            offset = Zone.GetAmbiguousTimeOffsets(wall).Max();
        else if (Zone.IsInvalidTime(wall))
            offset = Zone.GetUtcOffset(wall.AddHours(-3));
        else
            offset = Zone.GetUtcOffset(wall);
        return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
    }
}
