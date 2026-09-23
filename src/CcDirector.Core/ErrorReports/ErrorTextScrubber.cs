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
///     characters (<c>[A-Za-z0-9_-]</c>) with an upper-case letter and a lower-case letter or a digit. That is the shape of
///     every key this product mints (<see cref="Security.GatewaySessionKey"/>: 43 characters, most with a
///     hyphen in them). A GUID, a git hash and a lower-case folder or branch name have no upper-case letter,
///     so session ids, Director ids and paths survive - they are what make an error traceable. A long method
///     name in a stack frame survives too.
///   - control characters other than newline and tab.
/// and it caps the length.
/// </summary>
public static class ErrorTextScrubber
{
    private static readonly Regex MacHome = new(@"/Users/[^/\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex LinuxHome = new(@"/home/[^/\s""']+", RegexOptions.CultureInvariant);
    // One or two backslashes between the parts: a path quoted inside JSON or an escaped string doubles them
    // ("C:\\Users\\robert"), and that shape reached the store untouched before.
    private static readonly Regex WindowsHome = new(@"[A-Za-z]:\\{1,2}Users\\{1,2}[^\\\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // A network path to a home folder names the machine AND the person: \\MACHINE\Users\robert, or the
    // administrative share \\MACHINE\c$\Users\robert. The whole prefix becomes "~".
    private static readonly Regex UncHome = new(@"\\{2,4}[^\\\s""']+\\{1,2}(?:[A-Za-z]\$\\{1,2})?Users\\{1,2}[^\\\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // A camel-case identifier - our own class and method names, optionally joined by underscores, letters only.
    // Exempt from the key rule: a minted key is letters only AND in this exact shape about once in 190
    // million (5.2e-9 per key, computed over the 64-character alphabet in review round 4), while eight real Director error lines lost their method name to the rule without it.
    private static readonly Regex Identifier = new(@"^[A-Z][a-z]+(?:[A-Z][a-z]+)*(?:_[A-Z][a-z]+(?:[A-Z][a-z]+)*)*$", RegexOptions.CultureInvariant);

    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[^\s""']+", RegexOptions.CultureInvariant);
    private static readonly Regex NamedSecret = new(
        @"(?i)\b(token|key|apikey|api_key|secret|password|pwd|authorization)(\s*[=:]\s*)[^\s""'&,;]+",
        RegexOptions.CultureInvariant);
    // A run of 32 or more URL-safe base64 characters, not touching another one. Whether it is a key is
    // decided by LooksLikeAKey, not by the pattern, so a GUID or a long lower-case path segment is kept.
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
        s = UncHome.Replace(s, "~");
        s = WindowsHome.Replace(s, "~");
        s = Bearer.Replace(s, "Bearer " + Redacted);
        s = NamedSecret.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
        var text = s;
        s = KeyRun.Replace(text, m => LooksLikeAKey(m.Value) && !InStackFrame(text, m.Index) ? Redacted : m.Value);
        return s;
    }

    /// <summary>
    /// Whether a long run is a key: it has an upper-case letter AND a lower-case letter or a digit, and it is
    /// not a GUID. A minted key (43 random characters from 64) fails that only when it has no upper-case letter
    /// at all, about one key in ten thousand million. A lower-case branch or folder name with digits, a git
    /// hash and a GUID are kept.
    ///
    /// Requiring a digit as well, as the first version did, let one minted key in about 1,500 through
    /// untouched - a key with no digit in it.
    ///
    /// A camel-case identifier made only of letters (a class or method name, words joined by underscores) is
    /// kept, and so is anything on a stack frame line - see <see cref="InStackFrame"/>. The remaining cost: a
    /// mixed-case run of 32 or more with digits inside it, which our own names rarely are, is redacted.
    /// </summary>
    internal static bool LooksLikeAKey(string run)
    {
        if (Guid.TryParseExact(run, "D", out _)) return false;
        if (Identifier.IsMatch(run)) return false;
        bool upper = false, lower = false, digit = false;
        foreach (var c in run)
        {
            if (c is >= 'A' and <= 'Z') upper = true;
            else if (c is >= 'a' and <= 'z') lower = true;
            else if (c is >= '0' and <= '9') digit = true;
        }
        return upper && (lower || digit);
    }

    /// <summary>
    /// Whether the position is on a stack frame line ("   at Namespace.Type.Method(...)"). A frame is written
    /// by the runtime from our own code's names, never from data, so a long method name there is kept:
    /// a stack whose method names were redacted would be no use to anyone.
    /// </summary>
    internal static bool InStackFrame(string text, int index)
    {
        var lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        var i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i > lineStart && string.CompareOrdinal(text, i, "at ", 0, 3) == 0;
    }
}
