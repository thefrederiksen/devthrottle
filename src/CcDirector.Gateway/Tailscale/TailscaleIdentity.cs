using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tailscale;

/// <summary>
/// Resolves this machine's own Tailscale identity so the Gateway can hand out a
/// remotely reachable HTTPS URL (the tailnet front door, https://&lt;magicdns&gt;/,
/// which Tailscale Serve maps to the gateway port). Used by the agent-info guide
/// so an external agent on any tailnet machine gets a URL that actually works,
/// not a localhost URL that only resolves on this machine.
///
/// If tailscale.exe is absent or the node is not on a tailnet this returns null:
/// remote access is genuinely unavailable, which the caller surfaces truthfully
/// rather than substituting a localhost URL.
/// </summary>
public static class TailscaleIdentity
{
    private const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The tailnet front-door base URL, e.g. <c>https://soren-north.taildb08ed.ts.net</c>,
    /// or null if Tailscale is not available on this machine. No port: the Serve
    /// front door listens on 443 and proxies to the gateway.
    /// </summary>
    public static string? TryGetFrontDoorBaseUrl()
    {
        var dnsName = TryGetMagicDnsName();
        return dnsName is null ? null : $"https://{dnsName}";
    }

    private static string? TryGetMagicDnsName()
    {
        if (!File.Exists(TailscaleExe))
        {
            FileLog.Write($"[TailscaleIdentity] tailscale.exe not found at {TailscaleExe}");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = TailscaleExe,
                Arguments = "status --json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for tailscale.exe");
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit((int)CommandTimeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                FileLog.Write("[TailscaleIdentity] tailscale status timed out");
                return null;
            }
            if (proc.ExitCode != 0) { FileLog.Write($"[TailscaleIdentity] tailscale status exit {proc.ExitCode}"); return null; }

            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("Self", out var self)) return null;
            if (!self.TryGetProperty("DNSName", out var dnsEl)) return null;
            var dns = dnsEl.GetString();
            if (string.IsNullOrWhiteSpace(dns)) return null;
            return dns.TrimEnd('.'); // tailscale reports a trailing-dot FQDN
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleIdentity] resolve failed: {ex.Message}");
            return null;
        }
    }
}
