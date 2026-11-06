using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Core.Machine;

namespace ResourceLoadProof;

/// <summary>
/// Runs a command line and returns what it printed. The harness may start processes - it is not a probe and
/// is not read on a five-second timer - but every one has a deadline, and a non-zero exit is an error that
/// carries the command's own words.
/// </summary>
public static class Shell
{
    /// <summary>Runs <paramref name="commandLine"/> through the platform's shell and returns its standard output.</summary>
    public static string Run(string commandLine, TimeSpan deadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var (file, arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", commandLine })
            : ("/bin/sh", new[] { "-c", commandLine });

        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start: {commandLine}");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(deadline))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Gave up after {deadline.TotalSeconds:F0} s waiting for: {commandLine}");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Exit {process.ExitCode} from: {commandLine}{Environment.NewLine}{error.Result.Trim()}");
        return output.Result;
    }
}

/// <summary>
/// Takes a picture of the whole screen, so the report shows the meter as the owner would have seen it. The
/// command is the platform's own unless <c>--screenshot-command</c> names another (with <c>{path}</c> where
/// the file goes) - an explicit choice, never a second command tried when the first fails.
/// </summary>
public sealed class ScreenCapture(string? commandTemplate)
{
    private readonly string _template = commandTemplate ?? DefaultTemplate();

    /// <summary>Writes a PNG of the screen to <paramref name="path"/>.</summary>
    public void Take(string path)
    {
        Shell.Run(_template.Replace("{path}", path, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"The screenshot command ran but wrote nothing to {path}: {_template}");
        Console.WriteLine($"[shot] {path}");
    }

    private static string DefaultTemplate()
    {
        if (OperatingSystem.IsWindows())
            return "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; " +
                   "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen; " +
                   "$i=New-Object System.Drawing.Bitmap $b.Width,$b.Height; " +
                   "$g=[System.Drawing.Graphics]::FromImage($i); $g.CopyFromScreen($b.Left,$b.Top,0,0,$i.Size); " +
                   "$i.Save('{path}',[System.Drawing.Imaging.ImageFormat]::Png)";
        if (OperatingSystem.IsMacOS())
            return "screencapture -x '{path}'";
        if (OperatingSystem.IsLinux())
            return "gnome-screenshot -f '{path}'";
        throw new PlatformNotSupportedException($"No screenshot command for {RuntimeInformation.OSDescription}; pass --screenshot-command.");
    }
}

/// <summary>
/// The operating system's own figures, to set beside the meter's. On Linux they are scripted - <c>free</c>
/// for memory and <c>vmstat</c> for the processor. Task Manager (Windows) and Activity Monitor (macOS) cannot
/// be scripted, so there the harness PRINTS OURS AND ASKS THE OPERATOR FOR THEIRS, and the report says the
/// figure was typed by a person reading that monitor. Figures can also be passed on the command line for a
/// run with nobody at the keyboard.
/// </summary>
public sealed class OsMonitor(bool interactive)
{
    /// <summary>The name of the monitor the mission compares against on this platform.</summary>
    public static string MonitorName =>
        OperatingSystem.IsWindows() ? "Task Manager" : OperatingSystem.IsMacOS() ? "Activity Monitor" : "free and vmstat";

    /// <summary>The operating system's processor busy figure, in per cent.</summary>
    public OsFigure Cpu(double? ours, double? givenOnCommandLine)
    {
        if (givenOnCommandLine is { } given) return new OsFigure(given, $"{MonitorName}, given on the command line");
        if (OperatingSystem.IsLinux()) return ProofChecks.ParseVmstatBusyPercent(Shell.Run("vmstat 5 2", TimeSpan.FromSeconds(30)));
        return Ask("CPU", ours, $"{MonitorName}'s whole-machine CPU figure");
    }

    /// <summary>The operating system's memory-in-use figure, in per cent.</summary>
    public OsFigure Memory(double? ours, double? givenOnCommandLine)
    {
        if (givenOnCommandLine is { } given) return new OsFigure(given, $"{MonitorName}, given on the command line");
        if (OperatingSystem.IsLinux()) return ProofChecks.ParseFreeMemoryPercent(Shell.Run("free -b", TimeSpan.FromSeconds(30)));
        return Ask("MEM", ours, OperatingSystem.IsWindows()
            ? "Task Manager, Performance, Memory: In use divided by the total, as a per cent"
            : "Activity Monitor, Memory: Memory Used divided by Physical Memory, as a per cent");
    }

    private OsFigure Ask(string label, double? ours, string what)
    {
        if (!interactive)
            throw new InvalidOperationException(
                $"{MonitorName} cannot be read by a script, and this run is non-interactive. Pass --os-{label.ToLowerInvariant()} <percent> read from {what}.");

        Console.WriteLine();
        Console.WriteLine($"[ask] The meter shows {label} {(ours is { } v ? $"{v:F1}%" : "cannot read")}.");
        Console.Write($"[ask] Type {what} now and press Enter: ");
        var line = Console.ReadLine()
            ?? throw new InvalidOperationException("Standard input closed before a figure was typed.");
        if (!double.TryParse(line.Trim().TrimEnd('%'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent < 0 || percent > 100)
            throw new FormatException($"\"{line}\" is not a percentage from 0 to 100.");
        return new OsFigure(percent, $"typed by the operator from {MonitorName}");
    }
}

/// <summary>
/// Holds memory until the machine is short of it - at the meter's own Tight line and never past its Critical
/// line - and frees it on dispose. It reads the machine through Core's real <see cref="MachineMemoryProbe"/>,
/// so it stops at exactly the levels the meter is judged on.
///
/// IT WILL NOT TAKE A MACHINE TO CRITICAL. The target is midway between the two lines, and allocation stops
/// before available memory could fall under <see cref="MemoryPressure.CriticalAbsoluteBytes"/> plus one chunk.
/// A machine that is already Tight before it starts cannot show the warning RAISING, so that is refused.
/// </summary>
public sealed class MemoryLoad : IDisposable
{
    private const int ChunkBytes = 128 * 1024 * 1024;
    private const int PageBytes = 4096;

    private static readonly ulong TargetAvailableBytes =
        (MemoryPressure.TightAbsoluteBytes + MemoryPressure.CriticalAbsoluteBytes) / 2;

    private readonly List<byte[]> _held = new();
    private readonly IMachineMemoryProbe _probe = new MachineMemoryProbe();

    /// <summary>How much this load holds now.</summary>
    public ulong HeldBytes => (ulong)_held.Count * ChunkBytes;

    /// <summary>The level the machine is at, read fresh.</summary>
    public MemoryPressureLevel Level() => MemoryPressure.Level(Read());

    /// <summary>
    /// Allocates and touches memory until available memory is at the target, or tops it back up when other
    /// programs have freed some since. Safe to call repeatedly while holding.
    /// </summary>
    public void TopUp()
    {
        while (true)
        {
            var reading = Read();
            if (reading.AvailableBytes <= TargetAvailableBytes + ChunkBytes) return;

            // The probe caches for a second, so the chunks for one reading are sized from THAT reading and the
            // next batch waits for a fresh one. Allocating against a stale figure would overshoot the target and
            // could take the machine past Critical.
            var chunks = (int)((reading.AvailableBytes - TargetAvailableBytes) / ChunkBytes);
            for (var n = 0; n < chunks; n++)
            {
                var chunk = GC.AllocateUninitializedArray<byte>(ChunkBytes);
                // Touch one byte per page, so the operating system actually hands the pages over rather than
                // promising them; an untouched allocation costs the machine nothing and would prove nothing.
                for (var i = 0; i < chunk.Length; i += PageBytes) chunk[i] = 1;
                _held.Add(chunk);
            }
            Console.WriteLine($"[load] holding {HeldBytes / (1024 * 1024)} MB; machine was {reading}");
            Thread.Sleep(MachineMemoryProbe.CacheFor + TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>Refuses to start on a machine that is already short of memory or cannot be read.</summary>
    public void RequireRoomToRaise()
    {
        var reading = Read();
        var level = MemoryPressure.Level(reading);
        if (level != MemoryPressureLevel.Normal)
            throw new InvalidOperationException(
                $"Memory is {level} before the load starts ({reading}), so the warning could not be shown RAISING. Free some memory and run again.");
    }

    /// <summary>Lets go of everything and asks the runtime to give it back to the operating system.</summary>
    public void Dispose()
    {
        _held.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Console.WriteLine($"[load] memory released; machine now {Read()}");
    }

    private MachineMemoryReading Read()
    {
        var reading = _probe.Read();
        if (!reading.CouldRead)
            throw new InvalidOperationException($"The harness cannot read this machine's memory, so it will not load it blind: {reading.UnreadableReason}");
        return reading;
    }
}
