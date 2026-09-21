namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One row of the factory activity record, in the <c>factory_activity</c> table (Website Business Factory,
/// product track). What a factory agent did, and how it came out: a trigger check that found nothing to do, a
/// rule that blocked an action, a business action carried out.
///
/// APPEND-ONLY AND NEVER PRUNED. The record is the owner's answer to "what did the factory do while I was not
/// looking", so it is written once and never changed, and no retention sweep is registered for it. A wrong
/// row is answered by a NEW row whose <see cref="CorrectsId"/> points at it - the old row stays exactly as it
/// was written. This is why it is its own table rather than a use of <c>session_history</c> (pruned after 90
/// days, one row per session), <c>activity_events</c> (pruned after 30 days) or
/// <c>governance_audit_events</c> (every row must belong to a session, and a trigger check has none).
///
/// No secret and no copy: <see cref="What"/> is one capped sentence and <see cref="Link"/> points at the
/// evidence; the row never carries an email's text.
/// </summary>
public sealed class FactoryActivityEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory the row belongs to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The factory agent that acted.</summary>
    public string FactoryAgent { get; set; } = "";

    /// <summary>The factory agent definition's version, once definitions exist; null until then.</summary>
    public string? FactoryAgentVersion { get; set; }

    /// <summary>The session that did the work, when there was one (a value reference, never a foreign key -
    /// the record outlives the session).</summary>
    public string? SessionId { get; set; }

    /// <summary>What happened, in one plain sentence, at most 500 characters.</summary>
    public string What { get; set; } = "";

    /// <summary>One of the closed list in <c>FactoryActivityOutcome</c>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>What the row is about - for example the business name.</summary>
    public string? Subject { get; set; }

    /// <summary>A link to the evidence.</summary>
    public string? Link { get; set; }

    /// <summary>Who acted: the caller's own word, or the calling session the Gateway stamped.</summary>
    public string Actor { get; set; } = "";

    /// <summary>The row this one corrects, when it is a correction.</summary>
    public Guid? CorrectsId { get; set; }

    /// <summary>When it happened. Caller-supplied; defaults to the append time.</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When the Gateway appended the row (server-stamped).</summary>
    public DateTime RecordedUtc { get; set; }
}
