namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One change to one rule, as stored (<c>session_rule_changes</c>) - Session Rules part 2, phase 2, owner
/// decision D7: "Any agent should be able to change and set up the rules", and every change is recorded with
/// the session and agent that made it.
///
/// THE HISTORY IS APPEND ONLY. A row is added in the SAME save as the change it describes, so a rule that was
/// changed and a history that does not say so cannot both exist. The write gate in <c>GatewayDbContext</c>
/// refuses an update or a delete of a row here, and <c>RuleTableWriteInterceptor</c> refuses one sent as bulk
/// SQL - a record that can be rewritten afterwards is not a record of what happened.
///
/// <see cref="Before"/> and <see cref="After"/> are the rule in the same body shape a change is written in
/// (<c>SessionRuleWire.Body</c>), plus its state, so a reader compares like with like. Before is empty on a
/// create and After is empty on a delete; nothing else may be empty.
/// </summary>
public sealed class SessionRuleChangeEntity : GatewayMintedKeyEntity
{
    /// <summary>The rule that was changed. Kept after the rule is deleted - the history outlives the rule.</summary>
    public Guid RuleId { get; set; }

    /// <summary>What was done: create, change, promote or delete.</summary>
    public string Action { get; set; } = "";

    /// <summary>Who did it, as the request pipeline named them: "session:&lt;id&gt;", "device:&lt;id&gt;", or
    /// the signed-in person's name.</summary>
    public string ChangedBy { get; set; } = "";

    /// <summary>The session whose key made the change, or null when it was not a session.</summary>
    public string? SessionId { get; set; }

    /// <summary>The agent that session runs (ClaudeCode, Codex, ...), as the live roster said at the time of the
    /// change. Null when the caller was not a session.</summary>
    public string? Agent { get; set; }

    /// <summary>When it was done (UTC).</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>The order this change was made in, within the account: one more than the last change recorded.
    /// The history is read in this order, never by <see cref="OccurredUtc"/>, which two changes can share.</summary>
    public long Sequence { get; set; }

    /// <summary>The rule before the change, as JSON. Empty on a create.</summary>
    public string Before { get; set; } = "";

    /// <summary>The rule after the change, as JSON. Empty on a delete.</summary>
    public string After { get; set; } = "";
}
