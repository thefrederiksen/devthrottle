using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE PURE HALF OF THE macOS RESOURCE PROBE.
///
/// Nothing in this file touches the operating system. Every function takes output that was already
/// captured - a property list, a sysctl buffer, two counter readings and the time between them - and
/// returns a value. That is the whole point: the arithmetic that turns cumulative counters into
/// rates, and the structure reading that turns Apple's own property lists into figures, are the two
/// places this probe is most likely to be WRONG, and they are the two places that cannot be covered
/// by a test unless they are separated from the system calls that feed them.
///
/// The tests in <c>MacOsResourceParsingTests</c> drive these functions from property lists and
/// buffers captured on a real Mac and committed as fixtures, so the cover holds on Windows and Linux
/// build machines too.
/// </summary>
internal static class MacOsResourceParsing
{
    // ---------------------------------------------------------------------------------------------
    // Property lists
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the top level of a property list dictionary into plain text values.
    ///
    /// NESTED VALUES ARE SKIPPED, NOT GUESSED. A key whose value is itself a dictionary or an array
    /// is left out, because every caller here wants a flat set of named figures and silently
    /// flattening a tree would invent key names Apple never used. A caller that needs a nested
    /// dictionary asks the registry for that dictionary directly.
    ///
    /// THE DOCUMENT TYPE DECLARATION IS IGNORED AND NOTHING IS FETCHED. Apple's property lists carry
    /// a document type line pointing at a definition on apple.com. A reader left at its defaults
    /// either refuses the document or goes to the network for that definition - a sampler on a five
    /// second timer must do neither.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadPropertyListDictionary(string propertyListText)
    {
        if (string.IsNullOrWhiteSpace(propertyListText))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CloseInput = true,
        };

        using var textReader = new StringReader(propertyListText);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(xmlReader);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var dictionary = document.Root?.Name.LocalName == "dict"
            ? document.Root
            : document.Root?.Element("dict");

        if (dictionary is null) return values;

        string? pendingKey = null;
        foreach (var element in dictionary.Elements())
        {
            if (element.Name.LocalName == "key")
            {
                pendingKey = element.Value;
                continue;
            }

            if (pendingKey is null) continue;

            switch (element.Name.LocalName)
            {
                case "integer":
                case "real":
                case "string":
                case "date":
                    values[pendingKey] = element.Value;
                    break;
                case "true":
                    values[pendingKey] = "true";
                    break;
                case "false":
                    values[pendingKey] = "false";
                    break;
                default:
                    // A dictionary, an array or data. Left out on purpose - see the remarks above.
                    break;
            }

            pendingKey = null;
        }

        return values;
    }

    // ---------------------------------------------------------------------------------------------
    // A property list holding a single value
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a property list whose root is one string, which is how a single registry property such
    /// as "BSD Name" arrives once it has been serialised. Returns null when the root is not a string.
    ///
    /// Reading it this way rather than through <c>CFStringGetCString</c> keeps the whole probe down to
    /// one way of getting a value out of Core Foundation, and that one way is the one the tests cover.
    /// </summary>
    internal static string? ReadPropertyListString(string propertyListText)
    {
        if (string.IsNullOrWhiteSpace(propertyListText)) return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CloseInput = true,
        };

        using var textReader = new StringReader(propertyListText);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(xmlReader);

        var root = document.Root;
        if (root is null) return null;
        if (root.Name.LocalName == "string") return root.Value;

        var child = root.Element("string");
        return child?.Value;
    }

    // ---------------------------------------------------------------------------------------------
    // The IOKit disk statistics
    // ---------------------------------------------------------------------------------------------

    /// <summary>"Bytes (Read)" - cumulative bytes read since the driver attached.</summary>
    internal const string StatisticsBytesRead = "Bytes (Read)";

    /// <summary>"Bytes (Write)".</summary>
    internal const string StatisticsBytesWritten = "Bytes (Write)";

    /// <summary>"Operations (Read)" - cumulative count of read operations.</summary>
    internal const string StatisticsOperationsRead = "Operations (Read)";

    /// <summary>"Operations (Write)".</summary>
    internal const string StatisticsOperationsWritten = "Operations (Write)";

    /// <summary>
    /// "Total Time (Read)" - cumulative nanoseconds spent reading. This is the figure the whole
    /// disk-busy verdict rests on, and it was the phase's named risk on this platform. Measured on an
    /// Apple M4 on 2026-10-15 it read 2,133,904,457,652 nanoseconds over 7,877,981 operations, which
    /// is 0.27 milliseconds per read - the right order for a solid-state disk, and proof the units are
    /// nanoseconds rather than Mach absolute time units.
    /// </summary>
    internal const string StatisticsTotalTimeRead = "Total Time (Read)";

    /// <summary>"Total Time (Write)", in nanoseconds.</summary>
    internal const string StatisticsTotalTimeWritten = "Total Time (Write)";

    /// <summary>
    /// The cumulative counters one physical disk's driver reports, exactly as IOKit gave them. Every
    /// figure is a running total since the driver attached, so a rate needs two of these and the time
    /// between them.
    /// </summary>
    internal readonly record struct DiskCounters(
        long BytesRead,
        long BytesWritten,
        long OperationsRead,
        long OperationsWritten,
        long TotalTimeReadNanoseconds,
        long TotalTimeWrittenNanoseconds);

    /// <summary>
    /// Reads the six counters the probe uses out of an IOKit "Statistics" dictionary.
    ///
    /// A MISSING OR UNREADABLE KEY FAILS THE WHOLE READING RATHER THAN COUNTING AS ZERO. A zero here
    /// would not look like a fault: it would look like a disk that did no work, which is a perfectly
    /// ordinary reading, and it would then be averaged into a busy percentage and shown to the owner
    /// as fact. The probe reports that it could not read the disk instead.
    /// </summary>
    internal static bool TryReadDiskCounters(
        IReadOnlyDictionary<string, string> statistics,
        out DiskCounters counters,
        out string? missingKey)
    {
        counters = default;
        missingKey = null;

        if (!TryReadLong(statistics, StatisticsBytesRead, out var bytesRead, out missingKey)) return false;
        if (!TryReadLong(statistics, StatisticsBytesWritten, out var bytesWritten, out missingKey)) return false;
        if (!TryReadLong(statistics, StatisticsOperationsRead, out var operationsRead, out missingKey)) return false;
        if (!TryReadLong(statistics, StatisticsOperationsWritten, out var operationsWritten, out missingKey)) return false;
        if (!TryReadLong(statistics, StatisticsTotalTimeRead, out var timeRead, out missingKey)) return false;
        if (!TryReadLong(statistics, StatisticsTotalTimeWritten, out var timeWritten, out missingKey)) return false;

        counters = new DiskCounters(
            bytesRead, bytesWritten, operationsRead, operationsWritten, timeRead, timeWritten);
        return true;
    }

    private static bool TryReadLong(
        IReadOnlyDictionary<string, string> values,
        string key,
        out long value,
        out string? missingKey)
    {
        value = 0;
        missingKey = null;

        if (!values.TryGetValue(key, out var text))
        {
            missingKey = $"the IOKit statistics carried no \"{key}\"";
            return false;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            missingKey = $"the IOKit statistics gave \"{key}\" as \"{text}\", which is not a whole number";
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Processor ticks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One core's cumulative tick counters, exactly as the kernel reports them. Ticks since boot, so
    /// a percentage needs two of these.
    /// </summary>
    internal readonly record struct CoreTicks(uint User, uint System, uint Idle, uint Nice)
    {
        /// <summary>Every tick this core has accounted for, busy and idle together.</summary>
        internal ulong Total => (ulong)User + System + Idle + Nice;

        /// <summary>The ticks this core spent doing work rather than waiting.</summary>
        internal ulong Busy => (ulong)User + System + Nice;
    }

    /// <summary>
    /// Splits the flat array <c>host_processor_info</c> returns into one block of counters per core.
    ///
    /// The kernel returns a single array of <c>CPU_STATE_MAX</c> counters for each core, one core
    /// after another, with the count of cores reported separately. Reading it with the wrong stride
    /// does not fail - it silently attributes one core's idle ticks to the next core's user ticks and
    /// produces a processor percentage that looks plausible and is wrong, which is exactly why this is
    /// a separate function with a test behind it.
    /// </summary>
    internal static bool TryReadCoreTicks(
        IReadOnlyList<uint> processorInfo,
        int coreCount,
        out IReadOnlyList<CoreTicks> cores,
        out string? reason)
    {
        cores = Array.Empty<CoreTicks>();
        reason = null;

        if (coreCount <= 0)
        {
            reason = $"host_processor_info reported {coreCount} cores";
            return false;
        }

        var needed = coreCount * MacOsNative.CpuStateMax;
        if (processorInfo.Count < needed)
        {
            reason = $"host_processor_info returned {processorInfo.Count} counters for {coreCount} cores, " +
                     $"where {needed} were needed";
            return false;
        }

        var read = new CoreTicks[coreCount];
        for (var core = 0; core < coreCount; core++)
        {
            var at = core * MacOsNative.CpuStateMax;
            read[core] = new CoreTicks(
                processorInfo[at + MacOsNative.CpuStateUser],
                processorInfo[at + MacOsNative.CpuStateSystem],
                processorInfo[at + MacOsNative.CpuStateIdle],
                processorInfo[at + MacOsNative.CpuStateNice]);
        }

        cores = read;
        return true;
    }

    /// <summary>
    /// How busy a core was between two readings, as a percentage, or null when the two readings
    /// cannot be compared.
    ///
    /// NULL RATHER THAN ZERO when no ticks passed between the readings, which happens when the two
    /// were taken in the same tick, and when the counters went BACKWARDS, which happens when the
    /// thirty-two bit counters wrap. Zero would be a lie in both cases: it reads as an idle core.
    /// </summary>
    internal static double? BusyPercent(CoreTicks before, CoreTicks after)
    {
        if (after.Total <= before.Total) return null;

        var total = after.Total - before.Total;
        var busyBefore = before.Busy;
        var busyAfter = after.Busy;
        if (busyAfter < busyBefore) return null;

        return (busyAfter - busyBefore) / (double)total * 100.0;
    }

    // ---------------------------------------------------------------------------------------------
    // Swap
    // ---------------------------------------------------------------------------------------------

    /// <summary>The size of <c>struct xsw_usage</c>, what <c>vm.swapusage</c> returns.</summary>
    internal const int SwapUsageSize = 32;

    /// <summary>
    /// The machine's swap file, as <c>vm.swapusage</c> reports it. On a healthy Apple Silicon machine
    /// every figure here is legitimately zero, because macOS writes no swap file until it needs one -
    /// measured zero on the M4 Mac mini on 2026-10-15, with zero swap-ins and zero swap-outs. That is
    /// a real reading of a real counter, not an unread one, which is why it is reported rather than
    /// treated as a failure.
    /// </summary>
    internal readonly record struct SwapUsage(ulong TotalBytes, ulong AvailableBytes, ulong UsedBytes, bool Encrypted);

    /// <summary>
    /// Reads <c>struct xsw_usage</c> out of the bytes <c>sysctlbyname("vm.swapusage")</c> wrote.
    ///
    /// Reading a structure by its field offsets rather than by letting the marshaller lay it out means
    /// the layout is written down in one place and covered by a test against bytes captured from the
    /// real kernel. A wrong offset here reports somebody else's number as the size of the swap file.
    /// </summary>
    internal static bool TryReadSwapUsage(ReadOnlySpan<byte> buffer, out SwapUsage usage, out string? reason)
    {
        usage = default;
        reason = null;

        if (buffer.Length < SwapUsageSize)
        {
            reason = $"vm.swapusage returned {buffer.Length} bytes, where {SwapUsageSize} were needed";
            return false;
        }

        var total = BitConverter.ToUInt64(buffer[..8]);
        var available = BitConverter.ToUInt64(buffer[8..16]);
        var used = BitConverter.ToUInt64(buffer[16..24]);
        var encrypted = BitConverter.ToInt32(buffer[28..32]) != 0;

        usage = new SwapUsage(total, available, used, encrypted);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // The run queue
    // ---------------------------------------------------------------------------------------------

    /// <summary>The size of <c>struct loadavg</c> on a 64-bit machine: three counters, padding, and the scale.</summary>
    internal const int LoadAverageSize = 24;

    /// <summary>
    /// The one, five and fifteen minute run-queue averages, already divided by the kernel's scale.
    /// </summary>
    internal readonly record struct LoadAverage(double OneMinute, double FiveMinutes, double FifteenMinutes);

    /// <summary>
    /// Reads <c>struct loadavg</c> out of the bytes <c>vm.loadavg</c> wrote.
    ///
    /// THE SCALE COMES FROM THE KERNEL, IT IS NOT ASSUMED. The three figures are fixed-point and the
    /// divisor is the fourth field of the same structure. Hard-coding the customary 2048 would be a
    /// guess that happens to be right today, and it would read as a plausible load if it ever changed.
    /// </summary>
    internal static bool TryReadLoadAverage(ReadOnlySpan<byte> buffer, out LoadAverage load, out string? reason)
    {
        load = default;
        reason = null;

        if (buffer.Length < LoadAverageSize)
        {
            reason = $"vm.loadavg returned {buffer.Length} bytes, where {LoadAverageSize} were needed";
            return false;
        }

        var scale = BitConverter.ToInt64(buffer[16..24]);
        if (scale <= 0)
        {
            reason = $"vm.loadavg reported a scale of {scale}, which cannot be divided by";
            return false;
        }

        load = new LoadAverage(
            BitConverter.ToUInt32(buffer[..4]) / (double)scale,
            BitConverter.ToUInt32(buffer[4..8]) / (double)scale,
            BitConverter.ToUInt32(buffer[8..12]) / (double)scale);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // The kernel's memory-pressure level
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What the kernel answers for <c>kern.memorystatus_vm_pressure_level</c>.
    ///
    /// THE NUMBERS ARE THE KERNEL'S, AND AN UNKNOWN ONE STAYS UNKNOWN. Two different numbering
    /// schemes for macOS memory pressure are in circulation, one starting at zero for normal and one
    /// starting at one, and picking the wrong one reports a healthy machine as under pressure. This
    /// uses the scheme the kernel actually answers in, confirmed by measurement on the M4 Mac mini on
    /// 2026-10-15: the sysctl read 1 while <c>kern.memorystatus_level</c> reported 77 percent of
    /// memory free, with zero swap-ins and zero swap-outs and no swap file at all. A machine in that
    /// state is normal, so 1 is normal. A number outside this set is reported as
    /// <see cref="Unknown"/> rather than folded into the nearest level.
    /// </summary>
    internal enum KernelPressureLevel
    {
        /// <summary>The kernel answered a number this probe does not know. Never treated as normal.</summary>
        Unknown = 0,

        /// <summary>Nothing is being asked to give memory back.</summary>
        Normal = 1,

        /// <summary>The kernel is asking programs to give memory back.</summary>
        Warning = 2,

        /// <summary>The kernel is about to start killing programs to get memory back.</summary>
        Critical = 4,
    }

    /// <summary>Turns the kernel's number into a level, without guessing at one it does not know.</summary>
    internal static KernelPressureLevel ReadKernelPressureLevel(int level) => level switch
    {
        1 => KernelPressureLevel.Normal,
        2 => KernelPressureLevel.Warning,
        4 => KernelPressureLevel.Critical,
        _ => KernelPressureLevel.Unknown,
    };

    // ---------------------------------------------------------------------------------------------
    // Volumes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The size of <c>struct statfs</c> on a 64-bit macOS. Every field offset below is relative to the
    /// start of one of these, and <c>getfsstat</c> writes them one after another with no gaps.
    /// </summary>
    internal const int StatfsSize = 2168;

    private const int StatfsBlockSizeOffset = 0;
    private const int StatfsTotalBlocksOffset = 8;
    private const int StatfsFreeBlocksOffset = 16;
    private const int StatfsAvailableBlocksOffset = 24;
    private const int StatfsFileSystemTypeNameOffset = 72;
    private const int StatfsFileSystemTypeNameLength = 16;
    private const int StatfsMountedOnOffset = 88;
    private const int StatfsMountedFromOffset = 1112;
    private const int StatfsPathLength = 1024;

    /// <summary>
    /// One mounted volume, as <c>getfsstat</c> reports it.
    ///
    /// FREE AND AVAILABLE ARE BOTH KEPT, because they differ and the difference matters. Free blocks
    /// include the ones only a privileged program may use; available blocks are what an ordinary
    /// program can actually have. The mission warns on a volume running out of room, and warning on
    /// the figure a session cannot use would warn too late.
    /// </summary>
    internal sealed record Volume(
        string MountedOn,
        string MountedFrom,
        string FileSystemType,
        ulong BlockSizeBytes,
        ulong TotalBlocks,
        ulong FreeBlocks,
        ulong AvailableBlocks)
    {
        /// <summary>The volume's size in bytes.</summary>
        internal ulong TotalBytes => TotalBlocks * BlockSizeBytes;

        /// <summary>What an ordinary program could still write, in bytes.</summary>
        internal ulong AvailableBytes => AvailableBlocks * BlockSizeBytes;

        /// <summary>
        /// The device name this volume sits on, without the <c>/dev/</c>, or null when the volume is
        /// not backed by a device at all - which is the honest answer for a network mount or for one of
        /// the synthetic file systems macOS mounts over the place.
        /// </summary>
        internal string? DeviceName =>
            MountedFrom.StartsWith("/dev/", StringComparison.Ordinal) && MountedFrom.Length > 5
                ? MountedFrom[5..]
                : null;
    }

    /// <summary>
    /// Reads the volumes out of the buffer <c>getfsstat</c> filled.
    ///
    /// The count comes from the call rather than from the buffer length, because a buffer sized for
    /// more volumes than were mounted leaves the tail untouched, and reading the tail would report
    /// volumes made of whatever was in that memory before.
    /// </summary>
    internal static IReadOnlyList<Volume> ReadVolumes(ReadOnlySpan<byte> buffer, int count)
    {
        var volumes = new List<Volume>(Math.Max(count, 0));

        for (var index = 0; index < count; index++)
        {
            var at = index * StatfsSize;
            if (at + StatfsSize > buffer.Length) break;

            var one = buffer.Slice(at, StatfsSize);

            volumes.Add(new Volume(
                MountedOn: ReadFixedString(one, StatfsMountedOnOffset, StatfsPathLength),
                MountedFrom: ReadFixedString(one, StatfsMountedFromOffset, StatfsPathLength),
                FileSystemType: ReadFixedString(one, StatfsFileSystemTypeNameOffset, StatfsFileSystemTypeNameLength),
                BlockSizeBytes: BitConverter.ToUInt32(one.Slice(StatfsBlockSizeOffset, 4)),
                TotalBlocks: BitConverter.ToUInt64(one.Slice(StatfsTotalBlocksOffset, 8)),
                FreeBlocks: BitConverter.ToUInt64(one.Slice(StatfsFreeBlocksOffset, 8)),
                AvailableBlocks: BitConverter.ToUInt64(one.Slice(StatfsAvailableBlocksOffset, 8))));
        }

        return volumes;
    }

    /// <summary>
    /// Reads a fixed-length character array that the kernel terminates with a zero byte. Everything
    /// from the zero byte onwards is padding and is not part of the name.
    /// </summary>
    private static string ReadFixedString(ReadOnlySpan<byte> buffer, int offset, int length)
    {
        var field = buffer.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        if (end >= 0) field = field[..end];
        return System.Text.Encoding.UTF8.GetString(field);
    }

    // ---------------------------------------------------------------------------------------------
    // Network interfaces
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How many bytes of <c>struct if_data</c> this probe reads. The structure is considerably longer;
    /// the four counters this probe uses all sit within the first 48 bytes, and reading no further
    /// than that is what keeps the length check honest - a shorter structure on some future macOS
    /// fails the check rather than being read with a field cut off.
    /// </summary>
    internal const int InterfaceDataReadLength = 48;

    private const int IfDataPacketsInOffset = 20;
    private const int IfDataPacketsOutOffset = 28;
    private const int IfDataBytesInOffset = 40;
    private const int IfDataBytesOutOffset = 44;

    /// <summary>
    /// One network interface's cumulative counters.
    ///
    /// EVERY FIGURE HERE IS THIRTY-TWO BITS WIDE, because that is what <c>getifaddrs</c> reports, and
    /// that is the whole reason <see cref="DeltaOfWrappingCounter"/> exists: a byte counter of this
    /// width wraps every four gibibytes, which on a machine moving a hundred megabytes a second is
    /// about once a minute. Subtracting the readings without allowing for the wrap gives a hugely
    /// negative rate roughly once a minute, on exactly the busy machine the owner is asking about.
    /// </summary>
    /// <remarks>
    /// ONLY THE FOUR COUNTERS THAT ARE ACTUALLY USED ARE READ. The structure also carries error,
    /// collision and drop counters, and they were read here at first. They came out as zero on this
    /// machine - which is what a healthy machine reports, and also what a wrong offset reports - so
    /// nothing proved them right, and nothing in the mission reads them. Four counters that are
    /// checked against <c>netstat</c> are worth more than seven where three are unverified.
    /// </remarks>
    internal readonly record struct InterfaceCounters(
        string Name,
        uint BytesReceived,
        uint BytesSent,
        uint PacketsReceived,
        uint PacketsSent);

    /// <summary>Reads the counters out of the <c>struct if_data</c> that hangs off an AF_LINK address.</summary>
    internal static bool TryReadInterfaceCounters(
        string name,
        ReadOnlySpan<byte> buffer,
        out InterfaceCounters counters,
        out string? reason)
    {
        counters = default;
        reason = null;

        if (buffer.Length < InterfaceDataReadLength)
        {
            reason = $"the link-level data for {name} was {buffer.Length} bytes, " +
                     $"where {InterfaceDataReadLength} were needed";
            return false;
        }

        counters = new InterfaceCounters(
            Name: name,
            BytesReceived: BitConverter.ToUInt32(buffer.Slice(IfDataBytesInOffset, 4)),
            BytesSent: BitConverter.ToUInt32(buffer.Slice(IfDataBytesOutOffset, 4)),
            PacketsReceived: BitConverter.ToUInt32(buffer.Slice(IfDataPacketsInOffset, 4)),
            PacketsSent: BitConverter.ToUInt32(buffer.Slice(IfDataPacketsOutOffset, 4)));
        return true;
    }

    /// <summary>
    /// How much a thirty-two bit cumulative counter advanced between two readings, allowing for the
    /// one wrap it can have done.
    ///
    /// This CANNOT tell one wrap from two, and does not pretend to: a counter that wrapped twice
    /// between two readings is indistinguishable from one that wrapped once, and the only cure is to
    /// read often enough that it cannot have happened. At the mission's five second sampling interval
    /// a single wrap needs 858 megabytes a second sustained, and two needs over 1.7 gigabytes a
    /// second, which is faster than any interface on these machines. What it does buy is that the
    /// ordinary single wrap stops producing a negative rate.
    /// </summary>
    internal static ulong DeltaOfWrappingCounter(uint before, uint after) =>
        after >= before ? after - before : (ulong)uint.MaxValue + 1 - before + after;

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    /// <summary>The size of <c>struct proc_taskinfo</c>, the PROC_PIDTASKINFO flavour.</summary>
    internal const int ProcessTaskInfoSize = 96;

    /// <summary>
    /// The size of <c>struct proc_bsdinfo</c>, the PROC_PIDTBSDINFO flavour.
    ///
    /// MEASURED, NOT CALCULATED. Working it out from the field list gives 144, because the two name
    /// fields read as if they had room for a terminating zero byte. They do not:
    /// <c>pbi_comm</c> is sixteen characters and <c>pbi_name</c> is thirty-two, with no spare byte, and
    /// the kernel fills 136 bytes - which is what <c>proc_pidinfo</c> returned when asked on the M4
    /// Mac mini on 2026-10-15. The eight byte difference is not harmless: at the calculated offsets
    /// the process name came back shifted by one character and the start time read as 1970.
    /// </summary>
    internal const int ProcessBsdInfoSize = 136;

    /// <summary>The size of <c>struct rusage_info_v2</c>.</summary>
    internal const int RusageV2Size = 160;

    private const int TaskInfoResidentSizeOffset = 8;
    private const int TaskInfoTotalUserOffset = 16;
    private const int TaskInfoTotalSystemOffset = 24;

    private const int BsdInfoParentPidOffset = 16;
    private const int BsdInfoNameOffset = 64;
    private const int BsdInfoNameLength = 32;
    private const int BsdInfoStartSecondsOffset = 120;
    private const int BsdInfoStartMicrosecondsOffset = 128;

    private const int RusageDiskBytesReadOffset = 144;
    private const int RusageDiskBytesWrittenOffset = 152;

    /// <summary>
    /// The conversion between Mach absolute time units and nanoseconds, as the kernel's own fraction.
    /// </summary>
    internal readonly record struct MachTimebase(uint Numerator, uint Denominator)
    {
        /// <summary>
        /// True when the fraction can actually be used. A zero denominator cannot be divided by and a
        /// zero numerator would turn every processor time into nothing, so neither is accepted and the
        /// caller reports the processes as unreadable instead of reporting an idle machine.
        /// </summary>
        internal bool IsUsable => Numerator > 0 && Denominator > 0;
    }

    /// <summary>
    /// Turns Mach absolute time units into nanoseconds.
    ///
    /// THIS IS NOT A FORMALITY, AND SKIPPING IT IS A BUG THAT SHIPS. The processor times in
    /// <c>struct proc_taskinfo</c> are Mach absolute time units, which are widely described as
    /// nanoseconds because on Intel Macs the conversion is 1 to 1 - so code that treats them as
    /// nanoseconds is CORRECT on Intel and wrong by a factor of about forty-two on Apple Silicon.
    /// Measured on the M4 Mac mini the timebase is 125/3, so one unit is 41.67 nanoseconds: two
    /// threads spinning flat out reported 4.79 percent of a core instead of 200.
    ///
    /// The multiplication is done in a wider type because a long-lived process accumulates enough
    /// units that multiplying by the numerator first would be close to the top of a 64-bit number.
    /// </summary>
    internal static ulong MachAbsoluteToNanoseconds(ulong machTime, MachTimebase timebase) =>
        (ulong)((UInt128)machTime * timebase.Numerator / timebase.Denominator);

    /// <summary>
    /// What one process has used. The processor times are cumulative MACH ABSOLUTE TIME UNITS since
    /// the process started - NOT nanoseconds, see <see cref="MachAbsoluteToNanoseconds"/> - so a
    /// percentage needs two readings and the machine's timebase. The memory is a figure for right now.
    /// </summary>
    internal readonly record struct ProcessTaskInfo(
        ulong UserMachTime,
        ulong SystemMachTime,
        ulong ResidentBytes)
    {
        /// <summary>
        /// Every unit of processor time this process has had, in user code and in the kernel. Still
        /// Mach absolute time units, deliberately: the name says so, because the previous name said
        /// nanoseconds and it was believed.
        /// </summary>
        internal ulong TotalMachTime => UserMachTime + SystemMachTime;
    }

    /// <summary>Reads <c>struct proc_taskinfo</c>.</summary>
    internal static bool TryReadProcessTaskInfo(ReadOnlySpan<byte> buffer, out ProcessTaskInfo info)
    {
        info = default;
        if (buffer.Length < ProcessTaskInfoSize) return false;

        info = new ProcessTaskInfo(
            UserMachTime: BitConverter.ToUInt64(buffer.Slice(TaskInfoTotalUserOffset, 8)),
            SystemMachTime: BitConverter.ToUInt64(buffer.Slice(TaskInfoTotalSystemOffset, 8)),
            ResidentBytes: BitConverter.ToUInt64(buffer.Slice(TaskInfoResidentSizeOffset, 8)));
        return true;
    }

    /// <summary>Who started a process, what it is called, and when it started.</summary>
    internal readonly record struct ProcessBsdInfo(int ParentPid, string Name, DateTime StartedAtUtc);

    /// <summary>
    /// Reads <c>struct proc_bsdinfo</c>.
    ///
    /// THE LONGER NAME IS READ, NOT THE SHORT ONE. The structure carries both a sixteen character
    /// name and a thirty-two character one. The short one truncates, and a truncated name is what
    /// turns a culprit the owner can act on into one he cannot: the mission's whole point is naming
    /// the process that is making the machine slow.
    /// </summary>
    internal static bool TryReadProcessBsdInfo(ReadOnlySpan<byte> buffer, out ProcessBsdInfo info)
    {
        info = default;
        if (buffer.Length < ProcessBsdInfoSize) return false;

        var seconds = BitConverter.ToUInt64(buffer.Slice(BsdInfoStartSecondsOffset, 8));
        var microseconds = BitConverter.ToUInt64(buffer.Slice(BsdInfoStartMicrosecondsOffset, 8));

        info = new ProcessBsdInfo(
            ParentPid: (int)BitConverter.ToUInt32(buffer.Slice(BsdInfoParentPidOffset, 4)),
            Name: ReadFixedString(buffer, BsdInfoNameOffset, BsdInfoNameLength),
            StartedAtUtc: DateTime.UnixEpoch
                .AddSeconds(seconds)
                .AddMicroseconds(microseconds));
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Which interfaces count as the network
    // ---------------------------------------------------------------------------------------------

    private const uint InterfaceUp = 0x1;
    private const uint InterfaceLoopback = 0x8;
    private const uint InterfacePointToPoint = 0x10;
    private const uint InterfaceRunning = 0x40;

    /// <summary>IFT_ETHER. Wired and wireless hardware both report it on this platform.</summary>
    private const byte InterfaceTypeEthernet = 0x06;

    /// <summary>
    /// The virtual interfaces macOS gives the SAME type and flags as real hardware, so there is
    /// nothing to tell them apart by except their names: the Apple network processor interfaces, the
    /// wireless access point, Apple Wireless Direct Link, low latency WLAN, and a software bridge.
    /// Measured on this machine, every one of these reports Ethernet, up and running, exactly as the
    /// real Ethernet port does.
    /// </summary>
    private static readonly string[] VirtualInterfacePrefixes =
        { "anpi", "ap", "awdl", "llw", "bridge", "utun", "gif", "stf", "vmenet", "XHC" };

    /// <summary>
    /// Whether an interface's traffic is the machine's real network traffic.
    ///
    /// WHY THIS IS MORE THAN SKIPPING THE LOOPBACK. The shared contract says the network reading is
    /// summed over the physical interfaces that are up, excluding the loopback AND the virtual ones,
    /// and it says why: a virtual layer carries the same bytes a second time. Skipping only the
    /// loopback counted this fleet's own tunnels, and this machine has six of them: a hundred
    /// megabytes downloaded over the tunnel is counted once on the tunnel and once on the Ethernet
    /// port underneath it, and the probe reported two hundred.
    ///
    /// Three of the four tests here come from the operating system itself - the flags and the
    /// interface type, both measured against its headers. Only the last is a list of names, and it is
    /// there because macOS genuinely does not mark those interfaces as virtual in any field: they
    /// report Ethernet, up and running, just as the real port does.
    /// </summary>
    internal static bool IsPhysicalNetworkInterface(string name, uint flags, byte interfaceType)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // Not carrying traffic at all.
        if ((flags & InterfaceUp) == 0) return false;
        if ((flags & InterfaceRunning) == 0) return false;

        // The loopback is the machine talking to itself, and on a machine running many sessions it is
        // the busiest interface by far.
        if ((flags & InterfaceLoopback) != 0) return false;

        // One end of a tunnel. The bytes are already counted on whatever carries it.
        if ((flags & InterfacePointToPoint) != 0) return false;

        // Anything that is not hardware-shaped: tunnels that are not marked point to point, bridges,
        // and the placeholder interfaces.
        if (interfaceType != InterfaceTypeEthernet) return false;

        foreach (var prefix in VirtualInterfacePrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return false;

        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // The kernel's process table
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One row of the kernel's process table: enough to NAME a process, even one whose figures this
    /// user may not read.
    /// </summary>
    /// <param name="Pid">The process id.</param>
    /// <param name="ParentPid">The parent's process id. Zero for the kernel itself.</param>
    /// <param name="Name">
    /// The short name, sixteen characters at most. The longer thirty-two character name is only
    /// available through the per-process call, which is refused for another user's process, so for a
    /// refused process this truncated name is the only name there is - and a truncated name still
    /// names the culprit, where leaving the process out does not.
    /// </param>
    /// <param name="StartedAtUtc">When the process started.</param>
    /// <param name="UserId">Which user owns it. Used only to explain, in plain English, WHY a process's figures could not be read.</param>
    internal readonly record struct KernelProcessEntry(
        int Pid, int ParentPid, string Name, DateTime StartedAtUtc, uint UserId);

    /// <summary>
    /// The size of one row of the kernel's process table, <c>struct kinfo_proc</c>, measured on this
    /// machine rather than added up from the field list. Every offset below was measured the same
    /// way, by compiling the operating system's own headers: this probe has already been wrong three
    /// times by trusting a field list, once by four characters and once by a factor of forty-two.
    /// </summary>
    internal const int KernelProcessEntrySize = 648;

    private const int KernelProcessStartSecondsOffset = 0;
    private const int KernelProcessStartMicrosecondsOffset = 8;
    private const int KernelProcessPidOffset = 40;
    private const int KernelProcessNameOffset = 243;
    private const int KernelProcessNameLength = 17;
    private const int KernelProcessUserIdOffset = 420;
    private const int KernelProcessParentPidOffset = 560;

    /// <summary>
    /// Reads the kernel's whole process table.
    ///
    /// WHY THIS EXISTS AT ALL, when there is already a call that lists process identifiers: the
    /// per-process call that gives a name, a parent and a start time is REFUSED for a process
    /// belonging to another user, and roughly three hundred of this machine's seven hundred processes
    /// belong to root. Building the list from that call therefore dropped them entirely - not with
    /// null figures, but absent, from a set that reported itself as read without trouble. Measured on
    /// this machine, the two busiest processes on it were among the missing, and they were the
    /// Spotlight indexer: exactly the culprit a slowness diagnosis exists to name. This table is
    /// readable by an ordinary user for every process, so the LIST comes from here and only the
    /// FIGURES come from the per-process calls.
    /// </summary>
    internal static List<KernelProcessEntry> ReadKernelProcessTable(ReadOnlySpan<byte> buffer)
    {
        var entries = new List<KernelProcessEntry>(buffer.Length / KernelProcessEntrySize);

        for (var offset = 0; offset + KernelProcessEntrySize <= buffer.Length; offset += KernelProcessEntrySize)
        {
            var row = buffer.Slice(offset, KernelProcessEntrySize);

            var pid = BitConverter.ToInt32(row.Slice(KernelProcessPidOffset, 4));

            // The kernel itself is process zero and is not something a session can be blamed on, but a
            // negative identifier would mean the row is not a row at all, so it stops the walk.
            if (pid < 0) break;

            var seconds = BitConverter.ToInt64(row.Slice(KernelProcessStartSecondsOffset, 8));
            var microseconds = BitConverter.ToInt32(row.Slice(KernelProcessStartMicrosecondsOffset, 4));

            entries.Add(new KernelProcessEntry(
                Pid: pid,
                ParentPid: BitConverter.ToInt32(row.Slice(KernelProcessParentPidOffset, 4)),
                Name: ReadFixedString(row, KernelProcessNameOffset, KernelProcessNameLength),
                StartedAtUtc: DateTime.UnixEpoch.AddSeconds(seconds).AddMicroseconds(microseconds),
                UserId: BitConverter.ToUInt32(row.Slice(KernelProcessUserIdOffset, 4))));
        }

        return entries;
    }

    /// <summary>What one process has read from and written to disk, cumulatively, in bytes.</summary>
    internal readonly record struct ProcessDiskUsage(ulong BytesRead, ulong BytesWritten);

    /// <summary>Reads the two disk figures out of <c>struct rusage_info_v2</c>.</summary>
    internal static bool TryReadProcessDiskUsage(ReadOnlySpan<byte> buffer, out ProcessDiskUsage usage)
    {
        usage = default;
        if (buffer.Length < RusageV2Size) return false;

        usage = new ProcessDiskUsage(
            BytesRead: BitConverter.ToUInt64(buffer.Slice(RusageDiskBytesReadOffset, 8)),
            BytesWritten: BitConverter.ToUInt64(buffer.Slice(RusageDiskBytesWrittenOffset, 8)));
        return true;
    }

    /// <summary>
    /// What share of one core a process used between two readings, as a percentage, or null when the
    /// two readings cannot be compared.
    ///
    /// A hundred percent means one core's worth. A process using four cores fully reads as four
    /// hundred percent, which is what every system monitor on this platform shows and what the
    /// detail screen needs in order to add up to the machine's own figure.
    /// </summary>
    internal static double? ProcessCorePercent(ulong beforeNanoseconds, ulong afterNanoseconds, TimeSpan between)
    {
        if (between <= TimeSpan.Zero) return null;
        if (afterNanoseconds < beforeNanoseconds) return null;

        var used = afterNanoseconds - beforeNanoseconds;
        return used / (between.TotalSeconds * 1_000_000_000.0) * 100.0;
    }

    /// <summary>
    /// What share of the WHOLE MACHINE a process used between two readings, as a percentage, or null
    /// when the two readings cannot be compared.
    ///
    /// THIS IS THE FIGURE THE SHARED CONTRACT ASKS FOR, and it is not the one this platform's own
    /// monitors show. <c>ProcessReading.CpuPercent</c> is defined as a share of the whole machine from
    /// nought to a hundred, and the per-session total is a sum of those shares. Reporting one core's
    /// worth as a hundred instead would make two processes on a ten core machine add up to two
    /// hundred while the machine's own reading for the same moment said twenty - a session apparently
    /// using ten times the load of the machine it runs on. Every platform probe has to answer the same
    /// question the same way, so the division happens here.
    /// </summary>
    internal static double? ProcessMachinePercent(
        ulong beforeNanoseconds, ulong afterNanoseconds, TimeSpan between, int logicalCoreCount)
    {
        if (logicalCoreCount <= 0) return null;

        var ofOneCore = ProcessCorePercent(beforeNanoseconds, afterNanoseconds, between);
        return ofOneCore is { } percent ? percent / logicalCoreCount : null;
    }

    /// <summary>
    /// What share of the whole machine a process has used over its WHOLE LIFE, as a percentage, or
    /// null when that cannot be worked out.
    ///
    /// This is the honest answer for a process with no earlier reading to subtract - one that started
    /// during the interval, or whose identifier was reused. The alternative that was here before was
    /// to write such a process down as nought percent, which is a figure the rules forbid because it
    /// is indistinguishable from a measured idle: an agent session spawned mid-interval and already
    /// burning a core read as completely idle for a whole five second tick, which is precisely the
    /// machine state the owner asks about. An average over a short life is not the same quantity as a
    /// rate over the interval, and it is close to it for a process that has only just started, which
    /// is the only case it is used in.
    /// </summary>
    internal static double? ProcessLifetimeMachinePercent(
        ulong usedNanoseconds, TimeSpan age, int logicalCoreCount)
    {
        if (logicalCoreCount <= 0) return null;
        if (age <= TimeSpan.Zero) return null;

        return usedNanoseconds / (age.TotalSeconds * 1_000_000_000.0) * 100.0 / logicalCoreCount;
    }

    // ---------------------------------------------------------------------------------------------
    // Spinning or solid state
    // ---------------------------------------------------------------------------------------------

    /// <summary>The "Device Characteristics" key that carries the medium type.</summary>
    internal const string DeviceCharacteristicsMediumType = "Medium Type";

    /// <summary>What IOKit calls a solid-state disk.</summary>
    internal const string MediumTypeSolidState = "Solid State";

    /// <summary>What IOKit calls a spinning hard drive.</summary>
    internal const string MediumTypeRotational = "Rotational";
}
