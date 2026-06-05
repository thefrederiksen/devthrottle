using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Phase-1 Director-to-Gateway client. If a <see cref="GatewayConfig"/> is enabled
/// (gateway.url present in config.json), this client:
///
///   1. POSTs /directors/register on start.
///   2. POSTs /directors/{id}/heartbeat every <see cref="HeartbeatInterval"/>.
///   3. DELETEs /directors/{id}/registration on stop.
///   4. Reacts to 410 Gone on heartbeat by re-registering automatically.
///   5. Retries failed register and heartbeat calls with exponential backoff.
///
/// When the config is disabled (no gateway.url) the client is inert - every method
/// is a no-op so the Director boots normally in local-only mode.
/// </summary>
public sealed class GatewayClient : IDisposable
{
    /// <summary>How often the heartbeat fires.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Max delay between failed register/heartbeat retries.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromSeconds(60);

    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly int _port;
    private readonly string _version;
    private readonly HttpClient _http;

    private Timer? _heartbeat;
    private CancellationTokenSource? _cts;
    private bool _registered;
    private bool _disposed;

    // Cached MagicDNS name (resolving it shells the tailscale CLI). Cached once found; while
    // null we retry, so a Director that starts before the tailscale daemon still picks it up.
    private string? _cachedDnsName;

    /// <summary>
    /// Test seam: resolving the MagicDNS name shells the tailscale CLI, which makes the
    /// result environment-dependent. Tests pin this to simulate a node with or without a
    /// tailnet identity; production always uses the real resolver.
    /// </summary>
    internal Func<string?> MagicDnsResolver { get; set; } = TailscaleIdentity.TryGetMagicDnsName;

    public bool IsEnabled => _config.IsEnabled;
    public bool IsRegistered => _registered;

    public GatewayClient(GatewayConfig config, string directorId, int port, string version)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _port = port;
        _version = version ?? "0.0.0";

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (_config.IsEnabled)
        {
            _http.BaseAddress = new Uri(_config.Url.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(_config.Token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
    }

    /// <summary>
    /// Start the registration lifecycle. Fire-and-forget: the first register attempt
    /// runs in the background so a slow or unreachable Gateway never blocks Director
    /// startup. The heartbeat timer is set up regardless.
    /// </summary>
    public void Start()
    {
        if (!_config.IsEnabled)
        {
            FileLog.Write("[GatewayClient] Start: disabled (no gateway.url), running in local-only mode");
            return;
        }
        if (_disposed) throw new ObjectDisposedException(nameof(GatewayClient));

        _cts = new CancellationTokenSource();
        FileLog.Write($"[GatewayClient] Start: gateway={_config.Url}, directorId={_directorId}, port={_port}");

        // Kick off the first registration in the background. RegisterLoop retries on failure.
        _ = Task.Run(() => RegisterLoop(_cts.Token));

        _heartbeat = new Timer(_ => HeartbeatTick(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>
    /// Gracefully unregister. Best-effort: a failing DELETE is logged but does not
    /// throw - the Gateway will sweep the stale entry within 60 s anyway.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        if (!_config.IsEnabled) return;

        FileLog.Write($"[GatewayClient] StopAsync: directorId={_directorId}");
        try { _cts?.Cancel(); } catch { }
        _heartbeat?.Dispose();
        _heartbeat = null;

        if (_registered)
        {
            try
            {
                var resp = await _http.DeleteAsync($"directors/{_directorId}/registration");
                FileLog.Write($"[GatewayClient] DELETE registration -> {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] DELETE registration FAILED (best-effort): {ex.Message}");
            }
        }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _heartbeat?.Dispose();
        _cts?.Dispose();
        _http.Dispose();
    }

    // ===== Internals =====

    private async Task RegisterLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested && !_registered)
        {
            try
            {
                if (await TryRegisterAsync(ct))
                {
                    _registered = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] Register attempt failed: {ex.Message}");
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            // Exponential backoff, capped at MaxBackoff.
            var nextMs = Math.Min(delay.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(nextMs);
        }
    }

    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        var req = BuildRegistrationRequest();
        if (string.IsNullOrWhiteSpace(req.TailnetEndpoint))
        {
            // No tailnet-reachable address to advertise (see ResolveTailnetEndpoint). Registering
            // an empty endpoint would put an undialable entry in the Gateway. Skip and stay local.
            FileLog.Write("[GatewayClient] TryRegisterAsync: no tailnet endpoint to advertise; staying local-only");
            return false;
        }
        FileLog.Write($"[GatewayClient] POST /directors/register: endpoint={req.TailnetEndpoint}");
        var resp = await _http.PostAsJsonAsync("directors/register", req, ct);
        if (resp.IsSuccessStatusCode)
        {
            FileLog.Write($"[GatewayClient] Registered: status={(int)resp.StatusCode}");
            return true;
        }

        FileLog.Write($"[GatewayClient] Register returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return false;
    }

    private void HeartbeatTick()
    {
        if (_disposed || _cts is null || _cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_registered)
                {
                    // Still trying to do the initial registration. Let RegisterLoop handle it.
                    return;
                }

                var resp = await _http.PostAsync($"directors/{_directorId}/heartbeat", content: null, _cts.Token);
                if (resp.StatusCode == HttpStatusCode.Gone)
                {
                    // Gateway forgot about us (it restarted or swept us as stale).
                    // Drop registered=false so the next call to RegisterLoop re-registers.
                    FileLog.Write("[GatewayClient] Heartbeat returned 410 Gone, re-registering");
                    _registered = false;
                    _ = Task.Run(() => RegisterLoop(_cts.Token));
                    return;
                }
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] Heartbeat returned {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] Heartbeat FAILED: {ex.Message}");
            }
        });
    }

    private DirectorRegistrationRequest BuildRegistrationRequest()
    {
        return new DirectorRegistrationRequest
        {
            DirectorId = _directorId,
            TailnetEndpoint = ResolveTailnetEndpoint(),
            Pid = Environment.ProcessId,
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            Version = _version,
            StartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        };
    }

    private string ResolveTailnetEndpoint()
    {
        // The Director binds Kestrel to LOOPBACK only. Remote reachability is provided by the
        // Gateway's TailscaleServeProvisioner, which runs `tailscale serve --https=<port>
        // http://localhost:<port>` per Director. So the address we advertise MUST be that
        // Serve front door: HTTPS, this node's MagicDNS name, and THIS Director's OWN allocated
        // port (e.g. https://<machine>.<tailnet>.ts.net:7879). It is per-Director, tailnet-
        // routable from any node, and is NEVER a localhost URL (a remote Gateway or the Cockpit
        // could never reach loopback). This single value drives both Gateway fan-out reads and
        // the Cockpit's direct terminal WebSocket (wss://<magicdns>:<port>/sessions/{sid}/stream).
        if (_cachedDnsName is null)
            _cachedDnsName = MagicDnsResolver();
        if (!string.IsNullOrWhiteSpace(_cachedDnsName))
            return $"https://{_cachedDnsName}:{_port}";

        // No Tailscale identity on this node. An explicit gateway.tailnetEndpoint override is the
        // only remaining way to be remotely reachable (a hand-run `tailscale serve`, a reverse
        // proxy, etc.). Honored ONLY as this fallback - never ahead of the auto-derived MagicDNS
        // value, so a stale shared override cannot poison a multi-Director box.
        if (!string.IsNullOrWhiteSpace(_config.TailnetEndpoint))
        {
            FileLog.Write("[GatewayClient] ResolveTailnetEndpoint: no Tailscale identity; using configured gateway.tailnetEndpoint override");
            return _config.TailnetEndpoint!;
        }

        // Neither Tailscale nor an override: remote reachability is genuinely unavailable. We do
        // NOT advertise a loopback address (policy is tailnet-or-nothing - a localhost URL would
        // be a lie to any remote caller). Empty endpoint -> TryRegisterAsync skips registration
        // and the Director runs local-only, which is the truthful state.
        FileLog.Write("[GatewayClient] ResolveTailnetEndpoint: no Tailscale identity and no override; cannot advertise a tailnet endpoint, staying local-only");
        return "";
    }
}
