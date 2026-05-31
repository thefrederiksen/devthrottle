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
                FileLog.Start();
                FileLog.Write($"[Program] ApplyUpdate FAILED: {ex}");
                FileLog.Stop();
                return 1;
            }
        }

        // Remove the prior build's leftover ".old" file and prune stale staging
        // directories before normal startup.
        UpdateInstaller.CleanupAfterUpdate();

        // Apply a staged update at startup -- before any session exists, so no
        // running work is ever lost. If one is pending, the relauncher takes over
        // and we exit immediately so it can swap us out.
        if (UpdateInstaller.TryApplyStagedUpdateAtStartup())
            return 0;

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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
