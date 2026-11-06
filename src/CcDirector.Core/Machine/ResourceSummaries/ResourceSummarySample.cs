namespace CcDirector.Core.Machine.ResourceSummaries;

/// <summary>
/// One five-second sample as the five-minute summary needs it (Mission: Resource Monitor, section 6). The sampler
/// (phase 2) builds one of these from each <see cref="ResourceSample"/> it takes and hands it to
/// <see cref="ResourceSummaryRecorder.Add"/>; the summary is built from these and nothing else.
///
/// It is its own small shape, rather than <see cref="ResourceSample"/> itself, so the summary's contract does not
/// move every time the probe types do: phase 1 is still narrowing them.
///
/// THE LEVELS ARE ALREADY DECIDED. <see cref="ProcessorLevel"/> and <see cref="MemoryLevel"/> are the verdicts the
/// sampler's ResourcePressure gave this sample, 60-second rule included, so the summary records exactly the
/// crossings the meter showed and never second-guesses them.
/// </summary>
/// <param name="AtUtc">When the sample was taken, in UTC.</param>
/// <param name="ProcessorPercent">Whole-machine processor use, 0 to 100, or null when it could not be read.</param>
/// <param name="ProcessorUnreadableReason">Why the processor could not be read, when it could not.</param>
/// <param name="ProcessorLevel">The processor verdict for this sample.</param>
/// <param name="MemoryUsedBytes">Memory in use, or null when it could not be read.</param>
/// <param name="MemoryTotalBytes">The machine's total memory, or null when it could not be read.</param>
/// <param name="MemoryUnreadableReason">Why memory could not be read, when it could not.</param>
/// <param name="MemoryLevel">The memory verdict for this sample.</param>
/// <param name="Sessions">Each session's share at this sample, child processes included.</param>
public sealed record ResourceSummarySample(
    DateTime AtUtc,
    double? ProcessorPercent,
    string? ProcessorUnreadableReason,
    ResourceLevel ProcessorLevel,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    string? MemoryUnreadableReason,
    ResourceLevel MemoryLevel,
    IReadOnlyList<ResourceSummarySessionSample> Sessions);

/// <summary>One session's share of one sample.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Factory">The session's Factory value, or null when it belongs to none.</param>
/// <param name="ProcessorPercent">The session's process tree's processor use as a percentage of the whole machine,
/// or null when it could not be read.</param>
/// <param name="MemoryBytes">The session's process tree's memory, or null when it could not be read.</param>
/// <param name="UnreadableReason">Why a figure could not be read, when one could not.</param>
public sealed record ResourceSummarySessionSample(
    string SessionId,
    string? Factory,
    double? ProcessorPercent,
    long? MemoryBytes,
    string? UnreadableReason);
