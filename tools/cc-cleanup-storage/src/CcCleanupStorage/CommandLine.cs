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

    /// <summary>Read a saved scan, run the rules, and say what is safe to remove.</summary>
    Recommend,

    /// <summary>
    /// Run the rules, put every candidate through the refusal gate, and either report what would
    /// move or - only with the apply flag - move exactly what the gate passed into holding.
    /// </summary>
    Reclaim,

    /// <summary>Every entry in one volume's holding folder.</summary>
    HoldingList,

    /// <summary>Move one held entry's item back to the path its record names. Never a dry run.</summary>
    HoldingRestore,

    /// <summary>Remove the holding entries whose period has passed. A dry run by default.</summary>
    HoldingPurge,

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

    /// <summary>
    /// The one rule the reclaim was narrowed to, or null to run every rule that looks inside the
    /// folder asked about.
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// True only when the caller passed the apply flag. Nothing moves and no owner command runs
    /// without it, and there is no second way to say it.
    /// </summary>
    public bool Apply { get; init; }

    /// <summary>
    /// The holding root, when the command takes one as a flag; null when the command computes its
    /// own per-volume default.
    /// </summary>
    public string? HoldingRootPath { get; init; }

    /// <summary>The holding entry to restore, when the command is a restore.</summary>
    public string? EntryId { get; init; }

    /// <summary>A holding period for this purge call only, in days, or null for each record's own.</summary>
    public int? Days { get; init; }

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
///
/// The flag lists below are the one source of what each command takes: the reader refuses anything
/// they do not name, and the help page prints them as they stand. Every flag the reader takes is in
/// them, including the short spelling of help and the version flag, because a flag that works while
/// sitting outside the list is a flag the help page cannot name - the page would then be telling a
/// machine that a working flag does not exist, which is the same wrong answer as naming one that
/// does not work.
/// </summary>
public static class CommandLine
{
    /// <summary>Every flag the tool takes when it is run with no command word.</summary>
    public static IReadOnlyList<string> SavedScansFlags { get; } =
        ["--json", "--index-directory", "--help", "-h", "--version"];

    /// <summary>Every flag the scan command takes.</summary>
    public static IReadOnlyList<string> ScanFlags { get; } =
        ["--json", "--index-directory", "--top", "--folder-depth", "--help", "-h"];

    /// <summary>Every flag the report command takes.</summary>
    public static IReadOnlyList<string> ReportFlags { get; } =
        ["--json", "--index-directory", "--top", "--help", "-h"];

    /// <summary>Every flag the recommend command takes.</summary>
    public static IReadOnlyList<string> RecommendFlags { get; } =
        ["--json", "--index-directory", "--top", "--help", "-h"];

    /// <summary>Every flag the reclaim command takes.</summary>
    public static IReadOnlyList<string> ReclaimFlags { get; } =
        ["--json", "--rule", "--apply", "--help", "-h"];

    /// <summary>Every flag the holding list command takes.</summary>
    public static IReadOnlyList<string> HoldingListFlags { get; } =
        ["--json", "--help", "-h"];

    /// <summary>Every flag the holding restore command takes.</summary>
    public static IReadOnlyList<string> HoldingRestoreFlags { get; } =
        ["--json", "--holding-root", "--help", "-h"];

    /// <summary>Every flag the holding purge command takes.</summary>
    public static IReadOnlyList<string> HoldingPurgeFlags { get; } =
        ["--json", "--holding-root", "--days", "--apply", "--help", "-h"];

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
                case "recommend":
                    command = CommandName.Recommend;
                    allowed = RecommendFlags;
                    takesFolder = true;
                    break;
                case "reclaim":
                    command = CommandName.Reclaim;
                    allowed = ReclaimFlags;
                    takesFolder = true;
                    break;
                case "holding":
                {
                    // "holding" is a word of its own with a command after it. The command is read
                    // by the same rules as every other: an unknown word is an error naming the ones
                    // that exist, never a quiet empty answer.
                    if (arguments.Count < 2 || arguments[1].StartsWith('-'))
                    {
                        return new ParseOutcome(null,
                            "the holding command needs a word after it - list, restore or purge - and none was given");
                    }

                    switch (arguments[1])
                    {
                        case "list":
                            command = CommandName.HoldingList;
                            allowed = HoldingListFlags;
                            takesFolder = true;
                            break;
                        case "restore":
                            command = CommandName.HoldingRestore;
                            allowed = HoldingRestoreFlags;
                            takesFolder = false;
                            break;
                        case "purge":
                            command = CommandName.HoldingPurge;
                            allowed = HoldingPurgeFlags;
                            takesFolder = false;
                            break;
                        default:
                            return new ParseOutcome(null,
                                $"there is no holding command {arguments[1]}; the holding commands are list, restore and purge");
                    }

                    first = 2;
                    commandWord = "holding " + arguments[1];
                    break;
                }
                default:
                    return new ParseOutcome(null,
                        "there is no command " + commandWord +
                        "; the commands are scan, report, recommend, reclaim and holding");
            }
        }

        string? folder = null;
        string? entryId = null;
        var json = false;
        var indexDirectory = defaultIndexDirectory;
        var largestFolders = ScanReportBuilder.DefaultLargestFolders;
        var folderDepth = ScanOptions.DefaultFolderDepth;
        string? ruleId = null;
        string? holdingRoot = null;
        int? days = null;
        var apply = false;
        CommandName? earlyAnswer = null;

        for (var at = first; at < arguments.Count; at++)
        {
            var argument = arguments[at];

            if (!argument.StartsWith('-'))
            {
                // Help and version have been asked for and the whole command line is still being
                // read, so their answer carries every flag. A bare word here is the command the
                // caller wants the page about, not a folder: judging it as a folder would turn a
                // request for help into a usage error, which is a wrong answer, not a strict one.
                // The page answers for every command, so the word needs nothing done on it.
                if (earlyAnswer is not null)
                    continue;

                if (command == CommandName.HoldingRestore)
                {
                    if (entryId is not null)
                        return new ParseOutcome(null, $"the command {Named(commandWord)} takes one entry id, and two were given: {entryId} and {argument}");
                    entryId = argument;
                    continue;
                }

                if (!takesFolder)
                    return new ParseOutcome(null, $"the command {Named(commandWord)} takes no folder, and one was given: {argument}");
                if (folder is not null)
                    return new ParseOutcome(null, $"the command {Named(commandWord)} takes one folder, and two were given: {folder} and {argument}");
                folder = argument;
                continue;
            }

            // Every flag is judged against the command's own list first, help and version included.
            // They used to be answered before the list was consulted, which let them work while
            // sitting outside it, so --version was taken with no command word and named in no list
            // and -h was taken everywhere and named nowhere. Which command takes which flag is one
            // statement, in the lists above, and nothing decides it a second time here.
            if (!allowed.Contains(argument, StringComparer.Ordinal))
            {
                return new ParseOutcome(null,
                    $"there is no flag {argument} for {Named(commandWord)}; the flags are {string.Join(", ", allowed)}");
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

            if (argument == "--version")
            {
                earlyAnswer ??= CommandName.Version;
                continue;
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

                case "--rule":
                {
                    var outcome = ReadValue(arguments, ref at, argument);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    ruleId = outcome.Value;
                    break;
                }

                case "--holding-root":
                {
                    var outcome = ReadValue(arguments, ref at, argument);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    holdingRoot = outcome.Value;
                    break;
                }

                case "--days":
                {
                    var outcome = ReadWholeNumber(arguments, ref at, argument, 0, 36500);
                    if (outcome.Error is not null) return new ParseOutcome(null, outcome.Error);
                    days = outcome.Number;
                    break;
                }

                case "--apply":
                    apply = true;
                    break;

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

        if (command == CommandName.HoldingRestore && entryId is null)
            return new ParseOutcome(null,
                "the command holding restore needs an entry id: cc-cleanup-storage holding restore <entry-id> --holding-root \"<folder>\"");

        if (command is CommandName.HoldingRestore or CommandName.HoldingPurge && holdingRoot is null)
            return new ParseOutcome(null,
                $"the command {Named(commandWord)} needs the holding folder: cc-cleanup-storage {commandWord} --holding-root \"<folder>\"");

        return new ParseOutcome(new Request
        {
            Command = command,
            FolderPath = folder,
            Json = json,
            IndexDirectory = indexDirectory,
            LargestFolders = largestFolders,
            FolderDepth = folderDepth,
            RuleId = ruleId,
            Apply = apply,
            HoldingRootPath = holdingRoot,
            EntryId = entryId,
            Days = days,
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
