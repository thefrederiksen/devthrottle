namespace CcDirector.Core.Machine;

/// <summary>The resources the Resource Monitor gives a verdict on - one row each in mission section 5b.</summary>
public enum ResourceKind
{
    /// <summary>The processor, whole machine.</summary>
    Cpu,

    /// <summary>Memory available, folded by the existing <see cref="MemoryPressure"/> limits.</summary>
    Memory,

    /// <summary>How busy the disks are, or how much programs wait on them.</summary>
    DiskActivity,

    /// <summary>The swap file or page file.</summary>
    Swap,

    /// <summary>Free space on a volume.</summary>
    DiskFree,
}

/// <summary>
/// How close one resource is to maxing out. The same ladder as <see cref="MemoryPressureLevel"/>, and
/// <see cref="Unknown"/> keeps the same meaning: the reading failed, nothing is known, and it is never
/// a synonym for <see cref="Normal"/>.
/// </summary>
public enum ResourceLevel
{
    /// <summary>The resource could not be read. Nothing is known; assume nothing.</summary>
    Unknown = 0,

    /// <summary>Room to work.</summary>
    Normal = 1,

    /// <summary>Getting close to the limit - the meter shows amber.</summary>
    Tight = 2,

    /// <summary>Maxed out - the meter shows red.</summary>
    Critical = 3,
}

/// <summary>
/// The verdict on one resource. Computed by a pure function (phase 1, <c>ResourcePressure</c>); this is
/// only its shape.
/// </summary>
/// <param name="Kind">Which resource.</param>
/// <param name="Level">How close to the limit it is.</param>
/// <param name="Evidence">The reason in plain English with the figures, for example "97% busy for 2 minutes".</param>
public sealed record ResourceVerdict(ResourceKind Kind, ResourceLevel Level, string Evidence);

/// <summary>
/// The verdict on every resource at once - what the toolbar meter and the warning banner render.
/// </summary>
/// <param name="Resources">One verdict per resource.</param>
public sealed record ResourcePressureVerdict(IReadOnlyList<ResourceVerdict> Resources)
{
    /// <summary>
    /// The most severe level among the resources.
    ///
    /// UNKNOWN NEVER BEATS A REAL LEVEL. Worst is the highest of Normal, Tight and Critical that any
    /// resource reached, and is <see cref="ResourceLevel.Unknown"/> only when EVERY resource is Unknown
    /// (or there are none). A machine with a red processor and an unreadable disk is red, not unknown;
    /// one unreadable reading must not hide a real warning. The unreadable resource is still in
    /// <see cref="Resources"/>, so the meter can show "cannot read" for that bar.
    ///
    /// This falls out of the enum's order - Unknown is zero, below Normal - and is pinned by tests so a
    /// reordering cannot quietly change it.
    /// </summary>
    public ResourceLevel Worst
    {
        get
        {
            var worst = ResourceLevel.Unknown;
            foreach (var verdict in Resources)
            {
                if (verdict.Level > worst) worst = verdict.Level;
            }
            return worst;
        }
    }
}
