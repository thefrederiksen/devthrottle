namespace CcDirector.Core.Machine;

/// <summary>
/// The fixed numbers of the Director's resource sampler (the Resource Monitor mission, issue #2811,
/// section 5a and section 6). Held once, so the sampler, its tests, the meter's "updated" text and the
/// live load harness cannot disagree about how often the machine is read.
/// </summary>
public static class ResourceSampling
{
    /// <summary>
    /// How often the sampler reads the probe: every five seconds. The live meter and the 60-second
    /// warning rule both need this grain; the five-minute Gateway summary does not change it.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How much history the sampler keeps in memory: the last hour.</summary>
    public static readonly TimeSpan HistoryLength = TimeSpan.FromHours(1);

    /// <summary>
    /// The ring buffer's size in samples: one hour at five seconds, 720. The buffer is bounded by COUNT,
    /// never by comparing timestamps, so a wall clock that jumps forwards or backwards cannot empty it or
    /// let it grow without limit.
    /// </summary>
    public const int HistoryCapacity = 720;
}

/// <summary>
/// The Director's resource sampler: the one background job that reads the machine and tells the meter,
/// the banner and the detail screen what it found (the Resource Monitor mission, issue #2811).
///
/// THE CONTRACT EVERY IMPLEMENTATION KEEPS:
/// <list type="bullet">
/// <item>It reads its <see cref="IResourceProbe"/> every <see cref="ResourceSampling.Interval"/> and keeps
/// the last <see cref="ResourceSampling.HistoryCapacity"/> samples in a ring buffer.</item>
/// <item>After every read it folds the whole history into a <see cref="ResourcePressureVerdict"/> with
/// <c>ResourcePressure.Evaluate</c> from Core (phase 1), which owns the limits and the 60-second rule.
/// The sampler never decides what a figure means itself.</item>
/// <item>It feeds every verdict to one <see cref="ResourceCrossingDetector"/> and raises
/// <see cref="Crossed"/> once for each crossing the detector returns, AFTER <see cref="Sampled"/> for the
/// same read, so a handler of <see cref="Crossed"/> always finds <see cref="Current"/> already updated.</item>
/// <item>It takes a <see cref="TimeProvider"/> for its timer, so a test drives it with a virtual clock and
/// no real waiting. It never reads the wall clock to measure anything; the probe stamps each sample.</item>
/// <item>A probe that throws is a defect in the probe (the probe contract says it never throws). The
/// sampler logs it with the exception, skips that tick, and keeps its timer running: one broken read must
/// not end the meter for the rest of the Director's life.</item>
/// <item>Both events are raised on the sampler's own thread, never the user-interface thread. A view
/// model that handles them dispatches to the user-interface thread itself (CLAUDE.md rule 6).</item>
/// <item>No subscriber can stall it: an exception thrown by a handler is logged and does not stop the
/// timer.</item>
/// </list>
/// </summary>
public interface IResourceSampler : IDisposable
{
    /// <summary>
    /// The latest sample and its verdict. Before the first read this is
    /// <see cref="ResourceSamplerState.NotYetSampled"/>, which the meter shows as "cannot read" - never as
    /// zero. Safe to read from any thread.
    /// </summary>
    ResourceSamplerState Current { get; }

    /// <summary>
    /// A copy of the ring buffer, oldest first, at most <see cref="ResourceSampling.HistoryCapacity"/>
    /// samples. A copy, so a caller can walk it while the sampler keeps writing. Safe to read from any
    /// thread.
    /// </summary>
    IReadOnlyList<ResourceSample> History { get; }

    /// <summary>
    /// How long ago the latest sample was taken, on the monotonic clock; null before the first read. The meter's
    /// "updated N seconds ago" reads this and never the sample's wall-clock stamp, which a stepped clock would
    /// make read as "just now" or "40 minutes ago". Added by phase 2a; see the pull request for the reason.
    /// </summary>
    TimeSpan? LatestAge { get; }

    /// <summary>Raised after every read, carrying the new <see cref="Current"/>.</summary>
    event EventHandler<ResourceSamplerState>? Sampled;

    /// <summary>
    /// Raised when a resource crosses into amber or red, moves between them, or comes back to normal.
    /// What each crossing means for the banner is already decided on the event - see
    /// <see cref="ResourceCrossing.ShowsBanner"/> and <see cref="ResourceCrossing.EndsWarning"/>.
    /// </summary>
    event EventHandler<ResourceCrossingEventArgs>? Crossed;

    /// <summary>Starts the timer. The first read happens at once rather than five seconds later.</summary>
    void Start();
}

/// <summary>
/// What the sampler knows right now: the newest sample and the verdict folded from the history ending in it.
/// </summary>
/// <param name="Latest">The newest sample. Null before the first read.</param>
/// <param name="Verdict">The verdict on every resource. Every resource is Unknown before the first read.</param>
public sealed record ResourceSamplerState(ResourceSample? Latest, ResourcePressureVerdict Verdict)
{
    /// <summary>
    /// The state before the first read: no sample, and every resource Unknown. Unknown is not Normal - a
    /// Director that has not looked yet knows nothing about the machine.
    /// </summary>
    public static ResourceSamplerState NotYetSampled { get; } = new(
        Latest: null,
        new ResourcePressureVerdict(
        [
            new ResourceVerdict(ResourceKind.Cpu, ResourceLevel.Unknown, NotYetSampledReason),
            new ResourceVerdict(ResourceKind.Memory, ResourceLevel.Unknown, NotYetSampledReason),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Unknown, NotYetSampledReason),
        ]));

    /// <summary>The plain-English reason shown before the first read.</summary>
    public const string NotYetSampledReason = "the machine has not been read yet";

    /// <summary>The verdict for one resource, or Unknown when the verdict does not carry it.</summary>
    public ResourceVerdict VerdictFor(ResourceKind kind)
    {
        foreach (var verdict in Verdict.Resources)
        {
            if (verdict.Kind == kind) return verdict;
        }
        return new ResourceVerdict(kind, ResourceLevel.Unknown, "no verdict was given for this resource");
    }
}

/// <summary>The payload of <see cref="IResourceSampler.Crossed"/>.</summary>
public sealed class ResourceCrossingEventArgs(ResourceCrossing crossing, ResourceSamplerState state) : EventArgs
{
    /// <summary>The crossing.</summary>
    public ResourceCrossing Crossing { get; } = crossing;

    /// <summary>
    /// The sampler's state at the read that produced the crossing, so the banner names the biggest user
    /// from the same sample that caused it rather than from a later one.
    /// </summary>
    public ResourceSamplerState State { get; } = state;
}
