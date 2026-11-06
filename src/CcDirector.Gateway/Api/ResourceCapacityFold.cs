using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data.Entities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The display verdict for a machine's resources, folded ONCE on the Gateway (Mission: Resource Monitor, sections 5j
/// and 5k; CLAUDE.md rule 7). The colour, the label, the tone and the Spare figure the Cockpit shows are all decided
/// here and stamped onto the row it reads; the Cockpit renders them verbatim and decides nothing. A new state is one
/// edit in this file.
///
/// THE RULES, in one place:
///  - "Now" is the newest five-minute window from any Director on the machine; the peak is the highest five-second
///    reading in the last hour's windows.
///  - Spare is total memory minus the HIGHEST memory in use in the last hour - free memory at the hour's peak, not
///    right now - so a machine that spikes is not advertised as roomy.
///  - A tone comes from the newest window's crossings: red is bad, amber is warn, none is ok. A figure the machine
///    could not read says "cannot read" in the idle tone, with the reason in <see cref="FleetMachineCapacityDto.Why"/>.
///  - An offline machine (no running Director) and a silent one (no window in the last 15 minutes) show dashes, never
///    zeros, and no status of their own - reachability is the reach line's. Offline is not idle.
///
/// Pure: every input is handed in.
/// </summary>
internal static class ResourceCapacityFold
{
    /// <summary>A machine whose newest window ended longer ago than this is not reporting.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(15);

    /// <summary>How far back the "now" and "peak" figures look.</summary>
    public static readonly TimeSpan PeakSpan = TimeSpan.FromHours(1);

    /// <summary>
    /// Stamp a capacity on every machine of a folded Machines view. <paramref name="lastHourWindows"/> is every machine
    /// window of the tenant from the last <see cref="PeakSpan"/>, or null when the statistics store could not be read.
    /// </summary>
    public static void Stamp(FleetMachinesDto view, IReadOnlyList<ResourceWindowEntity>? lastHourWindows, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(view);
        foreach (var machine in view.Machines)
        {
            var running = machine.Directors.Any(d => d.StateLabel == FleetMachinesFold.RunningLabel);
            var windows = lastHourWindows?
                .Where(w => string.Equals(w.MachineName, machine.Machine, StringComparison.OrdinalIgnoreCase))
                .ToList();
            machine.Capacity = Fold(running, windows, nowUtc);
        }
    }

    /// <summary>One machine's capacity. <paramref name="windows"/> is null when the statistics store could not be read.</summary>
    public static FleetMachineCapacityDto Fold(bool directorRunning, IReadOnlyList<ResourceWindowEntity>? windows, DateTime nowUtc)
    {
        if (windows is null)
            return NotReporting("The Gateway's statistics store is not open, so no resource figures can be shown.");
        if (!directorRunning)
            return NotReporting("No Director on this machine is running, so it is not measuring anything. Offline is not idle.");

        var recent = windows
            .Where(w => w.WindowStartUtc >= nowUtc - PeakSpan)
            .ToList();
        var newest = recent
            .OrderByDescending(w => w.WindowStartUtc)
            .ThenByDescending(w => w.SampleCount)
            .FirstOrDefault();
        var newestReceived = windows.Count == 0 ? (DateTime?)null : windows.Max(w => w.ReceivedUtc);
        if (newest is null || newest.WindowStartUtc.AddSeconds(newest.WindowSeconds) < nowUtc - SilentAfter)
        {
            // A Director whose CLOCK is behind sends on time but stamps its windows in the past: received recently, timed
            // long ago. That is a clock problem, and naming the connection would send someone to the wrong place.
            if (newestReceived is { } received && received >= nowUtc - SilentAfter)
                return NotReporting("This machine sent a resource summary in the last 15 minutes, but every window it sent "
                                          + "is stamped more than 15 minutes ago by the Gateway's clock. Its clock is probably behind.");
            return NotReporting("This machine has sent no resource summary in the last 15 minutes. Its Director may be "
                                      + "older than the resource monitor, or its connection to the Gateway may be down.");
        }

        // A REFUSED newest window is neither a reading nor silence: the machine sent figures no machine can produce.
        if (newest.RefusedReason is { } refused)
            return new FleetMachineCapacityDto
            {
                Reporting = true,
                ReadingWindowStartUtc = newest.WindowStartUtc,
                ProcessorLabel = "cannot read",
                MemoryLabel = "cannot read",
                SpareLabel = "cannot read",
                StatusLabel = "Figures refused",
                StatusTone = FleetTone.Bad,
                Why = $"The Gateway refused this machine's newest figures because {refused}. That is a fault in how the "
                      + "Director measured, not an idle machine.",
            };

        // Whether the hour's peak is known is decided PER RESOURCE, and by every window in the hour - not by whether one
        // was refused. A window can be unread for the processor and read for memory, and Max over a nullable figure skips
        // the null without saying so: the highest READABLE peak would be printed as the hour's peak, a believable
        // understatement (issue #4670, after review finding 13). A refused window has no figures, so it is one case of this.
        var processorGap = HourGap(recent, w => w.ProcessorPeakPercent is not null, w => w.ProcessorUnreadableReason);
        var memoryGap = HourGap(recent, w => w.MemoryPeakUsedBytes is not null, w => w.MemoryUnreadableReason);
        var processorPeakUnknown = false;
        var spareUnknownFromGap = false;
        var crossings = ResourceSummaryStore.CrossingsOf(newest);
        var dto = new FleetMachineCapacityDto { Reporting = true, ReadingWindowStartUtc = newest.WindowStartUtc };
        var why = new List<string>();

        // ---- the processor ----
        if (newest.ProcessorAveragePercent is { } cpuNow)
        {
            dto.ProcessorLabel = Percent(cpuNow);
            dto.ProcessorTone = ToneOf(crossings, ResourceSummaryContract.Processor);
            if (processorGap is not null)
            {
                dto.ProcessorDetail = "peak not known";
                processorPeakUnknown = true;
            }
            else
            {
                // Every window in the hour read the processor and the newest is one of them, so there is a peak.
                var peak = recent.Max(w => w.ProcessorPeakPercent)
                           ?? throw new InvalidOperationException("Every window in the hour read the processor, yet none has a peak.");
                dto.ProcessorDetail = $"peak {Percent(peak)}";
            }
        }
        else
        {
            dto.ProcessorLabel = "cannot read";
            dto.ProcessorTone = FleetTone.Idle;
            why.Add($"The processor could not be read: {newest.ProcessorUnreadableReason}");
        }

        // ---- memory, and spare ----
        if (newest.MemoryAverageUsedBytes is { } usedNow && newest.MemoryTotalBytes is { } total && total > 0)
        {
            dto.MemoryLabel = Percent(usedNow * 100.0 / total);
            dto.MemoryDetail = $"{Gigabytes(usedNow)} / {Gigabytes(total)} GB";
            dto.MemoryTone = ToneOf(crossings, ResourceSummaryContract.Memory);
            // A window in the hour whose memory was not read may have held the hour's peak, so Spare is not known: computed
            // from the readable windows alone it would advertise room the machine did not have, and Spare's only reader is
            // the decision about where the next factory goes (issue #4670).
            if (memoryGap is not null)
            {
                dto.SpareLabel = "cannot read";
                spareUnknownFromGap = true;
            }
            else
            {
                // Every window in the hour read memory and the newest is one of them, so there is a peak.
                var peakUsed = recent.Max(w => w.MemoryPeakUsedBytes)
                               ?? throw new InvalidOperationException("Every window in the hour read memory, yet none has a peak.");
                // NEVER CLAMPED. A peak above the machine's total is a reading no machine can give; showing it as "0.0 GB"
                // spare would turn a defect into a believable figure.
                if (peakUsed > total)
                {
                    dto.SpareLabel = "cannot read";
                    why.Add($"The machine reported more memory in use than it has ({Gigabytes(peakUsed)} of {Gigabytes(total)} GB), "
                            + "so its spare capacity is not known.");
                }
                else
                {
                    dto.SpareBytes = total - peakUsed;
                    dto.SpareLabel = $"{Gigabytes(total - peakUsed)} GB";
                }
            }
        }
        else
        {
            dto.MemoryLabel = "cannot read";
            dto.MemoryTone = FleetTone.Idle;
            why.Add(newest.MemoryUnreadableReason is { } reason
                ? $"Memory could not be read: {reason}"
                : "Memory was read but the machine's total was not, so no share or spare capacity can be given.");
        }

        // One sentence when one cause sank both peaks - the usual case for a refused window - so the Cockpit does not show
        // the same time and reason twice; otherwise one per resource.
        const string processorWhat = "the hour's peak processor use is";
        const string memoryWhat = "the hour's peak memory use - and with it the spare capacity - is";
        if (processorPeakUnknown && spareUnknownFromGap && processorGap == memoryGap)
            why.Add($"{processorGap}, so the hour's peak processor and memory use - and with it the spare capacity - are not known.");
        else
        {
            if (processorPeakUnknown) why.Add($"{processorGap}, so {processorWhat} not known.");
            if (spareUnknownFromGap) why.Add($"{memoryGap}, so {memoryWhat} not known.");
        }

        (dto.StatusLabel, dto.StatusTone) = StatusOf(dto);
        dto.Why = why.Count == 0 ? null : string.Join(" ", why);
        return dto;
    }

    /// <summary>
    /// What kept the hour's figure for one resource from being known - "In the last hour ..." - or null when every window
    /// in the hour read it. Windows are taken by their start: two Directors on one machine both measure the whole machine,
    /// so a window one of them could not read is covered when the other read it. It says WHICH happened, and both when
    /// both did, because a refused window is a fault in how a Director measured and an unread one is a counter that did
    /// not answer - different places to look.
    /// </summary>
    private static string? HourGap(List<ResourceWindowEntity> recent, Func<ResourceWindowEntity, bool> read,
        Func<ResourceWindowEntity, string?> unreadReason)
    {
        var gaps = recent
            .GroupBy(w => w.WindowStartUtc)
            .Where(g => !g.Any(read))
            .OrderBy(g => g.Key)
            .ToList();
        if (gaps.Count == 0) return null;

        // A start where two Directors' rows were one refused and one unread counts under both, so neither cause is hidden.
        var refused = gaps.Where(g => g.Any(w => w.RefusedReason is not null)).ToList();
        var notRead = gaps.Where(g => g.Any(w => w.RefusedReason is null)).ToList();
        var causes = new List<string>();
        if (refused.Count > 0)
            causes.Add($"{Windows(refused.Count)} had figures the Gateway refused (at {Time(refused[0].Key)}: "
                       + $"{refused[0].First(w => w.RefusedReason is not null).RefusedReason})");
        if (notRead.Count > 0)
        {
            var row = notRead[0].First(w => w.RefusedReason is null);
            causes.Add($"{Windows(notRead.Count)} could not be read (at {Time(notRead[0].Key)}: "
                       + $"{unreadReason(row) ?? "the Director gave no reason"})");
        }
        return $"In the last hour {string.Join(" and ", causes)}";

        static string Windows(int count) => count == 1 ? "one window" : $"{count} windows";
        static string Time(DateTime utc) => utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>
    /// A machine that is not reporting gets dashes and a reason, and NO status: whether it can be reached is the reach
    /// line's verdict (another fold owns it), so a second pill saying "Offline" under it would be two verdicts on one
    /// machine. The capacity detail is in <see cref="FleetMachineCapacityDto.Why"/>.
    /// </summary>
    private static FleetMachineCapacityDto NotReporting(string why) => new()
    {
        Reporting = false,
        StatusTone = FleetTone.Idle,
        Why = why,
    };

    private static string ToneOf(List<ResourceCrossing> crossings, string resource)
    {
        var levels = crossings.Where(c => c.Resource == resource).Select(c => c.Level).ToList();
        if (levels.Contains(ResourceSummaryContract.Red)) return FleetTone.Bad;
        if (levels.Contains(ResourceSummaryContract.Amber)) return FleetTone.Warn;
        return FleetTone.Ok;
    }

    private static (string?, string) StatusOf(FleetMachineCapacityDto dto)
    {
        if (dto.ProcessorTone == FleetTone.Bad) return ("CPU maxed out", FleetTone.Bad);
        if (dto.MemoryTone == FleetTone.Bad) return ("Memory critical", FleetTone.Bad);
        if (dto.ProcessorTone == FleetTone.Warn) return ("CPU hot", FleetTone.Warn);
        if (dto.MemoryTone == FleetTone.Warn) return ("Memory tight", FleetTone.Warn);
        return (null, FleetTone.Ok);
    }

    internal static string Percent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Gigabytes to one decimal place, without a trailing ".0" at 10 or more (the mock-up's "16 GB"). BINARY gigabytes
    /// (1,073,741,824 bytes), because that is what Task Manager, free and Activity Monitor show, and the mission's check
    /// compares this view with them: a machine everyone calls "16 GB" must read 16 here, not 17 (review note 9).
    /// </summary>
    internal static string Gigabytes(long bytes)
    {
        var gb = bytes / 1_073_741_824.0;
        return gb >= 10
            ? Math.Round(gb, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : gb.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
