using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Registers THIS Gateway as a device with the DevThrottle cloud account on sign-in (issue #857): when
/// the Gateway is signed in, it calls the cloud "register this device" endpoint
/// (<see cref="DeviceRegistryClient.RegisterAsync"/>) with the Gateway's stable install identity
/// (<see cref="GatewayInstallId"/>), machine name, and platform, and stores the cloud-issued per-device
/// key locally (<see cref="GatewayDeviceKeyStore"/>). This is what makes "sign into the same account on a
/// new device" join the fleet and populates the account-wide device list.
///
/// Idempotency (issue #857) has two guards:
/// <list type="bullet">
/// <item>An in-run guard: once a registration has succeeded in this process, a second call is a no-op, so
/// the sign-in-completion trigger and the first-heartbeat trigger never register twice in one run.</item>
/// <item>A relaunch guard: if a per-device key is already stored for this install id (a previous run
/// registered it), a fresh process does NOT re-register - it reuses the stored key. Combined with the
/// cloud being idempotent per install id, a relaunch or a second sign-in never creates a duplicate
/// device record.</item>
/// </list>
///
/// Graceful degradation (issue #857, consistent with #651/#664): this service NEVER blocks or gates the
/// Gateway. <see cref="EnsureRegisteredAsync"/> simply returns when the Gateway is not signed in, and lets
/// a cloud failure throw to its caller (the heartbeat tick boundary or the detached sign-in callback),
/// which logs it and retries on the next heartbeat - the Gateway stays signed in and running.
///
/// Security rule DT-05: the per-device key is never written to the log on any path.
/// </summary>
public sealed class GatewayDeviceRegistrationService
{
    /// <summary>The device type this Gateway registers itself as.</summary>
    public const string GatewayDeviceType = "gateway";

    private readonly DevThrottleAccountService _account;
    private readonly DeviceRegistryClient _client;
    private readonly GatewayDeviceKeyStore _keyStore;
    private readonly Func<string> _installIdProvider;
    private readonly string _machineName;
    private readonly string _platform;
    private readonly string? _appVersion;
    private readonly object _gate = new();

    private string? _installId;
    private bool _registeredThisRun;

    /// <summary>
    /// Creates the registration coordinator.
    /// </summary>
    /// <param name="account">The Gateway-hosted credential service the egress token is read from. Required.</param>
    /// <param name="client">The cloud device-registry client (the injectable egress seam). Required.</param>
    /// <param name="keyStore">The local store the issued per-device key is written to. Required.</param>
    /// <param name="machineName">This machine's name, sent as the device name. Required.</param>
    /// <param name="platform">This device's platform string (for example "windows"). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    /// <param name="installIdProvider">
    /// Resolves the stable Gateway install id; defaults to <see cref="GatewayInstallId.LoadOrCreate()"/>.
    /// Tests inject a fixed provider so they never touch the real config root. Resolved lazily on first
    /// use (never at construction) and cached, so constructing this service does no disk I/O.
    /// </param>
    public GatewayDeviceRegistrationService(
        DevThrottleAccountService account,
        DeviceRegistryClient client,
        GatewayDeviceKeyStore keyStore,
        string machineName,
        string platform,
        string? appVersion = null,
        Func<string>? installIdProvider = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _appVersion = appVersion;
        _installIdProvider = installIdProvider ?? GatewayInstallId.LoadOrCreate;
    }

    /// <summary>This Gateway's stable install id, resolved lazily and cached (no disk I/O at construction).</summary>
    public string InstallId
    {
        get
        {
            lock (_gate)
            {
                _installId ??= _installIdProvider();
                return _installId;
            }
        }
    }

    /// <summary>True when a per-device key is stored for this install id (registered, in this or a prior run).</summary>
    public bool HasDeviceKey => _keyStore.HasKeyForInstall(InstallId);

    /// <summary>
    /// Registers this Gateway as a device when needed, exactly once per run. A no-op when: a registration
    /// already succeeded in this run (in-run guard); the Gateway is not signed in (graceful - logs and
    /// returns, never blocks); or a per-device key is already stored for this install id (relaunch guard -
    /// reuses the stored key, no duplicate device). Otherwise it calls the cloud register endpoint and
    /// stores the issued key. Does NOT swallow a cloud failure - the call throws to its boundary caller,
    /// which logs and retries on the next heartbeat (the per-#857 graceful-degradation contract). The
    /// issued key is never logged (DT-05).
    /// </summary>
    public async Task EnsureRegisteredAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_registeredThisRun)
            {
                FileLog.Write("[GatewayDeviceRegistrationService] EnsureRegisteredAsync: already registered this run -> no-op (in-run idempotency guard)");
                return;
            }
        }

        var token = _account.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write("[GatewayDeviceRegistrationService] EnsureRegisteredAsync: Gateway not signed in -> skipping device registration (retry on the next heartbeat)");
            return;
        }

        var installId = InstallId;
        if (_keyStore.HasKeyForInstall(installId))
        {
            lock (_gate) { _registeredThisRun = true; }
            FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: a per-device key is already stored for install_id={installId} -> skipping re-registration (relaunch idempotency guard)");
            return;
        }

        FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: registering this Gateway as a device, install_id={installId}, name={_machineName}, platform={_platform}");
        var request = new CloudDeviceRegistrationRequest(installId, _platform, _machineName, GatewayDeviceType, _appVersion);
        var result = await _client.RegisterAsync(token, request, ct).ConfigureAwait(false);

        _keyStore.Save(installId, result.DeviceKey);
        lock (_gate) { _registeredThisRun = true; }
        FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: registered device id={result.Device.Id} and stored its per-device key (key value not logged)");
    }

    /// <summary>
    /// Discards the local registration state - clears the in-run guard and removes the stored key - so the
    /// next <see cref="EnsureRegisteredAsync"/> re-registers. Used when the cloud reports it no longer knows
    /// this install (a 404 heartbeat).
    /// </summary>
    public void ResetRegistration()
    {
        lock (_gate) { _registeredThisRun = false; }
        _keyStore.Clear();
        FileLog.Write("[GatewayDeviceRegistrationService] ResetRegistration: cleared local registration state (will re-register on the next attempt)");
    }
}
