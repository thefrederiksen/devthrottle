namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A co-located Director's request to enroll with its own Gateway using the DevThrottle account
/// sign-in instead of a pairing code (issue #1069). The Director POSTs this to
/// <c>/devices/enroll-signed-in</c>; the Gateway issues the Director's own per-device key - a fresh one
/// if this device is already enrolled, since the registry keeps only a hash of the key it issued and so
/// has no plaintext to hand back (issue #1878) - gated on the Gateway being signed in to DevThrottle AND the
/// caller being a loopback same-machine connection. There is no pairing code: signing in is the
/// authorization. Carries no credential of its own; the loopback origin plus the Gateway's signed-in
/// account are the proof.
/// </summary>
public sealed class EnrollSignedInRequest
{
    /// <summary>The Director's stable device identity (its existing device GUID).</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The Director's machine name, recorded in the registry and echoed back for confirmation.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The operating-system platform string (for example "windows"), for the cloud mirror roster.</summary>
    public string Platform { get; set; } = "";

    /// <summary>The device type - defaults to "workstation" - for the cloud mirror roster.</summary>
    public string DeviceType { get; set; } = "";

    /// <summary>
    /// HOSTED ONLY (devthrottle_internal#2311): the team this Director is set up for, one of the teams
    /// <c>GET /devices/enroll-hosted/teams</c> offered. Null or absent sets it up for the person's own account,
    /// exactly as before teams existed. Refused by a Gateway on which Teams is not released.
    /// </summary>
    public string? TeamId { get; set; }
}

/// <summary>
/// The answer to <c>GET /devices/enroll-hosted/teams</c> (devthrottle_internal#2311): the teams the signed-in person may
/// set a Director up for - every team where they may run sessions on their own computers. Never a Collaborator's team,
/// and never the person's own account, which the Director always offers itself. Empty for a person in no such team.
/// </summary>
public sealed class EnrollHostedTeamsResponse
{
    public List<EnrollHostedTeam> Teams { get; set; } = new();
}

/// <summary>One team a Director can be set up for.</summary>
public sealed class EnrollHostedTeam
{
    /// <summary>The team's id: what <see cref="EnrollSignedInRequest.TeamId"/> and <see cref="MoveDirectorRequest.TeamId"/> take.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The team's name, as its members see it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The person's role in the team, as stored: <c>owner</c>, <c>manager</c> or <c>developer</c>.</summary>
    public string Role { get; set; } = "";

    /// <summary>How many members the team has, the person included.</summary>
    public int MemberCount { get; set; }
}

/// <summary>
/// HOSTED ONLY (devthrottle_internal#2311): move one enrolled Director to another team, or back to the person's own
/// account. Posted to <c>/devices/enroll-hosted/move</c> with the person's account token as the bearer, like
/// enrollment. The Director is named by the device key it holds now; on success that key is revoked and a new one,
/// bound to the new team, is returned in a <see cref="DeviceRegistrationResponse"/>.
/// </summary>
public sealed class MoveDirectorRequest
{
    /// <summary>The device key the Director holds now.</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>The team to move to; null or absent moves it to the person's own account.</summary>
    public string? TeamId { get; set; }
}

/// <summary>
/// The Gateway's response to a successful device enrollment. Carries the unique per-device key the
/// enrolling device writes to its local credential file. Shared by every enrollment path: the
/// co-located Director's <see cref="EnrollSignedInRequest"/>, and the mobile/browser flows.
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
/// included - the registry serves identity and status, not the secret. It DOES carry a NON-SECRET
/// masked key identity (<see cref="KeyPrefix"/> / <see cref="KeyLast4"/>, issue #1899) so a listing can
/// tell devices apart by key without ever substituting or exposing the raw key.
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

    /// <summary>
    /// The NON-SECRET first few characters of the device's key (issue #1899) - masked display metadata so a
    /// host can recognise which key a device holds without the registry ever returning the raw key. Reveals a
    /// handful of a 256-bit key and is not a credential. Empty for a record with no recorded key identity.
    /// </summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>The NON-SECRET last few characters of the device's key (issue #1899), the trailing half of the
    /// masked key identity. Not a credential.</summary>
    public string KeyLast4 { get; set; } = "";
}
