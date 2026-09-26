using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CcDirector.Core.Utilities;

/// <summary>
/// A message box that needs no window of our own - for the moments when the Director or the launcher has to
/// tell a person something and its own user interface is not there: before it started, or after it broke.
///
/// Why this exists (issue #3311, B2): on macOS a Director that failed to start showed NOTHING. The pre-start
/// notice only logged off Windows, and the fatal-startup handler called the Windows-only MessageBoxW with no
/// check, which threw, was swallowed as handled, and left a process with no window and no message.
///
///   - Windows: MessageBoxW, as before.
///   - macOS:   osascript's "display alert", which the system shows for any process, windowed or not. The
///              text is passed as an argument to the script, never spliced into it, so no message can break
///              the script's quoting.
///   - Linux:   zenity, when it is installed (GNOME ships it). When it is not, there is no dialog to show,
///              and this says so in the log and on standard error rather than pretending.
///
/// It blocks until the person dismisses it, like MessageBoxW, and always writes the notice to the log first,
/// so the text is on disk even if no dialog can be shown. It never throws.
/// </summary>
public static class NativeNotice
{
    public enum Kind { Info, Warning, Error }

    /// <summary>Show <paramref name="text"/> under <paramref name="caption"/>. Returns whether a dialog was shown.</summary>
    public static bool Show(string text, string caption, Kind kind)
    {
        FileLog.Write($"[NativeNotice] {caption}: {text.Replace('\n', ' ')}");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var icon = kind switch { Kind.Error => MB_ICONERROR, Kind.Warning => MB_ICONWARNING, _ => MB_ICONINFORMATION };
                MessageBoxW(IntPtr.Zero, text, caption, MB_OK | icon | MB_TOPMOST);
                return true;
            }
            if (OperatingSystem.IsMacOS())
                return Run("osascript", MacArguments(text, caption, kind));
            if (OperatingSystem.IsLinux())
            {
                if (FindOnPath("zenity") is { } zenity)
                    return Run(zenity, LinuxArguments(text, caption, kind));
                FileLog.Write("[NativeNotice] no dialog shown: zenity is not installed, so this notice is only in this log and on standard error");
            }
            Console.Error.WriteLine($"{caption}: {text}");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NativeNotice] Show FAILED ({ex.GetType().Name}): {ex.Message}");
            Console.Error.WriteLine($"{caption}: {text}");
            return false;
        }
    }

    /// <summary>The osascript arguments: a script that reads its text from argv, then the text itself.</summary>
    internal static IReadOnlyList<string> MacArguments(string text, string caption, Kind kind)
    {
        var level = kind switch { Kind.Error => "critical", Kind.Warning => "warning", _ => "informational" };
        return new[]
        {
            "-e", "on run argv",
            "-e", $"display alert (item 1 of argv) message (item 2 of argv) as {level} buttons {{\"OK\"}} default button \"OK\"",
            "-e", "end run",
            caption, text,
        };
    }

    internal static IReadOnlyList<string> LinuxArguments(string text, string caption, Kind kind)
    {
        var type = kind switch { Kind.Error => "--error", Kind.Warning => "--warning", _ => "--info" };
        return new[] { type, "--no-markup", "--title", caption, "--text", text };
    }

    private static bool Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{program} did not start");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0) return true;
        FileLog.Write($"[NativeNotice] {program} exited {process.ExitCode}: {stderr.Trim()}");
        return false;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
