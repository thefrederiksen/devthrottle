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
///     <c>secret=</c> / <c>password=</c> value, and a bare random key - a run of 32 or more URL-safe base64
///     characters (<c>[A-Za-z0-9_-]</c>) that mixes upper case, lower case and digits. That is the shape of
///     every key this product mints (<see cref="Security.GatewaySessionKey"/>: 43 characters, most with a
///     hyphen in them). A GUID or a hyphenated folder or branch name does not mix all three, so session ids,
///     Director ids and paths survive - they are what make an error traceable.
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
    // A run of 32 or more URL-safe base64 characters, not touching another one. Whether it is a key is
    // decided by LooksRandom, not by the pattern, so a GUID or a long lower-case path segment is kept.
    private static readonly Regex KeyRun = new(@"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant);

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
        s = KeyRun.Replace(s, m => LooksRandom(m.Value) ? Redacted : m.Value);
        return s;
    }

    /// <summary>Upper case, lower case and a digit all present, and not a GUID.</summary>
    internal static bool LooksRandom(string run)
    {
        if (Guid.TryParseExact(run, "D", out _)) return false;
        bool upper = false, lower = false, digit = false;
        foreach (var c in run)
        {
            if (c is >= 'A' and <= 'Z') upper = true;
            else if (c is >= 'a' and <= 'z') lower = true;
            else if (c is >= '0' and <= '9') digit = true;
        }
        return upper && lower && digit;
    }
}
