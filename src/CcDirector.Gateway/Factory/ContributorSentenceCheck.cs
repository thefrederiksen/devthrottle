using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// The rules a sentence written for a team contributor must meet (Watch your factory). A contributor is a
/// customer, not a developer, and never sees a machine: so the sentence may not name one of the account's
/// machines, may not hold a folder path, and may not hold an email address.
///
/// PURE. It reads nothing and writes nothing: the caller hands it the sentence and the account's machine names,
/// and it answers with the refusal sentence, or null when the sentence passes. The factory activity record
/// applies it on every row that carries a contributor sentence; a later reader of those sentences can apply the
/// same rules without a second copy of them.
/// </summary>
public static class ContributorSentenceCheck
{
    // A character that can be part of a word. A machine name matches only where the characters on both sides of it
    // are not one of these, so "north" never matches inside "northern".
    private const string WordChar = @"[\p{L}\p{Nd}_]";

    // A drive path: a single letter, a colon and a slash or backslash ("C:\", "d:/"), not preceded by a letter or a
    // digit - so "at 5:30" and "ratio 3:1" are not paths.
    private static readonly Regex DrivePath = new(@"(?<![\p{L}\p{Nd}])[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);

    // A network path: two backslashes followed by a server name ("\\server\share").
    private static readonly Regex NetworkPath = new(@"\\\\[^\s\\]", RegexOptions.CultureInvariant);

    // A network path written with forward slashes ("//server/share"): two slashes, a server name and a slash. Not
    // preceded by a colon, a word character or a slash, so a web address ("https://example.test/page") is not one.
    private static readonly Regex ForwardSlashNetworkPath = new(
        @"(?<![:\p{L}\p{Nd}_/])//[\p{L}\p{Nd}][\p{L}\p{Nd}_.\-]*/", RegexOptions.CultureInvariant);

    // A path that starts at an environment variable: "%APPDATA%\", "$HOME/", "$env:USERPROFILE\". The name must
    // start with a letter or an underscore, so "100% of it" and "costs $5" are not paths.
    private static readonly Regex EnvironmentVariablePath = new(
        @"(?:%[A-Za-z_][A-Za-z0-9_]*%|\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*)[\\/]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The characters a machine name may be written with in its place: "SOREN_NORTH" is also "soren-north",
    // "soren.north" and "soren north".
    private static readonly char[] NameSeparators = { '_', '-', '.', ' ' };
    private const string NameSeparatorPattern = @"[_\-. ]+";

    // A rooted Unix path under a well-known top folder, or a home-relative path ("~/"). Not preceded by a word
    // character or a dot, so "and/or", "km/h" and "1/2" are not paths.
    private static readonly Regex UnixPath = new(
        @"(?<![\p{L}\p{Nd}_.])(?:/(?:home|Users|mnt|root|tmp|var|etc|opt|usr|srv|Volumes)/|~/)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // An email address: a local part, an at sign, and a dotted domain ending in letters.
    private static readonly Regex EmailAddress = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The refusal for <paramref name="sentence"/>, or null when it meets every rule. A machine name is matched
    /// without regard to case, only as a whole word, and with its separators (underscore, hyphen, dot, space) treated
    /// as one another. Blank machine names are ignored.
    /// </summary>
    public static string? Refusal(string sentence, IEnumerable<string> machineNames)
    {
        ArgumentNullException.ThrowIfNull(sentence);
        ArgumentNullException.ThrowIfNull(machineNames);

        var names = machineNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FileLog.Write($"[ContributorSentenceCheck] Refusal: chars={sentence.Length}, machines={names.Count}");

        foreach (var name in names)
        {
            var parts = name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var wholeWord = new Regex($"(?<!{WordChar}){string.Join(NameSeparatorPattern, parts)}(?!{WordChar})",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (wholeWord.IsMatch(sentence))
            {
                FileLog.Write($"[ContributorSentenceCheck] Refusal: names a machine, chars={sentence.Length}");
                return $"The sentence for a contributor names the machine '{name}'. A contributor never sees a machine: " +
                       "describe the step without naming it.";
            }
        }

        if (DrivePath.IsMatch(sentence) || NetworkPath.IsMatch(sentence) || ForwardSlashNetworkPath.IsMatch(sentence)
            || UnixPath.IsMatch(sentence) || EnvironmentVariablePath.IsMatch(sentence))
        {
            FileLog.Write($"[ContributorSentenceCheck] Refusal: holds a folder path, chars={sentence.Length}");
            return "The sentence for a contributor holds a folder path. A contributor never sees a machine: " +
                   "describe the step without the path.";
        }

        if (EmailAddress.IsMatch(sentence))
        {
            FileLog.Write($"[ContributorSentenceCheck] Refusal: holds an email address, chars={sentence.Length}");
            return "The sentence for a contributor holds an email address. Describe the step without the address.";
        }

        return null;
    }
}
