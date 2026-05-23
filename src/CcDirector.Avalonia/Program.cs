using System.Runtime.InteropServices;
using Avalonia;
using CcDirector.ControlApi;

namespace CcDirector.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
