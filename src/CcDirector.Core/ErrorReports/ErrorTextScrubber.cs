using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// Makes error text safe to leave the machine (issue #3311). Used on BOTH sides: the Director and the
/// launcher scrub before sending, and the Gateway scrubs again on receipt, so a client that forgot - or an
/// older client with a weaker rule - still cannot put a user name or a credential into the store.
///
/// What it removes:
///   - a home folder in a path names the person, so <c>/Users/robert/...</c>, <c>/home/robert/...</c> and
///     <c>C:\Users\robert\...</c> become <c>~/...</c>. The rest of the path stays: it is what makes the report
///     useful.
///   - anything shaped like a credential: a bearer header value, a <c>token=</c> / <c>key=</c> /
///     <c>secret=</c> / <c>password=</c> value, and a long unbroken run of letters and digits (an API key,
///     a device key). A GUID is NOT removed - it has hyphens, and session and Director ids are what make an
///     error traceable.
///   - control characters other than newline and tab.
/// and it caps the length.
/// </summary>
public static class ErrorTextScrubber
{
    private static readonly Regex MacHome = new(@"/Users/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex LinuxHome = new(@"/home/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsHome = new(@"[A-Za-z]:\\Users\\[^\\\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[^\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex NamedSecret = new(
        @"(?i)\b(token|key|apikey|api_key|secret|password|pwd|authorization)(\s*[=:]\s*)[^\s""'&,;]+",
        RegexOptions.CultureInvariant);
    // 32 or more letters, digits, underscores with no hyphen: the shape of an API key or device key.
    // Deliberately excludes the hyphen so a GUID (8-4-4-4-12) survives.
    private static readonly Regex LongRun = new(@"\b[A-Za-z0-9_]{32,}\b", RegexOptions.CultureInvariant);

    public const string Redacted = "<redacted>";

    /// <summary>Scrub, drop control characters other than newline and tab, trim, and cap at <paramref name="max"/>.</summary>
    public static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var scrubbed = Scrub(value);
        var sb = new StringBuilder(Math.Min(scrubbed.Length, max));
        foreach (var c in scrubbed)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(c) && c != '\n' && c != '\t') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Home folders to "~" and credential-shaped values to <see cref="Redacted"/>. No length cap.</summary>
    public static string Scrub(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = MacHome.Replace(value, "~");
        s = LinuxHome.Replace(s, "~");
        s = WindowsHome.Replace(s, "~");
        s = Bearer.Replace(s, "Bearer " + Redacted);
        s = NamedSecret.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        s = LongRun.Replace(s, Redacted);
        return s;
    }
}
