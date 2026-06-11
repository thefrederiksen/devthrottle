using Avalonia;
using CcDirector.Core.Utilities;

namespace CcDirector.Launcher;

public static class Program
{
    // Session-scoped (not Global\) mutex: one launcher per logged-in user session.
    // A second launch on the same port (e.g. autostart racing a manual start) sees the
    // mutex held and exits without trying to bind the port a second time.
    private static string SingleInstanceMutexName => $"CcDirector.Launcher.SingleInstance.{LauncherAppOptions.Port}";

    [STAThread]
    public static int Main(string[] args)
    {
        FileLog.Start();

        LauncherAppOptions.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLog.Write("[Program] CC Launcher already running in this session; exiting second instance.");
            FileLog.Stop();
            return 0;
        }

        FileLog.Write($"[Program] CC Launcher starting (port={LauncherAppOptions.Port}), log: {FileLog.CurrentLogPath}");

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL: {ex}");
            return 1;
        }
        finally
        {
            FileLog.Write("[Program] CC Launcher exited");
            FileLog.Stop();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
