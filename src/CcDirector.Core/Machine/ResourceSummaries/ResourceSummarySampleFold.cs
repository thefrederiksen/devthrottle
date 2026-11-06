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
    /// <summary>Builds the summary sample, or null before the first read (there is nothing to summarise).</summary>
    public static ResourceSummarySample? From(ResourceSamplerState state, IReadOnlyList<ResourceSessionInput> sessions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sessions);
        if (state.Latest is not { } sample) return null;

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
            sessions.Select(Session).ToList());
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
