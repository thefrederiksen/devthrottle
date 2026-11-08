using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// Turns one sampler state into the five-minute summary's own sample shape. A pure function.
///
/// NOTHING UNKNOWN BECOMES A ZERO ON THE WAY. A processor or memory figure that could not be read stays
/// null with its reason, and a session whose every process refused a figure has a null figure rather than
/// the zero its sum would add up to.
/// </summary>
public static class ResourceSummarySampleFold
{
    /// <summary>
    /// Builds the summary sample, or null before the first read (there is nothing to summarise).
    /// <paramref name="sessions"/> is null when the sessions could not be measured at this read: the sample then
    /// carries no session shares AND says why, so the summary never reads "no sessions ran" into a read that failed.
    /// </summary>
    public static ResourceSummarySample? From(ResourceSamplerState state, IReadOnlyList<ResourceSessionInput>? sessions)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Latest is not { } sample) return null;

        var sessionsReason = sessions is null
            ? sample.Processes.CouldRead
                ? "the sessions were not measured at this read"
                : $"the process list could not be read: {sample.Processes.UnreadableReason}"
            : null;

        var cpuFraction = ResourceViewText.CpuFraction(sample.Cpu);
        var cpuReason = cpuFraction is null
            ? sample.Cpu.CouldRead ? ResourceViewText.CpuOutOfRangeReason(sample.Cpu) : sample.Cpu.UnreadableReason
            : null;

        var memoryFraction = ResourceViewText.MemoryUsedFraction(sample.Memory);
        long? used = memoryFraction is null ? null : (long)(sample.Memory.TotalBytes - sample.Memory.AvailableBytes);
        long? total = memoryFraction is null ? null : (long)sample.Memory.TotalBytes;
        var memoryReason = memoryFraction is null
            ? sample.Memory.CouldRead ? "the memory reading did not add up" : sample.Memory.UnreadableReason
            : null;

        return new ResourceSummarySample(
            sample.TakenAtUtc,
            cpuFraction * 100,
            cpuReason,
            state.VerdictFor(ResourceKind.Cpu).Level,
            used,
            total,
            memoryReason,
            state.VerdictFor(ResourceKind.Memory).Level,
            sessions is null ? Array.Empty<ResourceSummarySessionSample>() : sessions.Select(Session).ToList(),
            sessionsReason,
            Drives(sample.DiskSpace));
    }

    /// <summary>
    /// Each fixed drive, as figures or as the reason it could not be read. When the drives could not be listed at all
    /// there is no drive to name, so the sample carries none; the window then has no drive rows for those samples,
    /// which the Gateway reads as "not reported", exactly as from an older Director.
    /// </summary>
    private static IReadOnlyList<ResourceSummaryDriveSample> Drives(DiskSpaceSet disks)
    {
        if (!disks.CouldRead)
        {
            FileLog.Write($"[ResourceSummarySampleFold] Drives: the drives could not be listed at this sample: {disks.UnreadableReason}");
            return Array.Empty<ResourceSummaryDriveSample>();
        }
        return disks.Drives
            .Select(d => d.CouldRead
                ? new ResourceSummaryDriveSample(d.Name, (long)d.TotalBytes, (long)d.FreeBytes, null)
                : new ResourceSummaryDriveSample(d.Name, null, null, d.UnreadableReason))
            .ToList();
    }

    private static ResourceSummarySessionSample Session(ResourceSessionInput input)
    {
        var usage = input.Usage;
        var processes = usage.Pids.Count;
        var processorUnknown = processes == 0 || usage.ProcessesWithUnknownProcessor >= processes;
        var memoryUnknown = processes == 0 || usage.ProcessesWithUnknownMemory >= processes;

        string? reason = (processorUnknown, memoryUnknown) switch
        {
            (true, true) => "no process in this session's tree had a readable figure",
            (true, false) => "no process in this session's tree had a readable processor figure",
            (false, true) => "no process in this session's tree had a readable memory figure",
            _ => null,
        };

        // The summary carries one count. The larger of the two is the honest floor for "how many processes had a
        // figure missing", and it is above zero exactly when either total is a lower bound - which is what the
        // summary's rows use it for.
        var withUnknown = Math.Max(usage.ProcessesWithUnknownProcessor, usage.ProcessesWithUnknownMemory);

        return new ResourceSummarySessionSample(
            usage.SessionId,
            input.Factory,
            processorUnknown ? null : usage.CpuPercent,
            memoryUnknown ? null : (long)usage.MemoryBytes,
            reason,
            withUnknown);
    }
}
