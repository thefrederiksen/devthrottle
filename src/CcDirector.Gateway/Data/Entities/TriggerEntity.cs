namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One trigger (the Website Business Factory mission, product track): a check with no model in it that a
/// Director runs on an interval, and that has the Gateway start a named session only when the check counts
/// work. One row per trigger per account, in the <c>triggers</c> table. The name is unique in the account.
///
/// Besides the definition, the row carries the three pieces of state the decision needs: the session this
/// trigger last started (the one-at-a-time lock lasts until THAT session has ended), and which Director holds
/// the check (<see cref="ClaimedByDirectorId"/>), so that several Directors on one machine never all run it.
/// </summary>
public sealed class TriggerEntity : GatewayMintedKeyEntity
{
    public string Name { get; set; } = "";
    public string Factory { get; set; } = "";
    public string FactoryAgent { get; set; } = "";
    public string Machine { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string CheckCommand { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public string Prompt { get; set; } = "";
    public bool Paused { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>The session this trigger last started, or null. The lock: no second start while it lives.</summary>
    public string? LastSessionId { get; set; }

    /// <summary>When <see cref="LastSessionId"/> was started (UTC), or null.</summary>
    public DateTime? LastStartedUtc { get; set; }

    /// <summary>When the Gateway last recorded a check of this trigger (UTC), or null when it never has.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>The outcome of the last recorded check, one of <c>TriggerRunOutcome</c>, or null.</summary>
    public string? LastOutcome { get; set; }

    /// <summary>Why the last recorded check failed, on a <c>failed</c> outcome; otherwise null.</summary>
    public string? LastReason { get; set; }

    /// <summary>The Director that runs this trigger's check now, or empty when none has claimed it.</summary>
    public string ClaimedByDirectorId { get; set; } = "";

    /// <summary>When the claim was last renewed (UTC). A claim not renewed for two intervals lapses.</summary>
    public DateTime? ClaimedUtc { get; set; }
}
