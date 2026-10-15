using System.Runtime.InteropServices;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE macOS SYSTEM CALLS THE RESOURCE PROBE USES, AND NOTHING ELSE.
///
/// Every declaration here is a direct call into the operating system. NO SUBPROCESS IS STARTED
/// ANYWHERE IN THIS FILE, for the same reason <see cref="MachineMemoryProbe"/> starts none: the probe
/// runs on a five second timer, and a machine that is already short of processor time or disk time
/// must not be charged for a process launch so that we can tell it is busy. Starting
/// <c>iostat</c> or <c>ps</c> would make the measurement part of the problem it is measuring.
///
/// WHY THE PROPERTY DICTIONARIES ARE READ AS PROPERTY LISTS. The disk statistics live in an IOKit
/// registry property, which is a Core Foundation dictionary. Reading one field at a time through
/// Core Foundation means a long tail of <c>CFDictionaryGetValue</c>, <c>CFNumberGetValue</c>,
/// <c>CFStringGetCString</c> and matching <c>CFRelease</c> calls - every one of them a place where a
/// wrong type or a missed release costs a leak in a process that runs for weeks, and none of them
/// testable anywhere but on a Mac. Instead the whole dictionary is handed to
/// <c>CFPropertyListCreateData</c> once, which returns it as the operating system's own property
/// list text. That text is then read by pure functions in <see cref="MacOsResourceParsing"/>, which
/// are unit tested on every platform from property lists captured on a real Mac. The cost is one
/// serialisation of a twelve-key dictionary per disk per sample; what it buys is that the part most
/// likely to be wrong is the part that is covered by tests.
/// </summary>
internal static class MacOsNative
{
    private const string LibC = "libc";
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // ---------------------------------------------------------------------------------------------
    // sysctl
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a sysctl value by name into a caller-owned buffer. Returns 0 on success, -1 on failure.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int sysctlbyname(string name, IntPtr oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

    // ---------------------------------------------------------------------------------------------
    // Mach: processor load and the host port
    // ---------------------------------------------------------------------------------------------

    /// <summary>PROCESSOR_CPU_LOAD_INFO - the flavour that returns processor tick counters per core.</summary>
    internal const int ProcessorCpuLoadInfo = 2;

    /// <summary>CPU_STATE_MAX - how many tick counters each core reports: user, system, idle, nice.</summary>
    internal const int CpuStateMax = 4;

    /// <summary>CPU_STATE_USER, the index of the user tick counter within one core's block.</summary>
    internal const int CpuStateUser = 0;

    /// <summary>CPU_STATE_SYSTEM.</summary>
    internal const int CpuStateSystem = 1;

    /// <summary>CPU_STATE_IDLE.</summary>
    internal const int CpuStateIdle = 2;

    /// <summary>CPU_STATE_NICE.</summary>
    internal const int CpuStateNice = 3;

    /// <summary>
    /// The host port. This hands back a SEND RIGHT and the caller owns it - see the note in
    /// <see cref="MachineMemoryProbe"/>, where not releasing one leaked a reference per call.
    /// </summary>
    [DllImport(LibC)]
    internal static extern IntPtr mach_host_self();

    /// <summary>This task's port. Needed to give memory back with <see cref="vm_deallocate"/>.</summary>
    [DllImport(LibC)]
    internal static extern IntPtr mach_task_self();

    /// <summary>Releases a send right obtained from <see cref="mach_host_self"/>.</summary>
    [DllImport(LibC)]
    internal static extern int mach_port_deallocate(IntPtr task, IntPtr name);

    /// <summary>
    /// Per-core processor information. The kernel ALLOCATES the array it returns into our address
    /// space, and the caller must hand it back with <see cref="vm_deallocate"/>. Forgetting that
    /// leaks a page per sample, which on a five second timer is a leak the size of a day's uptime.
    /// </summary>
    [DllImport(LibC)]
    internal static extern int host_processor_info(
        IntPtr host,
        int flavor,
        out uint outProcessorCount,
        out IntPtr outProcessorInfo,
        out uint outProcessorInfoCount);

    /// <summary>
    /// The conversion between Mach absolute time units and nanoseconds, as a fraction. On Intel Macs
    /// it is 1/1, which is exactly why reading Mach absolute time as nanoseconds is a mistake that
    /// ships: it is CORRECT on Intel and wrong by a factor of about forty-two on Apple Silicon, where
    /// this machine reports 125/3.
    /// </summary>
    [DllImport(LibC)]
    internal static extern int mach_timebase_info(out MachTimebaseInfo info);

    /// <summary>mach_timebase_info_data_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MachTimebaseInfo
    {
        public uint numer;
        public uint denom;
    }

    /// <summary>Returns memory the kernel allocated into this task on our behalf.</summary>
    [DllImport(LibC)]
    internal static extern int vm_deallocate(IntPtr targetTask, IntPtr address, IntPtr size);

    // ---------------------------------------------------------------------------------------------
    // File systems and network interfaces
    // ---------------------------------------------------------------------------------------------

    /// <summary>MNT_NOWAIT - return the figures already cached rather than asking each file system,
    /// which is what makes this call cheap enough for a five second timer. A network file system
    /// that has gone away must not be allowed to block the sampler.</summary>
    internal const int MountNoWait = 2;

    /// <summary>
    /// Lists the mounted file systems. Called once with a null buffer to learn the count, then again
    /// with a buffer of that size. Returns the number of structures written, or -1.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int getfsstat(IntPtr buf, int bufsize, int flags);

    /// <summary>Builds the linked list of network interface addresses. Freed with <see cref="freeifaddrs"/>.</summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int getifaddrs(out IntPtr addresses);

    /// <summary>Frees the list built by <see cref="getifaddrs"/>.</summary>
    [DllImport(LibC)]
    internal static extern void freeifaddrs(IntPtr addresses);

    // ---------------------------------------------------------------------------------------------
    // Processes
    // ---------------------------------------------------------------------------------------------

    /// <summary>PROC_PIDTBSDINFO - the parent, the name and the start time.</summary>
    internal const int ProcPidTBsdInfo = 3;

    /// <summary>PROC_PIDTASKINFO - the processor time used and the resident memory.</summary>
    internal const int ProcPidTaskInfo = 4;

    /// <summary>RUSAGE_INFO_V2 - the first version that carries the bytes read and written to disk.</summary>
    internal const int RusageInfoV2 = 2;

    /// <summary>
    /// Every process identifier on the machine. Called once with a null buffer to learn the size
    /// needed, then again with a buffer. Returns the number of BYTES written, not the count.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int proc_listallpids(IntPtr buffer, int buffersize);

    /// <summary>
    /// One process's information, by flavour. Returns the number of bytes written, or 0 when the
    /// process has gone or we are not allowed to read it - which is an answer, not a failure.
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int buffersize);

    /// <summary>One process's resource usage, including the bytes it has read and written to disk.</summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int proc_pid_rusage(int pid, int flavor, IntPtr buffer);

    // ---------------------------------------------------------------------------------------------
    // IOKit: the disks
    // ---------------------------------------------------------------------------------------------

    /// <summary>kIORegistryIterateRecursively - keep walking rather than looking only at this entry.</summary>
    internal const uint RegistryIterateRecursively = 1;

    /// <summary>kIORegistryIterateParents - walk towards the providers rather than the clients.</summary>
    internal const uint RegistryIterateParents = 2;

    /// <summary>The IOService registry plane, the one the storage stack is wired up in.</summary>
    internal const string IoServicePlane = "IOService";

    /// <summary>Builds a matching dictionary for every registry entry of the named class.</summary>
    [DllImport(IoKit)]
    internal static extern IntPtr IOServiceMatching(string name);

    /// <summary>Builds a matching dictionary for the one registry entry with this BSD device name.</summary>
    [DllImport(IoKit)]
    internal static extern IntPtr IOBSDNameMatching(uint mainPort, uint options, string bsdName);

    /// <summary>
    /// Runs a matching dictionary and hands back an iterator. CONSUMES a reference on the matching
    /// dictionary whether it succeeds or fails, so the dictionary must not also be released.
    /// </summary>
    [DllImport(IoKit)]
    internal static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    /// <summary>The next registry entry, or 0 when the iterator is spent. The caller owns what it returns.</summary>
    [DllImport(IoKit)]
    internal static extern uint IOIteratorNext(uint iterator);

    /// <summary>Releases an IOKit object. Every entry and iterator obtained above needs one.</summary>
    [DllImport(IoKit)]
    internal static extern int IOObjectRelease(uint @object);

    /// <summary>The whole property dictionary of one registry entry. The caller owns the dictionary.</summary>
    [DllImport(IoKit)]
    internal static extern int IORegistryEntryCreateCFProperties(uint entry, out IntPtr properties, IntPtr allocator, uint options);

    /// <summary>
    /// One property, found by walking the registry from this entry. With
    /// <see cref="RegistryIterateParents"/> this is how "Device Characteristics" is found on the
    /// device above the driver, and without it how "BSD Name" is found on the media below.
    /// </summary>
    [DllImport(IoKit)]
    internal static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, string plane, IntPtr key, IntPtr allocator, uint options);

    /// <summary>
    /// An iterator over ALL of an entry's parents in the named plane, not just the first one. A
    /// storage stack can genuinely fan out - an Apple file system container may be built on more than
    /// one physical store - and taking only the first parent would quietly drop the others.
    /// </summary>
    [DllImport(IoKit)]
    internal static extern int IORegistryEntryGetParentIterator(uint entry, string plane, out uint iterator);

    /// <summary>
    /// An entry's identifier, stable for as long as the entry exists. Used to avoid walking the same
    /// branch twice, because the registry's parent links converge rather than forming a tree.
    /// </summary>
    [DllImport(IoKit)]
    internal static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);

    // ---------------------------------------------------------------------------------------------
    // Core Foundation: just enough to turn a dictionary into text and to make a key
    // ---------------------------------------------------------------------------------------------

    /// <summary>kCFPropertyListXMLFormat_v1_0. Chosen over the binary format because the captured
    /// fixtures are read by people during review, not only by the parser.</summary>
    internal const long PropertyListXmlFormat = 100;

    /// <summary>kCFStringEncodingUTF8.</summary>
    internal const uint StringEncodingUtf8 = 0x08000100;

    /// <summary>Makes a Core Foundation string, for use as a property key. The caller owns it.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    /// <summary>Serialises any property list value to the operating system's own text form.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFPropertyListCreateData(IntPtr allocator, IntPtr propertyList, long format, uint options, IntPtr error);

    /// <summary>The length in bytes of a CFData.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDataGetLength(IntPtr data);

    /// <summary>A pointer to a CFData's bytes. Valid only while the CFData is alive.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDataGetBytePtr(IntPtr data);

    /// <summary>Releases a Core Foundation object.</summary>
    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr cf);
}
