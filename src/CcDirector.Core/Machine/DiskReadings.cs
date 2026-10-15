using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// What a physical disk is made of. It matters to the slowness diagnosis: a spinning hard drive at 75%
/// busy and 26 milliseconds per write is the whole explanation for a slow machine, while a solid-state
/// drive at the same busy figure usually is not (devlinux, 2026-10-15).
///
/// <see cref="Unknown"/> is never treated as either kind, for the same reason
/// <see cref="MemoryPressureLevel.Unknown"/> is never Normal: assuming a fact that was not measured is
/// how a diagnosis names the wrong culprit with confidence.
/// </summary>
public enum DiskKind
{
    /// <summary>The kind could not be determined. Assume nothing.</summary>
    Unknown = 0,

    /// <summary>A solid-state drive (no moving parts).</summary>
    SolidState = 1,

    /// <summary>A spinning hard drive.</summary>
    Spinning = 2,
}

/// <summary>
/// One reading of one physical disk - the device, not a volume on it.
///
/// <see cref="Name"/> and <see cref="Kind"/> are known even when the statistics are not, which is why
/// they survive <see cref="Unreadable"/>: the detail screen can still list the disk and say why it
/// cannot show its figures.
///
/// The nullable figures are null when the platform does not offer them, which is not the same as a
/// failed read - a failed read is <see cref="CouldRead"/> false for the whole disk.
///
/// BUSY AND QUEUE DEPTH ARE DIFFERENT THINGS, AND macOS OFFERS ONLY ONE OF THEM. The IOKit disk-time
/// counter is concurrency-weighted service time: the average number of operations in flight, not a share
/// of time the disk was working. Read as a busy figure it said 260% for one healthy reader and 25% for a
/// contended one. So <see cref="BusyPercent"/> is null on macOS, and that counter is reported as
/// <see cref="AverageQueueDepth"/>, which is what it measures.
/// </summary>
/// <param name="Name">The operating system's own name for the disk: <c>sdc</c>, <c>PhysicalDrive2</c>, <c>disk0</c>.</param>
/// <param name="Kind">Spinning or solid-state, or Unknown when it could not be told.</param>
/// <param name="CouldRead">False when this disk's statistics could not be read; the figures are then zero and null.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="BusyPercent">
/// Share of the last interval the disk was doing any work, 0 to 100. Null where the platform offers no
/// true utilisation (macOS) - never estimated from a queue depth.
/// </param>
/// <param name="ReadBytesPerSecond">Bytes read per second over the last interval.</param>
/// <param name="WriteBytesPerSecond">Bytes written per second over the last interval.</param>
/// <param name="MillisecondsPerRead">Average time one read took. Null when not offered on this platform.</param>
/// <param name="MillisecondsPerWrite">Average time one write took. Null when not offered on this platform.</param>
/// <param name="AverageQueueDepth">
/// The time-averaged number of operations in flight over the last interval: Linux weighted input/output
/// ticks over the interval, Windows "Avg. Disk Queue Length", macOS total disk time over the interval.
/// Null when not offered on this platform.
/// </param>
public readonly record struct PhysicalDiskReading(
    string Name,
    DiskKind Kind,
    bool CouldRead,
    string? UnreadableReason,
    double? BusyPercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double? MillisecondsPerRead,
    double? MillisecondsPerWrite,
    double? AverageQueueDepth)
{
    /// <summary>A reading that succeeded. Pass null for any figure the platform does not offer.</summary>
    public static PhysicalDiskReading Read(
        string name,
        DiskKind kind,
        double? busyPercent,
        double readBytesPerSecond,
        double writeBytesPerSecond,
        double? millisecondsPerRead,
        double? millisecondsPerWrite,
        double? averageQueueDepth) =>
        new(name, kind, CouldRead: true, UnreadableReason: null, busyPercent, readBytesPerSecond,
            writeBytesPerSecond, millisecondsPerRead, millisecondsPerWrite, averageQueueDepth);

    /// <summary>A disk whose statistics could not be read, carrying why. The rates are zero, the rest null, and none mean anything.</summary>
    public static PhysicalDiskReading Unreadable(string name, DiskKind kind, string reason) =>
        new(name, kind, CouldRead: false, ResourceReadingGuard.Reason(reason), null, 0, 0, null, null, null);

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString()
    {
        if (!CouldRead) return $"disk {Name} ({KindText}) unreadable ({UnreadableReason})";

        var text = $"disk {Name} ({KindText})";
        if (BusyPercent is { } busy)
            text += string.Create(CultureInfo.InvariantCulture, $" {busy:F0}% busy,");
        text += string.Create(CultureInfo.InvariantCulture,
            $" {ReadBytesPerSecond / (1024.0 * 1024):F1} MB/s read, {WriteBytesPerSecond / (1024.0 * 1024):F1} MB/s written");
        if (AverageQueueDepth is { } depth)
            text += string.Create(CultureInfo.InvariantCulture, $", {depth:F1} operations in flight");
        if (MillisecondsPerWrite is { } perWrite)
            text += string.Create(CultureInfo.InvariantCulture, $", {perWrite:F0} ms per write");
        return text;
    }

    private string KindText => Kind switch
    {
        DiskKind.SolidState => "solid-state",
        DiskKind.Spinning => "spinning hard drive",
        _ => "kind unknown",
    };
}

/// <summary>
/// Every physical disk on the machine.
///
/// It fails at two levels, and both are kept: the whole set fails when the disks cannot even be listed
/// (<see cref="CouldRead"/> false, <see cref="Disks"/> empty), and one disk fails on its own while the
/// others read fine (that disk's own <see cref="PhysicalDiskReading.CouldRead"/> false).
/// </summary>
/// <param name="CouldRead">False when the disks could not be listed at all.</param>
/// <param name="UnreadableReason">Why the listing failed, in plain English. Null when it succeeded.</param>
/// <param name="Disks">Each physical disk. Empty when the listing failed.</param>
public sealed record DiskSetReading(
    bool CouldRead,
    string? UnreadableReason,
    IReadOnlyList<PhysicalDiskReading> Disks)
{
    /// <summary>A listing that succeeded.</summary>
    public static DiskSetReading Read(IReadOnlyList<PhysicalDiskReading> disks) =>
        new(CouldRead: true, UnreadableReason: null, disks);

    /// <summary>A listing that failed, carrying why. There are no disks in it.</summary>
    public static DiskSetReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), Array.Empty<PhysicalDiskReading>());

    /// <summary>Short, log-safe description. Prints no figure when the listing failed.</summary>
    public override string ToString() =>
        CouldRead
            ? Disks.Count == 0 ? "no physical disks" : string.Join("; ", Disks)
            : $"disks unreadable ({UnreadableReason})";
}

/// <summary>
/// One mounted volume and its free space.
///
/// <see cref="PhysicalDiskNames"/> is how the diagnosis knows that a slow spinning disk sits INSIDE the
/// volume the machine runs from - on devlinux the root volume spans two solid-state drives and the
/// spinning <c>sdc</c>, and that was the whole cause of the slowness. Empty when not known.
/// </summary>
/// <param name="MountPoint">Where the volume is mounted: <c>/</c>, <c>C:\</c>, <c>/Volumes/Data</c>.</param>
/// <param name="CouldRead">False when its space could not be read; the byte counts are then zero and mean nothing.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="TotalBytes">The size of the volume.</param>
/// <param name="FreeBytes">Space available to an ordinary program.</param>
/// <param name="PhysicalDiskNames">The physical disks the volume sits on, by <see cref="PhysicalDiskReading.Name"/>. Empty when not known.</param>
public sealed record VolumeReading(
    string MountPoint,
    bool CouldRead,
    string? UnreadableReason,
    ulong TotalBytes,
    ulong FreeBytes,
    IReadOnlyList<string> PhysicalDiskNames)
{
    /// <summary>A reading that succeeded. Pass an empty list when the physical disks are not known.</summary>
    public static VolumeReading Read(string mountPoint, ulong totalBytes, ulong freeBytes, IReadOnlyList<string> physicalDiskNames) =>
        new(mountPoint, CouldRead: true, UnreadableReason: null, totalBytes, freeBytes, physicalDiskNames);

    /// <summary>A volume whose space could not be read, carrying why. The byte counts are zero and mean nothing.</summary>
    public static VolumeReading Unreadable(string mountPoint, string reason) =>
        new(mountPoint, CouldRead: false, ResourceReadingGuard.Reason(reason), 0, 0, Array.Empty<string>());

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString() =>
        CouldRead
            ? string.Create(CultureInfo.InvariantCulture,
                $"volume {MountPoint} {FreeBytes / (1024.0 * 1024 * 1024):F1} GB free of {TotalBytes / (1024.0 * 1024 * 1024):F1} GB")
              + (PhysicalDiskNames.Count > 0 ? $" on {string.Join(", ", PhysicalDiskNames)}" : "")
            : $"volume {MountPoint} unreadable ({UnreadableReason})";
}
