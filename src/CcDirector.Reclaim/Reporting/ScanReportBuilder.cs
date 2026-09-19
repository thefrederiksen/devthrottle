using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Reporting;

/// <summary>
/// Turns what a scan saw into the report a person or an agent reads.
///
/// Two things here are required output and not diagnostics.
///
/// The UNSEEN GAP. Every report states the bytes the scan saw against the bytes the volume counts as
/// used, and shows the difference as a number, with the folders that refused their listing beside it.
/// A disk scan that is not elevated cannot see everything - about ninety-seven gigabytes of the
/// machine this was designed for is invisible without an administrator - and a report that printed
/// only what it saw would read as a complete answer. It is not one, and it says so on its own line.
///
/// The BROKEN VERDICT. A scan that saw nothing reports BROKEN. It never reports that there is nothing
/// there, because those two are the same output from two opposite situations, and one of them is a
/// measurement and the other is a fault.
/// </summary>
public static class ScanReportBuilder
{
    /// <summary>How many folders a report names by default when it lists the largest.</summary>
    public const int DefaultLargestFolders = 20;

    /// <summary>The smallest number of folders a report may be asked to name.</summary>
    public const int MinimumLargestFolders = 1;

    /// <summary>The largest number of folders a report may be asked to name.</summary>
    public const int MaximumLargestFolders = 1000;

    /// <summary>
    /// Build the report for one scan.
    /// </summary>
    /// <param name="scan">What the scan saw.</param>
    /// <param name="largestFolders">How many of the largest folders to name.</param>
    public static ScanReport Build(ScanResult scan, int largestFolders = DefaultLargestFolders)
    {
        ArgumentNullException.ThrowIfNull(scan);
        FileLog.Write($"[ScanReportBuilder] Build: root={scan.RootPath}, largestFolders={largestFolders}");

        if (largestFolders < MinimumLargestFolders || largestFolders > MaximumLargestFolders)
        {
            throw new ArgumentOutOfRangeException(
                nameof(largestFolders),
                largestFolders,
                $"The number of folders to name must be between {MinimumLargestFolders} and {MaximumLargestFolders}.");
        }

        var unseen = scan.Volume.Available ? scan.Volume.UsedBytes - scan.BytesSeen : (long?)null;
        var brokenReason = FindBrokenReason(scan);
        var verdict = brokenReason is null ? ReportVerdict.Ok : ReportVerdict.Broken;

        var largest = scan.FolderTotals
            .OrderByDescending(folder => folder.BytesSeen)
            .ThenBy(folder => folder.Path, StringComparer.Ordinal)
            .Take(largestFolders)
            .ToList();

        var reach = WriteReachLines(scan, unseen);
        var lines = WriteLines(scan, verdict, brokenReason, reach, largest);

        FileLog.Write(
            $"[ScanReportBuilder] Build done: root={scan.RootPath}, verdict={verdict}, " +
            $"unseen={(unseen.HasValue ? unseen.Value.ToString(CultureInfo.InvariantCulture) : "unknown")}, " +
            $"lines={lines.Count}");

        return new ScanReport
        {
            Verdict = verdict,
            BrokenReason = brokenReason,
            Scan = scan,
            UnseenBytes = unseen,
            LargestFolders = largest,
            Lines = lines,
            ReachLines = reach
        };
    }

    /// <summary>
    /// The plain words for why a folder would not be listed. The operating system's own message is
    /// never used: it is written in the machine's display language, and every line printed here is
    /// plain ASCII in plain English.
    /// </summary>
    /// <param name="refusal">Why the listing was refused.</param>
    public static string RefusalWords(DirectoryReadRefusal refusal) => refusal switch
    {
        DirectoryReadRefusal.AccessDenied => "access denied",
        DirectoryReadRefusal.NotFound => "not found",
        DirectoryReadRefusal.ReadFailed => "read failed",
        DirectoryReadRefusal.None => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "A folder that was listed has no refusal to put into words."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "There are no words for this refusal.")
    };

    // The instrument check. Order matters: the emptiness check comes first because it is the one the
    // mission names, and a volume that will not answer is checked next because it takes away the
    // second side of the unseen-gap line, which every report is required to carry.
    private static string? FindBrokenReason(ScanResult scan)
    {
        if (scan.FilesSeen == 0 && scan.FoldersSeen == 0)
        {
            return $"the scan saw no files and no folders under {scan.RootPath}, " +
                   "and an empty result is a broken instrument until it is proven otherwise";
        }

        if (scan.BytesSeen == 0)
        {
            return $"the scan saw {Count(scan.FilesSeen, "file", "files")} and " +
                   $"{Count(scan.FoldersSeen, "folder", "folders")} under {scan.RootPath} and not one byte, " +
                   "and a zero result is a broken instrument until it is proven otherwise";
        }

        if (!scan.Volume.Available)
        {
            return $"volume {scan.Volume.Name} would not say how much of it is used, so this report " +
                   "cannot say how much of it the scan did not see: " +
                   (scan.Volume.UnavailableReason ?? "no reason was given");
        }

        return null;
    }

    // How far the scan reached: what the volume holds, what the scan saw, the difference between
    // them, and the folders that refused a listing. Written once, here, because the report prints
    // these lines and the recommendations built on the report have to repeat them. Two renderings of
    // the same numbers would eventually disagree, and the reader would have no way to tell which was
    // the honest one.
    private static IReadOnlyList<string> WriteReachLines(ScanResult scan, long? unseen)
    {
        var lines = new List<string>
        {
            scan.Volume.Available
                ? $"volume: {scan.Volume.Name} is {SizeText.Exact(scan.Volume.TotalBytes)}, of which " +
                  $"{SizeText.Exact(scan.Volume.UsedBytes)} are used and " +
                  $"{SizeText.Exact(scan.Volume.FreeBytes)} are free"
                : $"volume: {scan.Volume.Name} would not say how big it is or how much of it is used, because " +
                  $"{scan.Volume.UnavailableReason ?? "no reason was given"}",

            $"seen: {SizeText.Exact(scan.BytesSeen)} in " +
            $"{Count(scan.FilesSeen, "file", "files")} and " +
            $"{Count(scan.FoldersSeen, "folder", "folders")}",

            unseen.HasValue
                ? $"unseen: {SizeText.Exact(unseen.Value)} that the volume counts as used and this scan did not see"
                : "unseen: unknown, because the volume would not say how much of it is used"
        };

        if (!scan.RootIsVolumeRoot)
        {
            lines.Add($"scope: the scan covered {scan.RootPath}, which is one folder on volume " +
                      $"{scan.Volume.Name}, so the unseen number also counts everything on that volume " +
                      "outside this folder");
        }

        lines.Add(scan.RefusedFolders.Count == 0
            ? "refused: no folder refused its listing"
            : $"refused: {Count(scan.RefusedFolders.Count, "folder", "folders")} refused a listing, " +
              "and every one of them is named below");

        return lines;
    }

    private static IReadOnlyList<string> WriteLines(
        ScanResult scan,
        ReportVerdict verdict,
        string? brokenReason,
        IReadOnlyList<string> reach,
        IReadOnlyList<FolderTotal> largest)
    {
        var lines = new List<string>
        {
            $"verdict: {(verdict == ReportVerdict.Ok ? "ok" : "broken")}"
        };

        if (brokenReason is not null)
        {
            lines.Add($"reason: {brokenReason}");
            lines.Add($"next: check that {scan.RootPath} is the folder you meant and that this account " +
                      $"can read it, then run: cc-cleanup-storage scan \"{scan.RootPath}\"");
        }

        lines.Add($"root: {scan.RootPath}");
        lines.AddRange(reach);

        lines.Add(scan.Links.Count == 0
            ? "links: no link or junction was found"
            : $"links: {Count(scan.Links.Count, "link or junction was", "links and junctions were")} found, " +
              "and none were followed");

        lines.Add(scan.PlaceholderFiles == 0
            ? "placeholders: no cloud placeholder file was found"
            : $"placeholders: {Count(scan.PlaceholderFiles, "cloud placeholder file holds", "cloud placeholder files hold")} " +
              $"{SizeText.Exact(scan.PlaceholderBytesInCloud)} in a cloud store and no bytes on this disk");

        lines.Add($"walked: started {Moment(scan.StartedUtc)}, finished {Moment(scan.FinishedUtc)}, " +
                  $"{scan.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture)} seconds");

        lines.AddRange(AxiOutput.List(
            "largest-folders",
            ["path", "bytes", "size", "files"],
            largest.Select(folder => (IReadOnlyList<string>)
            [
                AxiOutput.Value(folder.Path),
                AxiOutput.Value(folder.BytesSeen),
                AxiOutput.Value(SizeText.Describe(folder.BytesSeen)),
                AxiOutput.Value(folder.FilesSeen)
            ]).ToList()));

        lines.AddRange(AxiOutput.List(
            "refused-folders",
            ["path", "reason", "code"],
            scan.RefusedFolders.Select(folder => (IReadOnlyList<string>)
            [
                AxiOutput.Value(folder.Path),
                AxiOutput.Value(RefusalWords(folder.Refusal)),
                AxiOutput.Value(folder.RefusalCode)
            ]).ToList()));

        return lines;
    }

    private static string Count(long many, string one, string more) =>
        $"{many.ToString(CultureInfo.InvariantCulture)} {(many == 1 ? one : more)}";

    private static string Moment(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
