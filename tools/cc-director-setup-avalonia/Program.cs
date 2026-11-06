using Avalonia;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // On macOS the install is per-user: run as root it fills the user's own Library with root-owned
        // files, and launchd then refuses to start the launcher (#3411). Refused before anything is written.
        if (OperatingSystem.IsMacOS() && Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine($"Error: {CcDirector.Setup.Engine.MacFileOwnership.RefuseRootMessage}");
            Environment.Exit(1);
        }

        // Before the setup log is opened: it lives in the install folder, and a root-owned install folder
        // fails that first write - and every placement after it - long before the launcher step (#3411).
        if (OperatingSystem.IsMacOS())
        {
            var ownership = CcDirector.Setup.Engine.MacFileOwnership.EnsureOwnedByUser(
                CcDirector.Setup.Engine.MacFileOwnership.DefaultRunner,
                CcDirector.Setup.Engine.MacFileOwnership.InstallTargets(
                    CcDirector.Setup.Engine.InstallLayout.Default(),
                    CcDirector.Setup.Engine.LauncherLaunchdAutostart.PlistPath),
                offerPrompt: true, _ => { });
            if (ownership is not null)
            {
                Console.Error.WriteLine($"Error: {ownership}");
                CcDirector.Setup.Engine.MacFileOwnership.ShowAlert("DevThrottle Setup cannot continue", ownership);
                Environment.Exit(1);
            }
        }

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

        // The wizard is double-clicked in Finder, so launchd hands it a PATH of /usr/bin:/bin:/usr/sbin:/sbin
        // and nothing else. Its own .app bundle used to carry a #!/bin/bash wrapper that fixed that before
        // the process started; the wrapper is gone because it made the bundle's code identity Apple's bash
        // (issue #4518), so the same work is done here instead.
        //
        // IT IS NOT COSMETIC. AgentPresence probes PATH (plus ~/.npm-global/bin and ~/.local/bin) to decide
        // whether a coding agent is installed, so without this a user whose claude lives in ~/.claude/local
        // or in Homebrew is told on the Complete screen that no coding agent is set up - the exact lie that
        // probe was widened to stop telling.
        CcDirector.Core.Tools.ToolShimPath.EnsureGuiLaunchPathDefaults(SetupLog.Write);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Logged here and REPORTED once, by the UnhandledException handler above, when the rethrow leaves
            // Main. Reporting here too sent every start failure twice and blocked twice as long.
            SetupLog.Write($"[Program] FATAL: the wizard could not run: {ex}");
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
