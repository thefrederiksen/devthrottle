using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Tailscale;

/// <summary>
/// Keeps Tailscale Serve HTTPS mappings in lockstep with the live LOCAL Director set so a
/// phone can reach every Director over https://&lt;tailnet&gt;:&lt;port&gt;/ without anyone
/// having to re-run a script. One mapping per local fixed-range Director Control API port,
/// plus the Gateway's own front-door mapping (443 -> gateway port).
///
/// Mapping policy (issue #179): only Directors running on THIS machine with a port in the
/// fixed Director range get a mapping. A serve mapping proxies to http://localhost:&lt;port&gt;,
/// so a mapping for a remote machine's Director points at a dead local port (502 from a
/// phone) and leaks when its disappear event is missed. Hosted-agent Directors register
/// with ephemeral ports and short lifetimes; mapping them rewrote the serve table (every
/// tailscale CLI call is a full-config read-modify-write) hundreds of times a day and the
/// orphans piled up into a 300+ entry table.
///
/// Lifecycle:
///   - Start:               map 443 -> gateway port, and one mapping per known local Director.
///   - Director appears:    map that Director's port (local + fixed range only).
///   - Director disappears: remove that Director's mapping.
///   - Every 5 minutes:     self-healing reconcile - re-assert the front door and any missing
///                          live mapping, sweep provisioner-shaped orphans on ANY port. The
///                          serve table has been observed to lose entries (including 443)
///                          without this Gateway removing them, so desired state is re-asserted
///                          for the whole process lifetime, not only at Start().
///   - Every 60 seconds:    front-door watch - a cheap status read that re-asserts ONLY the
///                          443 mapping. A clobbered front door turns the one URL into a total
///                          502, and the 5-minute reconcile left up to a 5-minute blind window
///                          (issue #200: 443 was rewritten to a dead ephemeral port and a user
///                          hit the outage mid-window). Sweeping stays with the full reconcile.
///   - Gateway shutdown:    leave mappings in place. The Directors are still alive and
///                          reachable; a Gateway restart re-asserts every mapping.
///
/// If the tailscale CLI is not installed this provisioner logs once and becomes a no-op:
/// remote HTTPS is simply unavailable on this machine, which is a legitimate state and
/// NOT a hidden failure. Every tailscale invocation is serialized through one in-process
/// lock so concurrent appear/disappear events cannot race the CLI against itself, and all
/// serve-MUTATING calls additionally go through <see cref="TailscaleCli.RunServeMutating"/>,
/// whose cross-process named mutex stops this Gateway and self-provisioning Directors
/// (issue #197) from interleaving the CLI's whole-table read-modify-write and clobbering
/// each other's mappings (#179/#200).
/// </summary>
public sealed class TailscaleServeProvisioner : IDisposable
{
    /// <summary>Public front-door port: a phone hits https://&lt;tailnet&gt;/ with no port.</summary>
    private const int FrontDoorHttpsPort = 443;

    /// <summary>Director Control API port range (see PortAllocator). Only LOCAL Directors in
    /// this range get a serve mapping; ephemeral-port (hosted-agent) and remote-machine
    /// Directors never do (issue #179).</summary>
    internal const int DirectorPortMin = 7879;
    internal const int DirectorPortMax = 7898;

    /// <summary>How often the self-healing reconcile re-asserts desired state (issue #179).</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    /// <summary>How often the front-door watch verifies the 443 mapping (issue #200). One
    /// `serve status --json` read per tick; it only ever asserts, never sweeps.</summary>
    private static readonly TimeSpan FrontDoorWatchInterval = TimeSpan.FromSeconds(60);

    private readonly DirectorRegistry _registry;
    private readonly int _gatewayPort;
    private readonly int _legacyCockpitPort;
    private readonly string _localMachineName;
    private readonly bool _enabled;
    private readonly object _cliGate = new();
    private readonly ConcurrentDictionary<string, int> _portsById = new();
    private Timer? _reconcileTimer;
    private Timer? _frontDoorTimer;
    private int _reconcileRunning;
    private int _frontDoorRunning;
    private bool _disposed;

    public TailscaleServeProvisioner(DirectorRegistry registry, int gatewayPort, int legacyCockpitPort)
    {
        _registry = registry;
        _gatewayPort = gatewayPort;
        _legacyCockpitPort = legacyCockpitPort;
        _localMachineName = Environment.MachineName;

        // Dev/test isolation: CC_GATEWAY_NO_TAILSCALE=1 lets a second Gateway run on this machine
        // for local end-to-end testing WITHOUT touching the production Tailscale Serve mappings
        // (notably the 443 front door). Explicit opt-in, logged; never the default.
        var tailscaleDisabled = string.Equals(
            Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE"), "1", StringComparison.Ordinal);

        _enabled = TailscaleCli.IsAvailable && !tailscaleDisabled;
        if (tailscaleDisabled)
            FileLog.Write("[TailscaleServeProvisioner] CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve auto-provisioning disabled (local test mode).");
        else if (!_enabled)
            FileLog.Write("[TailscaleServeProvisioner] tailscale CLI not found; remote HTTPS auto-provisioning disabled.");
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

        // Self-healing loop (issue #179): the 443 front door vanished from the serve table
        // in production with no cc-director process removing it, turning the one URL into
        // a 502 until a Gateway restart. GatewayHost triggers the first reconcile right
        // after the registry load; this timer re-asserts desired state forever after.
        _reconcileTimer = new Timer(_ => ReconcileCore(), null, ReconcileInterval, ReconcileInterval);

        // Front-door watch (issue #200): an external `tailscale serve` invocation rewrote 443
        // to a dead ephemeral port mid-window and the one URL was a total 502 for ~5 minutes
        // until the next full reconcile. The front door is the single highest-value mapping,
        // so it gets its own fast, assert-only check.
        _frontDoorTimer = new Timer(_ => WatchFrontDoorCore(), null, FrontDoorWatchInterval, FrontDoorWatchInterval);
    }

    /// <summary>
    /// Run one self-healing reconcile pass in the background: re-assert the 443 front door
    /// and any missing live local Director mapping, and remove provisioner-shaped mappings
    /// (single "/" handler proxying to http://localhost:&lt;samePort&gt;) that no longer
    /// correspond to a live local Director - on ANY port, so leaked ephemeral-port mappings
    /// from older builds are swept too (issue #179). The 443 front door and any mapping the
    /// provisioner did not create are never removed. Call AFTER the registry has loaded so
    /// the live set is known.
    /// </summary>
    public void Reconcile()
    {
        if (!_enabled || _disposed) return;
        _ = Task.Run(ReconcileCore);
    }

    private void ReconcileCore()
    {
        // A reconcile sweeping a very large orphan backlog can take a while (one CLI call
        // per removal); skip a tick rather than stack a second pass on top of it.
        if (Interlocked.CompareExchange(ref _reconcileRunning, 1, 0) != 0) return;
        try
        {
            var desired = new HashSet<int>();
            foreach (var d in _registry.ListDirectors())
            {
                if (ShouldMap(d, _localMachineName, out var p))
                    desired.Add(p);
            }

            string statusJson;
            lock (_cliGate)
            {
                if (_disposed) return;
                var (ok, stdout, message) = TailscaleCli.Run("serve status --json");
                if (!ok)
                {
                    FileLog.Write($"[TailscaleServeProvisioner] Reconcile: could not read serve status: {message}");
                    return;
                }
                statusJson = stdout;
            }

            var actions = ComputeReconcileActions(statusJson, _gatewayPort, desired);

            if (actions.AssertFrontDoor)
            {
                // Log WHAT was found, not just that it was wrong: the observed backend is the
                // only forensic trace of whoever clobbered the mapping, and it evaporates the
                // moment we heal (issue #200 - the rogue port was only identified by a live
                // probe during the outage).
                FileLog.Write($"[TailscaleServeProvisioner] Reconcile: 443 front door missing or wrong backend (observed: {actions.FrontDoorBackend ?? "absent"}, expected: http://localhost:{_gatewayPort}) - re-asserting (issue #179)");
                ServeOn(FrontDoorHttpsPort, _gatewayPort, "gateway (reconcile)");
            }

            foreach (var port in actions.PortsToMap)
            {
                FileLog.Write($"[TailscaleServeProvisioner] Reconcile: live Director mapping --https={port} missing - re-asserting");
                ServeOn(port, port, "reconcile");
            }

            foreach (var port in actions.PortsToRemove)
            {
                if (_disposed) return;
                FileLog.Write($"[TailscaleServeProvisioner] Reconcile: removing orphan --https={port} (no live local Director)");
                ServeOff(port, "orphan");
            }

            if (!actions.AssertFrontDoor && actions.PortsToMap.Count == 0 && actions.PortsToRemove.Count == 0)
                FileLog.Write($"[TailscaleServeProvisioner] Reconcile: serve table consistent (front door + {desired.Count} Director mappings)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeProvisioner] Reconcile EXCEPTION: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }

    /// <summary>
    /// Pure decision core of the reconcile: given the serve table JSON, the gateway port,
    /// and the ports the live local Director set SHOULD occupy, decide what to assert and
    /// what to sweep. Provisioner-shaped means a single "/" handler proxying to
    /// http://localhost:&lt;samePort&gt;; only such mappings are removal candidates, so the
    /// 443 front door (different backend port) and anything created by another product are
    /// never touched.
    /// </summary>
    internal static ReconcileActions ComputeReconcileActions(string statusJson, int gatewayPort, IReadOnlyCollection<int> desiredPorts)
    {
        var frontDoorOk = false;
        string? frontDoorBackend = null;
        var managed = new List<int>();

        if (!string.IsNullOrWhiteSpace(statusJson))
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (doc.RootElement.TryGetProperty("Web", out var web) && web.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in web.EnumerateObject())
                {
                    // entry name is "<host>:<httpsPort>"
                    var colon = entry.Name.LastIndexOf(':');
                    if (colon < 0 || !int.TryParse(entry.Name[(colon + 1)..], out var httpsPort)) continue;

                    if (!entry.Value.TryGetProperty("Handlers", out var handlers)) continue;
                    if (!handlers.TryGetProperty("/", out var rootHandler)) continue;
                    if (!rootHandler.TryGetProperty("Proxy", out var proxyEl)) continue;

                    // Record whatever sits at 443 BEFORE the loopback filter: a non-loopback
                    // backend is still the forensic answer to "who clobbered the front door".
                    if (httpsPort == FrontDoorHttpsPort)
                        frontDoorBackend = proxyEl.GetString();

                    if (!Uri.TryCreate(proxyEl.GetString(), UriKind.Absolute, out var proxy) || !proxy.IsLoopback) continue;

                    if (httpsPort == FrontDoorHttpsPort)
                        frontDoorOk = proxy.Port == gatewayPort;
                    else if (proxy.Port == httpsPort)
                        managed.Add(httpsPort);
                }
            }
        }

        var toMap = desiredPorts.Where(p => !managed.Contains(p)).OrderBy(p => p).ToList();
        var toRemove = managed.Where(p => !desiredPorts.Contains(p)).OrderBy(p => p).ToList();
        return new ReconcileActions(!frontDoorOk, toMap, toRemove, frontDoorBackend);
    }

    /// <param name="FrontDoorBackend">The raw proxy string observed at 443, or null when the
    /// mapping is absent. Logged on re-assert so the clobberer leaves a trace (issue #200).</param>
    internal sealed record ReconcileActions(bool AssertFrontDoor, List<int> PortsToMap, List<int> PortsToRemove, string? FrontDoorBackend);

    /// <summary>
    /// One front-door watch tick: read the serve table and re-assert 443 -> gateway if it is
    /// missing or pointing anywhere else. Deliberately reuses ComputeReconcileActions with an
    /// empty desired set and IGNORES its map/sweep lists - mapping and orphan removal stay on
    /// the 5-minute reconcile cadence; this tick exists only to shrink the front-door outage
    /// window from minutes to seconds (issue #200).
    /// </summary>
    private void WatchFrontDoorCore()
    {
        if (Interlocked.CompareExchange(ref _frontDoorRunning, 1, 0) != 0) return;
        try
        {
            // A full reconcile re-asserts the front door anyway; don't double up behind it.
            if (Volatile.Read(ref _reconcileRunning) != 0) return;

            string statusJson;
            lock (_cliGate)
            {
                if (_disposed) return;
                var (ok, stdout, message) = TailscaleCli.Run("serve status --json");
                if (!ok)
                {
                    FileLog.Write($"[TailscaleServeProvisioner] FrontDoorWatch: could not read serve status: {message}");
                    return;
                }
                statusJson = stdout;
            }

            var actions = ComputeReconcileActions(statusJson, _gatewayPort, []);
            if (actions.AssertFrontDoor)
            {
                FileLog.Write($"[TailscaleServeProvisioner] FrontDoorWatch: 443 front door missing or wrong backend (observed: {actions.FrontDoorBackend ?? "absent"}, expected: http://localhost:{_gatewayPort}) - re-asserting (issue #200)");
                ServeOn(FrontDoorHttpsPort, _gatewayPort, "gateway (front-door watch)");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeProvisioner] FrontDoorWatch EXCEPTION: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _frontDoorRunning, 0);
        }
    }

    private void HandleAdded(DirectorDto d)
    {
        if (!_enabled || _disposed) return;

        if (!ShouldMap(d, _localMachineName, out var port))
        {
            FileLog.Write($"[TailscaleServeProvisioner] HandleAdded: not mapping id={d.DirectorId} (machine={d.MachineName}, control={d.ControlEndpoint}) - only local Directors on ports {DirectorPortMin}-{DirectorPortMax} get a serve mapping");
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

    /// <summary>
    /// A Director gets a serve mapping only when BOTH hold (issue #179):
    ///   1. It runs on THIS machine - the mapping proxies to http://localhost:&lt;port&gt;,
    ///      so a remote Director's mapping points at a dead local port.
    ///   2. Its port is in the fixed Director range - hosted-agent Directors use ephemeral
    ///      ports and short lifetimes; they are reached through the Gateway, not directly.
    /// </summary>
    internal static bool ShouldMap(DirectorDto d, string localMachineName, out int port)
    {
        port = ExtractPort(d);
        if (port < DirectorPortMin || port > DirectorPortMax) return false;

        if (Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var control) && control.IsLoopback)
            return true;
        return !string.IsNullOrEmpty(d.MachineName)
            && string.Equals(d.MachineName, localMachineName, StringComparison.OrdinalIgnoreCase);
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
                var (ok, _, message) = TailscaleCli.RunServeMutating($"serve --bg --https={httpsPort} http://localhost:{backendPort}");
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
                var (ok, _, message) = TailscaleCli.RunServeMutating($"serve --https={httpsPort} off");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconcileTimer?.Dispose();
        _frontDoorTimer?.Dispose();
        _registry.OnDirectorAdded -= HandleAdded;
        _registry.OnDirectorRemoved -= HandleRemoved;
    }
}
