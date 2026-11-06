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
        if (newest is null || newest.WindowStartUtc.AddSeconds(newest.WindowSeconds) < nowUtc - SilentAfter)
            return NotReporting(null, "This machine has sent no resource summary in the last 15 minutes. Its Director may be "
                                      + "older than the resource monitor, or its connection to the Gateway may be down.");

        var crossings = ResourceSummaryStore.CrossingsOf(newest);
        var dto = new FleetMachineCapacityDto { Reporting = true, ReadingWindowStartUtc = newest.WindowStartUtc };
        var why = new List<string>();

        // ---- the processor ----
        if (newest.ProcessorAveragePercent is { } cpuNow)
        {
            var peak = recent.Max(w => w.ProcessorPeakPercent);
            dto.ProcessorLabel = Percent(cpuNow);
            dto.ProcessorDetail = peak is { } p ? $"peak {Percent(p)}" : null;
            dto.ProcessorTone = ToneOf(crossings, ResourceSummaryContract.Processor);
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
            var peakUsed = recent.Max(w => w.MemoryPeakUsedBytes) ?? usedNow;
            var spare = Math.Max(0, total - peakUsed);
            dto.SpareBytes = spare;
            dto.SpareLabel = $"{Gigabytes(spare)} GB";
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

    /// <summary>Gigabytes to one decimal place, without a trailing ".0" at 10 or more (the mock-up's "16 GB").</summary>
    internal static string Gigabytes(long bytes)
    {
        var gb = bytes / 1_000_000_000.0;
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
            });
        }

        dto.Factories = sessionWindows
            .Where(s => string.Equals(s.MachineName, machine, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Factory) ? NoFactory : s.Factory!, StringComparer.OrdinalIgnoreCase)
            .Select(FactoryCost)
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
    /// </summary>
    private static FactoryResourceCostDto FactoryCost(IGrouping<string, ResourceSessionWindowEntity> factory)
    {
        var byWindow = factory.GroupBy(s => s.WindowStartUtc).ToList();
        var cpuAverages = byWindow.Where(g => g.Any(s => s.ProcessorAveragePercent.HasValue))
            .Select(g => g.Sum(s => s.ProcessorAveragePercent ?? 0)).ToList();
        var cpuPeaks = byWindow.Where(g => g.Any(s => s.ProcessorPeakPercent.HasValue))
            .Select(g => g.Sum(s => s.ProcessorPeakPercent ?? 0)).ToList();
        var memAverages = byWindow.Where(g => g.Any(s => s.MemoryAverageUsedBytes.HasValue))
            .Select(g => g.Sum(s => s.MemoryAverageUsedBytes ?? 0)).ToList();
        var memPeaks = byWindow.Where(g => g.Any(s => s.MemoryPeakUsedBytes.HasValue))
            .Select(g => g.Sum(s => s.MemoryPeakUsedBytes ?? 0)).ToList();

        double? cpuPeak = cpuPeaks.Count == 0 ? null : cpuPeaks.Max();
        return new FactoryResourceCostDto
        {
            Factory = factory.Key,
            Sessions = factory.Select(s => s.SessionId).Distinct(StringComparer.Ordinal).Count(),
            ProcessorPeakPercent = cpuPeak,
            ProcessorLabel = cpuAverages.Count == 0
                ? "cannot read"
                : $"{ResourceCapacityFold.Percent(cpuAverages.Average())} / {ResourceCapacityFold.Percent(cpuPeak!.Value)}",
            MemoryLabel = memAverages.Count == 0
                ? "cannot read"
                : $"{ResourceCapacityFold.Gigabytes((long)memAverages.Average())} / {ResourceCapacityFold.Gigabytes(memPeaks.Max())} GB",
        };
    }
}
