// Proof harness for the Windows resource probe (Resource Monitor mission, phase 1).
//
// Prints one full ResourceSample from this machine NEXT TO the operating system's own figures for the
// same seconds. The operating system's figures come from typeperf, the performance counter tool that
// ships with Windows. Starting a process is fine here, in a proof; the probe itself never does.
//
// Alignment, MEASURED rather than assumed: each typeperf row covers the second before its own
// timestamp, and typeperf's CSV file receives the row within 15 milliseconds of that timestamp (row
// stamped 09:59:15.916 landed at 09:59:15.922). So the probe is read when the file's first row lands
// and again when its fourth does, and the three seconds between them are typeperf's rows TWO to FOUR.
// (Its console output is no use for this: it arrives a whole second late and breaks the header.)
//
// Two rounds: the machine as it is, then with half the logical processors spun.
//
// Usage: dotnet run -c Release -- <output file>
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CcDirector.Core.Machine;

var output = args.Length > 0 ? args[0] : "windows-live-reading.txt";
var report = new StringBuilder();
void Line(string text = "") { report.AppendLine(text); Console.WriteLine(text); }

Line("WINDOWS RESOURCE PROBE - LIVE READING NEXT TO THE OPERATING SYSTEM'S OWN FIGURES");
Line($"Machine {Environment.MachineName}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors");
Line($"Taken {DateTime.UtcNow:O}");
Line();

var probe = new WindowsResourceProbe();
Round(probe, "ROUND 1: the machine as it is", spin: 0);
Round(probe, $"ROUND 2: {Environment.ProcessorCount / 2} of {Environment.ProcessorCount} logical processors spun for the whole interval", spin: Environment.ProcessorCount / 2);
Independent();

File.WriteAllText(output, report.ToString());
Console.WriteLine($"written to {output}");

void Round(WindowsResourceProbe probe, string title, int spin)
{
    Line(new string('=', 100));
    Line(title);
    Line(new string('=', 100));

    var stop = false;
    var spinners = Enumerable.Range(0, spin).Select(_ => new Thread(() => { while (!Volatile.Read(ref stop)) { } }) { IsBackground = true }).ToList();
    spinners.ForEach(t => t.Start());
    Thread.Sleep(500);

    var counters = new[]
    {
        @"\Processor(*)\% Processor Time",
        @"\Memory\Available Bytes",
        @"\Paging File(_Total)\% Usage",
        @"\PhysicalDisk(*)\% Idle Time",
        @"\PhysicalDisk(*)\Disk Read Bytes/sec",
        @"\PhysicalDisk(*)\Disk Write Bytes/sec",
        @"\PhysicalDisk(*)\Avg. Disk sec/Read",
        @"\PhysicalDisk(*)\Avg. Disk sec/Write",
        @"\PhysicalDisk(*)\Avg. Disk Queue Length",
        @"\System\Processor Queue Length",
        @"\Memory\Pages Input/sec",
        @"\Network Interface(*)\Bytes Received/sec",
        @"\Network Interface(*)\Bytes Sent/sec",
        @"\LogicalDisk(*)\Free Megabytes",
    };
    // typeperf writes its rows to a CSV file of its own. Its console output breaks the long header across
    // lines at arbitrary points, which mislabelled columns; the file does not. The file is watched as it
    // grows so the probe is read the moment each row lands, then parsed whole at the end.
    var csvPath = Path.Combine(Path.GetTempPath(), $"live-reading-{Guid.NewGuid():N}.csv");
    var startInfo = new ProcessStartInfo("typeperf") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var c in counters) startInfo.ArgumentList.Add(c);
    foreach (var a in new[] { "-si", "1", "-sc", "5", "-f", "CSV", "-y", "-o", csvPath }) startInfo.ArgumentList.Add(a);

    // The run queue at several moments, taken on a thread of its own between the two probe readings so
    // it never delays them.
    var runQueueMoments = new List<double>();
    var momentsWanted = new ManualResetEventSlim();
    var momentsDone = false;
    var momentThread = new Thread(() =>
    {
        momentsWanted.Wait();
        while (!Volatile.Read(ref momentsDone)) runQueueMoments.AddRange(RunQueueMoments(1, TimeSpan.FromMilliseconds(250)));
    }) { IsBackground = true };
    momentThread.Start();

    ResourceSample? first = null, second = null;
    using (var typeperf = Process.Start(startInfo)!)
    {
        typeperf.StandardOutput.ReadToEndAsync();
        while (!typeperf.HasExited)
        {
            var dataRows = CompleteLines(csvPath) - 1;
            if (dataRows >= 1 && first is null) { first = probe.Read(); momentsWanted.Set(); }
            if (dataRows >= 4 && second is null) { second = probe.Read(); Volatile.Write(ref momentsDone, true); }
            Thread.Sleep(10);
        }
    }
    // Released whatever happened above, so a round that lost a row cannot leave the thread waiting.
    momentsWanted.Set();
    Volatile.Write(ref momentsDone, true);
    momentThread.Join();
    Volatile.Write(ref stop, true);

    var records = File.Exists(csvPath) ? ReadCsv(csvPath) : new List<string[]>();
    File.Delete(csvPath);
    var header = records.FirstOrDefault();
    var rows = records.Skip(1).Where(r => header is not null && r.Length == header.Length).ToList();
    if (header is null || rows.Count < 5 || first is null || second is null)
    {
        Line($"typeperf gave {rows.Count} complete rows; the round could not be compared.");
        return;
    }

    // Read as rows 1 and 4 landed, the probe's three seconds are rows TWO to FOUR (alignment note at the top).
    var window = rows.Skip(1).Take(3).ToList();
    double Avg(string counterSuffix, string? instance = null)
    {
        var columns = Enumerable.Range(0, header.Length)
            .Where(i => header[i].EndsWith(counterSuffix, StringComparison.OrdinalIgnoreCase)
                        && (instance is null || header[i].Contains($"({instance})", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (columns.Count != 1) return double.NaN;
        return window.Average(r => double.Parse(r[columns[0]], CultureInfo.InvariantCulture));
    }
    double MaxOverInstances(string counterPrefix, string counterSuffix)
    {
        var columns = Enumerable.Range(0, header.Length)
            .Where(i => header[i].Contains(counterPrefix, StringComparison.OrdinalIgnoreCase)
                        && header[i].EndsWith(counterSuffix, StringComparison.OrdinalIgnoreCase)
                        && !header[i].Contains("_Total", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return columns.Max(c => window.Average(r => double.Parse(r[c], CultureInfo.InvariantCulture)));
    }
    string DiskInstance(string physicalDrive)
    {
        var number = physicalDrive["PhysicalDrive".Length..];
        var match = header.Select(h => h).FirstOrDefault(h => h.Contains($@"\PhysicalDisk({number} ", StringComparison.OrdinalIgnoreCase));
        if (match is null) return "?";
        var open = match.IndexOf("PhysicalDisk(", StringComparison.Ordinal) + "PhysicalDisk(".Length;
        return match[open..match.IndexOf(')', open)];
    }

    Line();
    Line("THE PROBE'S SAMPLE (the second reading; its rates cover the same three seconds as typeperf's rows 2 to 4)");
    PrintSample(second);
    Line();
    Line("SIDE BY SIDE                                   probe              operating system (typeperf average, rows 2-4)");
    Compare("processor, whole machine, % busy", second.Cpu.CouldRead ? second.Cpu.TotalBusyPercent : double.NaN, Avg(@"\% Processor Time", "_Total"));
    Compare("processor, busiest core, % busy", second.Cpu.CouldRead ? second.Cpu.BusiestCorePercent : double.NaN, MaxOverInstances(@"\Processor(", @"\% Processor Time"));
    Compare("memory available, GB", second.Memory.AvailableBytes / 1e9, Avg(@"\Memory\Available Bytes") / 1e9);
    Compare("page file, % in use", second.Swap.TotalBytes == 0 ? double.NaN : 100.0 * second.Swap.UsedBytes / second.Swap.TotalBytes, Avg(@"\Paging File(_Total)\% Usage"));
    foreach (var disk in second.Disks.Disks)
    {
        var instance = DiskInstance(disk.Name);
        Line($"  {disk.Name} = typeperf instance \"{instance}\"");
        Compare($"    {disk.Name} % busy", disk.BusyPercent, 100 - Avg(@"\% Idle Time", instance));
        Compare($"    {disk.Name} read MB/s", disk.ReadBytesPerSecond / 1e6, Avg(@"\Disk Read Bytes/sec", instance) / 1e6);
        Compare($"    {disk.Name} write MB/s", disk.WriteBytesPerSecond / 1e6, Avg(@"\Disk Write Bytes/sec", instance) / 1e6);
        Compare($"    {disk.Name} ms per write", disk.MillisecondsPerWrite ?? double.NaN, Avg(@"\Avg. Disk sec/Write", instance) * 1000);
        Compare($"    {disk.Name} average queue length", disk.QueueLength ?? double.NaN, Avg(@"\Avg. Disk Queue Length", instance));
    }
    foreach (var volume in second.Volumes)
    {
        var letter = volume.MountPoint.TrimEnd('\\');
        Compare($"volume {letter} free, GB", volume.FreeBytes / 1e9, Avg(@"\Free Megabytes", letter) * 1024 * 1024 / 1e9);
    }
    Compare($"run queue, mean of {runQueueMoments.Count} moments / of 3 moments", runQueueMoments.Count == 0 ? double.NaN : runQueueMoments.Average(), Avg(@"\System\Processor Queue Length"));
    Compare("hard page faults (pages input) per second", second.Stall.HardPageFaultsPerSecond ?? double.NaN, Avg(@"\Memory\Pages Input/sec"));
    Line("  network: the probe counts only physical interfaces that are up; typeperf lists every interface, so the");
    Line("  Ethernet adapter's own row is the comparable one:");
    Compare("network received MB/s", second.Network.ReceiveBytesPerSecond / 1e6, MaxOverInstances(@"\Network Interface(", @"\Bytes Received/sec") / 1e6);
    Compare("network sent MB/s", second.Network.SendBytesPerSecond / 1e6, MaxOverInstances(@"\Network Interface(", @"\Bytes Sent/sec") / 1e6);
    Line();
    Line("  Note on the run queue: each figure is a count at a moment, not an average over time, so the two tools");
    Line("  are compared as the mean of several moments each, taken at different instants within the same seconds.");
    Line("  Note on milliseconds per write: typeperf's figure here is the mean of three per-second averages; the probe's");
    Line("  is every write's time over the number of writes in the three seconds. They differ when the seconds differ.");
    Line();
}

// The run queue at several moments, from a probe of its own whose clock moves on two seconds per read
// so its one-second cache never answers. Only the run queue is used; it needs no interval.
IEnumerable<double> RunQueueMoments(int count, TimeSpan apart)
{
    var fakeNow = DateTime.UtcNow;
    var momentProbe = new WindowsResourceProbe(now: () => fakeNow = fakeNow.AddSeconds(2));
    momentProbe.Read();
    var moments = new List<double>();
    for (var i = 0; i < count; i++)
    {
        Thread.Sleep(apart);
        if (momentProbe.Read().Stall.RunQueueLength is { } queue) moments.Add(queue);
    }
    return moments;
}

void Compare(string what, double probe, double os)
{
    string F(double v) => double.IsNaN(v) ? "n/a" : v.ToString("F2", CultureInfo.InvariantCulture);
    Line($"  {what,-44} {F(probe),12}       {F(os),12}");
}

void PrintSample(ResourceSample s)
{
    Line($"  taken at        {s.TakenAtUtc:O}");
    Line($"  processor       {s.Cpu}");
    Line($"  memory          {s.Memory}");
    Line($"  page file       {s.Swap}");
    Line($"  disks           ({(s.Disks.CouldRead ? $"{s.Disks.Disks.Count} listed" : $"unreadable: {s.Disks.UnreadableReason}")})");
    foreach (var d in s.Disks.Disks)
        Line($"                  {d}; ms per read {d.MillisecondsPerRead?.ToString("F3", CultureInfo.InvariantCulture) ?? "none in interval"}, " +
             $"ms per write {d.MillisecondsPerWrite?.ToString("F3", CultureInfo.InvariantCulture) ?? "none in interval"}, average queue {d.QueueLength?.ToString("F3", CultureInfo.InvariantCulture) ?? "null"}");
    Line($"  volumes         ({s.Volumes.Count})");
    foreach (var v in s.Volumes) Line($"                  {v}");
    Line($"  network         {s.Network}");
    Line($"  stalls          {s.Stall}");
    Line($"  processes       {s.Processes}; the ten using the most processor:");
    foreach (var p in s.Processes.Processes.OrderByDescending(p => p.CpuPercent).Take(10))
        Line($"                  {p}, parent {p.ParentPid}, started {p.StartTimeUtc:O}, read {p.DiskReadBytesPerSecond / 1e6:F2} MB/s, written {p.DiskWriteBytesPerSecond / 1e6:F2} MB/s");
    var me = s.Processes.Processes.FirstOrDefault(p => p.Pid == Environment.ProcessId);
    Line($"  this harness    {(me is null ? "NOT FOUND" : $"{me}, parent {me.ParentPid}, started {me.StartTimeUtc:O}")}");
}

void Independent()
{
    Line(new string('=', 100));
    Line("INDEPENDENT CHECK OF DISK KIND AND VOLUME PLACEMENT (Get-PhysicalDisk and Get-Partition, read by hand)");
    Line(new string('=', 100));
    var probeDisks = probe.Read().Disks.Disks;
    foreach (var d in probeDisks) Line($"  probe: {d.Name} kind {d.Kind}");
    foreach (var v in probe.Read().Volumes) Line($"  probe: volume {v.MountPoint} on {string.Join(", ", v.PhysicalDiskNames)}");
    var ps = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-PhysicalDisk | Sort-Object DeviceId | Format-Table DeviceId, FriendlyName, MediaType, BusType -AutoSize | Out-String -Width 200; Get-Partition | Where-Object DriveLetter | Format-Table DriveLetter, DiskNumber -AutoSize | Out-String\"")
    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var p = Process.Start(ps)!;
    foreach (var l in p.StandardOutput.ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)) Line($"  os:    {l}");
    p.WaitForExit();
}

// Complete lines in a file another process is still writing.
static int CompleteLines(string path)
{
    if (!File.Exists(path)) return 0;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd().Count(ch => ch == '\n');
}

// A real CSV reader: quoted cells, commas inside quotes, doubled quotes.
static List<string[]> ReadCsv(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var text = new StreamReader(stream).ReadToEnd();
    var records = new List<string[]>();
    var cells = new List<string>();
    var cell = new StringBuilder();
    var quoted = false;
    for (var i = 0; i < text.Length; i++)
    {
        var ch = text[i];
        if (quoted)
        {
            if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (ch == '"') quoted = false;
            else cell.Append(ch);
        }
        else if (ch == '"') quoted = true;
        else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); }
        else if (ch == '\n')
        {
            cells.Add(cell.ToString()); cell.Clear();
            if (cells.Count > 1 || cells[0].Trim().Length > 0) records.Add(cells.ToArray());
            cells.Clear();
        }
        else if (ch != '\r') cell.Append(ch);
    }
    return records;
}
