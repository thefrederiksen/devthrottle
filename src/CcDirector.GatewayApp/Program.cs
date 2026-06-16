using System.Diagnostics;
using System.Net.Http;
using Avalonia;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.GatewayApp;

public static class Program
{
    // Session-scoped (not Global\) mutex: one gateway PER PORT per logged-in user session.
    // A second launch on the same port (e.g. autostart racing a manual start) sees the mutex
    // held and exits without trying to bind the port a second time, while an alternate-port
    // instance (self-update test harness, CC_GATEWAY_NO_TAILSCALE dev runs) is legitimate.
    private static string SingleInstanceMutexName => $"CcDirector.GatewayApp.SingleInstance.{GatewayAppOptions.Port}";

    [STAThread]
    public static int Main(string[] args)
    {
        FileLog.Start();

        // Detached self-update helper mode: this process is a STAGED copy of the new Gateway exe.
        // It asks the running tray app to exit (POST /shutdown), swaps itself into the installed
        // location, relaunches, and verifies the new build is healthy - rolling back to the .old
        // build (and pinning the bad version) if not. NEVER the normal startup path: it exits when
        // done. Launched by GatewayUpdater.LaunchDetachedUpdater.
        if (Array.IndexOf(args, "--apply-update") >= 0)
            return ApplyUpdate(args);

        GatewayAppOptions.Parse(args);

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            FileLog.Write("[Program] Gateway tray app already running in this session; exiting second instance.");
            FileLog.Stop();
            return 0;
        }

        FileLog.Write($"[Program] DevThrottle Gateway tray app starting (port={GatewayAppOptions.Port}, managed={GatewayAppOptions.Managed}), log: {FileLog.CurrentLogPath}");

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

    private static int ApplyUpdate(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var target = Arg("--target");
        var version = Arg("--new-version");
        var port = int.TryParse(Arg("--port"), out var p) ? p : CcDirector.Gateway.GatewayHost.DefaultPort;
        // Relaunch arguments: the installed Gateway always relaunches managed; the self-update
        // test harness overrides this to keep its throwaway instance off the live Cockpit.
        var relaunchArgs = Arg("--args");
        if (relaunchArgs.Length == 0) relaunchArgs = GatewayTrayInstaller.InstalledArguments;
        var stagedSelf = Environment.ProcessPath ?? "";
        FileLog.Write($"[Program] --apply-update: version={version}, target={target}, port={port}, args={relaunchArgs}, staged={stagedSelf}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var result = new GatewaySelfUpdate().ApplyAsync(
            target, stagedSelf, version,
            stopGateway: () =>
            {
                // Best-effort graceful exit; GatewaySelfUpdate's exe-writability wait is the real
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
                    FileLog.Write($"[Program] /shutdown unreachable ({ex.Message}); gateway presumably not running");
                    return false;
                }
            },
            startGateway: () =>
            {
                try
                {
                    // UseShellExecute=true so the relaunched Gateway does NOT inherit this
                    // helper's stdio handles - an inherited stdout pipe keeps the caller's
                    // pipe open for the Gateway's whole lifetime (observed as a hang in any
                    // script that pipes the helper's output).
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        Arguments = relaunchArgs,
                        WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
                        UseShellExecute = true,
                    };
                    using var proc = Process.Start(psi);
                    FileLog.Write($"[Program] relaunched Gateway pid={proc?.Id}");
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
