using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// Free and total space on one FIXED drive - a Windows drive letter ("C:"), or a Linux or macOS mount point ("/").
///
/// THE OWNER BROUGHT FREE SPACE BACK ON 7 NOVEMBER 2026. On that afternoon the C: drive on SOREN_NORTH went from
/// 8 GB free to nothing in a few hours and nothing in DevThrottle showed it coming. Only free and total space came
/// back; the disk activity figures that went out on 6 November stay out (see <see cref="ResourceSample"/>).
///
/// A drive that cannot be read says so - <see cref="CouldRead"/> false and a reason - and its byte counts are zero
/// and mean nothing. It is never drawn as "0 GB free", which would read as the emergency it is not.
/// </summary>
/// <param name="Name">The drive as the owner knows it: "C:" on Windows, the mount point ("/", "/home") elsewhere.</param>
/// <param name="CouldRead">False when the drive's space could not be read.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="TotalBytes">The drive's size. Zero when unreadable.</param>
/// <param name="FreeBytes">
/// Space a program running as this user can still write - the figure that runs out first and the one Claude Code hit
/// when it could not save its settings. Zero when unreadable.
/// </param>
public readonly record struct DriveSpaceReading(
    string Name,
    bool CouldRead,
    string? UnreadableReason,
    ulong TotalBytes,
    ulong FreeBytes)
{
    /// <summary>A drive that was read.</summary>
    public static DriveSpaceReading Read(string name, ulong totalBytes, ulong freeBytes) =>
        new(name, CouldRead: true, UnreadableReason: null, totalBytes, freeBytes);

    /// <summary>A drive that could not be read, carrying why.</summary>
    public static DriveSpaceReading Unreadable(string name, string reason) =>
        new(name, CouldRead: false, ResourceReadingGuard.Reason(reason), 0, 0);

    /// <summary>Short, log-safe description. Prints no figure when the read failed.</summary>
    public override string ToString() =>
        CouldRead
            ? $"drive {Name} {ResourceViewText.Bytes(FreeBytes)} free of {ResourceViewText.Bytes(TotalBytes)}"
            : $"drive {Name} unreadable ({UnreadableReason})";
}

/// <summary>
/// Every fixed drive on the machine at one moment. Network and removable drives are never read: a read of either can
/// hang for as long as the network or the device takes to answer, and this is read on the five-second timer.
///
/// It fails at two levels, the same way <see cref="DiskEvidenceSet"/> does: the whole set fails when the drives
/// cannot even be listed, and one drive fails on its own while the others read fine.
/// </summary>
/// <param name="CouldRead">False when the drives could not be listed at all.</param>
/// <param name="UnreadableReason">Why the listing failed. Null when it succeeded.</param>
/// <param name="Drives">Each fixed drive, in the order the operating system lists them. Empty when the listing failed.</param>
public sealed record DiskSpaceSet(
    bool CouldRead,
    string? UnreadableReason,
    IReadOnlyList<DriveSpaceReading> Drives)
{
    /// <summary>A listing that succeeded.</summary>
    public static DiskSpaceSet Read(IReadOnlyList<DriveSpaceReading> drives) =>
        new(CouldRead: true, UnreadableReason: null, drives);

    /// <summary>A listing that failed, carrying why. There are no drives in it.</summary>
    public static DiskSpaceSet Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), Array.Empty<DriveSpaceReading>());

    /// <summary>Short, log-safe description.</summary>
    public override string ToString() =>
        CouldRead
            ? Drives.Count == 0 ? "no fixed drives" : string.Join("; ", Drives)
            : $"drives unreadable ({UnreadableReason})";
}

/// <summary>
/// THE ONE PLACE A DRIVE'S FREE SPACE BECOMES A LEVEL (the owner's answer of 7 November 2026): amber under 25 GB
/// free, red under 10 GB free, on ANY drive, whatever its size.
///
/// The lines are in gigabytes, not a share of the drive, because what runs out is gigabytes: a build, a test run or a
/// video can take 8 GB in an afternoon. A share would show D: (325 GB free of 3.8 TB, 8.5%) amber while it has room
/// for months, and C: at 6 GB free on a small drive green.
///
/// No sixty-second hold, unlike the processor and memory: free space moves over hours, not seconds, and a drive that
/// has just crossed 10 GB is exactly as short of room as one that crossed an hour ago.
///
/// A pure function, so every line is pinned by a unit test. The Gateway applies the same lines to the uploaded
/// figures through <see cref="CcDirector.Gateway.Contracts.ResourceSummaryContract"/>, which holds the numbers both
/// sides read.
/// </summary>
public static class DiskSpacePressure
{
    /// <summary>A drive with less than this free is amber: 25 GB.</summary>
    public const ulong TightBelowBytes = CcDirector.Gateway.Contracts.ResourceSummaryContract.DiskAmberBelowBytes;

    /// <summary>A drive with less than this free is red: 10 GB.</summary>
    public const ulong CriticalBelowBytes = CcDirector.Gateway.Contracts.ResourceSummaryContract.DiskRedBelowBytes;

    /// <summary>The level of one drive. Unknown when it could not be read - never Normal.</summary>
    public static ResourceLevel Level(DriveSpaceReading drive)
    {
        if (!drive.CouldRead) return ResourceLevel.Unknown;
        return LevelOfFree(drive.FreeBytes);
    }

    /// <summary>The level of a free space figure that WAS read: red under 10 GB, amber under 25 GB, normal otherwise.</summary>
    public static ResourceLevel LevelOfFree(ulong freeBytes)
    {
        if (freeBytes < CriticalBelowBytes) return ResourceLevel.Critical;
        if (freeBytes < TightBelowBytes) return ResourceLevel.Tight;
        return ResourceLevel.Normal;
    }

    /// <summary>
    /// The drive with the least room left - the one the toolbar bar shows. Only readable drives count; null when none
    /// could be read. Ties keep the operating system's order, so the same reading always picks the same drive.
    /// </summary>
    public static DriveSpaceReading? Tightest(DiskSpaceSet disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        DriveSpaceReading? tightest = null;
        foreach (var drive in disks.Drives)
        {
            if (!drive.CouldRead) continue;
            if (tightest is null || drive.FreeBytes < tightest.Value.FreeBytes) tightest = drive;
        }
        return tightest;
    }

    /// <summary>The worst level among the readable drives; Unknown only when no drive could be read.</summary>
    public static ResourceLevel Worst(DiskSpaceSet disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var worst = ResourceLevel.Unknown;
        foreach (var drive in disks.Drives)
        {
            var level = Level(drive);
            if (level > worst) worst = level;
        }
        return worst;
    }

    /// <summary>"red, under 10 GB" / "amber, under 25 GB" / "normal" - the words after a drive's figures.</summary>
    public static string LevelWords(ResourceLevel level) => level switch
    {
        ResourceLevel.Critical => "red, under 10 GB",
        ResourceLevel.Tight => "amber, under 25 GB",
        ResourceLevel.Normal => "normal",
        _ => "cannot read",
    };
}

/// <summary>The words every disk space view shares, so the bar, the tooltip and the detail tiles cannot disagree.</summary>
public static class DiskSpaceText
{
    private const double Gigabyte = 1024.0 * 1024 * 1024;

    /// <summary>
    /// How much a drive must have lost or gained before the trend says so: 1 GB. Below it, a drive is "steady" - log
    /// files and caches move a few hundred megabytes an hour on any working machine, and calling that a loss would
    /// put a warning-shaped sentence on every healthy drive.
    /// </summary>
    public const ulong TrendThresholdBytes = 1024UL * 1024 * 1024;

    /// <summary>
    /// The bar's short figure, the way the approved mock-up writes it: "C: 6 GB", "C: 260 GB" - whole gigabytes, and
    /// megabytes below one gigabyte so a nearly full drive never reads "0 GB" while space is still left.
    /// </summary>
    public static string Short(DriveSpaceReading drive)
    {
        if (!drive.CouldRead) return $"{drive.Name} {ResourceViewText.CannotReadText}";
        var free = drive.FreeBytes;
        var text = free < Gigabyte
            ? string.Create(CultureInfo.InvariantCulture, $"{free / (1024.0 * 1024):F0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(free / Gigabyte):F0} GB");
        return $"{drive.Name} {text}";
    }

    /// <summary>"C: 6.40 GB free of 931 GB - red, under 10 GB", or "C: cannot read: the reason".</summary>
    public static string Line(DriveSpaceReading drive) =>
        drive.CouldRead
            ? $"{drive.Name} {ResourceViewText.Bytes(drive.FreeBytes)} free of {ResourceViewText.Bytes(drive.TotalBytes)} - {DiskSpacePressure.LevelWords(DiskSpacePressure.Level(drive))}"
            : $"{drive.Name} {ResourceViewText.CannotRead(drive.UnreadableReason)}";

    /// <summary>The share of the drive in USE, 0 to 1 - how full the bar is. Null when unreadable or not believable.</summary>
    public static double? UsedFraction(DriveSpaceReading drive)
    {
        if (!drive.CouldRead || drive.TotalBytes == 0 || drive.FreeBytes > drive.TotalBytes) return null;
        return 1.0 - (double)drive.FreeBytes / drive.TotalBytes;
    }

    /// <summary>
    /// How one drive's free space moved across the history the caller holds: "lost 8 GB in the last 3 hours",
    /// "gained 2 GB in the last 40 minutes", or "steady". Null when there is no earlier readable figure for this drive
    /// to compare with - a trend is never invented from one reading.
    /// </summary>
    /// <param name="driveName">The drive, matched by name.</param>
    /// <param name="history">Samples oldest first, each with its place on the monotonic timeline.</param>
    public static string? Trend(string driveName, IReadOnlyList<(TimeSpan At, DiskSpaceSet Disks)> history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);
        ArgumentNullException.ThrowIfNull(history);

        (TimeSpan At, ulong Free)? oldest = null;
        (TimeSpan At, ulong Free)? newest = null;
        foreach (var (at, disks) in history)
        {
            foreach (var drive in disks.Drives)
            {
                if (!drive.CouldRead || !string.Equals(drive.Name, driveName, StringComparison.Ordinal)) continue;
                oldest ??= (at, drive.FreeBytes);
                newest = (at, drive.FreeBytes);
            }
        }

        if (oldest is not { } first || newest is not { } last || last.At <= first.At) return null;
        var span = Span(last.At - first.At);
        if (first.Free > last.Free && first.Free - last.Free >= TrendThresholdBytes)
            return $"lost {ResourceViewText.Bytes(first.Free - last.Free)} in the last {span}";
        if (last.Free > first.Free && last.Free - first.Free >= TrendThresholdBytes)
            return $"gained {ResourceViewText.Bytes(last.Free - first.Free)} in the last {span}";
        return "steady";
    }

    private static string Span(TimeSpan span)
    {
        if (span.TotalMinutes < 2) return string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:F0} seconds");
        if (span.TotalHours < 2) return string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:F0} minutes");
        return string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:F0} hours");
    }
}
