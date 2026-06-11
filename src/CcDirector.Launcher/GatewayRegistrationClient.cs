using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Launcher;

/// <summary>
/// Registers the cc-launcher with the Gateway's launcher registry (POST /launchers/register)
/// and heartbeats every <see cref="HeartbeatInterval"/> so the Gateway knows this
/// launcher is live. Unregisters on <see cref="StopAsync"/>.
///
/// Issue #331: mirrors the Director's GatewayClient registration pattern, but much simpler:
/// no tailnet endpoint resolution, no two-way verify, no outbox. The launcher's registration
/// payload is just the machine name and the loopback port; the Gateway stores the token for
/// relay calls.
///
/// Inert when no gateway URL is configured (GatewayConfig.IsEnabled == false).
/// </summary>
public sealed class GatewayRegistrationClient : IAsyncDisposable
{
    /// <summary>How often the heartbeat fires.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Max delay between failed register/heartbeat retries.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromSeconds(60);

    private readonly GatewayConfig _config;
    private readonly int _port;
    private readonly string _token;
    private readonly string _version;
    private readonly HttpClient _http;

    private Timer? _heartbeat;
    private CancellationTokenSource? _cts;
    private bool _registered;
    private bool _disposed;

    public GatewayRegistrationClient(GatewayConfig config, int port, string token, string version)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _port = port;
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _version = version;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (!string.IsNullOrWhiteSpace(config.Token))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.Token);
    }

    /// <summary>
    /// Start the registration loop (no-op when the gateway is not configured).
    /// Returns immediately; registration happens in the background.
    /// </summary>
    public void Start()
    {
        if (!_config.IsEnabled)
        {
            FileLog.Write("[GatewayRegistrationClient] Gateway not configured; launcher will not register");
            return;
        }

        FileLog.Write($"[GatewayRegistrationClient] Start: gateway={_config.Url}, port={_port}");
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RegisterLoop(_cts.Token));
    }

    private async Task RegisterLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await TryRegisterAsync(ct))
                {
                    _registered = true;
                    FileLog.Write("[GatewayRegistrationClient] Registered; starting heartbeat");
                    StartHeartbeat();
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayRegistrationClient] RegisterLoop exception: {ex.Message}");
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            if (delay < MaxBackoff)
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }
    }

    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        var req = new LauncherRegistrationRequest
        {
            MachineName = Environment.MachineName,
            // NetworkAddress: supply the hostname so the Gateway can dial this launcher from
            // a DIFFERENT machine over the tailnet.  The Gateway uses this when the relay
            // target is not co-located with it (i.e. the launcher is on a remote machine).
            // Environment.MachineName is the NetBIOS name; Tailscale appends the domain but
            // DNS resolution normally works with just the hostname on the same tailnet.
            NetworkAddress = Environment.MachineName,
            Port = _port,
            Token = _token,
            Pid = Environment.ProcessId,
            Version = _version,
            StartedAt = DateTime.UtcNow,
        };

        try
        {
            var url = _config.Url.TrimEnd('/') + "/launchers/register";
            FileLog.Write($"[GatewayRegistrationClient] TryRegisterAsync: POST {url}");
            var resp = await _http.PostAsJsonAsync(url, req, ct);
            if (resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayRegistrationClient] TryRegisterAsync: registered ok (status={resp.StatusCode})");
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            FileLog.Write($"[GatewayRegistrationClient] TryRegisterAsync: failed status={resp.StatusCode} body={body}");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayRegistrationClient] TryRegisterAsync FAILED: {ex.Message}");
            return false;
        }
    }

    private void StartHeartbeat()
    {
        _heartbeat = new Timer(
            async _ => await SendHeartbeatAsync(),
            null,
            dueTime: HeartbeatInterval,
            period: HeartbeatInterval);
    }

    private async Task SendHeartbeatAsync()
    {
        if (_disposed || _cts is null) return;
        var ct = _cts.Token;

        try
        {
            var url = _config.Url.TrimEnd('/') + $"/launchers/{Uri.EscapeDataString(Environment.MachineName)}/heartbeat";
            var resp = await _http.PostAsync(url, null, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                // 410 = launcher not in registry (Gateway restarted) -> re-register.
                FileLog.Write("[GatewayRegistrationClient] Heartbeat 410 -> re-registering");
                _registered = false;
                _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
                _ = Task.Run(() => RegisterLoop(ct));
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayRegistrationClient] Heartbeat failed: {resp.StatusCode}");
            }
            else
            {
                FileLog.Write($"[GatewayRegistrationClient] Heartbeat ok");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayRegistrationClient] Heartbeat exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a graceful DELETE unregister to the Gateway and stop the heartbeat.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        _disposed = true;
        FileLog.Write("[GatewayRegistrationClient] StopAsync");

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync();
            _heartbeat = null;
        }

        if (_registered && _config.IsEnabled)
        {
            try
            {
                var url = _config.Url.TrimEnd('/') + $"/launchers/{Uri.EscapeDataString(Environment.MachineName)}";
                FileLog.Write($"[GatewayRegistrationClient] Unregistering: DELETE {url}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _http.DeleteAsync(url, cts.Token);
                FileLog.Write("[GatewayRegistrationClient] Unregistered");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayRegistrationClient] Unregister FAILED: {ex.Message}");
            }
        }

        _http.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
