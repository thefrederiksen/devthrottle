namespace CcDirector.Setup.Engine;

/// <summary>A single external command to run, as exe + a raw argument line.</summary>
/// <remarks>
/// The argument line is a verbatim command-line string (not a token list) because
/// sc.exe's "key= value" syntax and the binPath quoting are sensitive to exact
/// spacing/quoting; building the line ourselves keeps it controllable and testable.
/// </remarks>
public sealed record ServiceCommand(string Exe, string Arguments)
{
    /// <summary>A display string (for logs / dry-run).</summary>
    public string Display => $"{Exe} {Arguments}";
}

/// <summary>
/// Builds the command sequence that registers, configures, and controls the
/// <c>cc-gateway-service</c> Windows service using only built-in Windows tools
/// (sc.exe + reg.exe) - no NSSM (decision D1). The Gateway runs as LocalSystem so
/// it can write %ProgramFiles%\CC Director and stop/start its own service with no
/// further UAC after the first elevated install.
///
/// These builders are pure so the exact commands are unit-testable; execution is
/// done by <see cref="GatewayServiceInstaller"/>.
///
/// The running service exe is file-locked, so a swap cannot happen while the
/// service is up. The ordered flow is therefore: stop -> swap files -> start.
/// </summary>
public static class GatewayServiceCommands
{
    public const string ServiceName = "cc-gateway-service";
    public const string DisplayName = "CC Gateway Service";
    public const string Description = "CC Director Gateway (always-on, headless) + Cockpit supervisor.";

    /// <summary>sc.exe stop &lt;svc&gt; (used before swapping the Gateway exe).</summary>
    public static ServiceCommand Stop(string serviceName = ServiceName) =>
        new("sc.exe", $"stop {serviceName}");

    /// <summary>sc.exe start &lt;svc&gt; (used after swapping the Gateway exe / on first install).</summary>
    public static ServiceCommand Start(string serviceName = ServiceName) =>
        new("sc.exe", $"start {serviceName}");

    /// <summary>sc.exe query &lt;svc&gt; (used to confirm the service state).</summary>
    public static ServiceCommand Query(string serviceName = ServiceName) =>
        new("sc.exe", $"query {serviceName}");

    /// <summary>sc.exe qc &lt;svc&gt; (queries the service config, incl. BINARY_PATH_NAME).</summary>
    public static ServiceCommand QueryConfig(string serviceName = ServiceName) =>
        new("sc.exe", $"qc {serviceName}");

    /// <summary>
    /// Extract BINARY_PATH_NAME from `sc qc` output, or null if absent. Pure so the
    /// NSSM-vs-native detection is unit-testable without a real service.
    /// </summary>
    public static string? ParseBinaryPath(string scQcOutput)
    {
        if (string.IsNullOrEmpty(scQcOutput)) return null;
        foreach (var line in scQcOutput.Split('\n'))
        {
            var idx = line.IndexOf("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var colon = line.IndexOf(':', idx);            // first ':' after the label; the path's "C:" comes later
            if (colon < 0) continue;
            var value = line[(colon + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>True when the service's binary is NSSM (a wrapped service we are migrating off of).</summary>
    public static bool IsNssmBinary(string? binaryPath) =>
        binaryPath is not null && binaryPath.Contains("nssm", StringComparison.OrdinalIgnoreCase);

    /// <summary>sc.exe delete &lt;svc&gt; (used to make first install idempotent).</summary>
    public static ServiceCommand Delete(string serviceName = ServiceName) =>
        new("sc.exe", $"delete {serviceName}");

    /// <summary>
    /// sc.exe create as an auto-start LocalSystem service. The binPath wraps the
    /// (space-containing) exe path in inner quotes and appends "--port N", e.g.
    /// <c>binPath= "\"C:\Program Files\CC Director\gateway\cc-director-gateway.exe\" --port 7878"</c>.
    /// </summary>
    public static ServiceCommand Create(string gatewayExePath, int port, string serviceName = ServiceName) =>
        new("sc.exe",
            $"create {serviceName} binPath= \"\\\"{gatewayExePath}\\\" --port {port}\" " +
            $"start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");

    /// <summary>sc.exe description &lt;svc&gt; "...".</summary>
    public static ServiceCommand Describe(string serviceName = ServiceName) =>
        new("sc.exe", $"description {serviceName} \"{Description}\"");

    /// <summary>
    /// Writes the per-service Environment (a REG_MULTI_SZ under the service key) so the
    /// LocalSystem service process gets the vars the Gateway + its Cockpit supervisor
    /// need: CC_DIRECTOR_ROOT (the primary user's per-user root, since LocalSystem's
    /// %LOCALAPPDATA% is the system profile), OPENAI_API_KEY, CC_COCKPIT_MANAGED=1, and
    /// CC_COCKPIT_EXE. reg.exe takes a literal "\0" as the multi-string separator.
    /// </summary>
    public static ServiceCommand SetEnvironment(
        string directorRoot, string openAiKey, string cockpitExePath, string serviceName = ServiceName)
    {
        var data =
            $"CC_DIRECTOR_ROOT={directorRoot}\\0" +
            $"OPENAI_API_KEY={openAiKey}\\0" +
            $"CC_COCKPIT_MANAGED=1\\0" +
            $"CC_COCKPIT_EXE={cockpitExePath}";
        var key = $"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}";
        return new("reg.exe", $"add \"{key}\" /v Environment /t REG_MULTI_SZ /d \"{data}\" /f");
    }
}
