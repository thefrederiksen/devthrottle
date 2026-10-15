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

    /// <summary>When <see cref="LastSessionId"/> was started (UTC), or null. Set with no session while a start is
    /// pending - the lock is taken before the start begins.</summary>
    public DateTime? LastStartedUtc { get; set; }

    /// <summary>The exact name the last start gave its session, stored with the lock when the start begins, or null.
    /// A start whose outcome is not known finds its session by THIS name - never a name worked out again later from
    /// the trigger's name, its factory agent or the account's time zone, any of which the owner can change meanwhile.</summary>
    public string? LastStartName { get; set; }

    /// <summary>When the Gateway last recorded a check of this trigger (UTC), or null when it never has.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>The outcome of the last recorded check, one of <c>TriggerRunOutcome</c>, or null.</summary>
    public string? LastOutcome { get; set; }

    /// <summary>Why the last recorded check failed, on a <c>failed</c> outcome; otherwise null.</summary>
    public string? LastReason { get; set; }

    // ---- Factory Definition, phase 5b: a DEFINITION TRIGGER, written by a publish from an interval start. All four
    // are null on every command trigger, which is how the two kinds are told apart. ----

    /// <summary>The seat of the factory's definition this trigger's start belongs to, or null on a command trigger.</summary>
    public string? Seat { get; set; }

    /// <summary>The published definition version this trigger was written from, or null on a command trigger.</summary>
    public int? DefinitionVersion { get; set; }

    /// <summary>The check's path in the definition (<c>checks/&lt;name&gt;.py</c>). When set, the trigger has no
    /// <see cref="CheckCommand"/>: its check is downloaded from the published version and run outside any checkout.</summary>
    public string? CheckPath { get; set; }

    /// <summary>The cc-secrets entry names the check declares, as a JSON array in declared order, or null.</summary>
    public string? SecretsJson { get; set; }

    /// <summary>True when this trigger is a check from a definition rather than a command a person wrote.</summary>
    public bool IsDefinitionCheck => !string.IsNullOrEmpty(CheckPath);

    /// <summary>The Director that runs this trigger's check now, or empty when none has claimed it.</summary>
    public string ClaimedByDirectorId { get; set; } = "";

    /// <summary>When the claim was last renewed (UTC). A claim not renewed for two intervals lapses.</summary>
    public DateTime? ClaimedUtc { get; set; }
}
