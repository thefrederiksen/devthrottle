namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the whole machine, taken by an <see cref="IResourceProbe"/> at one moment.
///
/// Each part carries its own <c>CouldRead</c> and reason, because they fail independently: a machine
/// whose disk statistics are locked away still has a readable processor, and the meter must show the
/// one it has and say "cannot read" for the one it does not. There is no sample-wide "could read".
/// </summary>
/// <param name="TakenAtUtc">When the sample was taken, so a cached one can be aged.</param>
/// <param name="Cpu">The processor, whole machine and busiest core.</param>
/// <param name="Memory">The machine's memory - the existing reading from issue #2818, reused unchanged.</param>
/// <param name="Swap">The swap file or page file.</param>
/// <param name="Disks">Every physical disk, with how busy and how slow it is.</param>
/// <param name="Volumes">Every mounted volume, with its free space. Empty when none could be listed.</param>
/// <param name="Network">The machine's network throughput.</param>
/// <param name="Stall">How much of the time programs were frozen waiting on each resource.</param>
/// <param name="Processes">Every process, with what it costs the machine.</param>
public sealed record ResourceSample(
    DateTime TakenAtUtc,
    CpuReading Cpu,
    MachineMemoryReading Memory,
    SwapReading Swap,
    DiskSetReading Disks,
    IReadOnlyList<VolumeReading> Volumes,
    NetworkReading Network,
    StallReading Stall,
    ProcessSetReading Processes);
