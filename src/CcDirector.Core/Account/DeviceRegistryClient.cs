using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// The <see cref="HttpClient"/> is injectable so tests drive these calls against an in-process stub
/// handler (the proof seam for issue #854).
/// </summary>
public sealed class DeviceRegistryClient
{
    /// <summary>The path that lists the signed-in account's active devices.</summary>
    public const string DevicesPath = "/api/v1/devices";

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
        var root = JsonNode.Parse(json) as JsonArray
            ?? throw new InvalidOperationException("devices response was not a JSON array");

        var devices = new List<CloudDeviceRecord>(root.Count);
        foreach (var item in root)
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
