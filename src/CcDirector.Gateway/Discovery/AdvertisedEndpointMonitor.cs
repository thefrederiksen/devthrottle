using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// Issue #325: continuous re-verification of each registered Director's ADVERTISED endpoint.
///
/// The #223/#224 two-way handshake proves the advertised name once, at registration time. A
/// Director whose name goes bad afterward (lost tailscale serve mapping, cert expiry, firewall
/// change) keeps heartbeating happily while the fleet believes an address that no longer
/// answers. This monitor probes <c>GET {advertised}/healthz</c> on every heartbeat cycle
/// (15 s) and requires the answer to identify as the SAME Director (impostor guard); a failed
/// probe flags the registration <see cref="DirectorDto.EndpointStateUnreachableByName"/> -
/// explicitly distinct from heartbeat loss (the process is alive; its front door is not) - and
/// the next successful probe clears it. State transitions live in
/// <see cref="DirectorRegistry.RecordEndpointProbeResult"/>.
///
/// Probed: HTTP-registered Directors with an advertised tailnet endpoint. Skipped: FSW-discovered
/// same-machine Directors (no advertised name to verify) and #324 flagged no-endpoint
/// registrations (the Director itself declared the endpoint dead - probing it is pointless).
/// </summary>
public sealed class AdvertisedEndpointMonitor : IDisposable
{
    /// <summary>Probe cadence - one cycle per Director heartbeat (15 s), so an unreachable
    /// name is flagged well inside the 30 s (two-cycle) budget of issue #325.</summary>
    public static TimeSpan ProbeInterval { get; } = TimeSpan.FromSeconds(15);

    private readonly DirectorRegistry _registry;
    private readonly Func<string, CancellationToken, Task<(HealthDto? health, string? error)>> _probe;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _probing; // re-entrancy guard: a slow pass must not stack on the next tick
    private bool _disposed;

    /// <summary>Production wiring: probe through the Gateway's Director HTTP client (2 s timeout per probe).</summary>
    public AdvertisedEndpointMonitor(DirectorRegistry registry, DirectorEndpointClient client)
        : this(registry, client.GetHealthDetailedAsync)
    {
    }

    /// <summary>Test seam: inject the probe so the state machine is testable without HTTP.</summary>
    public AdvertisedEndpointMonitor(
        DirectorRegistry registry,
        Func<string, CancellationToken, Task<(HealthDto? health, string? error)>> probe)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>Begin the periodic probe loop. First pass after one full interval - a Director
    /// that just registered was verified by the registration-time handshake moments ago.</summary>
    public void Start()
    {
        FileLog.Write($"[AdvertisedEndpointMonitor] Start: probing advertised endpoints every {ProbeInterval.TotalSeconds:F0}s");
        _timer = new Timer(_ => _ = ProbeAllOnTimerAsync(), null, ProbeInterval, ProbeInterval);
    }

    /// <summary>Timer entry point (exception boundary): one probe pass, never throws.</summary>
    private async Task ProbeAllOnTimerAsync()
    {
        try
        {
            await ProbeAllAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AdvertisedEndpointMonitor] probe pass FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// One probe pass over the fleet, all Directors in parallel. Public so tests (and a future
    /// on-demand trigger) can run a deterministic single pass without the timer.
    /// </summary>
    public async Task ProbeAllAsync()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _probing, 1) == 1) return; // previous pass still in flight
        try
        {
            var targets = _registry.ListDirectors()
                .Where(d => d.Source == "http")
                .Where(d => !string.IsNullOrEmpty(d.TailnetEndpoint))
                .Where(d => d.EndpointUnreachableReason is null) // #324: the Director declared it dead already
                .ToList();
            if (targets.Count == 0) return;

            await Task.WhenAll(targets.Select(d => ProbeOneAsync(d, _cts.Token)));
        }
        catch (OperationCanceledException)
        {
            // disposing - the pass is moot
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    private async Task ProbeOneAsync(DirectorDto d, CancellationToken ct)
    {
        var endpoint = (d.TailnetEndpoint ?? "").TrimEnd('/');
        var (health, error) = await _probe(endpoint, ct);
        if (ct.IsCancellationRequested) return;

        if (health is null)
        {
            _registry.RecordEndpointProbeResult(d.DirectorId, ok: false, error ?? "healthz probe failed");
            return;
        }

        // Impostor guard: an answer from the wrong (or an unidentifiable) process is a
        // failure with its own reason, never a pass - the advertised name must reach THIS
        // Director, not merely something that speaks /healthz.
        if (!string.Equals(health.DirectorId, d.DirectorId, StringComparison.OrdinalIgnoreCase))
        {
            var who = string.IsNullOrEmpty(health.DirectorId)
                ? "a process that reports no directorId"
                : $"a DIFFERENT Director ({health.DirectorId})";
            _registry.RecordEndpointProbeResult(d.DirectorId, ok: false,
                $"healthz answered at {endpoint}, but as {who} - the advertised URL points at the wrong process");
            return;
        }

        _registry.RecordEndpointProbeResult(d.DirectorId, ok: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
