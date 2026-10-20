using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One desktop a Director hosts, as the Director pushes it up the tunnel (the Factory Boxes mission, phase 1,
/// issue #4258). It carries the box id and the name a person reads, and NOTHING ELSE: the local port the desktop
/// listens on never leaves the Director. The Gateway locates a box by its id and asks the hosting Director to relay
/// it; only that Director turns the id into a port, through its own lookup, so a port can never be named from the
/// Gateway side at all.
/// </summary>
public sealed class HostedDesktopDto
{
    /// <summary>The box id: lowercase letters, digits and hyphens (see <see cref="BoxIdRule"/>). Unique within an account.</summary>
    public string BoxId { get; set; } = "";

    /// <summary>The name a person reads in the Cockpit, for example "Soren's demo desktop".</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// What a box id may be, decided ONCE and read by the Director when it loads its list, by the Director when it is
/// asked to resolve one, and by the Gateway when a list is pushed to it - so the three can never disagree.
///
/// A box id is 1 to 63 characters of lowercase letters, digits and hyphens, starting and ending with a letter or a
/// digit. The rule is deliberately narrow: it can never hold a dot, a colon or a slash, so no box id can ever be
/// read as a host name, an address, a port or a path.
/// </summary>
public static class BoxIdRule
{
    /// <summary>The longest box id allowed, the length of one DNS label, so a box id can later be a host name too.</summary>
    public const int MaxLength = 63;

    /// <summary>The longest display name allowed.</summary>
    public const int MaxDisplayNameLength = 100;

    private static readonly Regex Shape = new("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="boxId"/> is a valid box id.</summary>
    public static bool IsValid(string? boxId)
        => !string.IsNullOrEmpty(boxId) && boxId.Length <= MaxLength && Shape.IsMatch(boxId);

    /// <summary>
    /// Why <paramref name="boxId"/> is not a valid box id, in a sentence a person can act on, or null when it is
    /// valid. A value that looks like a host or a port is named as such, because that is the one mistake the rule
    /// exists to refuse.
    /// </summary>
    public static string? WhyInvalid(string? boxId)
    {
        if (string.IsNullOrEmpty(boxId))
            return "the box id is empty";
        if (boxId.Length > MaxLength)
            return $"the box id is longer than {MaxLength} characters";
        if (boxId.Contains(':') || boxId.Contains('.') || boxId.Contains('/') || boxId.All(char.IsAsciiDigit))
            return "the box id looks like a host, an address, a port or a path, and only a box id may be named";
        if (!Shape.IsMatch(boxId))
            return "a box id is lowercase letters, digits and hyphens, and starts and ends with a letter or a digit";
        return null;
    }

    /// <summary>
    /// A value that may have come from anyone, made safe to write into one log line: control characters removed and
    /// cut to a readable length. Used for a box id that was REFUSED, which is by definition not to be trusted.
    /// </summary>
    public static string ForLog(string? value)
    {
        if (value is null) return "(none)";
        var clean = new string(value.Where(c => !char.IsControl(c)).Take(80).ToArray());
        return value.Length > 80 ? clean + "..." : clean;
    }
}
