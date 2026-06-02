using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Resolves the machine's Tailscale (MagicDNS) host so user-facing messages link to a URL that works
/// from the phone / anywhere on the tailnet - never localhost, which is useless off this machine.
/// Falls back to a non-localhost hint when Tailscale is not present.
/// </summary>
public static class TailnetResolver
{
    private const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>The tailnet MagicDNS name (e.g. host.tailnet.ts.net), or null if it can't be resolved.</summary>
    public static string? HostName()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(TailscaleExe)) return null;
        try
        {
            var (exit, output) = ProcessRunner.Run(TailscaleExe, "status --json");
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("Self", out var self) &&
                self.TryGetProperty("DNSName", out var dns))
            {
                var name = dns.GetString()?.TrimEnd('.');
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch
        {
            // tailscale missing / not running / unexpected output -> fall back below
        }
        return null;
    }

    /// <summary>
    /// A user-facing URL for a local service port: the tailnet URL when resolvable, otherwise a hint
    /// that points at the tailnet host (never a bare localhost URL).
    /// </summary>
    public static string Url(int port)
    {
        var host = HostName();
        return host is null
            ? $"http://<your-tailnet-host>:{port}"
            : $"http://{host}:{port}";
    }
}
