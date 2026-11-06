using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// Turns one five-minute window of five-second samples into the summary the Gateway stores (Mission: Resource
/// Monitor, section 6): for the machine and for each session, the average, the peak and every amber or red crossing,
/// for memory and the processor.
///
/// Pure: no clock, no input or output, no state. Every case is a unit test over hand-made samples.
///
/// THE CROSSING NOTE IS THE POINT. A run of consecutive samples whose verdict is Tight (amber) or Critical (red)
/// becomes one <see cref="ResourceCrossing"/>, carrying its worst level, when it started, how long it lasted inside the
/// window and how high the machine went. So a 90-second red spike in an otherwise quiet window survives the summary:
/// its average may be unremarkable, but its peak and its crossing are not.
///
/// A crossing is the MACHINE's. It is also copied onto the row of the session that was the biggest user of that
/// resource at the crossing's peak - the session the warning banner names - so cost per factory can say which factory
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

        var machineCrossings = new List<(ResourceCrossing Crossing, ResourceSummarySample PeakSample)>();
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

    private static ResourceSummaryFigures MachineFigures(List<ResourceSummarySample> samples, List<ResourceCrossing> crossings)
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

    private static ResourceSummaryFigures SessionFigures(List<ResourceSummarySessionSample> shares, List<ResourceCrossing> crossings)
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

    private static IEnumerable<(ResourceCrossing Crossing, ResourceSummarySample PeakSample)> Crossings(
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

    private static (ResourceCrossing, ResourceSummarySample) CrossingOf(List<ResourceSummarySample> run, string resource,
        Func<ResourceSummarySample, ResourceLevel> levelOf, Func<ResourceSummarySample, double?> percentOf, DateTime windowEnd)
    {
        var first = run[0];
        var last = run[^1];
        var worst = run.Max(levelOf);
        var lastEnds = last.AtUtc + SampleInterval;
        if (lastEnds > windowEnd) lastEnds = windowEnd;

        var peakSample = run.Where(s => percentOf(s).HasValue).OrderByDescending(s => percentOf(s)!.Value).FirstOrDefault() ?? first;
        var crossing = new ResourceCrossing
        {
            Resource = resource,
            Level = worst == ResourceLevel.Critical ? ResourceSummaryContract.Red : ResourceSummaryContract.Amber,
            StartedUtc = DateTime.SpecifyKind(first.AtUtc, DateTimeKind.Utc),
            Seconds = (int)Math.Round((lastEnds - first.AtUtc).TotalSeconds),
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

    private static ResourceCrossing Copy(ResourceCrossing c) => new()
    {
        Resource = c.Resource,
        Level = c.Level,
        StartedUtc = c.StartedUtc,
        Seconds = c.Seconds,
        PeakPercent = c.PeakPercent,
    };
}
