using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// The resource probe for the machine the Director is running on (the Resource Monitor mission, issue #2811).
/// The one place the operating system decides which probe the Director reads.
/// </summary>
public static class ResourceProbes
{
    /// <summary>
    /// What a Windows Director says for every figure but memory until the Windows probe is on main. Plain words,
    /// because the meter's tooltip and the 24-hour recording both carry it to a person.
    /// </summary>
    public const string NoWindowsProbeYet = "this Director has no Windows resource probe yet - only memory is read on Windows";

    /// <summary>The probe for this operating system.</summary>
    public static IResourceProbe ForThisMachine()
    {
        if (OperatingSystem.IsLinux())
        {
            FileLog.Write("[ResourceProbes] ForThisMachine: Linux");
            return new LinuxResourceProbe();
        }

        if (OperatingSystem.IsMacOS())
        {
            FileLog.Write("[ResourceProbes] ForThisMachine: macOS");
            return new MacOsResourceProbe();
        }

        if (OperatingSystem.IsWindows())
        {
            FileLog.Write($"[ResourceProbes] ForThisMachine: Windows - {NoWindowsProbeYet}");
            return new MemoryOnlyResourceProbe(MachineMemoryProbe.Shared, NoWindowsProbeYet);
        }

        throw new PlatformNotSupportedException(
            $"The resource monitor reads Windows, Linux and macOS; this operating system is {Environment.OSVersion}.");
    }
}

/// <summary>
/// A probe that reads the machine's memory and says plainly that it cannot read anything else - for a platform
/// whose full probe is not built yet. Memory comes from the existing, tested <see cref="IMachineMemoryProbe"/>
/// (issue #2818), so the MEM bar shows a real figure; the processor, swap, stalls, disks and processes are
/// unreadable with the reason, so the CPU bar shows a dash and its tooltip says why. The parts of a sample fail
/// independently, which is the design of <see cref="ResourceSample"/>, and nothing here is a made-up figure.
/// </summary>
public sealed class MemoryOnlyResourceProbe : IResourceProbe
{
    private readonly IMachineMemoryProbe _memory;
    private readonly string _reason;

    /// <summary>Creates the probe.</summary>
    /// <param name="memory">The memory probe.</param>
    /// <param name="reason">Why everything but memory is unreadable, in plain English.</param>
    public MemoryOnlyResourceProbe(IMachineMemoryProbe memory, string reason)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _memory = memory;
        _reason = reason;
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        var memory = _memory.Read();
        return new ResourceSample(
            memory.TakenAtUtc,
            CpuReading.Unreadable(_reason, Environment.ProcessorCount),
            memory,
            SwapReading.Unreadable(_reason),
            StallReading.Unreadable(_reason),
            DiskEvidenceSet.Unreadable(_reason),
            ProcessSetReading.Unreadable(_reason));
    }
}
