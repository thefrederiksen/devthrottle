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

/// <summary>
/// One secret transfer and its approval, as every surface reads it (the Secret Handoff mission). Never a value and
/// never an envelope. The display strings - <see cref="Summary"/>, <see cref="ReplaceNote"/>, <see cref="StatusText"/>,
/// <see cref="CanAnswer"/> - are folded once on the Gateway; the phone, the Cockpit, the cc-secrets window and the
/// command line show them as given.
/// </summary>
public sealed class SecretTransferDto
{
    public string TransferId { get; set; } = "";
    public string Entry { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string FromMachine { get; set; } = "";
    public string ToMachine { get; set; } = "";
    public bool Replace { get; set; }
    public string? AskedBySessionId { get; set; }
    public string AskedBy { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary><c>waiting</c>, <c>approved</c>, <c>denied</c>, <c>expired</c>, <c>delivered</c> or <c>failed</c>.</summary>
    public string State { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? AnsweredWhere { get; set; }
    public string? AnsweredBy { get; set; }
    public string? ApprovalWords { get; set; }
    public DateTime? AnsweredAtUtc { get; set; }
    public string? Outcome { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>"devlinux from SOREN_NORTH to devthrottle-mac-mini", with "(stored as NAME)" when renamed.</summary>
    public string Summary { get; set; } = "";

    /// <summary>"Replaces the entry already on MACHINE." when the approval replaces one; otherwise null.</summary>
    public string? ReplaceNote { get; set; }

    /// <summary>Where it stands, in one sentence for a person.</summary>
    public string StatusText { get; set; } = "";

    /// <summary>True while it waits for an answer and has not expired - the only time Approve and Deny are offered.</summary>
    public bool CanAnswer { get; set; }
}

/// <summary><c>POST /gateway/secrets/transfers</c>: ask for one entry to move from one machine to another.</summary>
public sealed class SecretTransferCreateRequest
{
    public string Entry { get; set; } = "";

    /// <summary>The name to store it under on the receiving machine; empty means the same name.</summary>
    public string? TargetName { get; set; }
    public string FromMachine { get; set; } = "";
    public string ToMachine { get; set; } = "";
    public bool Replace { get; set; }
    public string? Reason { get; set; }

    /// <summary>A session only: the owner's approval in the session's chat, in his words, verbatim. The transfer is then
    /// born approved, answered "in an agent's chat".</summary>
    public string? OwnerApproved { get; set; }

    /// <summary>A machine's own credential only: <c>window</c> or <c>terminal</c> - the owner clicked Send or Get in the
    /// cc-secrets window, or typed yes in his own terminal, so the transfer is born approved there.</summary>
    public string? ApprovedHere { get; set; }

    /// <summary>A machine's own credential only: the machine the window or terminal runs on, for "You, in the cc-secrets
    /// window on MACHINE".</summary>
    public string? AskedOn { get; set; }

    /// <summary>A machine's own credential, with <see cref="ApprovedHere"/> only: the fingerprint of the receiving
    /// machine's key the owner accepted although it changed since it was pinned.</summary>
    public string? AcceptReceiverFingerprint { get; set; }
}

/// <summary><c>POST /gateway/secrets/transfers/{id}/answer</c>: exactly one of approve or deny.</summary>
public sealed class SecretTransferAnswerRequest
{
    public bool Approve { get; set; }
    public bool Deny { get; set; }

    /// <summary>Where the answer is given. The owner's phone answers <c>phone</c>; his browser <c>cockpit</c> or
    /// <c>badge</c>; a machine's own credential <c>window</c> or <c>terminal</c>; a session is always <c>chat</c>.</summary>
    public string? Where { get; set; }

    /// <summary>A session only, and required to approve: the owner's approval in the session's chat, verbatim.</summary>
    public string? OwnerApproved { get; set; }

    /// <summary>A machine's own credential only: as on <see cref="SecretTransferCreateRequest.AcceptReceiverFingerprint"/>.</summary>
    public string? AcceptReceiverFingerprint { get; set; }
}

public sealed class SecretTransferResponse
{
    public SecretTransferDto Transfer { get; set; } = new();

    /// <summary>What the caller should do next, in a sentence.</summary>
    public string Note { get; set; } = "";
}

public sealed class SecretTransferListResponse
{
    public List<SecretTransferDto> Transfers { get; set; } = new();

    /// <summary>How far back finished transfers are listed, in hours.</summary>
    public int FinishedWithinHours { get; set; }
}
