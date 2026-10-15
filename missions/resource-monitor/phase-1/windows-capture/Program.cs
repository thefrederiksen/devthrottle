// Captures the raw Windows structures the probe reads, as fixtures for the parser tests.
using System.Buffers.Binary;
using System.Text;
using CcDirector.Core.Machine;

if (args.Length > 1 && args[0] == "states") { States.Run(int.Parse(args[1])); return; }
var outDir = args.Length > 0 ? args[0] : "out";
Directory.CreateDirectory(outDir);
var notes = new StringBuilder();
notes.AppendLine($"Captured on {Environment.MachineName}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {DateTime.UtcNow:O}");
notes.AppendLine($"Logical processors: {Environment.ProcessorCount}, page size: {Environment.SystemPageSize}");

// CPU: two readings one second apart.
var systemPerformance1 = WindowsResourceNative.ReadSystemPerformance();
var sys1 = WindowsResourceNative.ReadSystemTimes();
var cpu1 = WindowsResourceNative.ReadProcessorPerformance();
var disks1 = new Dictionary<int, byte[]>();
var handles = new Dictionary<int, Microsoft.Win32.SafeHandles.SafeFileHandle>();
for (var n = 0; n <= WindowsResourceNative.MaxPhysicalDiskNumber; n++)
{
    var h = WindowsResourceNative.OpenPhysicalDisk(n);
    if (h is null) continue;
    handles[n] = h;
    disks1[n] = WindowsResourceNative.ReadDiskPerformance(h);
}
Thread.Sleep(1000);
var systemPerformance2 = WindowsResourceNative.ReadSystemPerformance();
var sys2 = WindowsResourceNative.ReadSystemTimes();
File.WriteAllBytes(Path.Combine(outDir, "system-performance-1.bin"), systemPerformance1);
File.WriteAllBytes(Path.Combine(outDir, "system-performance-2.bin"), systemPerformance2);
var memoryStatus = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
GlobalMemoryStatusEx(ref memoryStatus);
notes.AppendLine($"System performance: {systemPerformance2.Length} bytes; PageReadCount 1={WindowsResourceParsing.ParsePageReadCount(systemPerformance1)} 2={WindowsResourceParsing.ParsePageReadCount(systemPerformance2)}; " +
                 $"AvailablePages={WindowsResourceParsing.ParseAvailablePages(systemPerformance2)} CommitLimit={WindowsResourceParsing.ParseCommitLimitPages(systemPerformance2)}; " +
                 $"GlobalMemoryStatusEx just after: available={memoryStatus.AvailPhys} commitLimit={memoryStatus.TotalPageFile}");
var cpu2 = WindowsResourceNative.ReadProcessorPerformance();
File.WriteAllBytes(Path.Combine(outDir, "processor-performance-1.bin"), cpu1);
File.WriteAllBytes(Path.Combine(outDir, "processor-performance-2.bin"), cpu2);
notes.AppendLine($"GetSystemTimes 1: idle={sys1.Idle} kernel={sys1.Kernel} user={sys1.User}");
notes.AppendLine($"GetSystemTimes 2: idle={sys2.Idle} kernel={sys2.Kernel} user={sys2.User}");
notes.AppendLine($"Whole-machine busy between them: {WindowsResourceParsing.BusyFraction(sys1, sys2):P1}");

foreach (var (n, h) in handles)
{
    var perf2 = WindowsResourceNative.ReadDiskPerformance(h);
    File.WriteAllBytes(Path.Combine(outDir, $"disk{n}-performance-1.bin"), disks1[n]);
    File.WriteAllBytes(Path.Combine(outDir, $"disk{n}-performance-2.bin"), perf2);
    File.WriteAllBytes(Path.Combine(outDir, $"disk{n}-seek-penalty.bin"), WindowsResourceNative.ReadSeekPenalty(h));
    var rates = WindowsResourceParsing.DiskRatesBetween(WindowsResourceParsing.ParseDiskPerformance(disks1[n]), WindowsResourceParsing.ParseDiskPerformance(perf2));
    notes.AppendLine($"PhysicalDrive{n}: seekPenalty={WindowsResourceParsing.ParseIncursSeekPenalty(WindowsResourceNative.ReadSeekPenalty(h))} " +
                     $"busy={rates.BusyFraction:P1} read/s={rates.ReadBytesPerSecond:F0} write/s={rates.WriteBytesPerSecond:F0} sec/read={rates.SecondsPerRead} sec/write={rates.SecondsPerWrite} averageQueue={rates.AverageQueueLength}");
    h.Dispose();
}

foreach (var letter in WindowsResourceNative.FixedDriveLetters())
{
    using var v = WindowsResourceNative.OpenVolume(letter);
    var extents = WindowsResourceNative.ReadVolumeDiskExtents(v);
    File.WriteAllBytes(Path.Combine(outDir, $"volume-{letter}-extents.bin"), extents);
    var (free, total) = WindowsResourceNative.ReadVolumeSpace($"{letter}:\\");
    notes.AppendLine($"Volume {letter}: disks={string.Join(",", WindowsResourceParsing.ParseVolumeDiskNumbers(extents))} free={free} total={total}");
}

var pf = WindowsResourceNative.ReadPageFileInformation();
File.WriteAllBytes(Path.Combine(outDir, "page-file.bin"), ScrubPageFileNames(pf));
var pft = WindowsResourceParsing.ParsePageFileInformation(pf, (ulong)Environment.SystemPageSize);
notes.AppendLine($"Page files: count={pft.Count} total={pft.TotalBytes} inUse={pft.InUseBytes}");

var ift = WindowsResourceNative.ReadInterfaceTable();
var keepAliases = new HashSet<string> { "Ethernet", "Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Ethernet-QoS Packet Scheduler-0000", "Ethernet-WFP 802.3 MAC Layer LightWeight Filter-0000", "Wi-Fi", "Loopback Pseudo-Interface 1", "vEthernet (Default Switch)" };
ift = KeepInterfaces(ift, keepAliases);
var rows = WindowsResourceParsing.ParseInterfaceTable(ift);
File.WriteAllBytes(Path.Combine(outDir, "interface-table.bin"), ScrubInterfaces(ift));
foreach (var r in rows)
    notes.AppendLine($"Interface {r.Index}: alias={r.Alias} type={r.Type} hardware={r.IsHardware} oper={r.OperStatus} in={r.InOctets} out={r.OutOctets} physicalAndUp={WindowsResourceParsing.IsPhysicalAndUp(r)}");

var stopBurn = false;
var burners = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(_ => new Thread(() => { while (!Volatile.Read(ref stopBurn)) { } }) { IsBackground = true }).ToList();
burners.ForEach(t => t.Start());
Thread.Sleep(500);
var procSize = 1 << 20;
var procBuf = WindowsResourceNative.ReadProcessInformation(ref procSize, out var address);
Volatile.Write(ref stopBurn, true);
var procs = WindowsResourceParsing.ParseProcessInformation(procBuf, address);
notes.AppendLine($"Processes: {procs.Count}, threads waiting on a processor: {procs.Sum(p => p.ThreadsWaitingOnCpu)}, self present: {procs.Any(p => p.ProcessId == Environment.ProcessId)}");
var (trimmed, kept) = TrimToSystemProcesses(procBuf, address);
File.WriteAllBytes(Path.Combine(outDir, "process-information.bin"), trimmed);
File.WriteAllText(Path.Combine(outDir, "process-information.address.txt"), address.ToString());
var trimmedParsed = WindowsResourceParsing.ParseProcessInformation(trimmed, address);
notes.AppendLine($"Trimmed process fixture keeps {kept} operating-system processes:");
foreach (var p in trimmedParsed)
    notes.AppendLine($"  pid={p.ProcessId} parent={p.ParentProcessId} name={p.Name ?? "(none)"} threads={p.ThreadCount} waitingOnCpu={p.ThreadsWaitingOnCpu} ws={p.WorkingSetBytes} private={p.PrivateBytes} read={p.ReadBytes} write={p.WriteBytes} created={(p.CreateTime == 0 ? "0" : DateTime.FromFileTimeUtc(p.CreateTime).ToString("O"))}");

File.WriteAllText(Path.Combine(outDir, "capture-notes.txt"), notes.ToString());
Console.WriteLine(notes);

// Page file names can carry a user's path; the parser does not read them. Name lengths zeroed.
static byte[] ScrubPageFileNames(byte[] b)
{
    var offset = 0;
    while (b.Length >= offset + 32)
    {
        b.AsSpan(offset + 16, 16).Clear();
        var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
        if (next == 0) break;
        offset += next;
    }
    return b;
}

// Hardware addresses and the interface and network identifiers are zeroed.
static byte[] ScrubInterfaces(byte[] b)
{
    var count = BinaryPrimitives.ReadInt32LittleEndian(b);
    for (var i = 0; i < count; i++)
    {
        var row = 8 + i * WindowsResourceParsing.InterfaceRowSize;
        b.AsSpan(row + 0, 8).Clear();       // InterfaceLuid
        b.AsSpan(row + 12, 16).Clear();     // InterfaceGuid
        b.AsSpan(row + 1060, 64).Clear();   // PhysicalAddress, PermanentPhysicalAddress
        b.AsSpan(row + 1168, 16).Clear();   // NetworkGuid
    }
    return b;
}

// Keeps only operating-system processes, so the public repository carries none of the owner's
// applications. Each kept entry is copied whole (entry, threads, name), then its link and its name
// pointer are rebased onto the original buffer address.
static (byte[] Bytes, int Kept) TrimToSystemProcesses(byte[] buf, long address)
{
    var allow = new HashSet<string?>(StringComparer.OrdinalIgnoreCase)
    { null, "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "fontdrvhost.exe", "svchost.exe", "dwm.exe", "Memory Compression", "Capture.exe" };
    var blocks = new List<byte[]>();
    var offset = 0;
    while (true)
    {
        var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
        var end = next == 0 ? buf.Length : offset + next;
        var one = WindowsResourceParsing.ParseProcessInformation(Slice(buf, offset, end), address + offset);
        if (allow.Contains(one[0].Name) && (blocks.Count < 40 || one[0].Name == "Capture.exe"))
        {
            var block = buf[offset..end];
            var namePtr = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(64));
            blocks.Add(block);
            // remember the name's position relative to its own entry, fixed up below
            BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(64), namePtr == 0 ? 0 : namePtr - (address + offset));
        }
        if (next == 0) break;
        offset = end;
    }
    var result = new byte[blocks.Sum(b => b.Length)];
    var at = 0;
    for (var i = 0; i < blocks.Count; i++)
    {
        var b = blocks[i];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), i == blocks.Count - 1 ? 0u : (uint)b.Length);
        var rel = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(64));
        if (rel != 0) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(64), address + at + rel);
        b.CopyTo(result, at);
        at += b.Length;
    }
    return (result, blocks.Count);
}

// A single entry parsed on its own: its link zeroed so the parser stops after it.
static byte[] Slice(byte[] buf, int from, int to)
{
    var s = buf[from..to];
    BinaryPrimitives.WriteUInt32LittleEndian(s, 0);
    return s;
}

static byte[] KeepInterfaces(byte[] b, HashSet<string> aliases)
{
    var rows = WindowsResourceParsing.ParseInterfaceTable(b);
    var kept = new List<int>();
    for (var i = 0; i < rows.Count; i++) if (aliases.Contains(rows[i].Alias)) kept.Add(i);
    var result = new byte[8 + kept.Count * WindowsResourceParsing.InterfaceRowSize];
    BinaryPrimitives.WriteInt32LittleEndian(result, kept.Count);
    for (var k = 0; k < kept.Count; k++)
        b.AsSpan(8 + kept[k] * WindowsResourceParsing.InterfaceRowSize, WindowsResourceParsing.InterfaceRowSize).CopyTo(result.AsSpan(8 + k * WindowsResourceParsing.InterfaceRowSize));
    return result;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MemoryStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
