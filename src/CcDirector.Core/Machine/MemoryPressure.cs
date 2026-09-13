namespace CcDirector.Core.Machine;

/// <summary>
/// THE ONE PLACE A MEMORY READING BECOMES A VERDICT (issue #2818).
///
/// A pure function of the reading - no clock, no platform call, no state - which is what lets it be
/// tested exhaustively in <c>CcDirector.Core.UnitTests</c>, the assembly that forbids wall-clock
/// dependence. Every caller that widens a deadline reads its verdict from here, so the thresholds
/// exist once and cannot drift between the tunnel, the composer and the roster push.
///
/// THE VERDICT IS ON ABSOLUTE AVAILABLE BYTES, AND DELIBERATELY NOT ON A FRACTION.
///
/// The first draft of this fold tested the fraction OR the absolute figure, with the absolute floor
/// described as the thing that stops a large machine being called Critical while gigabytes remain.
/// The design review of issue #2818 showed the predicate cannot do what that sentence claims: a
/// branch of an OR can only ever make a verdict MORE severe, never less. Under that rule a 128
/// gigabyte machine with 8 gigabytes available - 6.25 percent - was called Critical, which is the
/// exact machine the floor was written to protect.
///
/// It is fixed by dropping the fraction from the verdict rather than by rearranging it, because the
/// fraction was never the thing that matters. What decides whether the next allocation pages is HOW
/// MANY BYTES ARE ACTUALLY AVAILABLE. Eight gigabytes free is a comfortable machine whether that is 6
/// percent of a workstation or all of a laptop; 500 megabytes free is a machine in trouble on any
/// hardware. The absolute test alone gives the right answer to both machines that shaped this, and it
/// is the one a reader can check against their own operating system's memory monitor.
///
/// <see cref="MachineMemoryReading.AvailableFraction"/> is still carried on the reading, because the
/// toolbar meter in issue #2811 renders a proportion. That is a display concern, not a verdict.
/// </summary>
public static class MemoryPressure
{
    /// <summary>
    /// Below this many bytes available, the machine is at least <see cref="MemoryPressureLevel.Tight"/>.
    /// Two gigabytes is about the room one more agent session and its tooling need without the machine
    /// reaching for the page file.
    /// </summary>
    public const ulong TightAbsoluteBytes = 2UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Below this many bytes available, the machine is <see cref="MemoryPressureLevel.Critical"/> -
    /// expected to page, and to stall for whole seconds at a time. This is the state the Director was
    /// mistaking for a broken terminal interface and a dead Gateway.
    /// </summary>
    public const ulong CriticalAbsoluteBytes = 800UL * 1024 * 1024;

    /// <summary>
    /// Fold one reading into a level.
    ///
    /// A reading that could not be taken folds to <see cref="MemoryPressureLevel.Unknown"/> and NEVER to
    /// <see cref="MemoryPressureLevel.Normal"/>. So does a reading whose total is zero, which is not a
    /// machine with no memory - it is a platform that answered with nothing.
    /// </summary>
    public static MemoryPressureLevel Level(MachineMemoryReading reading)
    {
        if (!reading.CouldRead || reading.TotalBytes == 0) return MemoryPressureLevel.Unknown;

        // Available above total is not a machine with spare capacity, it is a bad reading. Refusing it
        // here keeps a nonsense value from folding to a confident "Normal".
        if (reading.AvailableBytes > reading.TotalBytes) return MemoryPressureLevel.Unknown;

        if (reading.AvailableBytes < CriticalAbsoluteBytes) return MemoryPressureLevel.Critical;
        if (reading.AvailableBytes < TightAbsoluteBytes) return MemoryPressureLevel.Tight;

        return MemoryPressureLevel.Normal;
    }

    /// <summary>
    /// True when the machine is measurably short of memory - Tight or Critical, and NOT Unknown.
    /// The question every caller in issue #2818 actually asks before it widens a deadline.
    /// </summary>
    public static bool IsUnderPressure(MemoryPressureLevel level) =>
        level is MemoryPressureLevel.Tight or MemoryPressureLevel.Critical;

    /// <summary>
    /// How much longer to wait for something that would normally be quick, given the pressure.
    ///
    /// This is a MULTIPLIER ON A DEADLINE THAT DETECTS A STUCK PEER, and it exists because those
    /// deadlines cannot tell "the other end is broken" from "this machine did not get scheduled".
    /// On a healthy machine - and on one we could not read - it is exactly 1, so nothing anywhere
    /// changes until a reading proves it should.
    /// </summary>
    public static double DeadlineMultiplier(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Critical => 4.0,
        MemoryPressureLevel.Tight => 2.0,
        _ => 1.0,
    };

    /// <summary>Plain-English name for a log line or a delivery-failure reason.</summary>
    public static string Describe(MemoryPressureLevel level) => level switch
    {
        MemoryPressureLevel.Critical => "the machine is nearly out of memory",
        MemoryPressureLevel.Tight => "the machine is short of memory",
        MemoryPressureLevel.Normal => "the machine has memory to spare",
        _ => "the machine's memory could not be read",
    };
}
