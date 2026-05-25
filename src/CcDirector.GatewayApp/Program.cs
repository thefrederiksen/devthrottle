using Avalonia;
using CcDirector.Core.Utilities;

namespace CcDirector.GatewayApp;

public static class Program
{
    // Session-scoped (not Global\) mutex: one gateway per logged-in user session,
    // which is exactly the scope the gateway needs since it serves that session's
    // Directors. A second launch (e.g. autostart racing a manual start) sees the
    // mutex held and exits without trying to bind the port a second time.
    private const string SingleInstanceMutexName = "CcDirector.GatewayApp.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        FileLog.Start();
        GatewayAppOptions.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLog.Write("[Program] Gateway tray app already running in this session; exiting second instance.");
            FileLog.Stop();
            return 0;
        }

        FileLog.Write($"[Program] CC Director Gateway tray app starting (port={GatewayAppOptions.Port}), log: {FileLog.CurrentLogPath}");

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
            FileLog.Write("[Program] Tray app exited");
            FileLog.Stop();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
