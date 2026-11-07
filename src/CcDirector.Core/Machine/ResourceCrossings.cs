using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>What kind of move a <see cref="ResourceCrossing"/> is.</summary>
public enum ResourceCrossingChange
{
    /// <summary>From normal (or never known) into amber or red. Shows a banner.</summary>
    Raised = 1,

    /// <summary>From amber up to red. Shows a banner: it has crossed into red.</summary>
    Escalated = 2,

    /// <summary>From red down to amber. Still a warning; no new banner.</summary>
    Eased = 3,

    /// <summary>From amber or red back to normal. Ends the warning.</summary>
    Cleared = 4,
}

/// <summary>
/// One resource moving across a limit, as the meter must treat it.
///
/// THE MEANING IS DECIDED HERE, NOT IN A VIEW. <see cref="IsNewWarning"/> and <see cref="EndsWarning"/> are
/// computed once in Core, so the banner renders what it is handed and adding a change later is one edit in
/// <see cref="ResourceCrossingDetector"/>, not a new branch in every view (CLAUDE.md rule 7).
/// </summary>
/// <param name="Kind">Which resource.</param>
/// <param name="From">The last level that was actually known. Unknown when nothing was known before.</param>
/// <param name="To">The level now. Never Unknown: losing a reading is not a crossing.</param>
/// <param name="Change">What kind of move this is.</param>
/// <param name="AtUtc">When the sample that produced it was taken.</param>
/// <param name="Evidence">The verdict's own plain-English evidence, for example "97% busy for 2 minutes".</param>
public sealed record ResourceCrossing(
    ResourceKind Kind,
    ResourceLevel From,
    ResourceLevel To,
    ResourceCrossingChange Change,
    DateTime AtUtc,
    string Evidence)
{
    /// <summary>
    /// True when this crossing is NEWS: the resource has just crossed into amber, or escalated from amber into
    /// red. False for easing back from red to amber, which is still a warning but not a new one.
    ///
    /// This was called ShowsBanner until the owner deleted the warning banner on 2026-11-07. It was renamed
    /// rather than kept, because a property named for a control that no longer exists is a false description of
    /// what it computes - the classification itself is unchanged and still correct.
    /// </summary>
    public bool IsNewWarning => Change is ResourceCrossingChange.Raised or ResourceCrossingChange.Escalated;

    /// <summary>True when this crossing ends the resource's warning, so its banner comes down.</summary>
    public bool EndsWarning => Change == ResourceCrossingChange.Cleared;

    /// <summary>Short, log-safe description.</summary>
    public override string ToString() => $"{Kind} {Change}: {From} to {To} at {AtUtc:O} ({Evidence})";
}

/// <summary>
/// Turns a stream of verdicts into crossings - the one place "did this resource just cross a limit?" is
/// answered. The 60-second rule is NOT here: it is inside the verdict, which <c>ResourcePressure</c>
/// computes from the whole history. This only compares each resource's level with the last one known.
///
/// UNKNOWN IS NEITHER A CROSSING NOR A RECOVERY. A reading that fails in the middle of a warning does not
/// clear it - nothing is known, so the banner stays - and it does not raise a second banner when the
/// reading comes back at the same level. The detector remembers the last KNOWN level per resource and
/// compares against that, so red, then unreadable, then normal is exactly one <see cref="ResourceCrossingChange.Cleared"/>,
/// and red, then unreadable, then red is nothing at all.
///
/// One instance per sampler. Not thread-safe: the sampler calls it from its own timer only.
/// </summary>
public sealed class ResourceCrossingDetector
{
    private readonly Dictionary<ResourceKind, ResourceLevel> _lastKnown = new();

    /// <summary>
    /// Feeds one verdict and returns the crossings it produced, in the verdict's resource order. Empty when
    /// nothing crossed, which is the usual answer.
    /// </summary>
    public IReadOnlyList<ResourceCrossing> Observe(ResourcePressureVerdict verdict, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        List<ResourceCrossing>? crossings = null;

        foreach (var resource in verdict.Resources)
        {
            if (resource.Level == ResourceLevel.Unknown) continue;

            var from = _lastKnown.TryGetValue(resource.Kind, out var known) ? known : ResourceLevel.Unknown;
            _lastKnown[resource.Kind] = resource.Level;

            var change = Classify(from, resource.Level);
            if (change is null) continue;

            var crossing = new ResourceCrossing(resource.Kind, from, resource.Level, change.Value, atUtc, resource.Evidence);
            FileLog.Write($"[ResourceCrossingDetector] Observe: {crossing}");
            (crossings ??= new List<ResourceCrossing>()).Add(crossing);
        }

        return crossings ?? (IReadOnlyList<ResourceCrossing>)Array.Empty<ResourceCrossing>();
    }

    private static ResourceCrossingChange? Classify(ResourceLevel from, ResourceLevel to)
    {
        if (from == to) return null;

        var wasWarning = from is ResourceLevel.Tight or ResourceLevel.Critical;
        var isWarning = to is ResourceLevel.Tight or ResourceLevel.Critical;

        if (isWarning && !wasWarning) return ResourceCrossingChange.Raised;
        if (isWarning && to == ResourceLevel.Critical) return ResourceCrossingChange.Escalated;
        if (isWarning) return ResourceCrossingChange.Eased;
        if (wasWarning) return ResourceCrossingChange.Cleared;

        // Never known, now Normal: the first healthy reading is not news.
        return null;
    }
}
