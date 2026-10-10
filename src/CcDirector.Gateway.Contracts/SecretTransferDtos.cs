namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The Secret Handoff mission: one machine that can receive a secret, as <c>GET /gateway/secrets/machines</c> lists it.
/// Built from what the account's CONNECTED Directors said about themselves on Hello - a machine with no connected
/// Director that can take part is not listed, because nothing there could open an envelope. Public keys only.
/// </summary>
public sealed class SecretMachineDto
{
    /// <summary>The machine's name as its Directors report it (the name the fleet shows).</summary>
    public string Machine { get; set; } = "";

    /// <summary>The machine's public key (X25519, 32 bytes, base64).</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The key's fingerprint: lower-case hex of the SHA-256 of the 32 key bytes. cc-secrets computes the
    /// same value from the key itself, and pins it, so a key the Gateway swapped is noticed.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>When a Director on this machine last said this key (UTC).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>How many connected Directors on this machine can take part.</summary>
    public int Directors { get; set; }

    /// <summary>Set when two connected Directors on this machine report DIFFERENT keys (two operating-system users
    /// on one machine, or a key replaced while an old Director still runs). Such a machine cannot receive until only
    /// one key remains; <see cref="PublicKey"/> and <see cref="Fingerprint"/> are then empty, and this says why.</summary>
    public string? Conflict { get; set; }
}

/// <summary>The answer of <c>GET /gateway/secrets/machines</c>.</summary>
public sealed class SecretMachineListResponse
{
    public List<SecretMachineDto> Machines { get; set; } = new();
}
