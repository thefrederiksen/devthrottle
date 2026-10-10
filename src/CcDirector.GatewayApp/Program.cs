using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using Avalonia;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Util;
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

        // This app's own errors reach DevThrottle (issue #3643), as they do for the Director and the launcher:
        // every FAILED line it logs - its fatal start, the controller, a self-update, an asset apply, and the
        // embedded Gateway's own handled errors - and anything that escapes a thread or a task. Before this they
        // stayed in the log on this machine. The connection is the HOSTED Gateway's or none: this machine's own
        // connection points at this very Gateway, whose store we cannot read. With none, the errors go through
        // the before-sign-in outbox to the hosted Gateway's public install-report route.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            FileLog.Write($"[Program] UNHANDLED ({(e.IsTerminating ? "terminating" : "non-terminating")}): {e.ExceptionObject}");
            if (e.IsTerminating) ErrorReporter.FlushBeforeExit(TimeSpan.FromSeconds(3));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Write($"[Program] UNOBSERVED TASK: {e.Exception}");
            e.SetObserved();
        };
        ErrorReporter.Start(ErrorReportLimits.GatewayApp, ErrorReporter.HostedConnectionOnly(GatewayConfig.Load, HostedGateway.ResolveUrl));

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
            ErrorReporter.FlushBeforeExit(TimeSpan.FromSeconds(3));
            return 1;
        }
        finally
        {
            FileLog.Write("[Program] Tray app exited");
            FileLog.Stop();
        }
    }

    /// <summary>
    /// Read the running Gateway's bearer token so the self-update helper can authenticate its
    /// POST /shutdown (issue #1609). Deliberately READ-ONLY: GatewayAuth.LoadOrCreate would MINT a token
    /// when the file is absent, and a freshly minted token is precisely the one the already-running
    /// Gateway does not know - it would 401 exactly like sending none, only more confusingly. Returns
    /// null when there is no token to read, so the caller can say so instead of blaming the exe lock.
    /// </summary>
    private static string? TryReadGatewayToken()
    {
        try
        {
            var path = GatewayAuth.DefaultTokenFile;
            if (!File.Exists(path))
            {
                FileLog.Write($"[Program] gateway token file not found at {path}");
                return null;
            }
            var token = File.ReadAllText(path).Trim();
            if (token.Length == 0)
            {
                FileLog.Write($"[Program] gateway token file is empty at {path}");
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] could not read the gateway token: {ex.Message}");
            return null;
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
                // Issue #1609: /shutdown is behind the Gateway's token gate, so this MUST authenticate.
                // Sent token-less, it was answered 401, the Gateway never exited, its exe never unlocked,
                // and every self-update aborted with the misleading "exe still locked after stop" - which
                // is why no managed install has updated since the gate closed. The exe-writability wait
                // below is only a barrier if the process actually exits; it cannot substitute for it.
                //
                // Reading the token is legitimate here: this helper is the Gateway's own exe, running as
                // the same user on the same machine, and the file it reads is the very one the running
                // Gateway loaded its token from (GatewayAuth.DefaultTokenFile).
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
                var token = TryReadGatewayToken();
                if (token is null)
                {
                    // Fail loudly rather than post a request we know will 401 and then blame the exe lock.
                    FileLog.Write("[Program] /shutdown SKIPPED: no gateway token readable; cannot ask the Gateway to exit, so the swap will abort");
                    return false;
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                try
                {
                    using var resp = http.SendAsync(request).GetAwaiter().GetResult();
                    FileLog.Write($"[Program] /shutdown -> {(int)resp.StatusCode}");
                    if (!resp.IsSuccessStatusCode)
                        FileLog.Write($"[Program] /shutdown REFUSED ({(int)resp.StatusCode}); the Gateway will not exit and the swap will abort on a locked exe");
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

        FileLog.Write(result.Outcome == SelfUpdateOutcome.Updated
            ? $"[Program] self-update outcome={result.Outcome}: {result.Message}"
            : $"[Program] self-update FAILED (outcome={result.Outcome}): {result.Message}");
        foreach (var step in result.Steps) FileLog.Write($"[Program]   {step}");

        // Issue #809: lay the matching mobile app down beside the freshly swapped exe so /mobile keeps
        // serving after a self-update (the single-file exe carries no loose content). The running
        // Gateway staged + SHA-verified the zip before launching this helper, so we just extract it.
        // ONLY after a successful exe update - a rollback leaves the prior wwwroot/mobile in place, matching
        // the rolled-back exe. Boundary try/catch: the prior mobile build still serves /mobile if this step
        // fails, so a failure is logged loudly (and the next self-update retries), never silently
        // hidden, and it does not undo an already-successful exe update.
        if (result.Outcome == SelfUpdateOutcome.Updated)
        {
            try
            {
                var layout = InstallLayout.Default();
                var stagedMobileZip = new GatewayUpdater(layout).StagedMobileZipPath;
                var appliedDir = MobilePackage.ExtractStagedZip(layout, stagedMobileZip);
                FileLog.Write(appliedDir is null
                    ? "[Program] no staged mobile zip to apply (release without the mobile app)"
                    : $"[Program] applied mobile app -> {appliedDir}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] mobile app apply FAILED (prior /mobile build still served): {ex.Message}");
            }

            // Issue #979: lay the matching React Cockpit down beside the freshly swapped exe so the
            // Cockpit keeps serving at the site root after a self-update (the single-file exe carries no
            // loose content). Same contract as the mobile app above: the running Gateway staged +
            // SHA-verified the zip before launching this helper. Boundary try/catch so a failure leaves
            // the prior Cockpit build serving and never undoes an already-successful exe update.
            try
            {
                var layout = InstallLayout.Default();
                var stagedCockpitZip = new GatewayUpdater(layout).StagedCockpitZipPath;
                var appliedDir = CockpitAssetPackage.ExtractStagedZip(layout, stagedCockpitZip);
                FileLog.Write(appliedDir is null
                    ? "[Program] no staged Cockpit zip to apply (release without the Cockpit)"
                    : $"[Program] applied Cockpit -> {appliedDir}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] Cockpit apply FAILED (prior Cockpit build still served): {ex.Message}");
            }

            // Issue #1186: lay the matching bundled ffmpeg down beside the freshly swapped exe so the
            // long-clip WebM/Opus -> PCM WAV transcode (issue #1139) keeps working after a self-update
            // (the single-file exe carries no loose content). Same contract as the mobile app / Cockpit
            // above: the running Gateway staged + SHA-verified the zip before launching this helper.
            // Boundary try/catch so a failure leaves the prior ffmpeg.exe in place and never undoes an
            // already-successful exe update.
            try
            {
                var layout = InstallLayout.Default();
                var stagedFfmpegZip = new GatewayUpdater(layout).StagedFfmpegZipPath;
                var appliedExe = FfmpegPackage.ExtractStagedZip(layout, stagedFfmpegZip);
                FileLog.Write(appliedExe is null
                    ? "[Program] no staged ffmpeg zip to apply (release without ffmpeg)"
                    : $"[Program] applied ffmpeg -> {appliedExe}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] ffmpeg apply FAILED (prior ffmpeg.exe still in place): {ex.Message}");
            }
        }

        // This helper exits as soon as it is done: what it logged as failed goes out first.
        ErrorReporter.FlushBeforeExit(TimeSpan.FromSeconds(5));
        FileLog.Stop();
        return result.Outcome == SelfUpdateOutcome.Updated ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
