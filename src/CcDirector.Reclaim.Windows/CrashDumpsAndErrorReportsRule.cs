using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// Crash dumps and Windows error reports: the system's own records of crashes that already
/// happened.
///
/// This is the first proof kind. For each kind of thing here the system keeps its own record that
/// the event is finished:
///
/// a crash dump is written by Windows Error Reporting at the moment a program stops working, and
/// it records that one crash and nothing else. Windows itself replaces older dumps as new crashes
/// arrive, so the system's own policy treats a dump here as a thing it is willing to lose at any
/// moment, and a dump nobody has come back for past the age gate is a record of a crash that is
/// over. Nothing else reads a finished dump.
///
/// a report in Windows Error Reporting's ARCHIVE is a report Windows has already sent: the archive
/// is Windows' own record of that. It keeps only its own local copy, and nothing needs a local
/// copy of a report that has been delivered. A report still in the QUEUE is the opposite case - it
/// is still waiting to be sent - so it is counted and never offered, because deleting it would
/// mean the error it describes never reaches anybody.
///
/// The machine's own dumps in C:\Windows and its memory dump file need an administrator, and the
/// candidates say so one by one. On a machine where this tool is not running as an administrator
/// those folders usually refuse a listing; a place that would not be listed is declined and
/// counted, not offered on an incomplete reading.
/// </summary>
public sealed class CrashDumpsAndErrorReportsRule : IReclaimRule
{
    /// <summary>How old a crash dump or an archived report must be before this rule will offer it.</summary>
    public const int DefaultAgeGateDays = 30;

    private static readonly string[] DumpExtension = [".dmp"];

    private readonly string _crashDumpsFolderPath;
    private readonly string _machineDumpsFolderPath;
    private readonly string _machineMemoryDumpFilePath;
    private readonly string _machineReportsFolderPath;
    private readonly string _userReportsFolderPath;

    /// <summary>
    /// Build the rule.
    /// </summary>
    /// <param name="crashDumpsFolderPath">
    /// The account's own crash dump folder, normally the CrashDumps folder in the account's local
    /// application data, which is where Windows Error Reporting writes them by default.
    /// </param>
    /// <param name="machineDumpsFolderPath">The machine's minidump folder, normally C:\Windows\Minidump.</param>
    /// <param name="machineMemoryDumpFilePath">The machine's full memory dump, normally C:\Windows\MEMORY.DMP.</param>
    /// <param name="machineReportsFolderPath">
    /// The machine's Windows Error Reporting folder, whose ReportArchive and ReportQueue
    /// subfolders are read.
    /// </param>
    /// <param name="userReportsFolderPath">
    /// The account's own Windows Error Reporting folder, whose ReportArchive and ReportQueue
    /// subfolders are read.
    /// </param>
    /// <param name="ageGateDays">How old a dump or a sent report must be before it is offered. Thirty days by default.</param>
    public CrashDumpsAndErrorReportsRule(
        string crashDumpsFolderPath,
        string machineDumpsFolderPath,
        string machineMemoryDumpFilePath,
        string machineReportsFolderPath,
        string userReportsFolderPath,
        int ageGateDays = DefaultAgeGateDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crashDumpsFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineDumpsFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineMemoryDumpFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineReportsFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userReportsFolderPath);
        ArgumentOutOfRangeException.ThrowIfNegative(ageGateDays);

        _crashDumpsFolderPath = crashDumpsFolderPath;
        _machineDumpsFolderPath = machineDumpsFolderPath;
        _machineMemoryDumpFilePath = machineMemoryDumpFilePath;
        _machineReportsFolderPath = machineReportsFolderPath;
        _userReportsFolderPath = userReportsFolderPath;
        AgeGateDays = ageGateDays;
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => "windows-crash-dumps-and-error-reports";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => "Crash dumps and Windows error reports";

    /// <summary>A record the system keeps says nothing needs it.</summary>
    public ProofKind Proof => ProofKind.SystemRecord;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"crash dumps Windows Error Reporting wrote when programs stopped working, in " +
        $"{_crashDumpsFolderPath} and {_machineDumpsFolderPath}, and the local copies of error reports " +
        "it has already sent, in the ReportArchive folders of its machine and account report stores";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "each is the system's own record of a crash that has already finished: a dump was written at the " +
        "moment the program stopped working and Windows replaces older ones as new crashes arrive, and a " +
        "report in Windows Error Reporting's archive is one it has already sent and keeps only its own " +
        "local copy of. A report still waiting to be sent is counted and never offered";

    /// <summary>What is lost.</summary>
    public string WhatIsLost =>
        "the ability to look back at a crash that already happened. Nothing else reads a finished dump, " +
        "a sent report is already delivered, and Windows writes a new one the next time a program stops working";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "there is no way back for any of them: nothing is held, the crashes they record are over, and " +
        "only the machine-wide dumps in the Windows folder could ever be looked at again, by somebody " +
        "debugging a crash nobody came back for";

    /// <summary>How old a dump or a sent report must be before this rule will offer it.</summary>
    public int AgeGateDays { get; }

    /// <summary>
    /// True, because the machine's own dumps in the Windows folder need one, and those candidates
    /// say so one by one. The account's own do not.
    /// </summary>
    public bool NeedsAdministrator => true;

    /// <summary>There is no command to print: this rule's items are records, moved by the tool that owns removal.</summary>
    public string? CommandToRun => null;

    /// <summary>The account's own crash dump folder, which is this rule's primary place.</summary>
    public string LooksIn => _crashDumpsFolderPath;

    /// <summary>Look, count, and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about, and the moment the age gate is judged against.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[CrashDumpsAndErrorReportsRule] Examine: dumps={_crashDumpsFolderPath}");

        var places = new List<string>
        {
            _crashDumpsFolderPath,
            _machineDumpsFolderPath,
            _machineMemoryDumpFilePath,
            Path.Combine(_machineReportsFolderPath, "ReportArchive"),
            Path.Combine(_machineReportsFolderPath, "ReportQueue"),
            Path.Combine(_userReportsFolderPath, "ReportArchive"),
            Path.Combine(_userReportsFolderPath, "ReportQueue")
        };

        var oldEnough = context.NowUtc.AddDays(-AgeGateDays);
        var candidates = new List<ReclaimCandidate>();
        var placesFound = 0;
        var placesOutsideTheFolderAskedAbout = 0;
        var placesThatWouldNotBeListed = 0;
        var dumpsFound = 0;
        var dumpsTooYoung = 0;
        var reportsAlreadySent = 0;
        var reportsStillWaiting = 0;
        var storesTooYoung = 0;
        var storesThatWouldNotBeRead = 0;

        foreach (var place in places)
        {
            // A place outside the folder that was asked about is not examined at all, and is
            // counted rather than passed over in silence: a tool that quietly looked at fewer
            // places than it has would report less to remove and read exactly like a cleaner disk.
            if (!RuleSelection.IsInside(place, context.ScanRootPath))
            {
                placesOutsideTheFolderAskedAbout++;
                continue;
            }

            if (!File.Exists(place) && !Directory.Exists(place)) continue;
            placesFound++;

            if (place == _machineMemoryDumpFilePath)
            {
                dumpsFound++;
                if (OfferOrDecline(new FileInfo(place), oldEnough, needsAdministrator: true,
                        "the machine's full memory dump, a record of one crash that already finished",
                        candidates))
                    continue;
                dumpsTooYoung++;
                continue;
            }

            if (place.EndsWith("ReportArchive", StringComparison.OrdinalIgnoreCase))
            {
                MeasureStore(place, oldEnough, candidates, needsAdministrator: false,
                    ref storesTooYoung, ref storesThatWouldNotBeRead, ref reportsAlreadySent);
                continue;
            }

            if (place.EndsWith("ReportQueue", StringComparison.OrdinalIgnoreCase))
            {
                var queued = ReportFoldersIn(place);
                if (queued.Unreadable)
                {
                    placesThatWouldNotBeListed++;
                    continue;
                }

                reportsStillWaiting += queued.ReportFolders;
                continue;
            }

            // The dump folders.
            var listed = ListDumpFolder(place);
            if (listed.Unreadable)
            {
                placesThatWouldNotBeListed++;
                continue;
            }

            foreach (var dump in listed.Dumps)
            {
                dumpsFound++;
                if (OfferOrDecline(dump, oldEnough, needsAdministrator: place == _machineDumpsFolderPath,
                        "a crash dump Windows Error Reporting wrote when a program stopped working, a record of one crash that already finished",
                        candidates))
                    continue;
                dumpsTooYoung++;
            }
        }

        var controls = new List<RuleControl>
        {
            // The places this rule knows about. Nought here is a rule constructed with no places at
            // all, which is a defect rather than an answer, and the fold refuses it.
            new("places-looked-for", places.Count, MustNotBeEmpty: true),

            // The places that were actually there. Windows Error Reporting maintains these folders
            // itself on every Windows machine, so a machine where not one of them exists is a
            // machine this rule can say nothing about, and that must not read as a machine with
            // nothing to remove.
            new("places-found", placesFound, MustNotBeEmpty: true),
            new("places-outside-the-folder-asked-about", placesOutsideTheFolderAskedAbout, MustNotBeEmpty: false),
            new("places-that-would-not-be-listed", placesThatWouldNotBeListed, MustNotBeEmpty: false),
            new("crash-dumps-found", dumpsFound, MustNotBeEmpty: false),
            new("crash-dumps-too-young-to-offer", dumpsTooYoung, MustNotBeEmpty: false),
            new("wer-reports-already-sent-and-archived", reportsAlreadySent, MustNotBeEmpty: false),
            new("wer-reports-still-waiting-to-be-sent", reportsStillWaiting, MustNotBeEmpty: false),
            new("wer-stores-too-young-to-offer", storesTooYoung, MustNotBeEmpty: false),
            new("wer-stores-that-would-not-be-read", storesThatWouldNotBeRead, MustNotBeEmpty: false)
        };

        FileLog.Write(
            $"[CrashDumpsAndErrorReportsRule] Examine done: found={placesFound}, outside={placesOutsideTheFolderAskedAbout}, " +
            $"refused={placesThatWouldNotBeListed}, dumps={dumpsFound}, tooYoung={dumpsTooYoung}, " +
            $"sent={reportsAlreadySent}, waiting={reportsStillWaiting}, storesTooYoung={storesTooYoung}");
        return new RuleAnswer(controls, candidates);
    }

    // Offer one dump file, or decline it as too young. True when it was offered.
    private static bool OfferOrDecline(
        FileInfo dump,
        DateTimeOffset oldEnough,
        bool needsAdministrator,
        string whatItRecords,
        List<ReclaimCandidate> candidates)
    {
        var written = new DateTimeOffset(dump.LastWriteTimeUtc, TimeSpan.Zero);
        if (written > oldEnough) return false;

        candidates.Add(new ReclaimCandidate(
            dump.FullName,
            dump.Length,
            written,
            $"{whatItRecords}, older than the age gate, and nothing has asked for it since" +
            (needsAdministrator ? "; removing it needs an administrator" : string.Empty)));
        return true;
    }

    private sealed record ListedDumpFolder(List<FileInfo> Dumps, bool Unreadable);

    private static ListedDumpFolder ListDumpFolder(string folder)
    {
        try
        {
            var dumps = new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => DumpExtension.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return new ListedDumpFolder(dumps, Unreadable: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ListedDumpFolder([], Unreadable: true);
        }
        catch (IOException)
        {
            return new ListedDumpFolder([], Unreadable: true);
        }
    }

    private sealed record ListedQueue(int ReportFolders, bool Unreadable);

    private static ListedQueue ReportFoldersIn(string folder)
    {
        try
        {
            var count = new DirectoryInfo(folder)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Count();
            return new ListedQueue(count, Unreadable: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ListedQueue(0, Unreadable: true);
        }
        catch (IOException)
        {
            return new ListedQueue(0, Unreadable: true);
        }
    }

    // Measure one archive store and offer it whole, as one thing, because a report folder on its
    // own is a record the owner never acts on one at a time. The age gate is judged on the newest
    // write anywhere inside it, because a store that received a report yesterday is one Windows is
    // still actively writing to.
    private static void MeasureStore(
        string storePath,
        DateTimeOffset oldEnough,
        List<ReclaimCandidate> candidates,
        bool needsAdministrator,
        ref int storesTooYoung,
        ref int storesThatWouldNotBeRead,
        ref int reportsAlreadySent)
    {
        try
        {
            var reportFolders = new DirectoryInfo(storePath)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Count();
            var measured = FolderMeasurer.Measure(storePath);

            if (measured.UnreadableFolders > 0)
            {
                storesThatWouldNotBeRead++;
                return;
            }

            if (measured.NewestWriteUtc > oldEnough)
            {
                storesTooYoung++;
                return;
            }

            reportsAlreadySent += reportFolders;

            if (measured.Files == 0) return;

            candidates.Add(new ReclaimCandidate(
                storePath,
                measured.Bytes,
                measured.NewestWriteUtc,
                $"Windows Error Reporting has already sent each of the {reportFolders.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                "reports this folder holds and keeps only this local copy of them, and the whole archive is " +
                "older than the age gate" +
                (needsAdministrator ? "; removing it needs an administrator" : string.Empty)));
        }
        catch (UnauthorizedAccessException)
        {
            storesThatWouldNotBeRead++;
        }
        catch (IOException)
        {
            storesThatWouldNotBeRead++;
        }
    }
}
