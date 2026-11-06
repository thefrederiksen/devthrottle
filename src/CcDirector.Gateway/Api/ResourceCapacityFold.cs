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
///    zeros. Offline is not idle.
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
            return NotReporting("Offline", "The Gateway's statistics store is not open, so no resource figures can be shown.");
        if (!directorRunning)
            return NotReporting("Offline", "No Director on this machine is running, so it is not measuring anything. Offline is not idle.");

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
                return NotReporting(null, "This machine sent a resource summary in the last 15 minutes, but every window it sent "
                                          + "is stamped more than 15 minutes ago by the Gateway's clock. Its clock is probably behind.");
            return NotReporting(null, "This machine has sent no resource summary in the last 15 minutes. Its Director may be "
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
        var processorGap = HourGap(recent, w => w.ProcessorPeakPercent is not null, w => w.ProcessorUnreadableReason,
            "the hour's peak processor use");
        var memoryGap = HourGap(recent, w => w.MemoryPeakUsedBytes is not null, w => w.MemoryUnreadableReason,
            "the hour's peak memory use - and with it the spare capacity -");
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
                why.Add(processorGap);
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
                why.Add(memoryGap);
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

        (dto.StatusLabel, dto.StatusTone) = StatusOf(dto);
        dto.Why = why.Count == 0 ? null : string.Join(" ", why);
        return dto;
    }

    /// <summary>
    /// Why the hour's figure for one resource is not known, or null when every window in the hour read it. Windows are
    /// taken by their start: two Directors on one machine both measure the whole machine, so a window one of them could
    /// not read is covered when the other read it. The sentence says WHICH happened, because a refused window is a fault
    /// in how a Director measured and an unread one is a counter that did not answer - different places to look.
    /// </summary>
    private static string? HourGap(List<ResourceWindowEntity> recent, Func<ResourceWindowEntity, bool> read,
        Func<ResourceWindowEntity, string?> unreadReason, string what)
    {
        var unread = recent
            .GroupBy(w => w.WindowStartUtc)
            .Where(g => !g.Any(read))
            .OrderBy(g => g.Key)
            .ToList();
        if (unread.Count == 0) return null;

        // A window start is refused when its only rows were refused; otherwise it was simply unread.
        var refused = unread.Where(g => g.All(w => w.RefusedReason is not null)).ToList();
        var notRead = unread.Where(g => !g.All(w => w.RefusedReason is not null)).ToList();
        var causes = new List<string>();
        if (refused.Count > 0)
            causes.Add($"{Windows(refused.Count)} had figures the Gateway refused (at {Time(refused[0].Key)}: "
                       + $"{refused[0].First().RefusedReason})");
        if (notRead.Count > 0)
        {
            var row = notRead[0].First(w => w.RefusedReason is null);
            causes.Add($"{Windows(notRead.Count)} could not be read (at {Time(notRead[0].Key)}: "
                       + $"{unreadReason(row) ?? "the Director gave no reason"})");
        }
        return $"In the last hour {string.Join(" and ", causes)}, so {what} is not known.";

        static string Windows(int count) => count == 1 ? "one window" : $"{count} windows";
        static string Time(DateTime utc) => utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    private static FleetMachineCapacityDto NotReporting(string? status, string why) => new()
    {
        Reporting = false,
        StatusLabel = status,
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

/// <summary>
/// One machine expanded (Mission: Resource Monitor, section 5k), folded once on the Gateway: the five-minute rows for the
/// chart, each crossing marked with its words and tone, and the cost per registered factory, grouped by each session's
/// Factory value - never by Mission. Pure.
/// </summary>
internal static class MachineResourceHistoryFold
{
    public const string NoFactory = "No factory";

    public static MachineResourceHistoryDto Fold(string machine, int hours, IReadOnlyList<ResourceWindowEntity>? windows,
        IReadOnlyList<ResourceSessionWindowEntity>? sessionWindows)
    {
        var dto = new MachineResourceHistoryDto { Machine = machine, Hours = hours };
        if (windows is null || sessionWindows is null)
        {
            dto.EmptyMessage = "The Gateway's statistics store is not open, so this machine's history cannot be shown.";
            return dto;
        }
        var mine = windows.Where(w => string.Equals(w.MachineName, machine, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
        {
            dto.EmptyMessage = $"No resource summaries from {machine} in the last {hours} hours.";
            return dto;
        }

        // Two Directors on one machine both measure the whole machine: one row per window start, the fuller one.
        foreach (var w in mine.GroupBy(w => w.WindowStartUtc).Select(g => g.OrderByDescending(x => x.SampleCount).First())
                     .OrderBy(w => w.WindowStartUtc))
        {
            var total = w.MemoryTotalBytes is > 0 ? w.MemoryTotalBytes : null;
            dto.Windows.Add(new MachineResourceWindowDto
            {
                WindowStartUtc = w.WindowStartUtc,
                ProcessorAveragePercent = w.ProcessorAveragePercent,
                ProcessorPeakPercent = w.ProcessorPeakPercent,
                MemoryAveragePercent = total is { } t1 && w.MemoryAverageUsedBytes is { } a ? a * 100.0 / t1 : null,
                MemoryPeakPercent = total is { } t2 && w.MemoryPeakUsedBytes is { } p ? p * 100.0 / t2 : null,
                Crossings = ResourceSummaryStore.CrossingsOf(w).Select(Mark).ToList(),
                RefusedReason = w.RefusedReason,
            });
        }

        // A refused window keeps no session rows, so every factory's cost over a span that holds one is a lower bound.
        var anyRefused = mine.Any(w => w.RefusedReason is not null);
        dto.Factories = sessionWindows
            .Where(s => string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Factory) ? NoFactory : s.Factory!, StringComparer.OrdinalIgnoreCase)
            .Select(g => FactoryCost(g, anyRefused))
            .OrderBy(f => f.Factory == NoFactory ? 1 : 0)
            .ThenByDescending(f => f.ProcessorPeakPercent ?? -1)
            .ThenBy(f => f.Factory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return dto;
    }

    private static ResourceCrossingMarkDto Mark(ResourceCrossing c)
    {
        var resource = c.Resource == ResourceSummaryContract.Processor ? "CPU" : "Memory";
        var peak = c.PeakPercent is { } p ? $", peak {ResourceCapacityFold.Percent(p)}" : "";
        return new ResourceCrossingMarkDto
        {
            StartedUtc = c.StartedUtc,
            Resource = resource,
            Label = $"{resource} {c.Level} for {c.Seconds} seconds{peak}",
            Tone = c.Level == ResourceSummaryContract.Red ? FleetTone.Bad : FleetTone.Warn,
        };
    }

    /// <summary>
    /// A factory's sessions run side by side, so within one window their figures ADD: the factory's average is the mean,
    /// over the windows it ran in, of its sessions' summed averages, and its peak is the highest summed peak.
    ///
    /// AN UNKNOWN IS NEVER ADDED AS ZERO. When a session's figure could not be read, or a session had processes whose
    /// figures could not be read, the sum leaves them out and is a LOWER BOUND, and the label says "at least" - a cost
    /// reported without that would be too cheap and still look plausible, which is the worst kind of wrong.
    /// </summary>
    private static FactoryResourceCostDto FactoryCost(IGrouping<string, ResourceSessionWindowEntity> factory, bool spanHasRefusedWindow)
    {
        var byWindow = factory.GroupBy(s => s.WindowStartUtc).ToList();
        var partialProcesses = spanHasRefusedWindow || factory.Any(s => s.ProcessesWithUnknownFigures > 0);

        var cpuAverages = Sums(byWindow, s => s.ProcessorAveragePercent);
        var cpuPeaks = Sums(byWindow, s => s.ProcessorPeakPercent);
        var memAverages = Sums(byWindow, s => s.MemoryAverageUsedBytes);
        var memPeaks = Sums(byWindow, s => s.MemoryPeakUsedBytes);

        double? cpuPeak = cpuPeaks.Values.Count == 0 ? null : cpuPeaks.Values.Max();
        var cpuAtLeast = partialProcesses || cpuAverages.LeftOut || cpuPeaks.LeftOut ? "at least " : "";
        var memAtLeast = partialProcesses || memAverages.LeftOut || memPeaks.LeftOut ? "at least " : "";
        return new FactoryResourceCostDto
        {
            Factory = factory.Key,
            Sessions = factory.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).Count(),
            ProcessorPeakPercent = cpuPeak,
            ProcessorLabel = cpuAverages.Values.Count == 0 || cpuPeak is null
                ? "cannot read"
                : $"{cpuAtLeast}{ResourceCapacityFold.Percent(cpuAverages.Values.Average())} / {ResourceCapacityFold.Percent(cpuPeak.Value)}",
            MemoryLabel = memAverages.Values.Count == 0 || memPeaks.Values.Count == 0
                ? "cannot read"
                : $"{memAtLeast}{ResourceCapacityFold.Gigabytes((long)memAverages.Values.Average())} / {ResourceCapacityFold.Gigabytes((long)memPeaks.Values.Max())} GB",
        };
    }

    /// <summary>Per window, the sum of the figures that were read - and whether any session's figure had to be left out.</summary>
    private static (List<double> Values, bool LeftOut) Sums<T>(List<IGrouping<DateTime, ResourceSessionWindowEntity>> byWindow,
        Func<ResourceSessionWindowEntity, T?> figure) where T : struct, IConvertible
    {
        var values = new List<double>();
        var leftOut = false;
        foreach (var window in byWindow)
        {
            var read = window.Select(figure).Where(v => v.HasValue).Select(v => Convert.ToDouble(v!.Value)).ToList();
            if (read.Count < window.Count()) leftOut = true;
            if (read.Count > 0) values.Add(read.Sum());
        }
        return (values, leftOut);
    }
}
