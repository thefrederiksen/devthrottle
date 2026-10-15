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
/// <param name="CouldRead">False when the processor could not be read; the figures are then zero and mean nothing.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="TotalBusyPercent">How busy the whole machine was over the last interval, 0 to 100.</param>
/// <param name="BusiestCorePercent">How busy the single busiest logical core was over the same interval, 0 to 100.</param>
/// <param name="LogicalCoreCount">How many logical cores the machine has. Zero only when unreadable.</param>
public readonly record struct CpuReading(
    bool CouldRead,
    string? UnreadableReason,
    double TotalBusyPercent,
    double BusiestCorePercent,
    int LogicalCoreCount)
{
    /// <summary>A reading that succeeded.</summary>
    public static CpuReading Read(double totalBusyPercent, double busiestCorePercent, int logicalCoreCount) =>
        new(CouldRead: true, UnreadableReason: null, totalBusyPercent, busiestCorePercent, logicalCoreCount);

    /// <summary>A reading that failed, carrying why. The figures are zero and mean nothing.</summary>
    public static CpuReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), 0, 0, 0);

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString() =>
        CouldRead
            ? string.Create(CultureInfo.InvariantCulture,
                $"processor {TotalBusyPercent:F0}% busy, busiest core {BusiestCorePercent:F0}%, {LogicalCoreCount} cores")
            : $"processor unreadable ({UnreadableReason})";
}
