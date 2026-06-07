using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Network;

/// <summary>
/// Resolves this machine's own Tailscale identity (its MagicDNS name) by shelling
/// <c>tailscale status --json</c> and reading <c>Self.DNSName</c>.
///
/// Two consumers:
///   - The Director registration path advertises <c>http://&lt;magicdns&gt;:&lt;port&gt;</c> by
///     default, so a gateway on ANY tailnet node can reach it (no loopback-only blind spot).
///   - The Gateway hands out the tailnet front door <c>https://&lt;magicdns&gt;/</c> (Serve maps
///     443 to the gateway port) so an agent on any tailnet machine gets a working URL.
///
/// When Tailscale is not installed or the node is not on a tailnet this returns null:
/// remote reachability is genuinely unavailable, which the caller surfaces truthfully
/// rather than substituting an unreachable localhost URL.
/// </summary>
public static class TailscaleIdentity
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// This node's MagicDNS name, e.g. <c>machine-a.tail0123.ts.net</c> (no scheme, no
    /// port, no trailing dot), or null if Tailscale is unavailable on this machine.
    /// </summary>
    public static string? TryGetMagicDnsName()
    {
        var json = RunStatusJson();
        if (json is null) return null;

        var name = ParseSelfDnsName(json);
        if (name is null)
            FileLog.Write("[TailscaleIdentity] tailscale status had no Self.DNSName");
        return name;
    }

    /// <summary>
    /// The MagicDNS names of the tailnet nodes a gateway could plausibly run on - online and
    /// not mobile (a gateway never runs on a phone) - so a caller can probe each for a gateway.
    /// Self is first. Returns an empty list when Tailscale is unavailable. The caller does the
    /// probing; this just supplies the candidate hosts.
    /// </summary>
    public static IReadOnlyList<string> ListGatewayHostCandidates()
    {
        var json = RunStatusJson();
        return json is null
            ? Array.Empty<string>()
            : ParseNodeDnsNames(json, includeSelf: true, onlineOnly: true, excludeMobile: true);
    }

    /// <summary>
    /// Run <c>tailscale status --json</c> and return its stdout, or null when the CLI is
    /// missing, times out, or exits non-zero (Tailscale genuinely unavailable).
    /// </summary>
    private static string? RunStatusJson()
    {
        var exe = ResolveExe();
        if (exe is null)
        {
            FileLog.Write("[TailscaleIdentity] tailscale CLI not found in any known location");
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "status --json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for {exe}");
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit((int)CommandTimeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                FileLog.Write("[TailscaleIdentity] tailscale status timed out");
                return null;
            }
            if (proc.ExitCode != 0)
            {
                FileLog.Write($"[TailscaleIdentity] tailscale status exit {proc.ExitCode}");
                return null;
            }
            return stdout;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleIdentity] tailscale status failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The tailnet front-door base URL, e.g. <c>https://machine-a.tail0123.ts.net</c>,
    /// or null if Tailscale is not available. No port: the Serve front door listens on 443.
    /// </summary>
    public static string? TryGetFrontDoorBaseUrl()
    {
        var dnsName = TryGetMagicDnsName();
        return dnsName is null ? null : $"https://{dnsName}";
    }

    /// <summary>
    /// The tailnet front-door URL for a specific backend port, e.g.
    /// <c>https://machine-a.tail0123.ts.net:7470</c> for the Cockpit, or null if
    /// Tailscale is unavailable. Tailscale Serve maps <c>https://&lt;magicdns&gt;:&lt;port&gt;</c>
    /// to <c>http://localhost:&lt;port&gt;</c>, so the public URL keeps the same port number.
    /// Callers MUST treat null as "remote URL unavailable" and refuse rather than substitute
    /// a loopback URL: the tailnet is the trust boundary and a localhost URL only works on
    /// the one machine.
    /// </summary>
    public static string? TryGetFrontDoorUrlForPort(int port)
    {
        var dnsName = TryGetMagicDnsName();
        return dnsName is null ? null : BuildFrontDoorUrlForPort(dnsName, port);
    }

    /// <summary>
    /// Compose the per-port front-door URL from a MagicDNS name. Pure - unit-tested.
    /// </summary>
    public static string BuildFrontDoorUrlForPort(string dnsName, int port)
    {
        if (string.IsNullOrWhiteSpace(dnsName))
            throw new ArgumentException("MagicDNS name is required", nameof(dnsName));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1-65535");

        return $"https://{dnsName}:{port}";
    }

    /// <summary>
    /// Extract <c>Self.DNSName</c> from <c>tailscale status --json</c> output, stripped of its
    /// trailing dot. Returns null when the field is absent or empty. Pure - unit-tested.
    /// </summary>
    public static string? ParseSelfDnsName(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("Self", out var self)) return null;
        if (!self.TryGetProperty("DNSName", out var dnsEl)) return null;
        if (dnsEl.ValueKind != JsonValueKind.String) return null;
        var dns = dnsEl.GetString();
        if (string.IsNullOrWhiteSpace(dns)) return null;
        return dns.TrimEnd('.'); // tailscale reports a trailing-dot FQDN
    }

    /// <summary>
    /// Extract <c>BackendState</c> from <c>tailscale status --json</c> output - "Running",
    /// "Stopped", "NeedsLogin", etc. Returns null when the field is absent. Pure - unit-tested.
    /// Used by the Gateway-connectivity troubleshooting ladder (issue #223): "Running" is the
    /// only state in which Serve mappings answer.
    /// </summary>
    public static string? ParseBackendState(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("BackendState", out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var state = el.GetString();
        return string.IsNullOrWhiteSpace(state) ? null : state;
    }

    /// <summary>
    /// Extract node MagicDNS names from <c>tailscale status --json</c> (Self + the Peer map),
    /// stripped of trailing dots, in Self-first order. Filters:
    ///   - <paramref name="onlineOnly"/>: drop nodes whose Online flag is false.
    ///   - <paramref name="excludeMobile"/>: drop android/iOS nodes (a gateway never runs there).
    /// Pure - unit-tested.
    /// </summary>
    public static IReadOnlyList<string> ParseNodeDnsNames(
        string statusJson, bool includeSelf = true, bool onlineOnly = true, bool excludeMobile = true)
    {
        var names = new List<string>();
        using var doc = JsonDocument.Parse(statusJson);
        var root = doc.RootElement;

        void Consider(JsonElement node)
        {
            if (!node.TryGetProperty("DNSName", out var d) || d.ValueKind != JsonValueKind.String) return;
            var name = d.GetString();
            if (string.IsNullOrWhiteSpace(name)) return;

            if (onlineOnly && node.TryGetProperty("Online", out var on) && on.ValueKind == JsonValueKind.False)
                return;

            if (excludeMobile && node.TryGetProperty("OS", out var os) && os.ValueKind == JsonValueKind.String)
            {
                var o = os.GetString();
                if (string.Equals(o, "android", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(o, "iOS", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            names.Add(name.TrimEnd('.'));
        }

        if (includeSelf && root.TryGetProperty("Self", out var self))
            Consider(self);

        if (root.TryGetProperty("Peer", out var peer) && peer.ValueKind == JsonValueKind.Object)
            foreach (var p in peer.EnumerateObject())
                Consider(p.Value);

        return names;
    }

    /// <summary>
    /// Candidate tailscale CLI locations for the current OS, in priority order. Cross-platform:
    /// the install path differs per platform and per install method.
    /// </summary>
    public static IReadOnlyList<string> CandidateExePaths()
    {
        if (OperatingSystem.IsWindows())
            return new[] { @"C:\Program Files\Tailscale\tailscale.exe" };

        if (OperatingSystem.IsMacOS())
            return new[]
            {
                "/Applications/Tailscale.app/Contents/MacOS/Tailscale", // Mac App Store build
                "/usr/local/bin/tailscale",                             // standalone / Homebrew (Intel)
                "/opt/homebrew/bin/tailscale",                          // Homebrew (Apple Silicon)
            };

        return new[] { "/usr/bin/tailscale", "/usr/local/bin/tailscale" }; // Linux / other unix
    }

    /// <summary>
    /// First existing CLI path, else the bare command name so Process.Start can resolve it via
    /// PATH (covers installs not in a known location). Returns null only on Windows when the
    /// known path is missing, where there is no PATH convention to fall back on.
    /// </summary>
    private static string? ResolveExe()
    {
        foreach (var path in CandidateExePaths())
            if (File.Exists(path)) return path;

        // On unix the CLI is commonly on PATH even when not at a known absolute path.
        if (!OperatingSystem.IsWindows())
            return "tailscale";

        return null;
    }
}
