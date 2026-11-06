using CcDirector.Core.Machine;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// A probe that returns exactly the sample it is told to, so everything above <see cref="IResourceProbe"/>
/// - the sampler, the crossing detector, the meter, the banner, the detail screen, the recording and the
/// diagnose command - can be built and tested with no platform call and no clock (the Resource Monitor
/// mission, issue #2811, phase 2).
///
/// It is a test double, so it lives beside the other test doubles in <c>CcDirector.Core.Tests</c> and is
/// link-compiled into each test project that needs it, exactly as <c>ResourceSampleContract</c> and
/// <c>PinnedMachineMemory</c> are. It never ships in the product.
///
/// Build its samples with <see cref="FakeSampleBuilder"/>. Two ways to drive it:
/// <list type="bullet">
/// <item><see cref="Set"/> - every read returns this sample until it is changed.</item>
/// <item><see cref="Enqueue"/> - reads return these in order; once they run out, the last one repeats,
/// so a sampler that reads once more than a test expected still sees the machine the test left it in.</item>
/// </list>
/// Like every probe it never throws from <see cref="Read"/>. Thread-safe.
/// </summary>
public sealed class FakeResourceProbe : IResourceProbe
{
    private readonly object _gate = new();
    private readonly Queue<ResourceSample> _queued = new();
    private ResourceSample _current;
    private int _readCount;

    /// <summary>Creates the probe returning <paramref name="initial"/>.</summary>
    public FakeResourceProbe(ResourceSample initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>Creates the probe returning a healthy machine: <see cref="FakeSampleBuilder"/>'s defaults.</summary>
    public FakeResourceProbe() : this(new FakeSampleBuilder().Build())
    {
    }

    /// <summary>How many times <see cref="Read"/> has been called.</summary>
    public int ReadCount
    {
        get { lock (_gate) return _readCount; }
    }

    /// <summary>Every read from now on returns <paramref name="sample"/>. Discards anything still queued.</summary>
    public void Set(ResourceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            _queued.Clear();
            _current = sample;
        }
    }

    /// <summary>
    /// The next reads return <paramref name="samples"/> in order, after anything already queued; the last
    /// one then repeats.
    /// </summary>
    public void Enqueue(params ResourceSample[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        // Every sample is checked before any is queued, so a null in the middle leaves the queue untouched.
        if (Array.IndexOf(samples, null) >= 0)
            throw new ArgumentException("A queued sample cannot be null.", nameof(samples));
        lock (_gate)
        {
            foreach (var sample in samples) _queued.Enqueue(sample);
        }
    }

    /// <inheritdoc />
    public ResourceSample Read()
    {
        lock (_gate)
        {
            _readCount++;
            if (_queued.Count > 0) _current = _queued.Dequeue();
            return _current;
        }
    }
}

/// <summary>
/// Builds a <see cref="ResourceSample"/> for a test or a fake probe: a healthy machine by default, with
/// any part replaced, including by an unreadable reading. Each part fails on its own, exactly as on a real
/// machine, so "the processor cannot be read but memory can" is one call.
///
/// The defaults are the Mac mini's real readings quoted in the mission's mock-ups (section 5d): 8 cores at
/// 28 per cent with the busiest at 71, 8.86 GB used of 16.0 GB, swap 966 MB of 2.00 GB. Every sample it
/// builds keeps the probe contract the platform probes are held to - the process list is never empty,
/// per-process processor is a share of the whole machine, and an unreadable part carries its reason and no
/// figures.
/// </summary>
public sealed class FakeSampleBuilder
{
    /// <summary>One gibibyte, for writing sizes in a test.</summary>
    public const ulong Gigabyte = 1024UL * 1024 * 1024;

    /// <summary>One mebibyte, for writing sizes in a test.</summary>
    public const ulong Megabyte = 1024UL * 1024;

    /// <summary>When a sample is taken if <see cref="At"/> is not called: noon on the day the mission was narrowed.</summary>
    public static readonly DateTime DefaultTakenAtUtc = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The process id of the one process every default sample carries, standing for the Director.</summary>
    public const int DefaultDirectorPid = 1000;

    private DateTime _takenAtUtc = DefaultTakenAtUtc;
    private CpuReading _cpu = CpuReading.Read(28, 71, 8);
    private ulong _memoryTotal = 16 * Gigabyte;
    private ulong _memoryAvailable = (ulong)(7.14 * Gigabyte);
    private string? _memoryUnreadableReason;
    private SwapReading _swap = SwapReading.Read(2 * Gigabyte, 966 * Megabyte);
    private StallReading _stall = StallReading.Read(StallSource.LinuxPressureStall,
        cpuStalledPercent: 0.06, memoryStalledPercent: 0.17, ioStalledPercent: 0.5);
    private DiskEvidenceSet _disks = DiskEvidenceSet.Read(
    [
        DiskEvidence.Read("disk0", DiskKind.SolidState, isInSystemVolume: true,
            busyPercent: null, millisecondsPerRead: 0.1, millisecondsPerWrite: 0.2, averageQueueDepth: 0.01),
    ]);
    private List<ProcessReading>? _processes;
    private string? _processesUnreadableReason;

    /// <summary>When the sample is taken. Converted to UTC kind; pass a UTC time.</summary>
    public FakeSampleBuilder At(DateTime takenAtUtc)
    {
        if (takenAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A sample is stamped in UTC.", nameof(takenAtUtc));
        _takenAtUtc = takenAtUtc;
        return this;
    }

    /// <summary>
    /// The processor. <paramref name="busiestCorePercent"/> defaults to the whole-machine figure, the lowest
    /// the busiest core can honestly read.
    /// </summary>
    public FakeSampleBuilder Cpu(double totalBusyPercent, double? busiestCorePercent = null, int logicalCoreCount = 8)
    {
        _cpu = CpuReading.Read(totalBusyPercent, busiestCorePercent ?? totalBusyPercent, logicalCoreCount);
        return this;
    }

    /// <summary>The processor could not be read, for this reason.</summary>
    public FakeSampleBuilder CpuUnreadable(string reason)
    {
        _cpu = CpuReading.Unreadable(reason);
        return this;
    }

    /// <summary>The machine's memory: how much is installed and how much can be handed out without paging.</summary>
    public FakeSampleBuilder Memory(ulong totalBytes, ulong availableBytes)
    {
        _memoryTotal = totalBytes;
        _memoryAvailable = availableBytes;
        _memoryUnreadableReason = null;
        return this;
    }

    /// <summary>Only the available memory changes; the installed total stays.</summary>
    public FakeSampleBuilder MemoryAvailable(ulong availableBytes)
    {
        _memoryAvailable = availableBytes;
        _memoryUnreadableReason = null;
        return this;
    }

    /// <summary>Memory could not be read, for this reason.</summary>
    public FakeSampleBuilder MemoryUnreadable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An unreadable reading must say why, in plain English.", nameof(reason));
        _memoryUnreadableReason = reason;
        return this;
    }

    /// <summary>The swap or page file.</summary>
    public FakeSampleBuilder Swap(ulong totalBytes, ulong usedBytes)
    {
        _swap = SwapReading.Read(totalBytes, usedBytes);
        return this;
    }

    /// <summary>Swap could not be read, for this reason.</summary>
    public FakeSampleBuilder SwapUnreadable(string reason)
    {
        _swap = SwapReading.Unreadable(reason);
        return this;
    }

    /// <summary>The stall readings, built with <see cref="StallReading.Read"/> or <see cref="StallReading.Unreadable"/>.</summary>
    public FakeSampleBuilder Stall(StallReading stall)
    {
        _stall = stall;
        return this;
    }

    /// <summary>The disks, for the slowness diagnosis only. Replaces the default single idle solid-state disk.</summary>
    public FakeSampleBuilder Disks(params DiskEvidence[] disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        _disks = DiskEvidenceSet.Read(disks);
        return this;
    }

    /// <summary>The disks could not be listed, for this reason.</summary>
    public FakeSampleBuilder DisksUnreadable(string reason)
    {
        _disks = DiskEvidenceSet.Unreadable(reason);
        return this;
    }

    /// <summary>
    /// Adds a process. The first call replaces the default lone Director process, so a test that names its
    /// processes gets exactly those. Pass null for a figure the platform refused - the process is still
    /// listed and named, as on a real machine.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="parentPid">The parent's process id, or null when not known.</param>
    /// <param name="name">The process name.</param>
    /// <param name="cpuPercent">Processor as a share of the WHOLE machine, 0 to 100; null when refused.</param>
    /// <param name="memoryBytes">Resident memory; null when refused.</param>
    public FakeSampleBuilder Process(int pid, int? parentPid, string name, double? cpuPercent, ulong? memoryBytes)
    {
        _processes ??= new List<ProcessReading>();
        _processes.Add(new ProcessReading(pid, parentPid, name, StartTimeUtc: null, cpuPercent, memoryBytes,
            DiskReadBytesPerSecond: null, DiskWriteBytesPerSecond: null));
        _processesUnreadableReason = null;
        return this;
    }

    /// <summary>The process list could not be read, for this reason.</summary>
    public FakeSampleBuilder ProcessesUnreadable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An unreadable reading must say why, in plain English.", nameof(reason));
        _processesUnreadableReason = reason;
        return this;
    }

    /// <summary>
    /// The first read after start-up: the rates - the processor and the per-process figures - are unreadable
    /// with <see cref="ResourceUnreadableReasons.FirstSample"/>, while memory and swap, which are levels and
    /// not rates, still read.
    /// </summary>
    public FakeSampleBuilder FirstSample()
    {
        _cpu = CpuReading.Unreadable(ResourceUnreadableReasons.FirstSample);
        _processesUnreadableReason = ResourceUnreadableReasons.FirstSample;
        return this;
    }

    /// <summary>Builds the sample. The builder can be changed and built again.</summary>
    public ResourceSample Build()
    {
        var memory = _memoryUnreadableReason is null
            ? MachineMemoryReading.Read(_memoryTotal, _memoryAvailable, _takenAtUtc)
            : MachineMemoryReading.Unreadable(_memoryUnreadableReason, _takenAtUtc);

        var processes = _processesUnreadableReason is null
            ? ProcessSetReading.Read(_processes is null ? [DefaultDirectorProcess()] : _processes.ToArray())
            : ProcessSetReading.Unreadable(_processesUnreadableReason);

        return new ResourceSample(_takenAtUtc, _cpu, memory, _swap, _stall, _disks, processes);
    }

    private static ProcessReading DefaultDirectorProcess() =>
        new(DefaultDirectorPid, ParentPid: 1, "cc-director", StartTimeUtc: null, CpuPercent: 2, MemoryBytes: 430 * Megabyte,
            DiskReadBytesPerSecond: null, DiskWriteBytesPerSecond: null);
}
