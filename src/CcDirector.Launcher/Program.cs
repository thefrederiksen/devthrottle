using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

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

        // Detached self-update helper mode: this process is a STAGED copy of the new Launcher exe.
        // It asks the running tray app to exit (POST /shutdown), swaps itself into the installed
        // location, relaunches, and verifies the new build is healthy - rolling back to the .old
        // build (and pinning the bad version) if not. NEVER the normal startup path: it exits when
        // done. Launched by LauncherUpdater.LaunchDetachedUpdater.
        if (Array.IndexOf(args, "--apply-update") >= 0)
            return ApplyUpdate(args);

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

    private static int ApplyUpdate(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var target = Arg("--target");
        var version = Arg("--new-version");
        var port = int.TryParse(Arg("--port"), out var p) ? p : LauncherAppOptions.DefaultPort;
        // Relaunch arguments: the installed Launcher always relaunches managed; the self-update
        // test harness overrides this to keep its throwaway instance off the real install.
        var relaunchArgs = Arg("--args");
        if (relaunchArgs.Length == 0) relaunchArgs = LauncherTrayInstaller.InstalledArguments;
        var stagedSelf = Environment.ProcessPath ?? "";
        FileLog.Write($"[Program] --apply-update: version={version}, target={target}, port={port}, args={relaunchArgs}, staged={stagedSelf}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var result = new LauncherSelfUpdate().ApplyAsync(
            target, stagedSelf, version,
            stopLauncher: () =>
            {
                // Best-effort graceful exit; LauncherSelfUpdate's exe-writability wait is the real
                // exit barrier (a single-file exe unlocks only when its process has fully exited,
                // which also releases the single-instance mutex for the relaunch).
                try
                {
                    using var resp = http.PostAsync($"http://127.0.0.1:{port}/shutdown", content: null)
                        .GetAwaiter().GetResult();
                    FileLog.Write($"[Program] /shutdown -> {(int)resp.StatusCode}");
                    return resp.IsSuccessStatusCode;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] /shutdown unreachable ({ex.Message}); launcher presumably not running");
                    return false;
                }
            },
            startLauncher: () =>
            {
                try
                {
                    // UseShellExecute=true so the relaunched Launcher does NOT inherit this helper's
                    // stdio handles - an inherited stdout pipe keeps the caller's pipe open for the
                    // Launcher's whole lifetime (observed as a hang in any script that pipes output).
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        Arguments = relaunchArgs,
                        WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
                        UseShellExecute = true,
                    };
                    using var proc = Process.Start(psi);
                    FileLog.Write($"[Program] relaunched Launcher pid={proc?.Id}");
                    return proc is not null;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[Program] relaunch FAILED: {ex.Message}");
                    return false;
                }
            },
            isHealthy: async ct =>
            {
                try { return (await http.GetAsync($"http://127.0.0.1:{port}/healthz", ct)).IsSuccessStatusCode; }
                catch { return false; }
            },
            healthTimeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

        FileLog.Write($"[Program] self-update outcome={result.Outcome}: {result.Message}");
        foreach (var step in result.Steps) FileLog.Write($"[Program]   {step}");
        FileLog.Stop();
        return result.Outcome == SelfUpdateOutcome.Updated ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
