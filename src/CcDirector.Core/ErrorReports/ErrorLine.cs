using System.Text.RegularExpressions;

namespace CcDirector.Core.ErrorReports;

/// <summary>
/// Decides whether a FileLog line is an ERROR and pulls it apart (issue #3311).
///
/// The codebase has no log levels: an error is a line written in the house form
/// <c>[ClassName] MethodName FAILED: ...</c> (docs/CodingStyle.md), and an unhandled exception is written by
/// the start-up hooks as <c>[App] UNHANDLED ...</c> / <c>UNOBSERVED ...</c>. So an error is recognised by
/// those upper-case words, matched exactly (ordinal), which is what keeps ordinary prose such as
/// "no errors" or "failed over" out.
///
/// The reporter's own lines are never errors, whatever they say - otherwise a failed send would report
/// itself, and that would be a loop.
/// </summary>
public static class ErrorLine
{
    /// <summary>The tag every line the reporter writes about itself starts with.</summary>
    public const string ReporterTag = "[ErrorReporter]";

    private static readonly string[] Markers = { "FAILED", "UNHANDLED", "UNOBSERVED", "FATAL", "ERROR:", " ERROR " };

    private static readonly Regex LeadingTag = new(@"^\[(?<tag>[^\]\r\n]{1,100})\]\s*", RegexOptions.CultureInvariant);
    private static readonly Regex ExceptionType = new(
        @"(?<type>\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:Exception|Error))\b(?=:|\s*\r?\n|\s*$|\s*\()",
        RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="message"/> (a FileLog message, without its timestamp) is an error line.</summary>
    public static bool IsError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (message.StartsWith(ReporterTag, StringComparison.Ordinal)) return false;
        foreach (var marker in Markers)
            if (message.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>What kind of error the line records.</summary>
    public static string KindOf(string message)
    {
        if (message.Contains("UI-THREAD", StringComparison.Ordinal)) return "ui-thread";
        if (message.Contains("UNOBSERVED", StringComparison.Ordinal)) return "unobserved-task";
        if (message.Contains("UNHANDLED", StringComparison.Ordinal)) return "unhandled";
        if (message.Contains("FATAL", StringComparison.Ordinal)) return "fatal";
        return "logged";
    }

    /// <summary>
    /// Split a line into the class that wrote it, the first line of the message, the exception type if
    /// one is named, and the rest (the stack) when the line carried a whole exception.
    /// </summary>
    public static (string Source, string Message, string ExceptionType, string Stack) Parse(string line)
    {
        var source = "";
        var rest = line;
        var tag = LeadingTag.Match(line);
        if (tag.Success)
        {
            source = tag.Groups["tag"].Value;
            rest = line[tag.Length..];
        }

        var newline = rest.IndexOf('\n');
        var first = (newline < 0 ? rest : rest[..newline]).TrimEnd('\r');
        var stack = newline < 0 ? "" : rest[(newline + 1)..];

        var type = ExceptionType.Match(rest);
        return (source, first, type.Success ? type.Groups["type"].Value : "", stack);
    }
}
