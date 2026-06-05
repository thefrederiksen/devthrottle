using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Tailscale;

/// <summary>
/// Keeps Tailscale Serve HTTPS mappings in lockstep with the live Director set so a
/// phone can reach every Director over https://&lt;tailnet&gt;:&lt;port&gt;/ without anyone
/// having to re-run a script. One mapping per Director Control API port, plus the
/// Gateway's own front-door mapping (443 -> gateway port).
///
/// Lifecycle:
///   - Start:               map 443 -> gateway port, and one mapping per already-known Director.
///   - Director appears:    map that Director's port.
///   - Director disappears: remove that Director's mapping.
///   - Gateway shutdown:    leave mappings in place. The Directors are still alive and
///                          reachable; a Gateway restart re-asserts every mapping.
///
/// If tailscale.exe is not installed this provisioner logs once and becomes a no-op:
/// remote HTTPS is simply unavailable on this machine, which is a legitimate state and
/// NOT a hidden failure. Every tailscale invocation is serialized through one lock so
/// concurrent appear/disappear events cannot race the CLI against itself.
/// </summary>
public sealed class TailscaleServeProvisioner : IDisposable
{
    private const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Public front-door port: a phone hits https://&lt;tailnet&gt;/ with no port.</summary>
    private const int FrontDoorHttpsPort = 443;

    /// <summary>Director Control API port range (see PortAllocator). Only mappings in this
    /// range are eligible for orphan cleanup; everything else is left alone.</summary>
    private const int DirectorPortMin = 7879;
    private const int DirectorPortMax = 7898;

    private readonly DirectorRegistry _registry;
    private readonly int _gatewayPort;
    private readonly int _legacyCockpitPort;
    private readonly bool _enabled;
    private readonly object _cliGate = new();
    private readonly ConcurrentDictionary<string, int> _portsById = new();
    private bool _disposed;

    public TailscaleServeProvisioner(DirectorRegistry registry, int gatewayPort, int legacyCockpitPort)
    {
        _registry = registry;
        _gatewayPort = gatewayPort;
        _legacyCockpitPort = legacyCockpitPort;

        // Dev/test isolation: CC_GATEWAY_NO_TAILSCALE=1 lets a second Gateway run on this machine
        // for local end-to-end testing WITHOUT touching the production Tailscale Serve mappings
        // (notably the 443 front door). Explicit opt-in, logged; never the default.
        var tailscaleDisabled = string.Equals(
            Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE"), "1", StringComparison.Ordinal);

        _enabled = File.Exists(TailscaleExe) && !tailscaleDisabled;
        if (tailscaleDisabled)
            FileLog.Write("[TailscaleServeProvisioner] CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve auto-provisioning disabled (local test mode).");
        else if (!_enabled)
            FileLog.Write($"[TailscaleServeProvisioner] tailscale.exe not found at {TailscaleExe}; remote HTTPS auto-provisioning disabled.");
    }

    /// <summary>
    /// Subscribe to registry events and assert mappings for everything already known.
    /// Call this BEFORE <see cref="DirectorRegistry.Start"/> so the initial file-load
    /// fires OnDirectorAdded into us; the ListDirectors sweep below is the belt-and-braces
    /// for any Director already present at subscribe time.
    /// </summary>
    public void Start()
    {
        if (!_enabled) return;
        FileLog.Write($"[TailscaleServeProvisioner] Start: gatewayPort={_gatewayPort}");

        _registry.OnDirectorAdded += HandleAdded;
        _registry.OnDirectorRemoved += HandleRemoved;

        // Front door: https://<tailnet>/ -> gateway. Idempotent. The Cockpit is reached
        // THROUGH this front door (one-URL plan: the gateway fallback-proxies to it), so it
        // no longer gets its own tailnet port.
        QueueServeOn(FrontDoorHttpsPort, _gatewayPort, "gateway");

        // One-time cleanup of the pre-one-URL world: drop the legacy direct Cockpit mapping
        // (https://<tailnet>:7470) if this machine still carries one. Idempotent and safe
        // when absent ("serve off" on a missing mapping is a no-op).
        _ = Task.Run(() => ServeOff(_legacyCockpitPort, "legacy cockpit (one-URL: served via the front door)"));

        foreach (var d in _registry.ListDirectors())
            HandleAdded(d);
    }

    /// <summary>
    /// Remove cc-director serve mappings that no longer correspond to a live Director.
    /// This catches the case where a Director died while the Gateway was down, so its
    /// disappear event was never observed and its mapping lingers (proxying to a dead
    /// port -> 502 from a phone).
    ///
    /// Surgical by design: only same-port Director-range mappings
    /// (--https=P -> http://localhost:P with P in [DirectorPortMin..DirectorPortMax])
    /// are eligible. The 443 front door and any mapping the provisioner did not create
    /// are never touched. Call AFTER the registry has loaded so the live set is known.
    /// </summary>
    public void ReconcileOrphans()
    {
        if (!_enabled || _disposed) return;
        _ = Task.Run(ReconcileOrphansCore);
    }

    private void ReconcileOrphansCore()
    {
        try
        {
            var live = new HashSet<int>();
            foreach (var d in _registry.ListDirectors())
            {
                var p = ExtractPort(d);
                if (p > 0) live.Add(p);
            }

            List<int> managed;
            lock (_cliGate)
            {
                if (_disposed) return;
                var (ok, stdout, message) = RunTailscale("serve status --json");
                if (!ok)
                {
                    FileLog.Write($"[TailscaleServeProvisioner] ReconcileOrphans: could not read serve status: {message}");
                    return;
                }
                managed = ParseManagedHttpsPorts(stdout);
            }

            foreach (var port in managed)
            {
                if (port < DirectorPortMin || port > DirectorPortMax) continue; // not a Director-range mapping
                if (live.Contains(port)) continue;                              // still a live Director
                FileLog.Write($"[TailscaleServeProvisioner] ReconcileOrphans: removing orphan --https={port} (no live Director)");
                ServeOff(port, "orphan");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeProvisioner] ReconcileOrphans EXCEPTION: {ex.Message}");
        }
    }

    /// <summary>
    /// From `tailscale serve status --json`, return the HTTPS ports whose mapping has the
    /// provisioner's own shape: a single "/" handler proxying to http://localhost:&lt;samePort&gt;.
    /// This excludes the 443 front door (proxies to the gateway port, not 443) and any
    /// mapping created by something other than this provisioner.
    /// </summary>
    private static List<int> ParseManagedHttpsPorts(string statusJson)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(statusJson)) return result;

        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var entry in web.EnumerateObject())
        {
            // entry name is "<host>:<httpsPort>"
            var colon = entry.Name.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(entry.Name[(colon + 1)..], out var httpsPort)) continue;

            if (!entry.Value.TryGetProperty("Handlers", out var handlers)) continue;
            if (!handlers.TryGetProperty("/", out var rootHandler)) continue;
            if (!rootHandler.TryGetProperty("Proxy", out var proxyEl)) continue;

            if (!Uri.TryCreate(proxyEl.GetString(), UriKind.Absolute, out var proxy)) continue;
            if (proxy.IsLoopback && proxy.Port == httpsPort)
                result.Add(httpsPort);
        }
        return result;
    }

    private void HandleAdded(DirectorDto d)
    {
        if (!_enabled || _disposed) return;

        var port = ExtractPort(d);
        if (port <= 0)
        {
            FileLog.Write($"[TailscaleServeProvisioner] HandleAdded: no usable port for id={d.DirectorId} (control={d.ControlEndpoint}, tailnet={d.TailnetEndpoint})");
            return;
        }

        _portsById[d.DirectorId] = port;
        QueueServeOn(port, port, d.DirectorId);
    }

    private void HandleRemoved(string directorId)
    {
        if (!_enabled || _disposed) return;
        if (_portsById.TryRemove(directorId, out var port))
            QueueServeOff(port, directorId);
    }

    private static int ExtractPort(DirectorDto d)
    {
        if (Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var control) && control.Port > 0)
            return control.Port;
        if (Uri.TryCreate(d.TailnetEndpoint, UriKind.Absolute, out var tailnet) && tailnet.Port > 0)
            return tailnet.Port;
        return -1;
    }

    private void QueueServeOn(int httpsPort, int backendPort, string who)
        => _ = Task.Run(() => ServeOn(httpsPort, backendPort, who));

    private void QueueServeOff(int httpsPort, string who)
        => _ = Task.Run(() => ServeOff(httpsPort, who));

    // ServeOn/ServeOff are the roots of background tasks and the boundary to an external
    // process, so they own the try-catch (an unobserved task exception would otherwise be lost).
    private void ServeOn(int httpsPort, int backendPort, string who)
    {
        try
        {
            lock (_cliGate)
            {
                if (_disposed) return;
                var (ok, _, message) = RunTailscale($"serve --bg --https={httpsPort} http://localhost:{backendPort}");
                if (ok)
                    FileLog.Write($"[TailscaleServeProvisioner] mapped --https={httpsPort} -> http://localhost:{backendPort} ({who})");
                else
                    FileLog.Write($"[TailscaleServeProvisioner] FAILED to map --https={httpsPort} -> http://localhost:{backendPort} ({who}): {message}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeProvisioner] ServeOn EXCEPTION for --https={httpsPort} ({who}): {ex.Message}");
        }
    }

    private void ServeOff(int httpsPort, string who)
    {
        try
        {
            lock (_cliGate)
            {
                if (_disposed) return;
                var (ok, _, message) = RunTailscale($"serve --https={httpsPort} off");
                if (ok)
                    FileLog.Write($"[TailscaleServeProvisioner] removed --https={httpsPort} ({who})");
                else if (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                    FileLog.Write($"[TailscaleServeProvisioner] --https={httpsPort} already absent ({who})");
                else
                    FileLog.Write($"[TailscaleServeProvisioner] FAILED to remove --https={httpsPort} ({who}): {message}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeProvisioner] ServeOff EXCEPTION for --https={httpsPort} ({who}): {ex.Message}");
        }
    }

    // Returns (ok, stdout, message). stdout is the raw standard output (used to parse
    // `serve status --json`); message is a human-readable summary for failure logging.
    private static (bool ok, string stdout, string message) RunTailscale(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TailscaleExe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {TailscaleExe}");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, stdout, $"timed out after {CommandTimeout.TotalSeconds:F0}s");
        }

        var message = string.Join(" | ", new[] { stdout, stderr }
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));
        return (proc.ExitCode == 0, stdout, message.Length > 0 ? message : $"exit {proc.ExitCode}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.OnDirectorAdded -= HandleAdded;
        _registry.OnDirectorRemoved -= HandleRemoved;
    }
}
