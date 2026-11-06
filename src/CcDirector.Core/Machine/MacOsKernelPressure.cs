namespace CcDirector.Core.Machine;

/// <summary>
/// The macOS kernel's own verdict on memory pressure, as the kernel itself numbers it.
///
/// The values are NOT the kernel's numbers. The kernel uses a bit flag - 1 normal, 2 warning, 4
/// critical - and a reader that treated those as an ordered ladder would rank critical as twice as bad
/// as warning and leave a gap at three. This is the ladder, in the same order and with the same
/// <see cref="Unknown"/> rule as <see cref="MemoryPressureLevel"/> and
/// <see cref="ResourceLevel"/>: Unknown is zero, below Normal, and is never a synonym for Normal.
/// </summary>
public enum KernelMemoryPressureLevel
{
    /// <summary>The kernel did not say, or said a number this version does not know. Assume nothing.</summary>
    Unknown = 0,

    /// <summary>The kernel reports normal memory pressure.</summary>
    Normal = 1,

    /// <summary>The kernel is warning about memory pressure.</summary>
    Warning = 2,

    /// <summary>The kernel reports critical memory pressure.</summary>
    Critical = 3,
}

/// <summary>
/// THE ONE PLACE THE macOS KERNEL'S MEMORY-PRESSURE NUMBER IS READ.
///
/// On macOS this level is one of only TWO stall signals the platform publishes - there is no equivalent
/// of Linux's <c>/proc/pressure</c> - so the mapping from the kernel's number to a level carries real
/// weight, and a mapping written twice drifts. The macOS probe, its tests and the Gateway's display fold
/// all call this function rather than each writing the <c>switch</c> again. That is CLAUDE.md rule 7,
/// the dumb client, applied to our own code: one place decides what a state MEANS.
///
/// It is public, not internal, for exactly that reason - the Gateway is a different assembly and must be
/// able to call it instead of re-implementing it.
/// </summary>
public static class MacOsKernelPressure
{
    /// <summary>
    /// Reads the kernel's <c>kern.memorystatus_vm_pressure_level</c> number into a level.
    ///
    /// A NUMBER WE DO NOT KNOW IS Unknown, NEVER FOLDED INTO Normal. If a future macOS adds a level,
    /// reporting it as Normal would tell the owner his machine is fine on the strength of a number
    /// nobody has read. Null - the platform offered no level at all - is Unknown for the same reason.
    /// </summary>
    public static KernelMemoryPressureLevel Read(int? kernelLevel) => kernelLevel switch
    {
        1 => KernelMemoryPressureLevel.Normal,
        2 => KernelMemoryPressureLevel.Warning,
        4 => KernelMemoryPressureLevel.Critical,
        _ => KernelMemoryPressureLevel.Unknown,
    };
}
