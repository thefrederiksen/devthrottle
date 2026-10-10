namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE SECRET TRANSFER and the owner's approval of it (the Secret Handoff mission, issue #2943), in the
/// <c>secret_transfers</c> table. This row IS the Gateway's audit trail of a transfer: what was asked, by whom, why,
/// who answered and where, and how it ended.
///
/// NEVER A VALUE, NEVER AN ENVELOPE. The secret travels sealed to the receiving machine's key, down the Directors'
/// own tunnels, held by the Gateway in memory only while it is being delivered. Nothing on this row could help
/// anyone read it: entry names, machine names, a reason, the approval words, an outcome sentence.
/// </summary>
public sealed class SecretTransferEntity : GatewayMintedKeyEntity
{
    /// <summary>The transfer's id, minted by the Gateway: 32 lower-case hex characters. It is bound into the sealed
    /// envelope, and the receiving machine accepts one envelope per id.</summary>
    public string TransferId { get; set; } = "";

    /// <summary>The entry's name on the machine that holds it.</summary>
    public string EntryName { get; set; } = "";

    /// <summary>The name it is stored under on the receiving machine (the same as <see cref="EntryName"/> unless the
    /// asker gave another with --as).</summary>
    public string TargetName { get; set; } = "";

    /// <summary>The machine that holds the entry.</summary>
    public string FromMachine { get; set; } = "";

    /// <summary>The machine that receives it.</summary>
    public string ToMachine { get; set; } = "";

    /// <summary>The approval says the entry REPLACES one already on the receiving machine. Without it, a receiving
    /// machine that already holds the name refuses, with the reason.</summary>
    public bool Replace { get; set; }

    /// <summary>The asking session's id, or null when the owner asked in the cc-secrets window or a terminal.</summary>
    public string? AskedBySessionId { get; set; }

    /// <summary>Who asked, in the words the owner reads: <c>Session 104 "name"</c>, or <c>You, in the cc-secrets window
    /// on SOREN_NORTH</c>.</summary>
    public string AskedBy { get; set; } = "";

    /// <summary>Why it is needed, as the asker wrote it.</summary>
    public string Reason { get; set; } = "";

    /// <summary><c>waiting</c>, <c>approved</c>, <c>denied</c>, <c>expired</c>, <c>delivered</c> or <c>failed</c>
    /// (<c>Secrets.SecretTransferStates</c>).</summary>
    public string State { get; set; } = "";

    /// <summary>When it was asked (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When a waiting approval expires (UTC): fifteen minutes after it was asked.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Where the answer was given: <c>phone</c>, <c>cockpit</c>, <c>badge</c>, <c>window</c>, <c>terminal</c> or
    /// <c>chat</c> (<c>Secrets.SecretTransferPlaces</c>); null while it waits.</summary>
    public string? AnsweredWhere { get; set; }

    /// <summary>Which credential answered - the owner's device, the machine, or the session that reported the owner's
    /// words; null while it waits.</summary>
    public string? AnsweredBy { get; set; }

    /// <summary>For an answer given in an agent's chat: the owner's words, verbatim, as the agent reported them.</summary>
    public string? ApprovalWords { get; set; }

    /// <summary>When it was answered (UTC), or null.</summary>
    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>The fingerprint of the receiving machine's key the owner accepted although it CHANGED since it was
    /// pinned (from the cc-secrets window or the owner's terminal only); null otherwise.</summary>
    public string? AcceptedReceiverFingerprint { get; set; }

    /// <summary>How it ended, in a sentence: "Stored on devthrottle-mac-mini.", or the reason it was not. Null until it
    /// ends.</summary>
    public string? Outcome { get; set; }

    /// <summary>When it ended (UTC), or null.</summary>
    public DateTime? FinishedAtUtc { get; set; }
}
