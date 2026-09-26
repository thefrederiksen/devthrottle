using Avalonia;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A local directory acting as a full release (release-manifest.json + asset files), the
        // same override the setup command line offers. Lets the wizard run a complete install with
        // no network - hermetic testing of a not-yet-published release on real hardware. The
        // environment variable form exists because a Finder-launched .app receives no arguments.
        var releaseDir = ParseOption(args, "--release-dir")
            ?? Environment.GetEnvironmentVariable("DEVTHROTTLE_RELEASE_DIR");
        if (!string.IsNullOrWhiteSpace(releaseDir))
            EngineInstallRunner.ReleaseDirectoryOverride = releaseDir;

        // A crash of the wizard itself used to leave nothing: no log line and no report (#3311). These
        // log it and report it before the process goes.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            SetupLog.Write($"[Program] UNHANDLED exception (terminating={e.IsTerminating}): {ex}");
            WizardErrorReport.SendAndWait("wizard", "crash", $"The setup wizard crashed: {ex?.GetType().Name}: {ex?.Message}", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            SetupLog.Write($"[Program] UNOBSERVED task exception: {e.Exception}");
            WizardErrorReport.SendAndWait("wizard", "unobserved-task", $"A background task in the setup wizard failed: {e.Exception.GetBaseException().Message}", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[Program] FATAL: the wizard could not run: {ex}");
            WizardErrorReport.SendAndWait("wizard", "start", $"The setup wizard could not start: {ex.GetType().Name}: {ex.Message}", ex);
            throw;
        }
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
