namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The Director's request to enroll with a Gateway using a pairing code (issue #469).
/// The Director reads the 4-digit code off the Gateway host's local window (Anchor B - the
/// code never crosses the network), then POSTs this to <c>/devices/register</c>. The Gateway
/// verifies the code (matches, not expired, not already used) and, on success, issues a unique
/// per-device key recorded in its device registry.
/// </summary>
public sealed class DeviceRegistrationRequest
{
    /// <summary>The Director's stable device identity (its existing device GUID).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The new machine's name, recorded in the registry and echoed back for confirmation.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The 4-digit pairing code read off the Gateway host's local window.</summary>
    public string PairingCode { get; set; } = "";
}

/// <summary>
/// The Gateway's response to a successful <see cref="DeviceRegistrationRequest"/> (issue #469).
/// Carries the unique per-device key the Director writes to its local credential file. The
/// pairing code is consumed and never returned.
/// </summary>
public sealed class DeviceRegistrationResponse
{
    /// <summary>The unique, individually-revocable per-device key issued by the Gateway.</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>The device id that was registered (echo of the request).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The machine name that was registered (echo of the request).</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The device's status in the registry at issue time (e.g. <c>active</c>).</summary>
    public string Status { get; set; } = "";

    /// <summary>How many devices are registered after this enrollment (host confirmation message).</summary>
    public int DeviceCount { get; set; }
}

/// <summary>
/// One device's public-facing entry in the Gateway device registry (issue #469): the
/// host-readable record used to list registered devices. The per-device key itself is NEVER
/// included - the registry serves identity and status, not the secret.
/// </summary>
public sealed class RegisteredDeviceDto
{
    /// <summary>The device's stable identity.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The device's machine name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>When the per-device key was issued (UTC).</summary>
    public DateTime IssuedAtUtc { get; set; }

    /// <summary>The device's status (e.g. <c>active</c>, <c>revoked</c>).</summary>
    public string Status { get; set; } = "";
}
