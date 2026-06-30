using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One device in the Cockpit-facing account device list (issue #854). The Gateway maps each masked cloud
/// record (cloud contract devthrottle_internal#81/#82) to this shape and serves it from
/// <c>GET /account/devices</c>, so the Cockpit Account page can list the account's devices with a
/// last-seen time and a per-device revoke - without ever holding the account token or calling the cloud.
///
/// Security (carries DT-05): this contract intentionally carries NO access- or refresh-token field. Every
/// field is a display value already masked by the cloud (the key prefix/last-four, never a raw key), so
/// the Cockpit can never receive or display a credential.
/// </summary>
public sealed class AccountDeviceDto
{
    /// <summary>The device's stable identifier, used as the path id for revoke.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The human-readable device name (typically the machine name at registration).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The operating-system platform string, or null when the cloud omits it.</summary>
    public string? Platform { get; set; }

    /// <summary>The device type (for example "gateway" or "phone"), or null when omitted.</summary>
    public string? DeviceType { get; set; }

    /// <summary>The app version last reported by the device, or null when omitted.</summary>
    public string? AppVersion { get; set; }

    /// <summary>The masked key prefix for display, or null when omitted.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>The masked key last-four for display, or null when omitted.</summary>
    public string? KeyLast4 { get; set; }

    /// <summary>When the device was registered (cloud timestamp), or null when omitted.</summary>
    public string? CreatedAt { get; set; }

    /// <summary>When the device was last seen (cloud timestamp), or null when omitted.</summary>
    public string? LastSeenAt { get; set; }

    /// <summary>
    /// True when this record is the Gateway's own machine, so the Cockpit can mark it "This device".
    /// Computed locally by matching the record name to this host's machine name.
    /// </summary>
    public bool ThisDevice { get; set; }
}

/// <summary>
/// The body of <c>GET /account/devices</c> (issue #854). When the Gateway holds a valid account
/// credential, <see cref="SignedIn"/> is true and <see cref="Devices"/> carries the account's devices
/// (possibly an empty list when the account has none). When the Gateway holds no credential,
/// <see cref="SignedIn"/> is false and <see cref="Devices"/> is OMITTED (null) - an explicit signed-out
/// envelope, never a fabricated empty 200 list.
///
/// Security (carries DT-05): this contract carries no token field, so the Cockpit-facing response can
/// never include the account access or refresh token.
/// </summary>
public sealed class AccountDevicesResponseDto
{
    /// <summary>Whether the Gateway holds a valid DevThrottle account credential.</summary>
    public bool SignedIn { get; set; }

    /// <summary>
    /// The account's devices when signed in (may be empty); omitted entirely when not signed in so the
    /// signed-out response is unmistakably distinct from a signed-in account with zero devices.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AccountDeviceDto>? Devices { get; set; }
}

/// <summary>
/// The body of <c>DELETE /account/devices/{id}</c> (issue #854). When signed in, <see cref="Revoked"/>
/// reports whether the cloud revoked the device (the id no longer appears in a subsequent list). When the
/// Gateway holds no credential, <see cref="SignedIn"/> is false and <see cref="Revoked"/> is false - an
/// explicit signed-out result, never a silent success.
///
/// Security (carries DT-05): this contract carries no token field.
/// </summary>
public sealed class RevokeDeviceResponseDto
{
    /// <summary>Whether the Gateway holds a valid DevThrottle account credential.</summary>
    public bool SignedIn { get; set; }

    /// <summary>The device id the revoke targeted.</summary>
    public string? Id { get; set; }

    /// <summary>Whether the device was revoked (false when signed out, or the id was not the account's).</summary>
    public bool Revoked { get; set; }
}
