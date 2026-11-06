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
/// What one physical disk contributes to the SLOWNESS DIAGNOSIS - the device, not a volume on it.
///
/// THIS IS EVIDENCE FOR A SENTENCE, NOT A METER. There is no disk bar in the toolbar, none in the detail
/// tiles, no disk figure in a Gateway row and none in the Cockpit - see <see cref="ResourceSample"/> for
/// why. These figures exist only to fill in the approved sentence "the hard drive sdc is 75% busy at 26
/// milliseconds per write, and programs are stalled waiting on it 32% of the time", and nothing else may
/// read them.
///
/// <see cref="Name"/>, <see cref="Kind"/> and <see cref="IsInSystemVolume"/> are known even when the
/// statistics are not, which is why they survive <see cref="Unreadable"/>: the diagnosis can still say
/// "sdc is a spinning hard drive inside the volume the machine runs from" when its timings are locked
/// away.
///
/// Every figure is nullable, because each platform offers a different subset and null means "not offered
/// here" - never "failed". A failed read is <see cref="CouldRead"/> false for the whole disk, and then
/// every figure is null as well, so there is no zero to mistake for a measurement.
///
/// BUSY AND QUEUE DEPTH ARE DIFFERENT THINGS, AND macOS OFFERS ONLY ONE OF THEM. The IOKit disk-time
/// counter is concurrency-weighted service time: the average number of operations in flight, not a share
/// of time the disk was working. Read as a busy figure it said 260% for one healthy reader and 25% for a
/// contended one. So <see cref="BusyPercent"/> is null on macOS, and that counter is reported as
/// <see cref="AverageQueueDepth"/>, which is what it measures.
///
/// A BUSY FIGURE NEVER DECIDES THE VERDICT, on any platform. What decides it depends on what the platform
/// can measure, and mission section 5c-i rules both the reading and the wording: on Linux the stall
/// figure (<see cref="StallReading.IoStalledPercent"/>) decides; on macOS and Windows, which publish no
/// stall figure, the LATENCY here decides and the diagnosis says "the disk is answering slowly" rather
/// than claiming a stall percentage it never measured. See <see cref="StallReading"/> for the whole rule.
/// </summary>
/// <param name="Name">The operating system's own name for the disk: <c>sdc</c>, <c>PhysicalDrive2</c>, <c>disk0</c>.</param>
/// <param name="Kind">Spinning or solid-state, or Unknown when it could not be told.</param>
/// <param name="IsInSystemVolume">
/// True when this disk backs THE VOLUME THE OPERATING SYSTEM RUNS FROM: <c>/</c> on Linux and macOS, and
/// on Windows the volume holding the Windows directory - which Windows itself calls the BOOT volume,
/// usually <c>C:\</c>. Windows uses "system volume" for the small boot-loader partition instead, so the
/// Windows reading must follow the meaning here and not that name.
///
/// It is the fact that turns a slow disk into an explanation: on devlinux the root volume spanned two
/// solid-state drives AND the spinning <c>sdc</c>, which is why everything the machine did waited on it.
/// Null when the platform cannot say which disks back which volume - which is not the same as false.
///
/// It replaces v3's per-volume reading, which also carried free space per volume and went out with the
/// disk meter.
/// </param>
/// <param name="CouldRead">False when this disk's statistics could not be read; every figure is then null.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="BusyPercent">
/// Share of the last interval the disk was doing any work, 0 to 100. Null where the platform offers no
/// true utilisation (macOS) - never estimated from a queue depth.
/// </param>
/// <param name="MillisecondsPerRead">Average time one read took. Null when not offered on this platform.</param>
/// <param name="MillisecondsPerWrite">Average time one write took. Null when not offered on this platform.</param>
/// <param name="AverageQueueDepth">
/// The time-averaged number of operations in flight over the last interval: Linux weighted input/output
/// ticks over the interval, Windows "Avg. Disk Queue Length", macOS total disk time over the interval.
/// Null when not offered on this platform.
/// </param>
public readonly record struct DiskEvidence(
    string Name,
    DiskKind Kind,
    bool? IsInSystemVolume,
    bool CouldRead,
    string? UnreadableReason,
    double? BusyPercent,
    double? MillisecondsPerRead,
    double? MillisecondsPerWrite,
    double? AverageQueueDepth)
{
    /// <summary>
    /// A reading that succeeded. Pass null for any figure the platform does not offer, and null for
    /// <paramref name="isInSystemVolume"/> when the platform cannot say which disks back which volume.
    /// </summary>
    public static DiskEvidence Read(
        string name,
        DiskKind kind,
        bool? isInSystemVolume,
        double? busyPercent,
        double? millisecondsPerRead,
        double? millisecondsPerWrite,
        double? averageQueueDepth) =>
        new(name, kind, isInSystemVolume, CouldRead: true, UnreadableReason: null, busyPercent,
            millisecondsPerRead, millisecondsPerWrite, averageQueueDepth);

    /// <summary>
    /// A disk whose statistics could not be read, carrying why. Its name, its kind and whether it backs
    /// the system volume are still known; every figure is null, because there is nothing to report.
    /// </summary>
    public static DiskEvidence Unreadable(string name, DiskKind kind, bool? isInSystemVolume, string reason) =>
        new(name, kind, isInSystemVolume, CouldRead: false, ResourceReadingGuard.Reason(reason),
            null, null, null, null);

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString()
    {
        var where = IsInSystemVolume == true ? ", in the system volume" : "";
        if (!CouldRead) return $"disk {Name} ({KindText}{where}) unreadable ({UnreadableReason})";

        var text = $"disk {Name} ({KindText}{where})";
        var parts = new List<string>();
        if (BusyPercent is { } busy) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{busy:F0}% busy"));
        if (AverageQueueDepth is { } depth)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{depth:F1} operations in flight"));
        if (MillisecondsPerRead is { } perRead) parts.Add($"{FormatMilliseconds(perRead)} ms per read");
        if (MillisecondsPerWrite is { } perWrite) parts.Add($"{FormatMilliseconds(perWrite)} ms per write");

        return parts.Count == 0 ? $"{text}: no figures offered" : $"{text} {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Two decimal places below ten milliseconds, whole milliseconds above. A solid-state drive answers in
    /// well under one millisecond, so whole milliseconds would print every one of them as "0 ms" - the
    /// made-up zero the meter must never show. Above ten, a decimal place is noise beside the figure that
    /// matters (a spinning drive at 26 ms per write).
    /// </summary>
    internal static string FormatMilliseconds(double milliseconds) =>
        milliseconds < 10
            ? milliseconds.ToString("F2", CultureInfo.InvariantCulture)
            : milliseconds.ToString("F0", CultureInfo.InvariantCulture);

    private string KindText => Kind switch
    {
        DiskKind.SolidState => "solid-state",
        DiskKind.Spinning => "spinning hard drive",
        _ => "kind unknown",
    };
}

/// <summary>
/// Every physical disk on the machine, as evidence for the slowness diagnosis and for nothing else.
///
/// It fails at two levels, and both are kept: the whole set fails when the disks cannot even be listed
/// (<see cref="CouldRead"/> false, <see cref="Disks"/> empty), and one disk fails on its own while the
/// others read fine (that disk's own <see cref="DiskEvidence.CouldRead"/> false).
/// </summary>
/// <param name="CouldRead">False when the disks could not be listed at all.</param>
/// <param name="UnreadableReason">Why the listing failed, in plain English. Null when it succeeded.</param>
/// <param name="Disks">Each physical disk. Empty when the listing failed.</param>
public sealed record DiskEvidenceSet(
    bool CouldRead,
    string? UnreadableReason,
    IReadOnlyList<DiskEvidence> Disks)
{
    /// <summary>A listing that succeeded.</summary>
    public static DiskEvidenceSet Read(IReadOnlyList<DiskEvidence> disks) =>
        new(CouldRead: true, UnreadableReason: null, disks);

    /// <summary>A listing that failed, carrying why. There are no disks in it.</summary>
    public static DiskEvidenceSet Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), Array.Empty<DiskEvidence>());

    /// <summary>Short, log-safe description. Prints no figure when the listing failed.</summary>
    public override string ToString() =>
        CouldRead
            ? Disks.Count == 0 ? "no physical disks" : string.Join("; ", Disks)
            : $"disks unreadable ({UnreadableReason})";
}
