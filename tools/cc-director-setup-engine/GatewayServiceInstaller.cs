using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of a Gateway service install, with the steps taken (for logs / UI).</summary>
public sealed record GatewayInstallResult(bool Success, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// Performs the Gateway-role first install that the generic <see cref="UpdateRunner"/> cannot:
///   1. extract the Cockpit .zip (the runner skips archive assets) into %ProgramFiles%\CC Director\cockpit,
///   2. register cc-gateway-service as an auto-start LocalSystem Windows service (sc.exe, no NSSM),
///   3. write the per-service environment the Gateway + Cockpit supervisor need,
///   4. start the service and wait for the Gateway (7878) and supervised Cockpit (7470) to answer.
///
/// The Gateway exe itself is already placed by the UpdateRunner at the Gateway component path before
/// this runs. This step must run elevated; the caller (CLI) verifies that and that OPENAI_API_KEY is
/// present before invoking. Windows-only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GatewayServiceInstaller
{
    private readonly InstallLayout _layout;
    private readonly HttpClient _http;

    public GatewayServiceInstaller(InstallLayout layout, HttpClient? http = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>True when the current process is running with Administrator rights.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Install + start the Gateway service from an already-resolved release. The Gateway exe must
    /// already be placed (by the UpdateRunner) at <see cref="InstallLayout.PathFor"/> for the Gateway
    /// component. <paramref name="openAiKey"/> is injected into the service environment.
    /// </summary>
    public async Task<GatewayInstallResult> InstallAsync(
        ResolvedRelease release, ReleaseSource source, string openAiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(openAiKey))
            throw new ArgumentException("OPENAI_API_KEY must be provided for a Gateway install.", nameof(openAiKey));

        var steps = new List<string>();
        EngineLog.Write("[GatewayServiceInstaller] InstallAsync begin");

        var gatewayExe = _layout.PathFor(ComponentRegistry.Gateway);
        if (!File.Exists(gatewayExe))
            return Fail(steps, $"Gateway exe not present at {gatewayExe}; the file swap must run first.");

        // 1. Detect an existing cc-gateway-service so the swap is explicit, never silent. An older
        // install registered the service through NSSM (binPath = ...\nssm.exe); the stop/delete below
        // removes it and Create stands up the native Windows service in its place.
        var (qcExit, qcOut) = Run(GatewayServiceCommands.QueryConfig());
        var serviceExisted = qcExit == 0;
        if (serviceExisted)
        {
            var existingBin = GatewayServiceCommands.ParseBinaryPath(qcOut);
            if (GatewayServiceCommands.IsNssmBinary(existingBin))
            {
                steps.Add($"migrating NSSM-wrapped service -> native Windows service (was: {existingBin})");
                EngineLog.Write($"[GatewayServiceInstaller] migrating NSSM -> native; existing binPath={existingBin}");
            }
            else
            {
                steps.Add($"replacing existing cc-gateway-service (was: {existingBin ?? "unknown binPath"})");
                EngineLog.Write($"[GatewayServiceInstaller] replacing existing service; binPath={existingBin}");
            }
        }
        else
        {
            steps.Add("fresh install (no existing cc-gateway-service)");
            EngineLog.Write("[GatewayServiceInstaller] no existing cc-gateway-service; fresh install");
        }

        // 2. Stop the existing service and kill the Cockpit child it supervises BEFORE extracting -
        // otherwise the running Cockpit locks cc-director-cockpit.dll and the extract is denied. Only
        // needed when a service is already present (a fresh install has nothing running).
        if (serviceExisted)
        {
            var (stopExit, _) = Run(GatewayServiceCommands.Stop());
            steps.Add($"sc stop cc-gateway-service -> exit {stopExit}");
            KillCockpitProcesses(steps);
            WaitBriefly();
        }

        // 3. Extract the Cockpit zip (the runner skips archive assets). The service supervises this exe.
        // If extraction fails after we stopped a prior service, restart it so the machine is not left down.
        string cockpitExe;
        try
        {
            cockpitExe = await CockpitPackage.ExtractAsync(_layout, release, source, ct);
            steps.Add($"extracted {CockpitPackage.AssetName} -> {_layout.CockpitDir}");
        }
        catch (Exception ex)
        {
            if (serviceExisted)
            {
                var (restartExit, _) = Run(GatewayServiceCommands.Start());
                steps.Add($"restarted previous service after extraction failure -> exit {restartExit}");
            }
            return Fail(steps, $"Cockpit extraction failed: {ex.Message}");
        }

        // 4. Register + configure the service (idempotent: stop/delete any prior one first).
        Directory.CreateDirectory(_layout.ServiceLogsDir);
        var port = GatewayHostDefaultPort;
        var commands = new[]
        {
            GatewayServiceCommands.Stop(),
            GatewayServiceCommands.Delete(),
            GatewayServiceCommands.Create(gatewayExe, port),
            GatewayServiceCommands.Describe(),
            GatewayServiceCommands.SetEnvironment(_layout.LocalRoot, openAiKey, cockpitExe),
        };

        // The SetEnvironment command embeds OPENAI_API_KEY; NEVER let it reach a log/step/UI.
        // Redact the literal key value out of every string we record.
        string Redact(string s) =>
            string.IsNullOrEmpty(openAiKey) ? s : s.Replace(openAiKey, "***REDACTED***");

        // Stop/Delete are allowed to fail (no prior service); Create/Describe/SetEnvironment must succeed.
        for (int i = 0; i < commands.Length; i++)
        {
            var cmd = commands[i];
            var (exit, output) = Run(cmd);
            var lenient = cmd.Arguments.StartsWith("stop ", StringComparison.Ordinal)
                       || cmd.Arguments.StartsWith("delete ", StringComparison.Ordinal);
            var safeDisplay = Redact(cmd.Display);
            steps.Add($"{safeDisplay} -> exit {exit}");
            EngineLog.Write($"[GatewayServiceInstaller] {safeDisplay} -> exit {exit}; {Redact(Trim(output))}");
            if (exit != 0 && !lenient)
                return Fail(steps, $"Command failed ({exit}): {safeDisplay}. {Redact(Trim(output))}");
            if (lenient) WaitBriefly();
        }

        // 5. Start and wait for health.
        var start = GatewayServiceCommands.Start();
        var (startExit, startOut) = Run(start);
        steps.Add($"{start.Display} -> exit {startExit}");
        if (startExit != 0)
            return Fail(steps, $"Service failed to start ({startExit}): {Trim(startOut)}");

        var gatewayUp = await WaitForHttpAsync($"http://127.0.0.1:{port}/healthz", TimeSpan.FromSeconds(20), ct);
        steps.Add($"gateway healthz on {port}: {(gatewayUp ? "OK" : "no response")}");
        if (!gatewayUp)
            return Fail(steps, $"Gateway service started but did not answer on {port}. Check {_layout.ServiceLogsDir}.");

        var cockpitUp = await WaitForHttpAsync("http://127.0.0.1:7470/", TimeSpan.FromSeconds(30), ct);
        steps.Add($"cockpit on 7470: {(cockpitUp ? "OK" : "no response")}");
        if (!cockpitUp)
            return Fail(steps, $"Gateway is up but the supervised Cockpit did not answer on 7470. Check {_layout.ServiceLogsDir}.");

        EngineLog.Write("[GatewayServiceInstaller] InstallAsync success");
        return new GatewayInstallResult(true, $"Gateway service installed and running; Cockpit live at {TailnetResolver.Url(7470)}.", steps);
    }

    // The Gateway's default port; kept here so the engine has no compile dependency on the Gateway exe.
    private const int GatewayHostDefaultPort = 7878;

    private static (int exit, string output) Run(ServiceCommand cmd) => ProcessRunner.Run(cmd);

    private static void WaitBriefly() => Thread.Sleep(1500);

    /// <summary>Kill any running Cockpit child so its files unlock before the zip is extracted.</summary>
    private static void KillCockpitProcesses(List<string> steps)
    {
        try
        {
            var procs = Process.GetProcessesByName("cc-director-cockpit");
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                catch { /* already gone */ }
                finally { p.Dispose(); }
            }
            if (procs.Length > 0) steps.Add($"stopped {procs.Length} running Cockpit process(es) before extract");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[GatewayServiceInstaller] kill Cockpit failed: {ex.Message}");
        }
    }

    private async Task<bool> WaitForHttpAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // not up yet
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;

    private static GatewayInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[GatewayServiceInstaller] FAILED: {message}");
        return new GatewayInstallResult(false, message, steps);
    }
}
