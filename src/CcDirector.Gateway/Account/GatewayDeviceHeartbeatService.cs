using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Sends the Gateway's periodic last-seen heartbeat to the DevThrottle cloud (issue #857) in the
/// BACKGROUND, mirroring the Gateway-owned token refresh (issue #640): a small timer that, on each tick,
/// (1) ensures this Gateway is registered as a device (so a registration that failed on sign-in retries
/// here) and (2) advances its last-seen with <see cref="DeviceRegistryClient.HeartbeatAsync"/>. It NEVER
/// blocks Gateway startup or request handling - <see cref="Start"/> returns immediately, the first sweep
/// runs after a short delay, and each tick is fire-and-forget behind the boundary try/catch the tick
/// owns.
///
/// Graceful degradation (issue #857, consistent with #651/#664): a cloud failure on either the
/// registration retry or the heartbeat only logs and lets the timer continue - the Gateway stays signed
/// in and running, and the next tick retries. When the cloud reports it no longer knows this install (a
/// 404 heartbeat) the local registration is reset so the next tick re-registers.
///
/// Security rule DT-05: the access token and the per-device key are never written to the log - only the
/// outcome (registered / heartbeat advanced / skipped / failed) is.
/// </summary>
public sealed class GatewayDeviceHeartbeatService : IDisposable
{
    /// <summary>How long after <see cref="Start"/> the first heartbeat sweep runs.</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(15);

    /// <summary>How often the heartbeat sweep advances last-seen.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(5);

    private readonly GatewayDeviceRegistrationService _registration;
    private readonly DevThrottleAccountService _account;
    private readonly DeviceRegistryClient _client;
    private readonly string? _appVersion;
    private readonly TimeSpan _startDelay;
    private readonly TimeSpan _sweepInterval;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Creates the background heartbeat service.
    /// </summary>
    /// <param name="registration">The registration coordinator (ensures registration + owns the install id). Required.</param>
    /// <param name="account">The Gateway-hosted credential service the egress token is read from. Required.</param>
    /// <param name="client">The cloud device-registry client (the injectable egress seam). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    /// <param name="startDelay">Delay before the first sweep; defaults to <see cref="DefaultStartDelay"/>. Tests pass a short delay.</param>
    /// <param name="sweepInterval">Interval between sweeps; defaults to <see cref="DefaultSweepInterval"/>. Tests pass a short interval.</param>
    public GatewayDeviceHeartbeatService(
        GatewayDeviceRegistrationService registration,
        DevThrottleAccountService account,
        DeviceRegistryClient client,
        string? appVersion = null,
        TimeSpan? startDelay = null,
        TimeSpan? sweepInterval = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _appVersion = appVersion;
        _startDelay = startDelay ?? DefaultStartDelay;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <summary>
    /// Starts the background heartbeat timer. Returns immediately - the first sweep runs after the start
    /// delay, then every sweep interval - so this never blocks the caller (Gateway startup). A second call
    /// is a no-op.
    /// </summary>
    public void Start()
    {
        FileLog.Write($"[GatewayDeviceHeartbeatService] Start: background device heartbeat every {_sweepInterval.TotalSeconds:0}s (first sweep in {_startDelay.TotalSeconds:0}s)");
        if (_timer is not null)
        {
            FileLog.Write("[GatewayDeviceHeartbeatService] Start: already started -> no-op");
            return;
        }

        _timer = new Timer(_ => Sweep(), null, _startDelay, _sweepInterval);
    }

    /// <summary>
    /// The timer callback (a boundary - it owns the try/catch so a sweep failure never crashes the timer
    /// thread). Fires the heartbeat tick fire-and-forget.
    /// </summary>
    private void Sweep()
    {
        try
        {
            _ = HeartbeatOnceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDeviceHeartbeatService] Sweep FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a single heartbeat tick (a boundary for the fire-and-forget sweep): retry registration if
    /// needed, then advance last-seen. Every cloud failure is contained here and only logged (the Gateway
    /// stays signed in and running; the next tick retries) - it never propagates into the timer thread.
    /// Exposed internally so a test can drive one deterministic tick. Tokens and keys are never logged.
    /// </summary>
    internal async Task HeartbeatOnceAsync()
    {
        try
        {
            // 1. Ensure we are registered. A registration that failed on sign-in (cloud down) retries
            //    here; once a key is stored this is a cheap no-op (the relaunch/in-run guards).
            await _registration.EnsureRegisteredAsync(CancellationToken.None).ConfigureAwait(false);

            var token = _account.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write("[GatewayDeviceHeartbeatService] HeartbeatOnceAsync: Gateway not signed in -> skipping heartbeat");
                return;
            }

            if (!_registration.HasDeviceKey)
            {
                FileLog.Write("[GatewayDeviceHeartbeatService] HeartbeatOnceAsync: no per-device key yet (registration pending) -> skipping heartbeat this tick");
                return;
            }

            var advanced = await _client.HeartbeatAsync(token, _registration.InstallId, _appVersion, CancellationToken.None).ConfigureAwait(false);
            if (advanced)
            {
                FileLog.Write($"[GatewayDeviceHeartbeatService] HeartbeatOnceAsync: heartbeat sent for install_id={_registration.InstallId}; last-seen advanced");
            }
            else
            {
                FileLog.Write($"[GatewayDeviceHeartbeatService] HeartbeatOnceAsync: cloud does not know install_id={_registration.InstallId} (404) -> resetting local registration to re-register next tick");
                _registration.ResetRegistration();
            }
        }
        catch (Exception ex)
        {
            // Graceful degradation (issue #857): a cloud failure must not crash, block, or gate the
            // Gateway. Log it and let the next tick retry - the Gateway stays signed in and running.
            FileLog.Write($"[GatewayDeviceHeartbeatService] HeartbeatOnceAsync: cloud call failed (Gateway stays signed in and running; retry on the next heartbeat): {ex.Message}");
        }
    }

    /// <summary>Stops the background heartbeat timer. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        FileLog.Write("[GatewayDeviceHeartbeatService] Dispose: stopping background device heartbeat");
        _timer?.Dispose();
        _timer = null;
    }
}
