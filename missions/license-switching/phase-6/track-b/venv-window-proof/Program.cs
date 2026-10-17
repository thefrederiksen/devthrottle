// A measured reproduction of the window in which a tools update leaves the machine with no cc-devthrottle.
//
// It installs a two-version tools bundle into an ISOLATED folder with the real setup engine, a real base Python
// (copied from the folder given on the command line), a real venv and a real pip, and while the second install
// runs it watches the venv's cc-devthrottle launcher once a millisecond, recording every stretch in which the
// file is not there. Then it runs the installed launcher and the bin shim to show the result really starts.
//
// Usage:  dotnet run -- first  <a base Python folder to copy> <an empty scratch folder>
//         dotnet run -- second <the same base Python folder>  <the same scratch folder>
// The two installs are two runs because the install lock is a named mutex released across an await, so a
// second install in the SAME process can wait on a lock its own first install still owns (a finding written up
// in tool-health.md, not fixed here). Nothing outside the scratch folder is written. The tool in the bundle is a
// stand-in named cc-devthrottle that prints its version; it is built here as a wheel, so no network is used.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Setup.Engine;

if (args.Length != 3 || (args[0] != "first" && args[0] != "second"))
{
    Console.Error.WriteLine("usage: VenvWindowProof first|second <base Python folder> <scratch folder>");
    return 2;
}
var isFirst = args[0] == "first";
var pythonSource = Path.GetFullPath(args[1]);
var work = Path.GetFullPath(args[2]);
if (isFirst && Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
{
    Console.Error.WriteLine($"the scratch folder {work} is not empty");
    return 2;
}
Directory.CreateDirectory(work);
var log = new StringBuilder();
EngineLog.Sink = line => { lock (log) log.AppendLine($"{DateTime.UtcNow:HH:mm:ss.fff} {line}"); };

var layout = new InstallLayout(Path.Combine(work, "local"));
var launcher = PythonToolsInstaller.ConsoleScriptPath(layout, "cc-devthrottle");
Console.WriteLine($"isolated install root: {layout.LocalRoot}");
Console.WriteLine($"watched launcher:      {launcher}");

if (isFirst)
{
    var first = await new PythonToolsInstaller(layout).InstallAsync(MakeRelease("1.0.0"), new ReleaseSource());
    Console.WriteLine($"first install: success={first.Success} {first.Message}");
    if (!first.Success) return Fail("the first install failed");
    Console.WriteLine($"installed launcher says: {Run(launcher)}");
    File.WriteAllText(Path.Combine(work, "engine-first.log"), log.ToString());
    return 0;
}
Console.WriteLine($"installed launcher says: {Run(launcher)}");

// Watch the launcher through the whole second install.
var absences = new List<(TimeSpan Start, TimeSpan Length)>();
var clock = Stopwatch.StartNew();
var stop = false;
long probes = 0;
var watcher = new Thread(() =>
{
    TimeSpan? goneAt = null;
    while (!Volatile.Read(ref stop))
    {
        probes++;
        var present = File.Exists(launcher);
        var now = clock.Elapsed;
        if (!present && goneAt is null) goneAt = now;
        if (present && goneAt is not null) { absences.Add((goneAt.Value, now - goneAt.Value)); goneAt = null; }
        // One look a millisecond: fine enough to tell a few milliseconds from a minute, and light enough not to
        // be the open handle that makes Windows refuse the swap rename (a tight loop here did exactly that).
        Thread.Sleep(1);
    }
    if (goneAt is not null) absences.Add((goneAt.Value, clock.Elapsed - goneAt.Value));
}) { IsBackground = true, Priority = ThreadPriority.AboveNormal };

var release2 = MakeRelease("2.0.0");
watcher.Start();
var installStart = clock.Elapsed;
var second = await new PythonToolsInstaller(layout).InstallAsync(release2, new ReleaseSource());
var installTook = clock.Elapsed - installStart;
Volatile.Write(ref stop, true);
watcher.Join();

Console.WriteLine($"second install: success={second.Success} took={installTook.TotalSeconds:F1}s {second.Message}");
Console.WriteLine($"launcher probes: {probes}");
Console.WriteLine($"stretches with no launcher: {absences.Count}");
foreach (var (start, length) in absences)
    Console.WriteLine($"  absent from {start.TotalMilliseconds:F1} ms for {length.TotalMilliseconds:F3} ms");
var longest = absences.Count == 0 ? TimeSpan.Zero : absences.Max(a => a.Length);
Console.WriteLine($"LONGEST ABSENCE: {longest.TotalMilliseconds:F3} ms");
Console.WriteLine($"installed launcher says: {Run(launcher)}");
var shim = OperatingSystem.IsWindows() ? Path.Combine(layout.BinDir, "cc-devthrottle.cmd") : Path.Combine(layout.MacUserBinDir, "cc-devthrottle");
Console.WriteLine($"shim says:               {Run(shim)}");
File.WriteAllText(Path.Combine(work, "engine.log"), log.ToString());
Console.WriteLine($"engine log: {Path.Combine(work, "engine.log")}");
return second.Success ? 0 : Fail("the second install failed");

int Fail(string why)
{
    File.WriteAllText(Path.Combine(work, "engine.log"), log.ToString());
    Console.Error.WriteLine($"FAILED: {why}; engine log at {Path.Combine(work, "engine.log")}");
    return 1;
}

static string Run(string exe)
{
    var info = OperatingSystem.IsWindows() && exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        ? new ProcessStartInfo("cmd.exe", $"/c \"{exe}\"")
        : new ProcessStartInfo(exe);
    info.RedirectStandardOutput = true;
    info.RedirectStandardError = true;
    using var p = Process.Start(info)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return $"exit={p.ExitCode} {output.Trim()}";
}

ResolvedRelease MakeRelease(string version)
{
    var dir = Path.Combine(work, "release-" + version);
    var stage = Path.Combine(work, "stage-" + version);
    Directory.CreateDirectory(dir);

    var tools = Path.Combine(stage, "tools");
    var wheelhouse = Path.Combine(tools, "wheelhouse");
    Directory.CreateDirectory(wheelhouse);
    WriteProbeWheel(Path.Combine(wheelhouse, $"cc_probe-{version}-py3-none-any.whl"), version);
    File.WriteAllText(Path.Combine(tools, "tools-manifest.json"),
        $"{{\"bundleVersion\":\"{version}\",\"tools\":[{{\"dist\":\"cc-probe\",\"scripts\":[\"cc-devthrottle\"]}}]}}");

    var pyAsset = Path.Combine(dir, PythonToolsInstaller.PythonAsset);
    var toolsAsset = Path.Combine(dir, PythonToolsInstaller.ToolsAsset);
    Pack(pythonSource, pyAsset);
    Pack(tools, toolsAsset);

    var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
    string Entry(string path) =>
        $"\"{Path.GetFileName(path)}\":{{\"version\":\"{version}\",\"sha256\":\"{Sha(path)}\",\"platform\":\"{platform}\",\"size\":{new FileInfo(path).Length}}}";
    File.WriteAllText(Path.Combine(dir, "release-manifest.json"),
        $"{{\"version\":\"{version}\",\"assets\":{{{Entry(pyAsset)},{Entry(toolsAsset)}}}}}");
    return ReleaseSource.LoadLocalReleaseDir(dir);
}

static void Pack(string folder, string archive)
{
    if (OperatingSystem.IsWindows())
    {
        ZipFile.CreateFromDirectory(folder, archive);
        return;
    }
    using var p = Process.Start(new ProcessStartInfo("/usr/bin/tar", $"-czf \"{archive}\" -C \"{folder}\" ."))!;
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"tar exited {p.ExitCode} packing {folder}");
}

static string Sha(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
}

// A wheel is a zip with a package and a dist-info folder; pip needs no build tool to install one.
static void WriteProbeWheel(string path, string version)
{
    var info = $"cc_probe-{version}.dist-info";
    var files = new Dictionary<string, string>
    {
        ["cc_probe/__init__.py"] = $"def main():\n    print('probe {version}')\n",
        [$"{info}/METADATA"] = $"Metadata-Version: 2.1\nName: cc-probe\nVersion: {version}\n",
        [$"{info}/WHEEL"] = "Wheel-Version: 1.0\nGenerator: venv-window-proof\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
        [$"{info}/entry_points.txt"] = "[console_scripts]\ncc-devthrottle = cc_probe:main\n",
    };
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    var record = new StringBuilder();
    foreach (var (name, body) in files)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using (var w = zip.CreateEntry(name).Open()) w.Write(bytes);
        var hash = Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        record.Append($"{name},sha256={hash},{bytes.Length}\n");
    }
    record.Append($"{info}/RECORD,,\n");
    using (var w = new StreamWriter(zip.CreateEntry($"{info}/RECORD").Open())) w.Write(record.ToString());
}
