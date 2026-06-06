using System.Text.Json;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #197: every Director owns its own Tailscale Serve front door.
///
/// The Director binds Kestrel to loopback only; the sole remote path is a
/// <c>tailscale serve --https=&lt;port&gt; -&gt; http://localhost:&lt;port&gt;</c> mapping ON
/// THIS MACHINE. Historically only the Gateway's TailscaleServeProvisioner created those
/// mappings, and only for Directors local to the Gateway (#179) - so a Director on a
/// Workstation-only machine advertised an endpoint nothing answered, and the Gateway
/// flapped through a register/timeout/evict/410/re-register loop forever. This class
/// closes the gap:
///
///   - Start:             assert the mapping for the Director's OWN port (background).
///   - Every 5 minutes:   re-assert if missing - the serve table is known to lose
///                        entries with no cc-director process removing them (#179, #200).
///   - Graceful shutdown: remove the mapping. Crash recovery needs nothing special:
///                        ports are stable per slot (PortAllocator), so the next startup
///                        re-asserts the same mapping; until then the leftover proxies to
///                        a dead loopback port, which the Gateway's circuit breaker
///                        already handles.
///
/// Failure policy (no fallbacks): a missing CLI or failed command is logged with the
/// exact CLI output and surfaced via <see cref="LastError"/>. Registration is gated by
/// the outside-in probe composed in ControlApiHost (GatewayClient.EndpointVerifier): the
/// probe is the single source of truth, this class only does the provisioning work. The
/// CC_GATEWAY_NO_TAILSCALE=1 dev/test switch disables touching the serve table here too,
/// for the same reason it disables the Gateway's provisioner.
/// </summary>
public sealed class TailscaleServeSelfProvisioner : IDisposable
{
    /// <summary>How often the re-assert tick checks that the own mapping still exists.</summary>
    public static TimeSpan ReassertInterval { get; } = TimeSpan.FromMinutes(5);

    private readonly int _port;
    private Timer? _timer;
    private int _ticking;
    private bool _disposed;

    /// <summary>Test seam: serve-mutating CLI calls. Production: cross-process-serialized real CLI.</summary>
    internal Func<string, (bool ok, string stdout, string message)> MutatingRunner { get; set; }
        = TailscaleCli.RunServeMutating;

    /// <summary>Test seam: read-only CLI calls (serve status). Production: real CLI, lock-free.</summary>
    internal Func<string, (bool ok, string stdout, string message)> Runner { get; set; }
        = TailscaleCli.Run;

    /// <summary>Test seam: CLI presence. Production: the real install check; tests pin it so
    /// the lifecycle is testable on machines (CI) without Tailscale installed.</summary>
    internal Func<bool> CliAvailable { get; set; } = () => TailscaleCli.IsAvailable;

    /// <summary>
    /// Whether this provisioner may touch the real serve table. Set from
    /// CC_GATEWAY_NO_TAILSCALE in the constructor; internal-settable as a test seam so the
    /// lifecycle tests (which fully seam the CLI) can opt back in while the test process
    /// runs with the kill switch set (Gateway.Tests sets it process-wide - test-spawned
    /// hosts were rewriting the machine's REAL serve table, the #179/#200 clobberer).
    /// </summary>
    internal bool Enabled { get; set; }

    /// <summary>Last provisioning failure, or null when the last attempt succeeded. Surfaced
    /// to the registration verifier and the Director log so the machine that can act on the
    /// problem sees the exact reason.</summary>
    public string? LastError { get; private set; }

    /// <summary>When the mapping was last confirmed or asserted. Null until the first attempt.</summary>
    public DateTime? LastAssertedAt { get; private set; }

    public TailscaleServeSelfProvisioner(int port)
    {
        if (port <= 0)
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be positive");
        _port = port;

        var tailscaleDisabled = string.Equals(
            Environment.GetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE"), "1", StringComparison.Ordinal);
        Enabled = !tailscaleDisabled;
        if (tailscaleDisabled)
            FileLog.Write("[TailscaleServeSelfProvisioner] CC_GATEWAY_NO_TAILSCALE=1; serve self-provisioning disabled (local test mode).");
    }

    /// <summary>Kick the initial assert in the background and start the re-assert timer.</summary>
    public void Start()
    {
        if (!Enabled || _disposed) return;
        FileLog.Write($"[TailscaleServeSelfProvisioner] Start: port={_port}");
        _ = Task.Run(() => EnsureMappingCore());
        _timer = new Timer(_ => Tick(), null, ReassertInterval, ReassertInterval);
    }

    /// <summary>
    /// Make sure the own-port serve mapping exists right now. Called by the registration
    /// verifier before every register attempt and by the re-assert timer. Returns
    /// (true, null) when the mapping is confirmed present or freshly asserted; otherwise
    /// (false, reason). Disabled mode reports success with a note - the outside-in probe
    /// composed in ControlApiHost remains the gate that actually decides registration.
    /// </summary>
    public Task<(bool ok, string? error)> EnsureMappingAsync(CancellationToken ct = default)
        => Task.Run(() => EnsureMappingCore(), ct);

    private (bool ok, string? error) EnsureMappingCore()
    {
        if (_disposed) return (false, "disposed");
        if (!Enabled) return (true, "skipped (CC_GATEWAY_NO_TAILSCALE=1)");
        if (!CliAvailable())
        {
            LastError = "tailscale CLI not found - install Tailscale and log into the tailnet";
            FileLog.Write($"[TailscaleServeSelfProvisioner] EnsureMapping FAILED: {LastError}");
            return (false, LastError);
        }

        try
        {
            // Read first (lock-free): only write the serve table when the mapping is
            // actually missing, so the steady state costs zero config writes.
            var (statusOk, statusJson, statusMsg) = Runner("serve status --json");
            if (statusOk && StatusHasMapping(statusJson, _port))
            {
                LastError = null;
                LastAssertedAt = DateTime.UtcNow;
                return (true, null);
            }
            if (!statusOk)
                FileLog.Write($"[TailscaleServeSelfProvisioner] serve status unreadable ({statusMsg}); asserting mapping anyway");

            var (ok, _, message) = MutatingRunner($"serve --bg --https={_port} http://localhost:{_port}");
            if (ok)
            {
                FileLog.Write($"[TailscaleServeSelfProvisioner] mapped --https={_port} -> http://localhost:{_port}");
                LastError = null;
                LastAssertedAt = DateTime.UtcNow;
                return (true, null);
            }

            LastError = $"tailscale serve --https={_port} failed: {message}";
            FileLog.Write($"[TailscaleServeSelfProvisioner] EnsureMapping FAILED: {LastError}");
            return (false, LastError);
        }
        catch (Exception ex)
        {
            LastError = $"tailscale serve --https={_port} threw: {ex.Message}";
            FileLog.Write($"[TailscaleServeSelfProvisioner] EnsureMapping EXCEPTION: {ex.Message}");
            return (false, LastError);
        }
    }

    private void Tick()
    {
        // Never stack a second pass on a slow CLI call.
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            var (ok, error) = EnsureMappingCore();
            if (!ok)
                FileLog.Write($"[TailscaleServeSelfProvisioner] re-assert tick failed: {error}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Remove the own-port mapping. Called from ControlApiHost.StopAsync on graceful
    /// shutdown only - never from Dispose, so an unexpected teardown path cannot yank a
    /// mapping out from under a Director that is in fact still running.
    /// </summary>
    public void RemoveOwnMapping()
    {
        if (!Enabled) return;
        try
        {
            var (ok, _, message) = MutatingRunner($"serve --https={_port} off");
            if (ok)
                FileLog.Write($"[TailscaleServeSelfProvisioner] removed --https={_port} (graceful shutdown)");
            else if (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                FileLog.Write($"[TailscaleServeSelfProvisioner] --https={_port} already absent");
            else
                FileLog.Write($"[TailscaleServeSelfProvisioner] FAILED to remove --https={_port}: {message}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TailscaleServeSelfProvisioner] RemoveOwnMapping EXCEPTION: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the serve table JSON already carries this machine's mapping for
    /// <paramref name="port"/>: a "&lt;host&gt;:&lt;port&gt;" Web entry whose "/" handler
    /// proxies to loopback on the SAME port (the provisioner shape, see
    /// TailscaleServeProvisioner.ComputeReconcileActions). Pure - unit-tested.
    /// </summary>
    internal static bool StatusHasMapping(string statusJson, int port)
    {
        if (string.IsNullOrWhiteSpace(statusJson)) return false;
        using var doc = JsonDocument.Parse(statusJson);
        if (!doc.RootElement.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var entry in web.EnumerateObject())
        {
            var colon = entry.Name.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(entry.Name[(colon + 1)..], out var httpsPort)) continue;
            if (httpsPort != port) continue;

            if (!entry.Value.TryGetProperty("Handlers", out var handlers)) continue;
            if (!handlers.TryGetProperty("/", out var rootHandler)) continue;
            if (!rootHandler.TryGetProperty("Proxy", out var proxyEl)) continue;
            if (!Uri.TryCreate(proxyEl.GetString(), UriKind.Absolute, out var proxy) || !proxy.IsLoopback) continue;

            if (proxy.Port == port) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
