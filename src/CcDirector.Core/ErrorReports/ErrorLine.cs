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

    // Matched only in the HEAD of a line - the part before the first ": ", which is where the house form puts
    // the marker ("[Class] Method FAILED: <detail>"). The detail after it can carry interpolated text - a
    // server's answer, an action's detail - and a marker word inside THAT must not turn an ordinary line into
    // a report, because then the leak surface would be every log line rather than the error lines.
    private static readonly Regex Marker = new(@"\b(FAILED|UNHANDLED|UNOBSERVED|FATAL|ERROR)\b", RegexOptions.CultureInvariant);

    // ...or ANYWHERE in the first line when the marker is in the verdict position: followed by ":", "(", ","
    // or the end of the line - "[X] seat {name}: PassDevReportsAsync FAILED: ...", "tool reconcile FAILED
    // (ignored)", "registry read FAILED, falling back". A word inside interpolated prose ("the build FAILED and
    // ...") or a quoted value ("\"FATAL\"}") is not in that position and stays out.
    private static readonly Regex VerdictMarker = new(@"\b(FAILED|UNHANDLED|UNOBSERVED|FATAL|ERROR)(?=\s*[:(,]|\s*$)", RegexOptions.CultureInvariant);

    private static readonly Regex LeadingTag = new(@"^\[(?<tag>[^\]\r\n]{1,100})\]\s*", RegexOptions.CultureInvariant);
    private static readonly Regex ExceptionType = new(
        @"(?<type>\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:Exception|Error))\b(?=:|\s*\r?\n|\s*$|\s*\()",
        RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="message"/> (a FileLog message, without its timestamp) is an error line.</summary>
    public static bool IsError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        if (message.StartsWith(ReporterTag, StringComparison.Ordinal)) return false;
        return Marker.IsMatch(Head(message)) || VerdictMarker.IsMatch(FirstLine(message));
    }

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n');
        var line = newline < 0 ? message : message[..newline];
        return line.Length > 2000 ? line[..2000] : line.TrimEnd('\r');
    }

    /// <summary>What kind of error the line records, read from the head only.</summary>
    public static string KindOf(string message)
    {
        var head = Head(message);
        if (head.Contains("UI-THREAD", StringComparison.Ordinal)) return "ui-thread";
        if (head.Contains("UNOBSERVED", StringComparison.Ordinal)) return "unobserved-task";
        if (head.Contains("UNHANDLED", StringComparison.Ordinal)) return "unhandled";
        if (head.Contains("FATAL", StringComparison.Ordinal)) return "fatal";
        return "logged";
    }

    /// <summary>The part of the first line before the first ": " (the whole first line when there is none),
    /// capped at 300 characters so a line with no separator is not scanned end to end.</summary>
    internal static string Head(string message)
    {
        var end = message.IndexOf(": ", StringComparison.Ordinal);
        var newline = message.IndexOf('\n');
        if (end < 0 || (newline >= 0 && newline < end)) end = newline;
        if (end < 0) end = message.Length;
        return message[..Math.Min(end, 300)];
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
