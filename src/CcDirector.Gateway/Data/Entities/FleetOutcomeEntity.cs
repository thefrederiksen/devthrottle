namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One piece of news the Fleet Manager brought the owner - something READY, something FOUND, or something
/// that needs the owner's DECISION - kept until the owner answers it (the Fleet Manager mission, step 3).
///
/// THE RECORD BELONGS TO THE ACCOUNT, NOT TO ONE FLEET MANAGER SESSION. A Fleet Manager is reset, restarted
/// and moved to other computers; a new one must see and answer what the old one filed. So nothing here is
/// keyed by the session that filed it - <see cref="FiledBy"/> is a fact about the record, not its owner.
///
/// The Cockpit draws a card for each record from these fields, never from the model's prose. The fields
/// that differ by kind are held as one serialized JSON document in <see cref="DetailsJson"/> - the shape the
/// turn verdict and session rule tables use for a sub-document - and the store hands them back typed.
/// </summary>
public sealed class FleetOutcomeEntity : GatewayMintedKeyEntity
{
    /// <summary><c>ready</c>, <c>finding</c> or <c>decision</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The session id that filed it, or <c>owner</c> when a person's device filed it.</summary>
    public string FiledBy { get; set; } = "";

    /// <summary>The session the news is about, when there is one.</summary>
    public string? AboutSessionId { get; set; }

    /// <summary>When it was filed (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>One line naming the news.</summary>
    public string Title { get; set; } = "";

    /// <summary>The kind-specific fields, serialized.</summary>
    public string DetailsJson { get; set; } = "";

    /// <summary><c>open</c> until answered, then <c>answered</c>.</summary>
    public string Status { get; set; } = "";

    /// <summary>When the owner answered (UTC), or null while open.</summary>
    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>The owner's answer, exactly as given, or null while open.</summary>
    public string? AnswerText { get; set; }

    /// <summary><c>owner</c> when a person's device answered, or the session id of the Fleet Manager that
    /// relayed the owner's word. Null while open.</summary>
    public string? AnsweredBy { get; set; }

    /// <summary>WHO gave the answer: <c>owner</c> (the owner, through their own signed-in device) or
    /// <c>fleet-manager</c> (the account's Fleet Manager session, relaying the owner's word). Null while open.</summary>
    public string? AnsweredByRole { get; set; }

    /// <summary>On a decision only: whether the answer was exactly one of the offered options. Null on the
    /// other kinds and while open.</summary>
    public bool? AnswerMatchedOption { get; set; }
}
