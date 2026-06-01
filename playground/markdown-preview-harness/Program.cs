using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using CcDirector.Avalonia.Controls;
using CcDirector.Avalonia.Helpers;

namespace MarkdownPreviewHarness;

// Hosts the real WebView2Host (overlay-window implementation) and renders
// INSTALLATION.md via the real MarkdownHtmlRenderer. Topmost at a fixed position so a
// real on-screen screen-grab can verify it actually paints. Default composition mode,
// same as the real app.
internal static class Program
{
    private static string _file =
        @"D:\ReposFred\cc-director\docs\install\INSTALLATION.md";

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0) _file = args[0];
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    public static string File => _file;
}

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new HarnessWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public class HarnessWindow : Window
{
    // The REAL viewer: toolbar (Source/Preview/Open) above a WebView2Host in a Grid, with
    // the IsVisible toggle on mode switch. Exercises toolbar offset + full viewer wiring.
    private readonly MarkdownViewerControl _viewer = new();

    public HarnessWindow()
    {
        Title = "Markdown Preview Harness";
        Width = 1000;
        Height = 820;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(80, 80);
        Content = _viewer;

        Opened += async (_, _) =>
        {
            await _viewer.LoadFileAsync(Program.File);

            // Simulate a document-tab switch: detach the viewer then re-attach it. The
            // WebView2 must survive (keep-alive) and re-show its content in the right spot.
            DispatcherTimer.RunOnce(() =>
            {
                Content = null;
                Console.Error.WriteLine("HARNESS: detached");
                DispatcherTimer.RunOnce(() =>
                {
                    Content = _viewer;
                    Console.Error.WriteLine("HARNESS: re-attached");
                }, TimeSpan.FromMilliseconds(700));
            }, TimeSpan.FromMilliseconds(3000));
        };
    }
}
