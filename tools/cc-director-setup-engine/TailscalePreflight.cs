using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>One Tailscale preflight check: what was checked, whether it passed, what was
/// observed, and - on failure - the exact remediation step (issue #197 WS6).</summary>
public sealed record TailscaleCheckResult(string Check, bool Ok, string Detail, string? Remedy);

/// <summary>
/// Detection-only Tailscale preflight for installs (issue #197). A Director is reachable
/// from the Gateway/Cockpit ONLY through a tailscale serve mapping on its own machine, so
/// the deployment story is: install Tailscale, install cc-director, set gateway.url - done.
/// This preflight tells the user up front which of those legs is missing and exactly how
/// to fix it. It never installs anything and never blocks: a local-only Director (no
/// Tailscale) is a legitimate state; the Director itself refuses to advertise an endpoint
/// that does not answer (verify-before-advertise).
///
/// Checks, in dependency order (later checks are skipped once one fails):
///   1. CLI installed   - tailscale.exe at the standard install path.
///   2. Daemon running  - `tailscale status --json` answers with BackendState=Running
///                        (NeedsLogin / Stopped get their own remedies).
///   3. MagicDNS name   - Self.DNSName present, which is what the Director advertises.
/// </summary>
public static class TailscalePreflight
{
    private const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

    public static IReadOnlyList<TailscaleCheckResult> Run()
    {
        var results = new List<TailscaleCheckResult>();

        if (!OperatingSystem.IsWindows())
        {
            results.Add(new TailscaleCheckResult("CLI installed", false,
                "preflight is Windows-only in this build", null));
            return results;
        }

        // 1. CLI installed
        if (!File.Exists(TailscaleExe))
        {
            results.Add(new TailscaleCheckResult("CLI installed", false,
                $"not found at {TailscaleExe}",
                "Install Tailscale: winget install tailscale.Tailscale (then log in from the tray icon)"));
            return results;
        }
        results.Add(new TailscaleCheckResult("CLI installed", true, TailscaleExe, null));

        // 2. Daemon running + logged in
        string output;
        try
        {
            var (exit, stdout) = ProcessRunner.Run(TailscaleExe, "status --json");
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                results.Add(new TailscaleCheckResult("Daemon running", false,
                    $"tailscale status exited {exit}",
                    "Start Tailscale (launch it from the Start Menu, or: net start Tailscale)"));
                return results;
            }
            output = stdout;
        }
        catch (Exception ex)
        {
            results.Add(new TailscaleCheckResult("Daemon running", false,
                $"tailscale status failed: {ex.Message}",
                "Start Tailscale (launch it from the Start Menu, or: net start Tailscale)"));
            return results;
        }

        string? backendState = null;
        string? dnsName = null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("BackendState", out var bs) && bs.ValueKind == JsonValueKind.String)
                backendState = bs.GetString();
            if (doc.RootElement.TryGetProperty("Self", out var self)
                && self.TryGetProperty("DNSName", out var dns) && dns.ValueKind == JsonValueKind.String)
                dnsName = dns.GetString()?.TrimEnd('.');
        }
        catch (JsonException ex)
        {
            results.Add(new TailscaleCheckResult("Daemon running", false,
                $"tailscale status output unparsable: {ex.Message}", null));
            return results;
        }

        if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            var remedy = string.Equals(backendState, "NeedsLogin", StringComparison.OrdinalIgnoreCase)
                ? "Log into the tailnet: tailscale up (or use the tray icon)"
                : "Start Tailscale and connect (tray icon -> Connect, or: tailscale up)";
            results.Add(new TailscaleCheckResult("Daemon running", false,
                $"BackendState={backendState ?? "unknown"}", remedy));
            return results;
        }
        results.Add(new TailscaleCheckResult("Daemon running", true, "BackendState=Running", null));

        // 3. MagicDNS name (what the Director advertises as https://<name>:<port>)
        if (string.IsNullOrWhiteSpace(dnsName))
        {
            results.Add(new TailscaleCheckResult("MagicDNS name", false,
                "Self.DNSName is empty",
                "Enable MagicDNS for the tailnet (admin console -> DNS -> MagicDNS)"));
            return results;
        }
        results.Add(new TailscaleCheckResult("MagicDNS name", true, dnsName, null));

        return results;
    }

    /// <summary>True when every check passed (remote access is fully available).</summary>
    public static bool AllOk(IReadOnlyList<TailscaleCheckResult> results)
        => results.Count > 0 && results.All(r => r.Ok);

    /// <summary>Human-readable multi-line summary (one line per check, remedies indented).</summary>
    public static string Summary(IReadOnlyList<TailscaleCheckResult> results)
    {
        var lines = new List<string>();
        foreach (var r in results)
        {
            lines.Add($"  {(r.Ok ? "[OK]  " : "[FAIL]")} {r.Check}: {r.Detail}");
            if (!r.Ok && r.Remedy is not null)
                lines.Add($"         fix: {r.Remedy}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
