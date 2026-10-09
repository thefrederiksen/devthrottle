using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The fingerprint of an error report (issue #3675): which PROBLEM a report is, so that the same failure on two
/// sessions, two machines or two days counts as one problem and two different failures never do.
///
/// COMPUTED ONCE, HERE, ON THE GATEWAY. <see cref="ErrorReportStore.Append"/> stamps it on every record it writes,
/// whoever sent the record. The client is dumb (rule 7): no client sends a fingerprint, so the command line, the
/// nightly Defects seat and the grouped read can never disagree about which reports are the same problem.
///
/// WHAT IT IS BUILT FROM. The component, the source (the class that logged it), the exception type, and the
/// message with everything that varies between two occurrences of one problem replaced by a placeholder:
/// quoted text, guids, times, paths, hexadecimal values, ids that mix letters and digits, and plain numbers.
///
/// WHY NOT JUST DIGITS. The Director's own reporter merges repeats by ignoring digits. Session and command ids
/// contain letters (<c>2c3c4215</c>, <c>cmd-a1b2c3</c>), so the same failure on two sessions became two rows.
/// </summary>
internal static class ErrorFingerprint
{
    /// <summary>How much of the normalised message counts. Two messages that agree this far are one problem.</summary>
    internal const int MaxNormalisedLength = 400;

    // Order matters: each pass sees what the passes before it left. Quoted text goes first, because anything at
    // all can be inside quotes; a time goes before a number, so a time is one placeholder and not five.
    private static readonly (Regex Pattern, string Placeholder)[] Passes =
    [
        // "quoted" and 'quoted' text. A single quote only counts when it is not inside a word, so the apostrophes
        // in "can't open, don't retry" are not read as a quotation running from one to the other.
        (new Regex("\"[^\"\\r\\n]*\"", RegexOptions.Compiled), "<q>"),
        (new Regex(@"(?<![\w])'[^'\r\n]*'(?![\w])", RegexOptions.Compiled), "<q>"),
        (new Regex(@"`[^`\r\n]*`", RegexOptions.Compiled), "<q>"),
        (new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled), "<guid>"),
        (new Regex(@"\b\d{4}-\d{2}-\d{2}([T ]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?)?(Z|[+-]\d{2}:?\d{2})?", RegexOptions.Compiled), "<time>"),
        (new Regex(@"\b\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?\b", RegexOptions.Compiled), "<time>"),
        // Paths: a Windows drive path, a network share, a home path the scrubber already shortened to ~, and an
        // absolute Unix path of two or more parts. A path runs to the next space or quote.
        (new Regex(@"\b[A-Za-z]:[\\/][^\s""'<>|]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled), "<path>"),
        (new Regex(@"(?<![\w.])~[\\/][^\s""'<>|]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"(?<![\w.:/])/[\w.\-]+(/[\w.\-]*)+", RegexOptions.Compiled), "<path>"),
        (new Regex(@"\b0[xX][0-9a-fA-F]+\b", RegexOptions.Compiled), "<hex>"),
        // A bare hexadecimal id of eight or more characters, even one made only of the letters a to f
        // (deadbeefcafe). An id with a digit in it is also caught by the id pass below; this one is for the rest.
        (new Regex(@"(?<![\w-])[0-9a-fA-F]{8,}(?![\w-])", RegexOptions.Compiled), "<hex>"),
        // An id: a run of six or more letters, digits, dashes and underscores holding at least one digit and at
        // least one letter - 2c3c4215, cmd-a1b2c3, sess_9f8e7d. A word with no digit is never an id.
        (new Regex(@"(?<![\w-])(?=[\w-]*\d)(?=[\w-]*[A-Za-z])[\w-]{6,}(?![\w-])", RegexOptions.Compiled), "<id>"),
        (new Regex(@"\d+", RegexOptions.Compiled), "<n>"),
        (new Regex(@"\s+", RegexOptions.Compiled), " "),
    ];

    /// <summary>16 lower-case hex characters of SHA-256 over the component, source, exception type and the
    /// normalised message. The same problem always gives the same fingerprint.</summary>
    public static string Of(string? component, string? source, string? exceptionType, string? message)
    {
        var basis = string.Join('\n',
            (component ?? "").Trim().ToLowerInvariant(),
            (source ?? "").Trim(),
            (exceptionType ?? "").Trim(),
            Normalise(message));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>The message with everything that varies between two occurrences of one problem replaced by a
    /// placeholder, whitespace collapsed, and cut to <see cref="MaxNormalisedLength"/>.</summary>
    public static string Normalise(string? message)
    {
        var text = message ?? "";
        foreach (var (pattern, placeholder) in Passes)
            text = pattern.Replace(text, placeholder);
        text = text.Trim();
        return text.Length > MaxNormalisedLength ? text[..MaxNormalisedLength] : text;
    }

    /// <summary>True when <paramref name="value"/> has the shape <see cref="Of"/> produces.</summary>
    public static bool IsFingerprint(string value)
        => value.Length == 16 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
