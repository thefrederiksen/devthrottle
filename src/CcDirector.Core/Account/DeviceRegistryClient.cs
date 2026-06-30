using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// One MASKED device record as it lives on the DevThrottle account device registry (cloud contract
/// devthrottle_internal#81/#82). Every field here is safe to surface to a caller: the registry never
/// returns the device's key hash or any raw key, only the prefix/last4 of the key for display. This
/// record carries NO account access or refresh token (security rule DT-05) - it describes a device,
/// not a credential.
/// </summary>
/// <param name="Id">The device's stable identifier (used to revoke it).</param>
/// <param name="Name">The human-readable device name (typically the machine name at registration).</param>
/// <param name="Platform">The operating-system platform string, or null when the cloud omits it.</param>
/// <param name="DeviceType">The device type (for example "gateway" or "phone"), or null when omitted.</param>
/// <param name="AppVersion">The app version last reported by the device, or null when omitted.</param>
/// <param name="KeyPrefix">The masked key prefix for display, or null when omitted.</param>
/// <param name="KeyLast4">The masked key last-four for display, or null when omitted.</param>
/// <param name="CreatedAt">When the device was registered, or null when omitted.</param>
/// <param name="LastSeenAt">When the device was last seen, or null when omitted.</param>
public sealed record CloudDeviceRecord(
    string Id,
    string Name,
    string? Platform,
    string? DeviceType,
    string? AppVersion,
    string? KeyPrefix,
    string? KeyLast4,
    string? CreatedAt,
    string? LastSeenAt);

/// <summary>
/// The request body for registering THIS device with the DevThrottle account (cloud contract
/// devthrottle_internal#81): <c>POST /api/v1/devices/register</c>. The cloud is idempotent per
/// (member, <see cref="InstallId"/>) - re-registering the same install rotates the device key and
/// updates the record rather than creating a second row - so the caller MUST always send the same
/// stable install id to avoid duplicate device records (issue #857).
/// </summary>
/// <param name="InstallId">The Gateway's stable, per-machine install identifier (the idempotency key). Required.</param>
/// <param name="Platform">The operating-system platform string (for example "windows"). Required.</param>
/// <param name="Name">A human-readable device name (typically the machine name), or null to let the cloud default it.</param>
/// <param name="DeviceType">The device type (for example "gateway"), or null when omitted.</param>
/// <param name="AppVersion">The reporting app version, or null when omitted.</param>
public sealed record CloudDeviceRegistrationRequest(
    string InstallId,
    string Platform,
    string? Name,
    string? DeviceType,
    string? AppVersion);

/// <summary>
/// The result of a device registration: the per-device key the cloud issues ONCE (in plain text, only
/// in this response - it is never returned again and is never written to the log, security rule DT-05)
/// plus the masked <see cref="CloudDeviceRecord"/> describing the registered device.
/// </summary>
/// <param name="DeviceKey">The plain per-device key, returned exactly once. Stored locally, never logged.</param>
/// <param name="Device">The masked device record (no raw key) for display and identification.</param>
public sealed record CloudDeviceRegistrationResult(
    string DeviceKey,
    CloudDeviceRecord Device);

/// <summary>
/// A small HTTP client for the DevThrottle account device registry (cloud contract
/// devthrottle_internal#81/#82). It lists the signed-in account's active devices with
/// <c>GET /api/v1/devices</c> and revokes one with <c>DELETE /api/v1/devices/{id}</c>, both authenticated
/// with the Bearer access token the Gateway already holds for cloud egress (the same credential
/// <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns for telemetry forwarding).
///
/// The endpoint base is resolved from <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> when set (so
/// development and QA can point at a local stub), otherwise the documented production default
/// <see cref="AccountTelemetryClient.DefaultApiBaseUrl"/> - the SAME cloud base the Gateway already
/// targets for the rest of its account egress, so this client introduces no new hard-coded URL.
///
/// The access token is sent only as the Authorization header and is NEVER written to the log (security
/// rule DT-05): this client logs only the request shape and the response outcome, never the token. The
/// returned <see cref="CloudDeviceRecord"/> values are masked by the cloud and carry no token either.
///
/// It also registers THIS device with <c>POST /api/v1/devices/register</c> and advances its last-seen
/// with <c>POST /api/v1/devices/heartbeat</c> (cloud contract devthrottle_internal#81/#83), the egress
/// behind the Gateway's "sign in = register this device" flow (issue #857).
///
/// The <see cref="HttpClient"/> is injectable so tests drive these calls against an in-process stub
/// handler (the proof seam for issues #854 / #857).
/// </summary>
public sealed class DeviceRegistryClient
{
    /// <summary>The path that lists the signed-in account's active devices.</summary>
    public const string DevicesPath = "/api/v1/devices";

    /// <summary>The path that registers (or re-registers, idempotent per install id) this device.</summary>
    public const string RegisterPath = "/api/v1/devices/register";

    /// <summary>The path that advances this device's last-seen timestamp.</summary>
    public const string HeartbeatPath = "/api/v1/devices/heartbeat";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates the client. <paramref name="client"/> defaults to a short-timeout
    /// <see cref="HttpClient"/>; tests inject one over a stub handler. <paramref name="baseUrl"/>
    /// defaults to the <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> override when set, otherwise
    /// the production default - the same resolution the rest of the account egress uses.
    /// </summary>
    public DeviceRegistryClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _baseUrl = ResolveBaseUrl(baseUrl);
    }

    /// <summary>
    /// Resolves the API base URL: the explicit <paramref name="baseUrl"/> argument when given
    /// (trimmed, non-empty), otherwise the <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/>
    /// environment override, otherwise the production default. The trailing slash is removed so the path
    /// concatenation never double-slashes.
    /// </summary>
    private static string ResolveBaseUrl(string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl.Trim().TrimEnd('/');

        var fromEnv = Environment.GetEnvironmentVariable(AccountTelemetryClient.ApiBaseUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim().TrimEnd('/');

        return AccountTelemetryClient.DefaultApiBaseUrl;
    }

    /// <summary>
    /// Lists the signed-in account's active devices via <c>GET /api/v1/devices</c>. Throws on a
    /// non-success response (so an unreachable or erroring cloud surfaces as a clear failure the caller
    /// reports, never a fabricated empty list) or a malformed body. The returned records are the cloud's
    /// masked shape; no token is ever returned or logged.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<IReadOnlyList<CloudDeviceRecord>> ListDevicesAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{DevicesPath}";
        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // The cloud wraps the list under a top-level "data" envelope (devthrottle_internal#82,
        // website/api/v1/devices.js: `json({ data: (data || []).map(toRecord) })`), so the array is
        // read from data, never the root.
        var data = DataArray(json, "devices");

        var devices = new List<CloudDeviceRecord>(data.Count);
        foreach (var item in data)
        {
            if (item is not JsonObject obj)
                throw new InvalidOperationException("devices response contained a non-object entry");
            devices.Add(ParseRecord(obj));
        }

        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: parsed {devices.Count} device(s)");
        return devices;
    }

    /// <summary>
    /// Revokes one device via <c>DELETE /api/v1/devices/{id}</c>. Returns true when the cloud revoked it
    /// (200), false when the id is not the caller's device (404). Throws on any other non-success status
    /// (so an unreachable or erroring cloud surfaces as a clear failure, never a silent success).
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="id">The device id to revoke.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<bool> RevokeDeviceAsync(string accessToken, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Device id is required", nameof(id));

        var endpoint = $"{_baseUrl}{DevicesPath}/{Uri.EscapeDataString(id)}";
        FileLog.Write($"[DeviceRegistryClient] RevokeDeviceAsync: DELETE {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] RevokeDeviceAsync: id={id}, response status={(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Registers (or re-registers) THIS device with <c>POST /api/v1/devices/register</c> and returns the
    /// per-device key the cloud issues once, plus the masked device record. The cloud is idempotent per
    /// (member, install id): re-registering the same <see cref="CloudDeviceRegistrationRequest.InstallId"/>
    /// rotates the key and updates the record rather than creating a duplicate device (issue #857), so the
    /// caller must always send the same stable install id. Throws on a non-success response or a malformed
    /// body (so an unreachable or erroring cloud surfaces as a clear failure the caller handles, never a
    /// fabricated success). The plain key in the result is never written to the log (security rule DT-05):
    /// this method logs only the request shape and the registered device id.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="request">The registration request (install id, platform, optional name/type/version).</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<CloudDeviceRegistrationResult> RegisterAsync(string accessToken, CloudDeviceRegistrationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.InstallId))
            throw new ArgumentException("Install id is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Platform))
            throw new ArgumentException("Platform is required", nameof(request));

        var endpoint = $"{_baseUrl}{RegisterPath}";
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: POST {endpoint}, install_id={request.InstallId}, platform={request.Platform}");

        var body = new JsonObject
        {
            ["install_id"] = request.InstallId,
            ["platform"] = request.Platform,
        };
        if (!string.IsNullOrWhiteSpace(request.Name))
            body["name"] = request.Name;
        if (!string.IsNullOrWhiteSpace(request.DeviceType))
            body["device_type"] = request.DeviceType;
        if (!string.IsNullOrWhiteSpace(request.AppVersion))
            body["app_version"] = request.AppVersion;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // The cloud wraps the issued key and the masked record under a top-level "data" envelope
        // (devthrottle_internal#81, website/api/v1/devices.js:
        // `json({ data: { device_key: key.raw, record: toRecord(...) } })`), so both are read from
        // data, never the root.
        var data = DataObject(json, "device register");

        var deviceKey = StringField(data, "device_key")
            ?? throw new InvalidOperationException("device register response had no string 'data.device_key'");
        var record = data["record"] as JsonObject
            ?? throw new InvalidOperationException("device register response had no object 'data.record'");
        var device = ParseRecord(record);

        // DT-05: log the registered device id only - NEVER the issued key.
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: registered device id={device.Id} (per-device key received, not logged)");
        return new CloudDeviceRegistrationResult(deviceKey, device);
    }

    /// <summary>
    /// Advances this device's last-seen with <c>POST /api/v1/devices/heartbeat</c>. Returns true when the
    /// cloud advanced last-seen (200), false when it does not know this install id (404) - the signal that
    /// the device must be (re-)registered. Throws on any other non-success status (so an unreachable or
    /// erroring cloud surfaces as a clear failure the best-effort caller logs, never a silent success).
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="installId">This device's stable install id (identifies the row to advance). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<bool> HeartbeatAsync(string accessToken, string installId, string? appVersion = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(installId))
            throw new ArgumentException("Install id is required", nameof(installId));

        var endpoint = $"{_baseUrl}{HeartbeatPath}";
        FileLog.Write($"[DeviceRegistryClient] HeartbeatAsync: POST {endpoint}, install_id={installId}");

        var body = new JsonObject { ["install_id"] = installId };
        if (!string.IsNullOrWhiteSpace(appVersion))
            body["app_version"] = appVersion;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] HeartbeatAsync: install_id={installId}, response status={(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Unwraps the cloud's <c>{ "data": { ... } }</c> envelope and returns the inner object. Every device
    /// endpoint in the cloud contract (devthrottle_internal#81/#82/#83, website/api/v1/devices.js) wraps
    /// its success payload under a single top-level "data" key, so callers parse from this inner object,
    /// never the raw root. Throws when the body is not a JSON object or carries no "data" object (so a
    /// malformed or contract-violating response surfaces as a clear failure, never a silent misparse).
    /// </summary>
    private static JsonObject DataObject(string json, string what)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"{what} response was not a JSON object");
        return root["data"] as JsonObject
            ?? throw new InvalidOperationException($"{what} response had no object 'data' envelope");
    }

    /// <summary>
    /// Unwraps the cloud's <c>{ "data": [ ... ] }</c> envelope and returns the inner array. The list
    /// endpoint (devthrottle_internal#82) returns its records under the same top-level "data" key as the
    /// object-returning endpoints. Throws when the body is not a JSON object or carries no "data" array.
    /// </summary>
    private static JsonArray DataArray(string json, string what)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"{what} response was not a JSON object");
        return root["data"] as JsonArray
            ?? throw new InvalidOperationException($"{what} response had no array 'data' envelope");
    }

    /// <summary>
    /// Parses one masked device record from the cloud response object. The id and name are required; the
    /// remaining display fields are optional and read as null when absent. No token field exists in this
    /// shape, so none can be parsed (security rule DT-05).
    /// </summary>
    private static CloudDeviceRecord ParseRecord(JsonObject obj)
    {
        var id = StringField(obj, "id")
            ?? throw new InvalidOperationException("device record had no string 'id'");
        var name = StringField(obj, "name")
            ?? throw new InvalidOperationException($"device record '{id}' had no string 'name'");

        return new CloudDeviceRecord(
            id,
            name,
            StringField(obj, "platform"),
            StringField(obj, "device_type"),
            StringField(obj, "app_version"),
            StringField(obj, "key_prefix"),
            StringField(obj, "key_last4"),
            StringField(obj, "created_at"),
            StringField(obj, "last_seen_at"));
    }

    /// <summary>Reads a string field from the object, or null when absent or not a string value.</summary>
    private static string? StringField(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return null;
    }
}
