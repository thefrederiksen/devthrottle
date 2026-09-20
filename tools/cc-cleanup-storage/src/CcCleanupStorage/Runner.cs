using System.Globalization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;
using CcDirector.Reclaim.Removal;
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
            CommandName.Reclaim => Reclaim(request),
            CommandName.HoldingList => HoldingList(request),
            CommandName.HoldingRestore => HoldingRestore(request),
            CommandName.HoldingPurge => HoldingPurge(request),
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

    private static Answer Reclaim(Request request)
    {
        var folder = request.FolderPath
            ?? throw new InvalidOperationException("A reclaim was asked for with no folder, which the command line refuses.");

        // The command line is the one place the per-volume holding default is computed. The engine
        // takes the root as a parameter and holds no default of its own, which is what lets every
        // test keep its holding inside a fixture tree it built, so no test ever creates a folder at
        // the root of a real volume.
        var holdingRoot = request.HoldingRootPath ?? DefaultHoldingRootFor(folder);

        var result = ReclaimRunner.Run(new ReclaimRunRequest
        {
            Rules = MachineRules.ForThisMachine(),
            RootPath = Path.GetFullPath(folder),
            HoldingRootPath = holdingRoot,
            ProtectedPaths = CcStorage.ProtectedPaths(),
            UserFolders = TheUsersOwnFolders(),
            Apply = request.Apply,
            NowUtc = DateTimeOffset.UtcNow,
            RuleId = request.RuleId
        });

        // A rule id that is not among the rules that look inside the folder asked about is an error
        // naming the rules that do, never a quiet empty answer.
        if (result.RuleFilterError is not null)
        {
            return Failure(request.CommandWord, "no-such-rule-here", result.RuleFilterError, ExitCodes.Usage);
        }

        if (result.BrokenReason is not null)
        {
            var brokenLines = new List<string>(result.Lines);
            brokenLines.AddRange(AxiOutput.Help([$"cc-cleanup-storage recommend \"{folder}\" --json"]));
            return new Answer(ExitCodes.Failed, brokenLines, ToReclaimJson(result));
        }

        var lines = new List<string>(result.Lines) { string.Empty };
        lines.AddRange(result.Apply
            ? AxiOutput.Help(
            [
                $"cc-cleanup-storage holding list \"{folder}\"",
                $"cc-cleanup-storage holding purge --holding-root \"{result.HoldingRootPath}\"",
                "cc-cleanup-storage --help"
            ])
            : AxiOutput.Help(
            [
                $"cc-cleanup-storage reclaim \"{folder}\" --apply",
                "cc-cleanup-storage --help"
            ]));

        return new Answer(ExitCodes.Ok, lines, ToReclaimJson(result));
    }

    /// <summary>
    /// The per-volume default holding root: a folder named cc-reclaim-holding at the root of the
    /// volume the reclaimed folder sits on. Visible in a directory listing on purpose - a folder
    /// holding thirty days of the owner's disk is not something to hide.
    /// </summary>
    private static string DefaultHoldingRootFor(string folder)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(folder))
            ?? throw new InvalidOperationException($"The folder {folder} sits on no volume.");
        return Path.Combine(volumeRoot, "cc-reclaim-holding");
    }

    /// <summary>
    /// The user's own folders, read from the system's own resolver so any folder redirection the
    /// machine has is followed, plus the OneDrive folder when the machine has one. The absence of
    /// the OneDrive variable means no OneDrive folder is configured, not that the check is off.
    /// </summary>
    private static IReadOnlyList<string> TheUsersOwnFolders()
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive)) folders.Add(oneDrive);

        return folders.Where(folder => !string.IsNullOrWhiteSpace(folder)).ToList();
    }

    internal static ReclaimJson ToReclaimJson(ReclaimRunResult result) => new()
    {
        Command = "reclaim",
        Ok = result.BrokenReason is null,
        Apply = result.Apply,
        RootPath = result.RootPath,
        HoldingRootPath = result.HoldingRootPath,
        RuleId = result.RuleFilter,
        BrokenReason = result.BrokenReason,
        Rules = result.Findings.Select(ToRuleJson).ToList(),
        RulesNotRun = result.RulesNotRun
            .Select(rule => new RuleNotRunJson(rule.RuleId, rule.RuleName, rule.LooksIn))
            .ToList(),
        Items = result.Items.Select(item => new ReclaimItemJson
        {
            Path = item.Path,
            RuleId = item.RuleId,
            Proof = item.Proof.ToString(),
            RecommendedBytes = item.RecommendedBytes,
            Eligible = item.Gate.Eligible,
            FiredCheck = item.Gate.FiredCheck is null ? null : (int)item.Gate.FiredCheck,
            Reason = item.Gate.Reason,
            ConfigurationReason = item.Gate.ConfigurationReason,
            Checks = item.Gate.Checks.Select(check => new CheckResultJson(
                (int)check.Check,
                GateOutcome.WordsFor(check.Check),
                check.Outcome == CheckOutcome.Passed ? "passed"
                    : check.Outcome == CheckOutcome.Refused ? "refused"
                    : "not-reached",
                check.Reason)).ToList(),
            Moved = item.Moved,
            HoldingEntryId = item.HoldingEntryId,
            OwnersCommandRan = item.OwnersCommandRan,
            OwnersCommandExitCode = item.OwnersCommandExitCode,
            OutcomeReason = item.OutcomeReason
        }).ToList(),
        CandidateBytesBefore = result.CandidateBytesBefore,
        CandidateBytesAfter = result.CandidateBytesAfter,
        BytesMoved = result.BytesMoved,
        VolumeBefore = result.VolumeBefore,
        VolumeAfter = result.VolumeAfter,
        Lines = result.Lines
    };

    private static Answer HoldingList(Request request)
    {
        var folder = request.FolderPath
            ?? throw new InvalidOperationException("A holding list was asked for with no folder or volume, which the command line refuses.");

        var holdingRoot = request.HoldingRootPath ?? DefaultHoldingRootFor(folder);
        var listing = new HoldingStore(holdingRoot).List();

        // A holding root that exists but cannot be read is an error naming why, never an empty list.
        if (listing.UnreadableReason is not null)
        {
            return Failure(request.CommandWord, "holding-unreadable", listing.UnreadableReason, ExitCodes.Failed);
        }

        var lines = new List<string>
        {
            $"holding-root: {holdingRoot}",
            listing.RootExists
                ? $"count: {listing.Complete.Count.ToString(CultureInfo.InvariantCulture)}"
                : $"count: 0 (there is no holding root at {holdingRoot}, which is an honest empty answer)"
        };

        lines.AddRange(AxiOutput.List(
            "held",
            ["entry", "from", "bytes", "moved", "purgeable-from"],
            listing.Complete.Select(ToHoldingRow).ToList()));

        if (listing.Incomplete.Count > 0)
        {
            lines.AddRange(AxiOutput.List(
                "incomplete",
                ["entry"],
                listing.Incomplete.Select(entry => (IReadOnlyList<string>)
                [
                    AxiOutput.Value(entry.EntryId)
                ]).ToList()));
            lines.Add(
                "incomplete entries are never purged; restore resolves them, and a record that cannot " +
                "be read is a refusal, not an assumption");
        }

        var offers = new List<string>
        {
            $"cc-cleanup-storage holding restore <entry-id> --holding-root \"{holdingRoot}\"",
            $"cc-cleanup-storage holding purge --holding-root \"{holdingRoot}\"",
            "cc-cleanup-storage --help"
        };
        lines.AddRange(AxiOutput.Help(offers));

        return new Answer(ExitCodes.Ok, lines, ToHoldingListJson(holdingRoot, listing));
    }

    private static IReadOnlyList<string> ToHoldingRow(HoldingEntry entry) =>
    [
        AxiOutput.Value(entry.EntryId),
        AxiOutput.Value(entry.Record.OriginalPath),
        AxiOutput.Value(entry.Record.Bytes),
        AxiOutput.Value(Moment(entry.Record.MovedAtUtc)),
        AxiOutput.Value(Moment(entry.Record.PurgeNotBeforeUtc))
    ];

    private static Answer HoldingRestore(Request request)
    {
        var entryId = request.EntryId
            ?? throw new InvalidOperationException("A restore was asked for with no entry id, which the command line refuses.");
        var holdingRoot = request.HoldingRootPath
            ?? throw new InvalidOperationException("A restore was asked for with no holding folder, which the command line refuses.");

        var outcome = new HoldingStore(holdingRoot).Restore(entryId);

        var lines = new List<string>
        {
            $"entry: {outcome.EntryId}",
            outcome.OriginalPath is null ? "from: unknown, the record could not be read" : $"from: {outcome.OriginalPath}"
        };

        if (outcome.Restored)
        {
            lines.Add($"restored: yes, the item is back at {outcome.OriginalPath}");
        }
        else if (outcome.AlreadyHome)
        {
            lines.Add("restored: the move never happened and the item is already home; the entry is cleared");
        }
        else
        {
            lines.Add($"refused: {outcome.RefusalReason}");
        }

        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(holdingRoot)) ?? holdingRoot;
        lines.AddRange(AxiOutput.Help(
        [
            $"cc-cleanup-storage holding list \"{volumeRoot}\"",
            "cc-cleanup-storage --help"
        ]));

        return new Answer(
            outcome.Restored || outcome.AlreadyHome ? ExitCodes.Ok : ExitCodes.Failed,
            lines,
            new HoldingRestoreJson
            {
                Command = "holding-restore",
                Ok = outcome.Restored || outcome.AlreadyHome,
                EntryId = outcome.EntryId,
                OriginalPath = outcome.OriginalPath,
                Restored = outcome.Restored,
                AlreadyHome = outcome.AlreadyHome,
                RefusalReason = outcome.RefusalReason
            });
    }

    private static Answer HoldingPurge(Request request)
    {
        var holdingRoot = request.HoldingRootPath
            ?? throw new InvalidOperationException("A purge was asked for with no holding folder, which the command line refuses.");

        var outcome = new HoldingStore(holdingRoot).Purge(DateTimeOffset.UtcNow, request.Apply, request.Days);

        var lines = new List<string>
        {
            $"holding-root: {holdingRoot}",
            request.Apply
                ? "apply: yes, entries past their period are removed"
                : "apply: no - this is a dry run, nothing is removed"
        };

        lines.AddRange(AxiOutput.List(
            "purgeable",
            ["entry", "from", "bytes", "moved"],
            outcome.Purgeable.Select(entry => (IReadOnlyList<string>)
            [
                AxiOutput.Value(entry.EntryId),
                AxiOutput.Value(entry.Record.OriginalPath),
                AxiOutput.Value(entry.Record.Bytes),
                AxiOutput.Value(Moment(entry.Record.MovedAtUtc))
            ]).ToList()));

        if (outcome.NotYetPurgeable.Count > 0)
        {
            lines.Add(
                $"not-yet-purgeable: {outcome.NotYetPurgeable.Count.ToString(CultureInfo.InvariantCulture)} entries");
        }

        if (outcome.Incomplete.Count > 0)
        {
            lines.Add(
                $"incomplete: {outcome.Incomplete.Count.ToString(CultureInfo.InvariantCulture)} entries, " +
                "which a purge always refuses");
        }

        foreach (var kept in outcome.Kept)
            lines.Add($"kept: {kept.Entry.EntryId}, {kept.Reason}");

        if (outcome.Applied)
        {
            lines.Add(
                $"purged: {outcome.PurgedEntryIds.Count.ToString(CultureInfo.InvariantCulture)} entries removed, " +
                "and their space is freed");
        }

        lines.Add(
            "space: purging is the only step that frees space. Until it runs, a held item occupies " +
            "exactly what it always did");

        lines.AddRange(AxiOutput.Help(request.Apply
            ?
            [
                $"cc-cleanup-storage holding list \"{Path.GetPathRoot(Path.GetFullPath(holdingRoot)) ?? holdingRoot}\"",
                "cc-cleanup-storage --help"
            ]
            :
            [
                $"cc-cleanup-storage holding purge --holding-root \"{holdingRoot}\" --apply",
                "cc-cleanup-storage --help"
            ]));

        return new Answer(ExitCodes.Ok, lines, new HoldingPurgeJson
        {
            Command = "holding-purge",
            Ok = true,
            Applied = outcome.Applied,
            HoldingRootPath = holdingRoot,
            Purgeable = outcome.Purgeable.Select(ToHoldingEntryJson).ToList(),
            NotYetPurgeable = outcome.NotYetPurgeable.Select(ToHoldingEntryJson).ToList(),
            Incomplete = outcome.Incomplete.Select(ToHoldingEntryJson).ToList(),
            Kept = outcome.Kept.Select(kept => new KeptEntryJson(kept.Entry.EntryId, kept.Reason)).ToList(),
            PurgedCount = outcome.PurgedEntryIds.Count
        });
    }

    private static HoldingEntryJson ToHoldingEntryJson(HoldingEntry entry) => new(
        entry.EntryId,
        entry.Record.OriginalPath,
        entry.Record.Name,
        entry.Record.Bytes,
        entry.Record.Rule,
        entry.Record.MovedAtUtc,
        entry.Record.PurgeNotBeforeUtc,
        HoldingRecord.StateWords(entry.Record.State));

    private static HoldingListJson ToHoldingListJson(string holdingRoot, HoldingListing listing) => new()
    {
        Command = "holding-list",
        Ok = listing.UnreadableReason is null,
        RootExists = listing.RootExists,
        HoldingRootPath = holdingRoot,
        UnreadableReason = listing.UnreadableReason,
        Count = listing.Complete.Count,
        Entries = listing.Complete.Select(ToHoldingEntryJson).ToList(),
        Incomplete = listing.Incomplete.Select(ToHoldingEntryJson).ToList()
    };

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
                CommandLine.RecommendFlags),
            new(
                "reclaim",
                "reclaim",
                "cc-cleanup-storage reclaim \"<folder>\" [flags]",
                "report what would move into holding, or move it with --apply",
                CommandLine.ReclaimFlags),
            new(
                "holding-list",
                "holding list",
                "cc-cleanup-storage holding list \"<folder-or-volume>\" [flags]",
                "every entry in one volume's holding folder",
                CommandLine.HoldingListFlags),
            new(
                "holding-restore",
                "holding restore",
                "cc-cleanup-storage holding restore <entry-id> --holding-root \"<folder>\" [flags]",
                "put one held item back where it came from",
                CommandLine.HoldingRestoreFlags),
            new(
                "holding-purge",
                "holding purge",
                "cc-cleanup-storage holding purge --holding-root \"<folder>\" [flags]",
                "remove the holding entries whose period has passed",
                CommandLine.HoldingPurgeFlags)
        };

        var flags = new List<HelpFlagJson>
        {
            new("--json", "answer as machine-readable text, with every field"),
            new("--index-directory <folder>", "where saved scans live"),
            new("--top <number>", "how many of the largest folders to name, 1 to 1000"),
            new("--folder-depth <number>", "how deep a scan records folder totals, 1 to 10 (scan only)"),
            new("--rule <id>", "narrow a reclaim to one rule (reclaim only)"),
            new("--apply", "move what every refusal check passes (reclaim, and holding purge)"),
            new("--holding-root <folder>", "the holding folder (holding restore and purge)"),
            new("--days <number>", "a holding period for this purge call only, 0 to 36500"),
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
            "what a reclaim does:",
            "  removal is a move into a holding folder on the same volume, never a deletion. A dry",
            "  run is the default and runs every refusal check for real, so --apply moves exactly",
            "  what the dry run said it would. Ten refusals are checked for every item, and each",
            "  one has a numbered test. A move to holding frees no space; space is freed only when",
            "  holding is purged, which is its own explicit command.",
            "",
            "what this tool does not do:",
            "  it never deletes outright - a move into holding can be restored until it is purged.",
            "  It never raises itself to administrator, never removes anything unattended or on a",
            "  schedule, and never touches what it cannot positively classify. An item cleared by",
            "  its owner's own cleanup command is not held and has no way back; the rule said so."
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
