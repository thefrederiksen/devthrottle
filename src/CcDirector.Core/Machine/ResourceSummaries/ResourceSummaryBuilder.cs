using CcDirector.Gateway.Contracts;
// Two types share the name ResourceCrossing: the Gateway row type used here, and the level-change edge in
// CcDirector.Core.Machine. The nearer namespace would otherwise win, so this file names the one it means.
using GatewayResourceCrossing = CcDirector.Gateway.Contracts.ResourceCrossing;

namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// Turns one five-minute window of five-second samples into the summary the Gateway stores (Mission: Resource
/// Monitor, section 6): for the machine and for each session, the average, the peak and every amber or red crossing,
/// for memory and the processor.
///
/// Pure: no clock, no input or output, no state. Every case is a unit test over hand-made samples.
///
/// THE CROSSING NOTE IS THE POINT. A run of consecutive samples whose verdict is Tight (amber) or Critical (red)
/// becomes one <see cref="GatewayResourceCrossing"/>, carrying its worst level, when it started, how long it lasted inside the
/// window and how high the machine went. So a 90-second red spike in an otherwise quiet window survives the summary:
/// its average may be unremarkable, but its peak and its crossing are not.
///
/// A crossing is the MACHINE's. It is also copied onto the row of the session that was the biggest user of that
/// resource at the crossing's peak - so cost per factory can say which factory
/// was running hot when the machine was. A session never gets a crossing of its own invention: there are no
/// per-session limits.
/// </summary>
public static class ResourceSummaryBuilder
{
    /// <summary>The sampler's cadence. A crossing made of one sample lasted this long.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Build the summary of the window starting at <paramref name="windowStartUtc"/> from its samples. Samples outside
    /// the window are ignored, so a caller cannot smear one window into the next.
    /// </summary>
    public static ResourceSummaryWindow Build(DateTime windowStartUtc, string machineName, IEnumerable<ResourceSummarySample> samples)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("A resource summary needs the machine's name.", nameof(machineName));
        if (!ResourceSummaryContract.IsWindowStart(windowStartUtc))
            throw new ArgumentException($"{windowStartUtc:O} is not the start of a five-minute window.", nameof(windowStartUtc));
        ArgumentNullException.ThrowIfNull(samples);

        var start = DateTime.SpecifyKind(windowStartUtc, DateTimeKind.Utc);
        var end = start.AddSeconds(ResourceSummaryContract.WindowSeconds);
        var inWindow = samples
            .Where(s => s.AtUtc >= start && s.AtUtc < end)
            .OrderBy(s => s.AtUtc)
            .ToList();

        var machineCrossings = new List<(GatewayResourceCrossing Crossing, ResourceSummarySample PeakSample)>();
        machineCrossings.AddRange(Crossings(inWindow, ResourceSummaryContract.Processor, s => s.ProcessorLevel, ProcessorPercentOf, end));
        machineCrossings.AddRange(Crossings(inWindow, ResourceSummaryContract.Memory, s => s.MemoryLevel, MemoryPercentOf, end));
        machineCrossings.Sort((a, b) => a.Crossing.StartedUtc.CompareTo(b.Crossing.StartedUtc));

        var window = new ResourceSummaryWindow
        {
            WindowStartUtc = start,
            WindowSeconds = ResourceSummaryContract.WindowSeconds,
            MachineName = machineName.Trim(),
            SampleCount = inWindow.Count,
            Machine = MachineFigures(inWindow, machineCrossings.Select(c => c.Crossing).ToList()),
            SamplesWithUnknownSessions = inWindow.Count(s => s.SessionsUnreadableReason is not null),
            SessionsUnreadableReason = inWindow.LastOrDefault(s => s.SessionsUnreadableReason is not null)?.SessionsUnreadableReason,
            Drives = DriveSummaries(inWindow, end),
        };

        // Sessions in first-seen order, so the same samples always give the same row order.
        var sessionOrder = new List<string>();
        var bySession = new Dictionary<string, List<ResourceSummarySessionSample>>(StringComparer.Ordinal);
        foreach (var sample in inWindow)
        {
            foreach (var share in sample.Sessions)
            {
                if (string.IsNullOrWhiteSpace(share.SessionId)) continue;
                if (!bySession.TryGetValue(share.SessionId, out var list))
                {
                    list = new List<ResourceSummarySessionSample>();
                    bySession[share.SessionId] = list;
                    sessionOrder.Add(share.SessionId);
                }
                list.Add(share);
            }
        }

        foreach (var sessionId in sessionOrder)
        {
            var shares = bySession[sessionId];
            var attributed = machineCrossings
                .Where(c => BiggestUserAt(c.PeakSample, c.Crossing.Resource) == sessionId)
                .Select(c => Copy(c.Crossing))
                .ToList();
            window.Sessions.Add(new ResourceSessionSummary
            {
                SessionId = sessionId,
                Factory = shares.Select(s => s.Factory).LastOrDefault(f => !string.IsNullOrWhiteSpace(f))?.Trim(),
                SampleCount = shares.Count,
                ProcessesWithUnknownFigures = shares.Max(s => s.ProcessesWithUnknownFigures),
                Figures = SessionFigures(shares, attributed),
            });
        }

        return window;
    }

    /// <summary>
    /// One row per fixed drive seen in the window, in first-seen order: its size, free space at the newest sample that
    /// read it ("at the window's end"), the lowest free space any sample read, and each run of samples under the amber
    /// (25 GB) or red (10 GB) line. A drive no sample could read is a row with the reason and no figures.
    /// </summary>
    private static List<ResourceDriveSummary> DriveSummaries(List<ResourceSummarySample> samples, DateTime windowEnd)
    {
        var order = new List<string>();
        var byDrive = new Dictionary<string, List<(ResourceSummarySample Sample, ResourceSummaryDriveSample Drive)>>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            foreach (var drive in sample.Drives)
            {
                if (string.IsNullOrWhiteSpace(drive.Name)) continue;
                if (!byDrive.TryGetValue(drive.Name, out var list))
                {
                    list = new List<(ResourceSummarySample, ResourceSummaryDriveSample)>();
                    byDrive[drive.Name] = list;
                    order.Add(drive.Name);
                }
                list.Add((sample, drive));
            }
        }

        var rows = new List<ResourceDriveSummary>(order.Count);
        foreach (var name in order)
        {
            var seen = byDrive[name];
            var read = seen.Where(x => x.Drive.FreeBytes.HasValue && x.Drive.TotalBytes.HasValue).ToList();
            var row = new ResourceDriveSummary { Name = name, CouldRead = read.Count > 0 };
            if (read.Count == 0)
            {
                row.UnreadableReason = ReasonOf(seen.Select(x => x.Drive.UnreadableReason), seen.Count, $"drive {name}");
                rows.Add(row);
                continue;
            }
            row.TotalBytes = read[^1].Drive.TotalBytes;
            row.FreeBytesAtEnd = read[^1].Drive.FreeBytes;
            row.LowestFreeBytes = read.Min(x => x.Drive.FreeBytes!.Value);
            row.Crossings = DriveCrossings(read, windowEnd);
            rows.Add(row);
        }
        return rows;
    }

    private static List<GatewayResourceCrossing> DriveCrossings(
        List<(ResourceSummarySample Sample, ResourceSummaryDriveSample Drive)> read, DateTime windowEnd)
    {
        var crossings = new List<GatewayResourceCrossing>();
        var run = new List<(ResourceSummarySample Sample, ResourceSummaryDriveSample Drive)>();
        foreach (var point in read)
        {
            if (DiskLevel(point.Drive.FreeBytes!.Value) is ResourceLevel.Tight or ResourceLevel.Critical)
            {
                run.Add(point);
                continue;
            }
            if (run.Count > 0) crossings.Add(DriveCrossingOf(run, windowEnd));
            run = new List<(ResourceSummarySample, ResourceSummaryDriveSample)>();
        }
        if (run.Count > 0) crossings.Add(DriveCrossingOf(run, windowEnd));
        return crossings;
    }

    private static GatewayResourceCrossing DriveCrossingOf(
        List<(ResourceSummarySample Sample, ResourceSummaryDriveSample Drive)> run, DateTime windowEnd)
    {
        var first = run[0].Sample.AtUtc;
        var lastEnds = run[^1].Sample.AtUtc + SampleInterval;
        if (lastEnds > windowEnd) lastEnds = windowEnd;
        var lowestFree = run.Min(x => x.Drive.FreeBytes!.Value);
        var fullest = run.Where(x => x.Drive.TotalBytes > 0)
            .Select(x => (double?)(100.0 - x.Drive.FreeBytes!.Value * 100.0 / x.Drive.TotalBytes!.Value))
            .Max();
        return new GatewayResourceCrossing
        {
            Resource = ResourceSummaryContract.Disk,
            Level = DiskLevel(lowestFree) == ResourceLevel.Critical ? ResourceSummaryContract.Red : ResourceSummaryContract.Amber,
            StartedUtc = DateTime.SpecifyKind(first, DateTimeKind.Utc),
            Seconds = Math.Max(1, (int)Math.Round((lastEnds - first).TotalSeconds)),
            PeakPercent = fullest,
        };
    }

    // The same lines as the meter's DISK bar, read from the one pair of numbers both use.
    private static ResourceLevel DiskLevel(long freeBytes) =>
        freeBytes < 0 ? ResourceLevel.Unknown : DiskSpacePressure.LevelOfFree((ulong)freeBytes);

    private static ResourceSummaryFigures MachineFigures(List<ResourceSummarySample> samples, List<GatewayResourceCrossing> crossings)
    {
        var figures = new ResourceSummaryFigures { Crossings = crossings };

        var processor = samples.Where(s => s.ProcessorPercent.HasValue).Select(s => s.ProcessorPercent!.Value).ToList();
        figures.ProcessorSamples = processor.Count;
        figures.ProcessorCouldRead = processor.Count > 0;
        if (processor.Count > 0)
        {
            figures.ProcessorAveragePercent = processor.Average();
            figures.ProcessorPeakPercent = processor.Max();
        }
        else
        {
            figures.ProcessorUnreadableReason = ReasonOf(samples.Select(s => s.ProcessorUnreadableReason), samples.Count, "the processor");
        }

        var memory = samples.Where(s => s.MemoryUsedBytes.HasValue).Select(s => s.MemoryUsedBytes!.Value).ToList();
        figures.MemorySamples = memory.Count;
        figures.MemoryCouldRead = memory.Count > 0;
        if (memory.Count > 0)
        {
            figures.MemoryAverageUsedBytes = (long)Math.Round(memory.Average(b => (double)b));
            figures.MemoryPeakUsedBytes = memory.Max();
            figures.MemoryTotalBytes = samples.LastOrDefault(s => s.MemoryTotalBytes.HasValue)?.MemoryTotalBytes;
        }
        else
        {
            figures.MemoryUnreadableReason = ReasonOf(samples.Select(s => s.MemoryUnreadableReason), samples.Count, "memory");
        }
        return figures;
    }

    private static ResourceSummaryFigures SessionFigures(List<ResourceSummarySessionSample> shares, List<GatewayResourceCrossing> crossings)
    {
        var figures = new ResourceSummaryFigures { Crossings = crossings };

        var processor = shares.Where(s => s.ProcessorPercent.HasValue).Select(s => s.ProcessorPercent!.Value).ToList();
        figures.ProcessorSamples = processor.Count;
        figures.ProcessorCouldRead = processor.Count > 0;
        if (processor.Count > 0)
        {
            figures.ProcessorAveragePercent = processor.Average();
            figures.ProcessorPeakPercent = processor.Max();
        }
        else
        {
            figures.ProcessorUnreadableReason = ReasonOf(shares.Select(s => s.UnreadableReason), shares.Count, "the session's processor use");
        }

        var memory = shares.Where(s => s.MemoryBytes.HasValue).Select(s => s.MemoryBytes!.Value).ToList();
        figures.MemorySamples = memory.Count;
        figures.MemoryCouldRead = memory.Count > 0;
        if (memory.Count > 0)
        {
            figures.MemoryAverageUsedBytes = (long)Math.Round(memory.Average(b => (double)b));
            figures.MemoryPeakUsedBytes = memory.Max();
        }
        else
        {
            figures.MemoryUnreadableReason = ReasonOf(shares.Select(s => s.UnreadableReason), shares.Count, "the session's memory");
        }
        return figures;
    }

    /// <summary>The reason the newest sample gave, or a plain statement of what happened when none gave one.</summary>
    private static string ReasonOf(IEnumerable<string?> reasons, int sampleCount, string what)
    {
        var given = reasons.LastOrDefault(r => !string.IsNullOrWhiteSpace(r));
        if (given is not null) return given.Trim();
        return sampleCount == 0
            ? $"No sample was taken in this window, so {what} was not read."
            : $"None of the {sampleCount} samples in this window read {what}, and the sampler gave no reason.";
    }

    private static IEnumerable<(GatewayResourceCrossing Crossing, ResourceSummarySample PeakSample)> Crossings(
        List<ResourceSummarySample> samples, string resource, Func<ResourceSummarySample, ResourceLevel> levelOf,
        Func<ResourceSummarySample, double?> percentOf, DateTime windowEnd)
    {
        var run = new List<ResourceSummarySample>();
        foreach (var sample in samples)
        {
            if (IsOver(levelOf(sample)))
            {
                run.Add(sample);
                continue;
            }
            if (run.Count > 0) yield return CrossingOf(run, resource, levelOf, percentOf, windowEnd);
            run = new List<ResourceSummarySample>();
        }
        if (run.Count > 0) yield return CrossingOf(run, resource, levelOf, percentOf, windowEnd);
    }

    private static bool IsOver(ResourceLevel level) => level is ResourceLevel.Tight or ResourceLevel.Critical;

    private static (GatewayResourceCrossing, ResourceSummarySample) CrossingOf(List<ResourceSummarySample> run, string resource,
        Func<ResourceSummarySample, ResourceLevel> levelOf, Func<ResourceSummarySample, double?> percentOf, DateTime windowEnd)
    {
        var first = run[0];
        var last = run[^1];
        var worst = run.Max(levelOf);
        var lastEnds = last.AtUtc + SampleInterval;
        if (lastEnds > windowEnd) lastEnds = windowEnd;

        var peakSample = run.Where(s => percentOf(s).HasValue).OrderByDescending(s => percentOf(s)!.Value).FirstOrDefault() ?? first;
        var crossing = new GatewayResourceCrossing
        {
            Resource = resource,
            Level = worst == ResourceLevel.Critical ? ResourceSummaryContract.Red : ResourceSummaryContract.Amber,
            StartedUtc = DateTime.SpecifyKind(first.AtUtc, DateTimeKind.Utc),
            // At least one second: a crossing that began in the window's last half second still happened, and rounding
            // it to zero would describe a crossing of no length.
            Seconds = Math.Max(1, (int)Math.Round((lastEnds - first.AtUtc).TotalSeconds)),
            PeakPercent = percentOf(peakSample),
        };
        return (crossing, peakSample);
    }

    private static double? ProcessorPercentOf(ResourceSummarySample s) => s.ProcessorPercent;

    private static double? MemoryPercentOf(ResourceSummarySample s) =>
        s.MemoryUsedBytes is { } used && s.MemoryTotalBytes is { } total && total > 0
            ? used * 100.0 / total
            : null;

    /// <summary>The session using the most of a resource at one sample, or null when no session's figure was read.</summary>
    private static string? BiggestUserAt(ResourceSummarySample sample, string resource)
    {
        ResourceSummarySessionSample? biggest = null;
        foreach (var share in sample.Sessions)
        {
            var value = resource == ResourceSummaryContract.Processor ? share.ProcessorPercent : share.MemoryBytes;
            if (value is null) continue;
            var current = biggest is null ? (double?)null
                : resource == ResourceSummaryContract.Processor ? biggest.ProcessorPercent : biggest.MemoryBytes;
            if (current is null || value.Value > current.Value) biggest = share;
        }
        return biggest?.SessionId;
    }

    private static GatewayResourceCrossing Copy(GatewayResourceCrossing c) => new()
    {
        Resource = c.Resource,
        Level = c.Level,
        StartedUtc = c.StartedUtc,
        Seconds = c.Seconds,
        PeakPercent = c.PeakPercent,
    };
}
