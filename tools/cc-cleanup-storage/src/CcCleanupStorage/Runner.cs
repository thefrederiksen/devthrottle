using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Reporting;
using CcDirector.Reclaim.Rules;
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
            CommandName.Recommend => Recommend(request),
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

    private static Answer Recommend(Request request)
    {
        var folder = request.FolderPath
            ?? throw new InvalidOperationException("A recommendation was asked for with no folder, which the command line refuses.");

        // The recommendations are made against a saved scan, the same one a screen will render. They
        // never walk the disk themselves: a caller who asked what is safe to remove did not ask for a
        // three minute walk, and a command that quietly started one would be doing something nobody
        // asked for. When there is no saved scan the answer says so and prints the command that makes
        // one, rather than falling back to scanning.
        var indexPath = ScanIndexStore.PathFor(request.IndexDirectory, folder);
        var index = ScanIndexStore.Load(indexPath);

        // Made by the engine, in the one place the background scan makes them too, so a person asking
        // and the Launcher not being asked can never run different rules or judge an answer differently.
        var report = RecommendationRun.Against(
            index.Scan, MachineRules.ForThisMachine(), DateTimeOffset.UtcNow, request.LargestFolders);

        var lines = new List<string>(report.Lines)
        {
            string.Empty,
            $"index: {indexPath}",
            $"saved: {index.WrittenUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}",
            "removal: this tool cannot remove anything. It reads, measures and explains"
        };
        lines.AddRange(AxiOutput.Help(
        [
            $"cc-cleanup-storage recommend \"{index.Scan.RootPath}\" --json",
            $"cc-cleanup-storage report \"{index.Scan.RootPath}\"",
            $"cc-cleanup-storage scan \"{index.Scan.RootPath}\""
        ]));

        return new Answer(
            report.Verdict == ReportVerdict.Ok ? ExitCodes.Ok : ExitCodes.Failed,
            lines,
            ToRecommendJson(report, indexPath, index.WrittenUtc));
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
            new(
                "saved-scans",
                string.Empty,
                "cc-cleanup-storage",
                "the saved scans on this machine",
                CommandLine.SavedScansFlags),
            new(
                "scan",
                "scan",
                "cc-cleanup-storage scan \"<folder>\" [flags]",
                "walk a folder and save what was seen",
                CommandLine.ScanFlags),
            new(
                "report",
                "report",
                "cc-cleanup-storage report \"<folder>\" [flags]",
                "report the saved scan of a folder",
                CommandLine.ReportFlags),
            new(
                "recommend",
                "recommend",
                "cc-cleanup-storage recommend \"<folder>\" [flags]",
                "say what is provably safe to remove, and why",
                CommandLine.RecommendFlags)
        };

        var flags = new List<HelpFlagJson>
        {
            new("--json", "answer as machine-readable text, with every field"),
            new("--index-directory <folder>", "where saved scans live"),
            new("--top <number>", "how many of the largest folders to name, 1 to 1000"),
            new("--folder-depth <number>", "how deep a scan records folder totals, 1 to 10 (scan only)"),
            new("--help", "this page"),
            new("-h", "this page, the short spelling"),
            new("--version", "the version of this tool (no command word only)")
        };

        var exitCodes = new List<ExitCodeJson>
        {
            new(ExitCodes.Ok, "the command succeeded"),
            new(ExitCodes.Failed, "the command failed, or the scan behind it is a broken instrument"),
            new(ExitCodes.Usage, "the command line was wrong: an unknown command, an unknown flag, or a bad value")
        };

        // The usage lines are the commands' own invocation lines, read off the commands themselves.
        // They used to be a separate list joined to the commands by nothing but position, so a fourth
        // command with no fourth line added beside it threw from the help page - the one answer that
        // must never fail. There is now one list, and a command cannot be added without its line.
        var usage = commands.Select(command => command.Invocation).ToList();

        var notes = new[]
        {
            "what a report always says:",
            "  the bytes the scan saw, the bytes the volume counts as used, and the difference",
            "  between them, with every folder that refused a listing named underneath. A scan that",
            "  saw nothing reports broken, never that there is nothing there.",
            "",
            "what a recommendation always says:",
            "  the rule, what it removes, the proof that it is safe, what is lost, how to get it",
            "  back, and the rule's own controls. A rule that could not do its work reports broken",
            "  and offers nothing, never that there is nothing to remove. Anything no rule matched",
            "  is reported as unclassified and is never offered.",
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
        // The columns are wide enough for the longest entry in them and never narrower than the
        // widths the page has always used, so a longer command or flag added later pushes its column
        // out instead of running into the words beside it, and today's page is unchanged to the
        // character. One space is the gap the page already leaves at its widest flag.
        const int smallestGap = 1;
        var usageColumn = Math.Max(47, commands.Max(command => command.Invocation.Length) + smallestGap);
        foreach (var command in commands)
            lines.Add("  " + command.Invocation.PadRight(usageColumn) + command.Purpose);
        lines.Add("");
        lines.Add("flags:");
        var flagColumn = Math.Max(27, flags.Max(flag => flag.Name.Length) + smallestGap);
        foreach (var flag in flags)
            lines.Add("  " + flag.Name.PadRight(flagColumn) + flag.Purpose);
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

    private static RecommendJson ToRecommendJson(
        RecommendationReport report, string indexPath, DateTimeOffset scannedUtc) => new()
    {
        Command = "recommend",
        Ok = report.Verdict == ReportVerdict.Ok,
        Verdict = report.Verdict == ReportVerdict.Ok ? "ok" : "broken",
        BrokenReason = report.BrokenReason,
        RootPath = report.Scan.Scan.RootPath,
        IndexPath = indexPath,
        ScannedUtc = scannedUtc,
        RulesRun = report.Findings.Count,
        RulesBroken = report.BrokenRules.Count,
        RulesNotRun = report.RulesNotRun
            .Select(rule => new RuleNotRunJson(rule.RuleId, rule.RuleName, rule.LooksIn))
            .ToList(),
        RulesSawMoreThanTheScan = report.RulesSawMoreThanTheScan,
        ItemsOffered = report.ItemsOffered,
        ReclaimableBytes = report.ReclaimableBytes,
        UnclassifiedBytes = report.UnclassifiedBytes,
        UnseenBytes = report.Scan.UnseenBytes,
        Volume = report.Scan.Scan.Volume,
        ReachLines = report.Scan.ReachLines,
        Rules = report.Findings.Select(ToRuleJson).ToList(),
        Lines = report.Lines
    };

    private static RuleFindingJson ToRuleJson(RuleFinding finding) => new()
    {
        Rule = finding.RuleId,
        Name = finding.RuleName,
        Proof = finding.Proof.ToString(),
        ProofInWords = RuleFinding.ProofWords(finding.Proof),
        Verdict = finding.Verdict == RuleVerdict.Ok ? "ok" : "broken",
        Ok = finding.Verdict == RuleVerdict.Ok,
        BrokenReason = finding.BrokenReason,
        WhatItRemoves = finding.WhatItRemoves,
        WhyItIsSafe = finding.WhyItIsSafe,
        WhatIsLost = finding.WhatIsLost,
        HowToGetItBack = finding.HowToGetItBack,
        AgeGateDays = finding.AgeGateDays,
        NeedsAdministrator = finding.NeedsAdministrator,
        CommandToRun = finding.CommandToRun,
        Controls = finding.Controls
            .Select(control => new RuleControlJson(control.Name, control.Count, control.MustNotBeEmpty))
            .ToList(),
        Candidates = finding.Candidates
            .Select(candidate => new ReclaimCandidateJson(
                candidate.Path,
                candidate.Bytes,
                SizeText.Describe(candidate.Bytes),
                candidate.LastWrittenUtc,
                candidate.Why))
            .ToList(),
        ItemsOffered = finding.Candidates.Count,
        Bytes = finding.CandidateBytes,
        Lines = finding.Lines
    };

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
