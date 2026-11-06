using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the processor.
///
/// Both figures are kept because they answer different questions. <see cref="TotalBusyPercent"/> is
/// whether the MACHINE is maxed out - the figure the warning fires on. <see cref="BusiestCorePercent"/>
/// is whether one program is stuck spinning on one core: worth showing in the detail screen, never a
/// warning on its own, because one full core of thirty-two is not a machine out of processor.
/// </summary>
/// <param name="CouldRead">False when the processor's busy figure could not be read; it is then zero and means nothing.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="TotalBusyPercent">How busy the whole machine was over the last interval, 0 to 100.</param>
/// <param name="BusiestCorePercent">
/// How busy the single busiest logical core was over the same interval, 0 to 100. NULL WHEN THE PLATFORM
/// DOES NOT OFFER IT, which is not a failed read: on Windows a machine with more than 64 logical
/// processors needs processor-group-aware calls to see per-core figures at all, so the whole-machine
/// figure can be known while this one is not. The two are therefore independent, and the machine figure -
/// the one the warning fires on - is never held back by this one being missing.
/// </param>
/// <param name="LogicalCoreCount">
/// How many logical cores the machine has. NULL WHEN NOT KNOWN. It is a static fact about the machine
/// rather than a rate, so it usually survives a failed busy read and should be passed to
/// <see cref="Unreadable"/> when it is known: reporting zero cores would be a made-up figure for
/// something that was never measured, which is the one thing this interface refuses everywhere else.
/// </param>
public readonly record struct CpuReading(
    bool CouldRead,
    string? UnreadableReason,
    double TotalBusyPercent,
    double? BusiestCorePercent,
    int? LogicalCoreCount)
{
    /// <summary>
    /// A reading that succeeded. Pass null for <paramref name="busiestCorePercent"/> where the platform
    /// does not offer a per-core figure.
    /// </summary>
    public static CpuReading Read(double totalBusyPercent, double? busiestCorePercent, int? logicalCoreCount) =>
        new(CouldRead: true, UnreadableReason: null, totalBusyPercent, busiestCorePercent, logicalCoreCount);

    /// <summary>
    /// A reading that failed, carrying why. The busy figures mean nothing - but pass the core count if it
    /// is known, because it is a fact about the machine and not part of what failed.
    /// </summary>
    public static CpuReading Unreadable(string reason, int? logicalCoreCount = null) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), 0, null, logicalCoreCount);

    /// <summary>Short, log-safe description. Prints no busy figure when the reading failed.</summary>
    public override string ToString()
    {
        var cores = LogicalCoreCount is { } count
            ? string.Create(CultureInfo.InvariantCulture, $", {count} cores")
            : "";

        if (!CouldRead) return $"processor unreadable ({UnreadableReason}){cores}";

        var busiest = BusiestCorePercent is { } percent
            ? string.Create(CultureInfo.InvariantCulture, $", busiest core {percent:F0}%")
            : ", busiest core not offered";

        return string.Create(CultureInfo.InvariantCulture, $"processor {TotalBusyPercent:F0}% busy")
               + busiest + cores;
    }
}
