using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Runs the Gateway-owned token refresh in the BACKGROUND (issue #640, Gateway Centralization Phase 2):
/// a small timer that periodically asks the Gateway-hosted credential service to renew its access token
/// when it has expired. It NEVER blocks Gateway startup or request handling - <see cref="Start"/> returns
/// immediately, the first sweep runs after a short delay, and each sweep is fire-and-forget with the
/// boundary try/catch the timer callback owns so a refresh failure never crashes the timer thread.
///
/// The decision of WHETHER to refresh lives entirely in <see cref="DevThrottleAccountService.RefreshIfNeededAsync"/>:
/// a still-valid access token is a no-op (no exchange, no network call), an expired-but-well-formed token
/// triggers the backend exchange via the configured <see cref="GatewayHttpTokenRefresher"/>, and a
/// tampered token is left alone. When the refresh endpoint is unconfigured or unreachable the service keeps
/// the cached credential and logs the unavailability - never a fallback that hides the failure.
///
/// Security rule DT-05 (carried over from #636/#637): the access and refresh tokens are never written to
/// the log - only the outcome (refreshed / no-op / unavailable) is, all of it inside the account service.
/// </summary>
public sealed class GatewayTokenRefreshService : IDisposable
{
    /// <summary>How long after <see cref="Start"/> the first background refresh sweep runs.</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(10);

    /// <summary>How often the background refresh sweep re-evaluates the cached credential.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(5);

    private readonly DevThrottleAccountService _account;
    private readonly TimeSpan _startDelay;
    private readonly TimeSpan _sweepInterval;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Creates the background refresh service over the Gateway-hosted credential service.
    /// </summary>
    /// <param name="account">The Gateway-hosted DevThrottle credential service whose token is refreshed. Required.</param>
    /// <param name="startDelay">Delay before the first sweep; defaults to <see cref="DefaultStartDelay"/>. Tests pass a short delay.</param>
    /// <param name="sweepInterval">Interval between sweeps; defaults to <see cref="DefaultSweepInterval"/>. Tests pass a short interval.</param>
    public GatewayTokenRefreshService(DevThrottleAccountService account, TimeSpan? startDelay = null, TimeSpan? sweepInterval = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _startDelay = startDelay ?? DefaultStartDelay;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <summary>
    /// Starts the background refresh timer. Returns immediately - the first sweep runs after the start
    /// delay, then every sweep interval - so this never blocks the caller (Gateway startup). A second call
    /// is a no-op (the timer is already running).
    /// </summary>
    public void Start()
    {
        FileLog.Write($"[GatewayTokenRefreshService] Start: background token refresh every {_sweepInterval.TotalSeconds:0}s (first sweep in {_startDelay.TotalSeconds:0}s)");
        if (_timer is not null)
        {
            FileLog.Write("[GatewayTokenRefreshService] Start: already started -> no-op");
            return;
        }

        _timer = new Timer(_ => Sweep(), null, _startDelay, _sweepInterval);
    }

    /// <summary>
    /// The timer callback (a boundary - it owns the try/catch so a refresh failure never crashes the timer
    /// thread). Fires the background refresh fire-and-forget; the decision to actually exchange (and all
    /// token handling) is the account service's, never blocking and never logging the tokens.
    /// </summary>
    private void Sweep()
    {
        try
        {
            _ = RefreshOnceAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTokenRefreshService] Sweep FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a single background refresh pass. Awaits the account service's refresh decision and logs the
    /// outcome; any error is contained here (the sweep is best-effort) so it never propagates into the
    /// timer thread. The tokens are never logged.
    /// </summary>
    private async Task RefreshOnceAsync()
    {
        try
        {
            var refreshed = await _account.RefreshIfNeededAsync(CancellationToken.None).ConfigureAwait(false);
            FileLog.Write(refreshed
                ? "[GatewayTokenRefreshService] RefreshOnceAsync: access token renewed in the background"
                : "[GatewayTokenRefreshService] RefreshOnceAsync: no renewal performed (token still valid, or refresh unavailable -> cached credential kept)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTokenRefreshService] RefreshOnceAsync FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the background refresh timer. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        FileLog.Write("[GatewayTokenRefreshService] Dispose: stopping background token refresh");
        _timer?.Dispose();
        _timer = null;
    }
}
