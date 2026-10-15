using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the swap file (Linux, macOS) or page file (Windows): memory the operating system has
/// moved to disk. Growing swap is the late sign of a machine short of memory, which is why it has its
/// own limits in the mission document rather than being folded into memory.
///
/// A machine with no swap configured is READABLE with a total of zero - that is a fact about the
/// machine, not a failed read.
/// </summary>
/// <param name="CouldRead">False when swap could not be read; the byte counts are then zero and mean nothing.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="TotalBytes">The size of the swap or page file. Zero when none is configured.</param>
/// <param name="UsedBytes">How much of it is in use.</param>
public readonly record struct SwapReading(
    bool CouldRead,
    string? UnreadableReason,
    ulong TotalBytes,
    ulong UsedBytes)
{
    /// <summary>A reading that succeeded.</summary>
    public static SwapReading Read(ulong totalBytes, ulong usedBytes) =>
        new(CouldRead: true, UnreadableReason: null, totalBytes, usedBytes);

    /// <summary>A reading that failed, carrying why. The byte counts are zero and mean nothing.</summary>
    public static SwapReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), 0, 0);

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString() =>
        CouldRead
            ? string.Create(CultureInfo.InvariantCulture,
                $"swap {UsedBytes / (1024.0 * 1024 * 1024):F1} GB used of {TotalBytes / (1024.0 * 1024 * 1024):F1} GB")
            : $"swap unreadable ({UnreadableReason})";
}
