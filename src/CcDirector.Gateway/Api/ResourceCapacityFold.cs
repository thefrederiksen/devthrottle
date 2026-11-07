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
/// THE RULES, in one place (the hour's peak and Spare also say how much of the hour they rest on - see
/// <see cref="MeasuredMinutes"/>):
///  - "Now" is the newest five-minute window start on the machine, and each figure is taken from whichever Director's
///    row at that start READ it - two Directors both measure the whole machine, so one that could not read a figure
///    does not hide the other's reading. The peak is the highest five-second reading in the last hour's windows, by
///    the same rule.
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
    /// window of the tenant from one window before the last <see cref="PeakSpan"/> (so the window straddling the hour's
    /// start is in), or null when the statistics store could not be read.
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

        // Every window with any part inside the hour, including the one straddling its start: the minutes it measured
        // inside the hour are counted (MeasuredMinutes), so its readings must be too, or a spike in them is claimed as
        // measured and left out of the peak (review of #4687, finding 3).
        var recent = windows
            .Where(w => w.WindowStartUtc.AddSeconds(w.WindowSeconds > 0 ? w.WindowSeconds : ResourceSummaryContract.WindowSeconds) > nowUtc - PeakSpan)
            .ToList();
        // THE "NOW" FIGURES COME PER RESOURCE FROM THE NEWEST START GROUP, the same way the hour's peak does
        // (issue #4678). Two Directors on one machine both measure the WHOLE machine, so a figure one of them could
        // not read is there when the other read it. Choosing a single row - by start, then by sample count, ties
        // broken by whatever order the store happened to return - made the two halves of this fold disagree about
        // what a same-start pair means: the hour counted that start as covered, while "now" showed "cannot read" or
        // "Figures refused" for the whole machine depending on which row came back first.
        var newestStart = recent.Count == 0 ? (DateTime?)null : recent.Max(w => w.WindowStartUtc);
        var atNewest = recent
            .Where(w => w.WindowStartUtc == newestStart)
            .OrderByDescending(w => w.SampleCount)
            .ToList();
        var newest = atNewest.FirstOrDefault();
        // The GROUP's end, not the chosen row's: one Director's row may carry a shorter window than another's, and
        // silence is a question about the machine.
        var newestEnd = newestStart?.AddSeconds(
            atNewest.Max(w => w.WindowSeconds > 0 ? w.WindowSeconds : ResourceSummaryContract.WindowSeconds));
        var newestReceived = windows.Count == 0 ? (DateTime?)null : windows.Max(w => w.ReceivedUtc);
        if (newest is null || newestEnd < nowUtc - SilentAfter)
        {
            // A Director whose CLOCK is behind sends on time but stamps its windows in the past: received recently, timed
            // long ago. That is a clock problem, and naming the connection would send someone to the wrong place.
            if (newestReceived is { } received && received >= nowUtc - SilentAfter)
                return NotReporting("This machine sent a resource summary in the last 15 minutes, but every window it sent "
                                          + "is stamped more than 15 minutes ago by the Gateway's clock. Its clock is probably behind.");
            return NotReporting("This machine has sent no resource summary in the last 15 minutes. Its Director may be "
                                      + "older than the resource monitor, or its connection to the Gateway may be down.");
        }

        // A REFUSED window is neither a reading nor silence: the machine sent figures no machine can produce. The
        // machine says so only when EVERY row at the newest start was refused - one Director's refusal must not hide
        // another Director's good reading of the same machine (issue #4678).
        if (atNewest.TrueForAll(w => w.RefusedReason is not null))
        {
            var refused = atNewest[0].RefusedReason!;
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
        }

        // Whether the hour's peak is known is decided PER RESOURCE, and by every window in the hour - not by whether one
        // was refused. A window can be unread for the processor and read for memory, and Max over a nullable figure skips
        // the null without saying so: the highest READABLE peak would be printed as the hour's peak, a believable
        // understatement (issue #4670, after review finding 13). A refused window has no figures, so it is one case of this.
        var processorGap = HourGap(recent, w => w.ProcessorPeakPercent is not null, w => w.ProcessorUnreadableReason);
        var memoryGap = HourGap(recent, w => w.MemoryPeakUsedBytes is not null, w => w.MemoryUnreadableReason);
        var processorPeakUnknown = false;
        var spareUnknownFromGap = false;
        // How much of the hour each figure rests on (issue #4679). Null when the hour was measured whole.
        var processorMeasured = MeasuredMinutes(windows, nowUtc, w => w.ProcessorSamples);
        var memoryMeasured = MeasuredMinutes(windows, nowUtc, w => w.MemorySamples);
        var processorPeakPartial = false;
        var sparePartial = false;
        // PER RESOURCE, THE ROW AT THE NEWEST START THAT READ IT, and "cannot read" only when none of them did.
        var processorRow = atNewest.Find(w => w.ProcessorAveragePercent is not null);
        var memoryRow = atNewest.Find(w => w.MemoryAverageUsedBytes is not null && w.MemoryTotalBytes > 0);
        var dto = new FleetMachineCapacityDto { Reporting = true, ReadingWindowStartUtc = newest.WindowStartUtc };
        var why = new List<string>();

        // ---- the processor ----
        if (processorRow?.ProcessorAveragePercent is { } cpuNow)
        {
            dto.ProcessorLabel = Percent(cpuNow);
            // THE TONE COMES FROM THE ROW THAT GAVE THE FIGURE. Reading the figure from the Director that measured it
            // and the crossing from another would show one Director's 95 per cent beside the other's "no crossing".
            dto.ProcessorTone = ToneOf(ResourceSummaryStore.CrossingsOf(processorRow), ResourceSummaryContract.Processor);
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
                // An hour that was not measured whole has no peak, only a highest reading: the real peak is at least it.
                processorPeakPartial = processorMeasured is not null;
                dto.ProcessorDetail = processorPeakPartial ? $"peak at least {Percent(peak)}" : $"peak {Percent(peak)}";
            }
        }
        else
        {
            dto.ProcessorLabel = "cannot read";
            dto.ProcessorTone = FleetTone.Idle;
            var reason = atNewest.Select(w => w.ProcessorUnreadableReason).FirstOrDefault(r => r is not null);
            why.Add($"The processor could not be read: {reason ?? "the Director gave no reason"}");
        }

        // ---- memory, and spare ----
        if (memoryRow?.MemoryAverageUsedBytes is { } usedNow && memoryRow.MemoryTotalBytes is { } total && total > 0)
        {
            dto.MemoryLabel = Percent(usedNow * 100.0 / total);
            dto.MemoryDetail = $"{Gigabytes(usedNow)} / {Gigabytes(total)} GB";
            dto.MemoryTone = ToneOf(ResourceSummaryStore.CrossingsOf(memoryRow), ResourceSummaryContract.Memory);
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
                // An hour that was not measured whole may have peaked in the part nobody measured, so Spare from it is an
                // UPPER bound - "at most". It is not sent as a number to sort on: placing a factory by an upper bound is the
                // mistake Spare exists to prevent, so it sorts with the unknowns (issue #4679).
                else if (memoryMeasured is not null)
                {
                    sparePartial = true;
                    dto.SpareLabel = $"at most {Gigabytes(total - peakUsed)} GB";
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
            var reason = atNewest.Select(w => w.MemoryUnreadableReason).FirstOrDefault(r => r is not null);
            why.Add(reason is not null
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

        // How much of the hour the figures rest on, said once when both rest on the same minutes.
        if (processorPeakPartial && sparePartial && processorMeasured == memoryMeasured)
            why.Add($"Only {processorMeasured} of the last {(int)PeakSpan.TotalMinutes} minutes were measured, so the hour's peak "
                    + "processor use is at least what is shown and the spare capacity at most what is shown.");
        else
        {
            if (processorPeakPartial)
                why.Add($"The processor was measured for only {processorMeasured} of the last {(int)PeakSpan.TotalMinutes} minutes, "
                        + "so the hour's peak is at least what is shown.");
            if (sparePartial)
                why.Add($"Memory was measured for only {memoryMeasured} of the last {(int)PeakSpan.TotalMinutes} minutes, "
                        + "so the spare capacity is at most what is shown.");
        }

        (dto.StatusLabel, dto.StatusTone) = StatusOf(dto);
        dto.Why = why.Count == 0 ? null : string.Join(" ", why);
        return dto;
    }

    /// <summary>A full window's samples: one every five seconds for the window's five minutes.</summary>
    internal static readonly int FullWindowSamples =
        (int)(ResourceSummaryContract.WindowSeconds / CcDirector.Core.Machine.ResourceSummaries.ResourceSummaryBuilder.SampleInterval.TotalSeconds);

    /// <summary>
    /// How many whole minutes of the last hour one resource was measured for, or null when the hour was measured whole
    /// (issue #4679). It is the hour MINUS what was not measured, so it agrees with the expanded chart by construction:
    ///  - minus every gap <see cref="ResourceWindowGaps.Of"/> finds - the one definition the chart draws. That rule
    ///    has two allowances of its own, which this inherits rather than redefines: a stretch shorter than one window is
    ///    an hour boundary, not a gap, and the stretch after the newest window is not a gap until
    ///    <see cref="ResourceWindowGaps.TailAllowance"/> (one window plus delivery), because the window being collected
    ///    is not missing yet. Not <see cref="SilentAfter"/>: that is when the row goes to dashes, a different question;
    ///  - minus the samples a window is short of a full one, five seconds each, for the part of the window inside the
    ///    hour. A row of four samples was measured for twenty seconds, not five minutes.
    /// The hour counts as whole when there is no gap and no window inside it is more than ONE sample short. That one
    /// sample is an expectation about the sampler (phase 2, not yet built when this was written): a five-second timer
    /// against five-minute wall-clock boundaries should land 59 as well as 60. A sampler that drifts further than that -
    /// one that does its work and then waits five seconds will, on a loaded machine - makes every hour read as partial,
    /// which is the honest answer for such a sampler, not a reason to widen this. The minutes are rounded DOWN.
    /// <paramref name="windows"/> must include the window straddling the hour's start (the caller reads from
    /// <see cref="MachineResourceHistoryFold.ReadFromUtc"/>), or the hour's first minutes would count as a gap.
    /// </summary>
    internal static int? MeasuredMinutes(IReadOnlyList<ResourceWindowEntity> windows, DateTime nowUtc, Func<ResourceWindowEntity, int> samples)
    {
        var hourStart = nowUtc - PeakSpan;
        var sampleSeconds = CcDirector.Core.Machine.ResourceSummaries.ResourceSummaryBuilder.SampleInterval.TotalSeconds;
        // One row per window start (two Directors on one machine measure the same machine), the fuller one.
        var perStart = windows
            .GroupBy(w => w.WindowStartUtc)
            .Select(g => (Start: g.Key, Seconds: g.Max(w => w.WindowSeconds > 0 ? w.WindowSeconds : ResourceSummaryContract.WindowSeconds),
                Samples: g.Max(samples)))
            .Where(w => w.Start.AddSeconds(w.Seconds) > hourStart && w.Start < nowUtc)
            .ToList();
        var gaps = ResourceWindowGaps.Of(windows, hourStart, nowUtc);
        if (gaps.Count == 0 && perStart.All(w => w.Samples >= FullWindowSamples - 1)) return null;

        var gapSeconds = gaps.Sum(g => (g.EndUtc - g.StartUtc).TotalSeconds);
        var shortSeconds = 0.0;
        foreach (var w in perStart)
        {
            var end = w.Start.AddSeconds(w.Seconds);
            var inside = ((end < nowUtc ? end : nowUtc) - (w.Start > hourStart ? w.Start : hourStart)).TotalSeconds;
            shortSeconds += (FullWindowSamples - Math.Min(w.Samples, FullWindowSamples)) * sampleSeconds * (inside / w.Seconds);
        }
        return (int)Math.Floor((PeakSpan.TotalSeconds - gapSeconds - shortSeconds) / 60);
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
