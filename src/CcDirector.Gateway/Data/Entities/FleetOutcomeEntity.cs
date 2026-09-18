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

    /// <summary>The stop the news is about: the id of the Wingman verdict the Fleet Manager filed it from (the event
    /// carries it), set when the record is filed and never after. Null when the record is about no one stop - such a
    /// record is never closed by an answer sent to a session, only by the ordinary record answer (steps 7 to 9, round 2
    /// fixes).</summary>
    public string? AboutVerdictId { get; set; }

    /// <summary>The turn end of that verdict, as the Gateway stored it when the record was filed - with
    /// <see cref="AboutVerdictId"/>, the identity of the stop. Null exactly when <see cref="AboutVerdictId"/> is.</summary>
    public DateTime? AboutTurnEndObservedAtUtc { get; set; }

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

    /// <summary>The Fleet Manager's ONE line of advice for the owner, written when the record is filed and settable
    /// afterwards by the Fleet Manager only (step 7). It says what the Fleet Manager knows and the Wingman does not.
    /// Null when none was written.</summary>
    public string? Advice { get; set; }

    /// <summary>The Fleet Manager's pick: the key of one of the Wingman's options for the record's session, set with
    /// the advice (step 7). Stored as given; the walkthrough fold stamps which current option it names. Null when
    /// none was picked.</summary>
    public string? FleetManagerPick { get; set; }

    /// <summary>When the advice and pick were last written (UTC), or null when never.</summary>
    public DateTime? AdviceSetAtUtc { get; set; }

    /// <summary>What the owner did about this record WITHOUT answering it - today only a snooze from the walkthrough
    /// (step 7), in the Gateway's words, so the Fleet Manager's digest carries it. Null when nothing was done.</summary>
    public string? OwnerNote { get; set; }

    /// <summary>When <see cref="OwnerNote"/> was written (UTC), or null.</summary>
    public DateTime? OwnerNoteAtUtc { get; set; }
}
