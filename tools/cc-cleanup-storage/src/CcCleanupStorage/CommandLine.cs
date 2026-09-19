using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Scanning;

namespace CcCleanupStorage;

/// <summary>What the caller asked for.</summary>
public enum CommandName
{
    /// <summary>No command: show the saved scans on this machine.</summary>
    SavedScans,

    /// <summary>Walk a folder and save what was seen.</summary>
    Scan,

    /// <summary>Read a saved scan and report it.</summary>
    Report,

    /// <summary>Show how to use the tool.</summary>
    Help,

    /// <summary>Show the version of the tool.</summary>
    Version
}

/// <summary>One command, fully decided, with every flag read.</summary>
public sealed record Request
{
    /// <summary>What to do.</summary>
    public required CommandName Command { get; init; }

    /// <summary>The folder a scan or a report is about, or null when the command takes none.</summary>
    public required string? FolderPath { get; init; }

    /// <summary>True when the answer is wanted as machine-readable text.</summary>
    public required bool Json { get; init; }

    /// <summary>The folder saved scans live in.</summary>
    public required string IndexDirectory { get; init; }

    /// <summary>How many of the largest folders a report names.</summary>
    public required int LargestFolders { get; init; }

    /// <summary>How deep a scan records folder totals.</summary>
    public required int FolderDepth { get; init; }

    /// <summary>The command word as it was typed, for the help offered after an answer.</summary>
    public required string CommandWord { get; init; }
}

/// <summary>Either a decided request, or the reason the command line could not be used.</summary>
/// <param name="Request">The request, when the command line was understood.</param>
/// <param name="UsageError">Why it was not, in one line, or null.</param>
public sealed record ParseOutcome(Request? Request, string? UsageError);

/// <summary>
/// Reads the command line.
///
/// Every flag this tool takes is named per command, and a flag it does not know is an error rather
/// than something quietly ignored. An agent that passes a flag and receives no complaint reasonably
/// concludes the flag did something; a flag that is read and dropped is therefore not a small
/// untidiness but a wrong answer, and the AXI standard names it as a defect.
/// </summary>
public static class CommandLine
{
    private static readonly string[] SavedScansFlags = ["--json", "--index-directory", "--help"];
    private static readonly string[] ScanFlags = ["--json", "--index-directory", "--top", "--folder-depth", "--help"];
    private static readonly string[] ReportFlags = ["--json", "--index-directory", "--top", "--help"];

    /// <summary>
    /// Read one command line.
    /// </summary>
    /// <param name="arguments">The arguments, without the name of the program.</param>
    /// <param name="defaultIndexDirectory">Where saved scans live when the caller does not say.</param>
    public static ParseOutcome Parse(IReadOnlyList<string> arguments, string defaultIndexDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (string.IsNullOrWhiteSpace(defaultIndexDirectory))
            throw new ArgumentException("A default index directory cannot be blank.", nameof(defaultIndexDirectory));

        FileLog.Write($"[CommandLine] Parse: arguments={arguments.Count}, defaultIndexDirectory={defaultIndexDirectory}");

        var outcome = Read(arguments, defaultIndexDirectory);

        if (outcome.UsageError is not null)
            FileLog.Write($"[CommandLine] Parse FAILED: {outcome.UsageError}");
        else
            FileLog.Write(
                $"[CommandLine] Parse done: command={outcome.Request?.Command}, json={outcome.Request?.Json}");

        return outcome;
    }

    private static ParseOutcome Read(IReadOnlyList<string> arguments, string defaultIndexDirectory)
    {
        var command = CommandName.SavedScans;
        var commandWord = string.Empty;
        var allowed = SavedScansFlags;
        var takesFolder = false;
        var first = 0;

        if (arguments.Count > 0 && !arguments[0].StartsWith('-'))
        {
            commandWord = arguments[0];
            first = 1;
            switch (commandWord)
            {
                case "scan":
                    command = CommandName.Scan;
                    allowed = ScanFlags;
                    takesFolder = true;
                    break;
                case "report":
                    command = CommandName.Report;
                    allowed = ReportFlags;
                    takesFolder = true;
                    break;
                default:
                    return new ParseOutcome(null, $"there is no command {commandWord}; the commands are scan and report");
            }
        }

        string? folder = null;
        var json = false;
        var indexDirectory = defaultIndexDirectory;
        var largestFolders = ScanReportBuilder.DefaultLargestFolders;
        var folderDepth = ScanOptions.DefaultFolderDepth;
        CommandName? earlyAnswer = null;

        for (var at = first; at < arguments.Count; at++)
        {
            var argument = arguments[at];

            if (!argument.StartsWith('-'))
            {
                if (!takesFolder)
                    return new ParseOutcome(null, $"the command {Named(commandWord)} takes no folder, and one was given: {argument}");
                if (folder is not null)
                    return new ParseOutcome(null, $"the command {Named(commandWord)} takes one folder, and two were given: {folder} and {argument}");
                folder = argument;
                continue;
            }

            // Help and version stop the command line, but not from inside the loop: the request is
            // built only after every flag has been read, so the machine-readable flag survives in
            // whichever order it is given - before them, or after them. When both are given, the
            // first to appear wins, which is what the command line answered before this change.
            if (argument is "--help" or "-h")
            {
                earlyAnswer ??= CommandName.Help;
                continue;
            }

            if (argument == "--version" && command == CommandName.SavedScans)
            {
                earlyAnswer ??= CommandName.Version;
                continue;
            }

            if (!allowed.Contains(argument, StringComparer.Ordinal))
            {
                return new ParseOutcome(null,
                    $"there is no flag {argument} for {Named(commandWord)}; the flags are {string.Join(", ", allowed)}");
            }

            switch (argument)
            {
                case "--json":
                    json = true;
                    break;

                case "--index-directory":
                {
                    var outcome = ReadValue(arguments, ref at, argument);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    indexDirectory = outcome.Value;
                    break;
                }

                case "--top":
                {
                    var outcome = ReadWholeNumber(
                        arguments, ref at, argument,
                        ScanReportBuilder.MinimumLargestFolders, ScanReportBuilder.MaximumLargestFolders);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    largestFolders = outcome.Number;
                    break;
                }

                case "--folder-depth":
                {
                    var outcome = ReadWholeNumber(
                        arguments, ref at, argument,
                        ScanOptions.MinimumFolderDepth, ScanOptions.MaximumFolderDepth);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    folderDepth = outcome.Number;
                    break;
                }

                default:
                    throw new InvalidOperationException($"The flag {argument} is allowed and is not read.");
            }
        }

        // Help and version are answered before the missing-folder check, so that asking for the help
        // page of a command with no folder given still shows the help page, as it always did.
        if (earlyAnswer is not null)
        {
            return new ParseOutcome(earlyAnswer == CommandName.Help
                ? HelpRequest(indexDirectory, json)
                : VersionRequest(indexDirectory, json), null);
        }

        if (takesFolder && folder is null)
            return new ParseOutcome(null, $"the command {Named(commandWord)} needs a folder: cc-cleanup-storage {commandWord} \"<folder>\"");

        return new ParseOutcome(new Request
        {
            Command = command,
            FolderPath = folder,
            Json = json,
            IndexDirectory = indexDirectory,
            LargestFolders = largestFolders,
            FolderDepth = folderDepth,
            CommandWord = commandWord
        }, null);
    }

    private static Request HelpRequest(string indexDirectory, bool json) => new()
    {
        Command = CommandName.Help,
        FolderPath = null,
        Json = json,
        IndexDirectory = indexDirectory,
        LargestFolders = ScanReportBuilder.DefaultLargestFolders,
        FolderDepth = ScanOptions.DefaultFolderDepth,
        CommandWord = "help"
    };

    private static Request VersionRequest(string indexDirectory, bool json) => new()
    {
        Command = CommandName.Version,
        FolderPath = null,
        Json = json,
        IndexDirectory = indexDirectory,
        LargestFolders = ScanReportBuilder.DefaultLargestFolders,
        FolderDepth = ScanOptions.DefaultFolderDepth,
        CommandWord = "version"
    };

    private static string Named(string commandWord) =>
        commandWord.Length == 0 ? "cc-cleanup-storage with no command" : commandWord;

    private static (string Value, string? Error) ReadValue(IReadOnlyList<string> arguments, ref int at, string flag)
    {
        if (at + 1 >= arguments.Count)
            return (string.Empty, $"the flag {flag} needs a value after it");

        at++;
        var value = arguments[at];
        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, $"the flag {flag} was given a blank value");

        return (value, null);
    }

    private static (int Number, string? Error) ReadWholeNumber(
        IReadOnlyList<string> arguments, ref int at, string flag, int smallest, int largest)
    {
        var read = ReadValue(arguments, ref at, flag);
        if (read.Error is not null) return (0, read.Error);

        if (!int.TryParse(read.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return (0, $"the flag {flag} needs a whole number and was given {read.Value}");

        if (number < smallest || number > largest)
        {
            return (0, $"the flag {flag} takes a number between " +
                       $"{smallest.ToString(CultureInfo.InvariantCulture)} and " +
                       $"{largest.ToString(CultureInfo.InvariantCulture)}, and was given " +
                       $"{number.ToString(CultureInfo.InvariantCulture)}");
        }

        return (number, null);
    }
}
