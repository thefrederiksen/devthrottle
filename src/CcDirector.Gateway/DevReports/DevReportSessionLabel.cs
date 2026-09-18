using System.Globalization;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// What the Gateway knows about a session for the purpose of NAMING it to the owner: its three-digit session
/// number and its display name, either of which may be unknown. Deliberately carries no session id, so nothing
/// downstream can accidentally put one on a screen.
/// </summary>
internal sealed record DevReportSessionNaming(int? Number, string? Name);

/// <summary>
/// What a report calls the session it came from, and the way back to it - folded ONCE, here, on the Gateway
/// (repository rule 7: the client is dumb). Every report record carries the two finished strings and every
/// client renders them verbatim, so the Cockpit, the phone and the Director all say the same words.
///
/// THE RULE THAT SHAPES ALL OF THIS: no internal identifier anywhere the owner can see. This fold is handed a
/// number and a name - never a session id - so it CANNOT emit one, and when it knows neither it says a plain
/// true sentence rather than a ten-character hexadecimal string. What the caller cannot supply, the owner does
/// not get shown.
///
/// The number is normally three digits because the session-number allocator's band is 100-999. It is rendered
/// as the Gateway holds it: a number outside that band is printed as it is, never zero-padded into a shape the
/// allocator never issued.
///
/// The name is tidied the way <see cref="DevReportTitle"/> tidies a title - whitespace collapsed to one line,
/// cut at <see cref="DevReportTitle.MaxLength"/> - because both are page furniture that must sit on one row.
/// </summary>
internal static class DevReportSessionLabel
{
    /// <summary>What the report says when the Gateway knows neither the session's number nor its name.</summary>
    public const string UnknownSession = "the session";

    private const string BackPrefix = "back to ";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The session's name for a human: "&lt;number&gt; &lt;name&gt;" when both are known, the number alone when
    /// the name is not, the name alone when the number is not, and <see cref="UnknownSession"/> when neither is.
    /// </summary>
    public static string Session(int? number, string? name)
    {
        var cleaned = Clean(name);
        var digits = number is { } n ? n.ToString(CultureInfo.InvariantCulture) : "";

        if (digits.Length > 0 && cleaned.Length > 0) return digits + " " + cleaned;
        if (digits.Length > 0) return digits;
        if (cleaned.Length > 0) return cleaned;
        return UnknownSession;
    }

    /// <summary>The way back, in the same words: "back to " plus <see cref="Session"/>.</summary>
    public static string Back(int? number, string? name) => BackPrefix + Session(number, name);

    private static string Clean(string? name)
    {
        var text = Whitespace.Replace(name ?? "", " ").Trim();
        return text.Length <= DevReportTitle.MaxLength ? text : text[..DevReportTitle.MaxLength];
    }
}
