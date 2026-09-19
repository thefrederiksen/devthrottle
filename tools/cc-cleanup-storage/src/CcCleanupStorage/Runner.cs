using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Scanning;

namespace CcCleanupStorage;

/// <summary>
/// Does what the command line asked for.
///
/// It decides nothing about what the numbers mean. The engine writes the report's sentences and this
/// prints them as they stand, so the words an agent reads in a terminal are the words a screen will
/// show when the Director renders the same saved scan - critical rule 7 in CLAUDE.md.
/// </summary>
public static class Runner
{
    /// <summary>
    /// Run one request.
    /// </summary>
    /// <param name="request">What was asked for.</param>
    public static Answer Run(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileLog.Write($"[Runner] Run: command={request.Command}, folder={request.FolderPath}, json={request.Json}");

        var answer = request.Command switch
        {
            CommandName.Help => Help(),
            CommandName.Version => Version(),
            CommandName.SavedScans => SavedScans(request),
            CommandName.Scan => Scan(request),
            CommandName.Report => Report(request),
            _ => throw new InvalidOperationException($"There is no command {request.Command}.")
        };

        FileLog.Write($"[Runner] Run done: command={request.Command}, exitCode={answer.ExitCode}");
        return answer;
    }

    /// <summary>The version of this tool, as its own name and number.</summary>
    public static string VersionLine()
    {
        var version = typeof(Runner).Assembly.GetName().Version;
        var number = version is null
            ? "unknown"
            : $"{version.Major.ToString(CultureInfo.InvariantCulture)}." +
              $"{version.Minor.ToString(CultureInfo.InvariantCulture)}." +
              $"{version.Build.ToString(CultureInfo.InvariantCulture)}";
        return $"cc-cleanup-storage {number}";
    }

    /// <summary>The answer when something went wrong.</summary>
    /// <param name="commandWord">The command that was asked for.</param>
    /// <param name="code">A short word an agent can branch on.</param>
    /// <param name="message">What went wrong and what to do about it.</param>
    /// <param name="exitCode">The exit code to end on.</param>
    public static Answer Failure(string commandWord, string code, string message, int exitCode)
    {
        FileLog.Write($"[Runner] Failure: command={commandWord}, code={code}, message={message}");

        var lines = new List<string>
        {
            $"error: {code}",
            $"message: {message}"
        };
        lines.AddRange(AxiOutput.Help(
        [
            "cc-cleanup-storage scan \"<folder>\"",
            "cc-cleanup-storage report \"<folder>\"",
            "cc-cleanup-storage --help"
        ]));

        return new Answer(exitCode, lines, new ErrorJson
        {
            Command = commandWord.Length == 0 ? "saved-scans" : commandWord,
            Ok = false,
            Code = code,
            Message = message
        });
    }

    private static Answer Scan(Request request)
    {
        var folder = request.FolderPath
            ?? throw new InvalidOperationException("A scan was asked for with no folder, which the command line refuses.");

        var scan = new DirectoryScanner().Scan(new ScanOptions
        {
            RootPath = folder,
            MaxFolderDepth = request.FolderDepth
        });

        var report = ScanReportBuilder.Build(scan, request.LargestFolders);

        // A broken instrument is not saved. The saved scan is what a screen will show later without
        // walking anything, and a measurement this tool has just called broken must not be the thing
        // it shows. The report says so on its own line rather than leaving the caller to wonder.
        string? indexPath = null;
        if (report.Verdict == ReportVerdict.Ok)
        {
            indexPath = ScanIndexStore.PathFor(request.IndexDirectory, scan.RootPath);
            ScanIndexStore.Save(indexPath, scan, DateTimeOffset.UtcNow);
        }

        var lines = new List<string>(report.Lines)
        {
            indexPath is null
                ? "index: not written, because a broken scan must never be the answer a screen shows later"
                : $"index: {indexPath}"
        };
        // What is offered next follows the verdict the engine already reached. Offering to report a
        // scan that was never saved would send the caller to an error.
        lines.AddRange(AxiOutput.Help(indexPath is null
            ?
            [
                $"cc-cleanup-storage scan \"{scan.RootPath}\"",
                "cc-cleanup-storage"
            ]
            :
            [
                $"cc-cleanup-storage report \"{scan.RootPath}\"",
                $"cc-cleanup-storage report \"{scan.RootPath}\" --json",
                "cc-cleanup-storage"
            ]));

        return new Answer(
            report.Verdict == ReportVerdict.Ok ? ExitCodes.Ok : ExitCodes.Failed,
            lines,
            ToJson("scan", report, indexPath));
    }

    private static Answer Report(Request request)
    {
        var folder = request.FolderPath
            ?? throw new InvalidOperationException("A report was asked for with no folder, which the command line refuses.");

        var indexPath = ScanIndexStore.PathFor(request.IndexDirectory, folder);
        var index = ScanIndexStore.Load(indexPath);
        var report = ScanReportBuilder.Build(index.Scan, request.LargestFolders);

        var lines = new List<string>(report.Lines)
        {
            $"index: {indexPath}",
            $"saved: {index.WrittenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}"
        };
        lines.AddRange(AxiOutput.Help(
        [
            $"cc-cleanup-storage scan \"{index.Scan.RootPath}\"",
            $"cc-cleanup-storage report \"{index.Scan.RootPath}\" --top 50",
            "cc-cleanup-storage"
        ]));

        return new Answer(
            report.Verdict == ReportVerdict.Ok ? ExitCodes.Ok : ExitCodes.Failed,
            lines,
            ToJson("report", report, indexPath));
    }

    private static Answer SavedScans(Request request)
    {
        var listing = ScanIndexStore.List(request.IndexDirectory);

        var lines = new List<string>
        {
            $"count: {listing.Scans.Count.ToString(CultureInfo.InvariantCulture)}",
            $"index-directory: {request.IndexDirectory}"
        };

        lines.AddRange(AxiOutput.List(
            "saved-scans",
            ["root", "scanned", "bytes", "files"],
            listing.Scans.Select(saved => (IReadOnlyList<string>)
            [
                AxiOutput.Value(saved.RootPath),
                AxiOutput.Value(Moment(saved.WrittenUtc)),
                AxiOutput.Value(saved.BytesSeen),
                AxiOutput.Value(saved.FilesSeen)
            ]).ToList()));

        if (listing.Unreadable.Count > 0)
        {
            lines.AddRange(AxiOutput.List(
                "unreadable-index-files",
                ["path", "reason"],
                listing.Unreadable.Select(bad => (IReadOnlyList<string>)
                [
                    AxiOutput.Value(bad.IndexPath),
                    AxiOutput.Value(bad.Reason)
                ]).ToList()));
        }

        var offers = new List<string> { "cc-cleanup-storage scan \"<folder>\"" };
        if (listing.Scans.Count > 0)
            offers.Add($"cc-cleanup-storage report \"{listing.Scans[0].RootPath}\"");
        offers.Add("cc-cleanup-storage --help");
        lines.AddRange(AxiOutput.Help(offers));

        return new Answer(ExitCodes.Ok, lines, new SavedScansJson
        {
            Command = "saved-scans",
            Ok = true,
            IndexDirectory = request.IndexDirectory,
            Count = listing.Scans.Count,
            Scans = listing.Scans
                .Select(saved => new SavedScanJson(
                    saved.RootPath, saved.WrittenUtc, saved.BytesSeen, saved.FilesSeen, saved.IndexPath))
                .ToList(),
            Unreadable = listing.Unreadable
                .Select(bad => new UnreadableIndexJson(bad.IndexPath, bad.Reason))
                .ToList()
        });
    }

    private static Answer Version() =>
        new(ExitCodes.Ok, [VersionLine()], new { command = "version", ok = true, version = VersionLine() });

    private static Answer Help()
    {
        // The one place the help page is written. Both pages - the text one and the machine-readable
        // one - are rendered from the data below, so they cannot drift apart, and the flags each
        // command takes are the command line reader's own lists, so the page can never name a flag
        // the reader refuses or miss one it takes.
        var commands = new List<HelpCommandJson>
        {
            new("saved-scans", "the saved scans on this machine", CommandLine.SavedScansFlags),
            new("scan", "walk a folder and save what was seen", CommandLine.ScanFlags),
            new("report", "report the saved scan of a folder", CommandLine.ReportFlags)
        };

        var flags = new List<HelpFlagJson>
        {
            new("--json", "answer as machine-readable text, with every field"),
            new("--index-directory <folder>", "where saved scans live"),
            new("--top <number>", "how many of the largest folders to name, 1 to 1000"),
            new("--folder-depth <number>", "how deep a scan records folder totals, 1 to 10 (scan only)"),
            new("--help", "this page"),
            new("--version", "the version of this tool")
        };

        var exitCodes = new List<ExitCodeJson>
        {
            new(ExitCodes.Ok, "the command succeeded"),
            new(ExitCodes.Failed, "the command failed, or the scan behind it is a broken instrument"),
            new(ExitCodes.Usage, "the command line was wrong: an unknown command, an unknown flag, or a bad value")
        };

        var usage = new[]
        {
            "cc-cleanup-storage",
            "cc-cleanup-storage scan \"<folder>\" [flags]",
            "cc-cleanup-storage report \"<folder>\" [flags]"
        };

        var notes = new[]
        {
            "what a report always says:",
            "  the bytes the scan saw, the bytes the volume counts as used, and the difference",
            "  between them, with every folder that refused a listing named underneath. A scan that",
            "  saw nothing reports broken, never that there is nothing there.",
            "",
            "what this tool does not do:",
            "  it never deletes, moves or changes anything. It reads."
        };

        var lines = new List<string>
        {
            "cc-cleanup-storage - walk a disk, save what was seen, and report it",
            "",
            "usage:"
        };
        for (var at = 0; at < commands.Count; at++)
            lines.Add("  " + usage[at].PadRight(47) + commands[at].Purpose);
        lines.Add("");
        lines.Add("flags:");
        foreach (var flag in flags)
            lines.Add("  " + flag.Name.PadRight(27) + flag.Purpose);
        lines.Add("");
        lines.Add("exit codes:");
        foreach (var exitCode in exitCodes)
            lines.Add("  " + exitCode.Code.ToString(CultureInfo.InvariantCulture).PadRight(3) + exitCode.Purpose);
        lines.Add("");
        lines.AddRange(notes);

        return new Answer(ExitCodes.Ok, lines, new HelpJson
        {
            Command = "help",
            Ok = true,
            Usage = usage,
            Commands = commands,
            Flags = flags,
            ExitCodes = exitCodes,
            Notes = notes
        });
    }

    private static ReportJson ToJson(string command, ScanReport report, string? indexPath) => new()
    {
        Command = command,
        Ok = report.Verdict == ReportVerdict.Ok,
        Verdict = report.Verdict == ReportVerdict.Ok ? "ok" : "broken",
        BrokenReason = report.BrokenReason,
        RootPath = report.Scan.RootPath,
        RootIsVolumeRoot = report.Scan.RootIsVolumeRoot,
        IndexPath = indexPath,
        StartedUtc = report.Scan.StartedUtc,
        FinishedUtc = report.Scan.FinishedUtc,
        ElapsedSeconds = report.Scan.ElapsedSeconds,
        FilesSeen = report.Scan.FilesSeen,
        FoldersSeen = report.Scan.FoldersSeen,
        BytesSeen = report.Scan.BytesSeen,
        UnseenBytes = report.UnseenBytes,
        Volume = report.Scan.Volume,
        PlaceholderFiles = report.Scan.PlaceholderFiles,
        PlaceholderBytesInCloud = report.Scan.PlaceholderBytesInCloud,
        Links = report.Scan.Links,
        RefusedFolders = report.Scan.RefusedFolders
            .Select(folder => new RefusedFolderJson(
                folder.Path, ScanReportBuilder.RefusalWords(folder.Refusal), folder.RefusalCode))
            .ToList(),
        LargestFolders = report.LargestFolders
            .Select(folder => new FolderJson(folder.Path, folder.Depth, folder.BytesSeen, folder.FilesSeen))
            .ToList(),
        Lines = report.Lines
    };

    private static string Moment(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
