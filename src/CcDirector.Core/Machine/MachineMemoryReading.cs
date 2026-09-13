namespace CcDirector.Core.Machine;

/// <summary>
/// How short of memory the machine is, folded from a <see cref="MachineMemoryReading"/> by
/// <see cref="MemoryPressure"/>.
///
/// <see cref="Unknown"/> is NOT a synonym for <see cref="Normal"/> and must never be treated as one.
/// It means the reading failed, so nothing is known about the machine - and a caller that widens a
/// deadline on pressure must leave the deadline alone here rather than assume the machine is fine.
/// Assuming health from an absent measurement is how a machine in trouble goes on being treated as
/// healthy, which is the whole defect in issue #2818.
/// </summary>
public enum MemoryPressureLevel
{
    /// <summary>The memory could not be read. Nothing is known; assume nothing.</summary>
    Unknown = 0,

    /// <summary>Room to work. Every deadline stays exactly where it is today.</summary>
    Normal = 1,

    /// <summary>Getting short. Deadlines that exist to detect a stuck peer are widened.</summary>
    Tight = 2,

    /// <summary>Nearly out. The machine is expected to page, and to stall for whole seconds.</summary>
    Critical = 3,
}

/// <summary>
/// One reading of the whole machine's memory, taken at a moment in time.
///
/// THE READING IS ALLOWED TO FAIL, AND SAYING SO IS PART OF ITS JOB. A platform that cannot be read
/// returns <see cref="Unreadable"/> carrying the reason, never a zero and never a guess. A zero would
/// fold to "Critical" and silently widen every deadline on the fleet; a guess would do something
/// worse and quieter. Issue #2811 asks for the same honesty from the toolbar meter it will render
/// this on: "It never shows a made-up or zero value."
/// </summary>
/// <param name="TotalBytes">Physical memory installed in the machine. Zero when <paramref name="CouldRead"/> is false.</param>
/// <param name="AvailableBytes">
/// Memory that can be handed to a process without paging something else out. This is the figure that
/// matters - "free" alone understates every modern operating system, all of which keep memory usefully
/// occupied by caches they will surrender on demand.
/// </param>
/// <param name="TakenAtUtc">When the reading was taken, so a cached one can be aged.</param>
/// <param name="CouldRead">False when the platform could not be read; the other numbers are then meaningless.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
public readonly record struct MachineMemoryReading(
    ulong TotalBytes,
    ulong AvailableBytes,
    DateTime TakenAtUtc,
    bool CouldRead,
    string? UnreadableReason)
{
    /// <summary>A reading that succeeded.</summary>
    public static MachineMemoryReading Read(ulong totalBytes, ulong availableBytes, DateTime takenAtUtc) =>
        new(totalBytes, availableBytes, takenAtUtc, CouldRead: true, UnreadableReason: null);

    /// <summary>A reading that failed, carrying why. The byte counts are zero and mean nothing.</summary>
    public static MachineMemoryReading Unreadable(string reason, DateTime takenAtUtc) =>
        new(TotalBytes: 0, AvailableBytes: 0, takenAtUtc, CouldRead: false, UnreadableReason: reason);

    /// <summary>
    /// Available memory as a fraction of the total, from 0.0 to 1.0. Returns null when the reading
    /// failed or the total is zero, so a caller cannot divide by a number that was never measured.
    /// </summary>
    public double? AvailableFraction =>
        CouldRead && TotalBytes > 0 ? (double)AvailableBytes / TotalBytes : null;

    /// <summary>Short, log-safe description. Used in the lines that explain a widened deadline.</summary>
    public override string ToString() =>
        CouldRead
            ? $"{AvailableBytes / (1024.0 * 1024 * 1024):F1} GB available of {TotalBytes / (1024.0 * 1024 * 1024):F1} GB"
            : $"memory unreadable ({UnreadableReason})";
}
