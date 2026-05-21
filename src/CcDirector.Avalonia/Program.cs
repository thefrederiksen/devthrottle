using Avalonia;
using Avalonia.WebView.Desktop;

namespace CcDirector.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseDesktopWebView()
            .LogToTrace();
}
