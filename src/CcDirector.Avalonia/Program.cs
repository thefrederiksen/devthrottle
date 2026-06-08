using System.Runtime.InteropServices;
using Avalonia;
using CcDirector.ControlApi;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Start logging first so the pre-startup update steps below (which run
        // before App initializes its own logging) are actually recorded.
        FileLog.Start();

        // Catch anything that escapes a background thread so a crash is at least
        // recorded to a findable file rather than vanishing silently (issue #242).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            FileLog.Write($"[Program] UNHANDLED ({(e.IsTerminating ? "terminating" : "non-terminating")}): {ex}");
            if (e.IsTerminating && ex is not null) WriteCrashFile("unhandled", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Write($"[Program] UNOBSERVED TASK: {e.Exception}");
            e.SetObserved();
        };

        // Hidden auto-update relauncher mode, handled BEFORE the single-instance
        // guard: a freshly downloaded build is invoked as
        //   cc-director --apply-update <installTarget> <parentPid>
        // to wait for the old process to exit, swap itself into place, and
        // relaunch. It must not acquire the guard or run any normal startup.
        if (args.Length >= 3 && args[0] == "--apply-update")
        {
            try
            {
                return UpdateInstaller.ApplyUpdate(args[1], int.Parse(args[2]));
            }
            catch (Exception ex)
            {
                // A failed apply must NOT be silent (issue #242): show the user why and
                // exit non-zero. The old build's bounded-apply logic will give up after a
                // couple of these and boot the working version with its own notice.
                FileLog.Write($"[Program] ApplyUpdate FAILED: {ex}");
                WriteCrashFile("apply-update", ex);
                MessageBoxW(IntPtr.Zero,
                    $"CC Director could not apply an update:\n\n{ex.Message}\n\n" +
                    "It will continue on the current version.",
                    "CC Director - Update failed", MB_OK | MB_ICONWARNING | MB_TOPMOST);
                FileLog.Stop();
                return 1;
            }
        }

        // Remove the prior build's leftover ".old" file and prune stale staging
        // directories before normal startup.
        UpdateInstaller.CleanupAfterUpdate();

        // Apply a staged update at startup -- before any session exists, so no
        // running work is ever lost. If one is pending, the relauncher takes over
        // and we exit immediately so it can swap us out. If a staged update has
        // failed to apply too many times, we get a notice instead of looping forever
        // (issue #242) and continue booting the current build.
        if (UpdateInstaller.TryApplyStagedUpdateAtStartup(out var updateNotice))
            return 0;
        if (updateNotice is not null)
            MessageBoxW(IntPtr.Zero, updateNotice, "CC Director - Update", MB_OK | MB_ICONWARNING | MB_TOPMOST);

        using var guard = SingleInstanceGuard.TryAcquire();
        if (guard is null)
        {
            var exe = Environment.ProcessPath ?? "(unknown)";
            var msg =
                "CC Director is already running.\n\n" +
                $"Exe: {exe}\n\n" +
                "Only one instance per install location can run at a time. " +
                "Identity, ports, and state files are keyed by the exe path -- " +
                "running a second copy would collide with the existing one.";
            MessageBoxW(IntPtr.Zero, msg, "CC Director", MB_OK | MB_ICONWARNING | MB_TOPMOST);
            return 1;
        }

        // Never let a startup exception exit with no window and no message (issue #242).
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL startup error: {ex}");
            var crashPath = WriteCrashFile("startup", ex);
            MessageBoxW(IntPtr.Zero,
                $"CC Director failed to start:\n\n{ex.Message}\n\n" +
                (crashPath is null ? "" : $"Details written to:\n{crashPath}"),
                "CC Director - Startup error", MB_OK | MB_ICONERROR | MB_TOPMOST);
            return 1;
        }
    }

    /// <summary>
    /// Write a crash report to the director log directory so a startup failure leaves a
    /// findable trail even when the UI never came up. Best-effort; returns the path or null.
    /// </summary>
    private static string? WriteCrashFile(string kind, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "logs", "director");
            Directory.CreateDirectory(dir);
            // No Date.Now in a reproducible context is fine here -- this is a real crash path.
            var path = Path.Combine(dir, $"crash-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(path, $"[{kind}] {DateTime.Now:o}\n\n{ex}\n");
            return path;
        }
        catch
        {
            return null; // never let crash-reporting itself throw
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
