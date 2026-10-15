namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE STATE A FACTORY CHECK LEFT, in the append-only <c>factory_check_state</c> table (Factory Definition mission,
/// phase 5b, design 1.4). A check from a definition may keep a small file between runs - the last message it saw, the
/// last row it counted - and the Gateway holds it, so a check never depends on one machine's disk.
///
/// KEYED BY THE START, NOT THE MACHINE: <see cref="OwnerKey"/> is <c>trigger:&lt;trigger id&gt;</c> or
/// <c>cron:&lt;schedule id&gt;</c>. A publish keeps a start's row id when the start is unchanged or edited in place,
/// so the state follows the start across versions, and dies with it when the start is removed.
///
/// APPEND-ONLY: every row is kept, and the newest is the state. A row is written only from a check the contract reads
/// as a success, and only when the content differs from the newest row - a failed check's half-written state never
/// becomes the truth. A person's reset appends an empty row with <see cref="Reset"/> set.
/// </summary>
public sealed class FactoryCheckStateEntity : TenantScopedEntity
{
    /// <summary>The factory whose definition holds the check.</summary>
    public string Factory { get; set; } = "";

    /// <summary>Whose state this is: <c>trigger:&lt;id&gt;</c> or <c>cron:&lt;id&gt;</c>.</summary>
    public string OwnerKey { get; set; } = "";

    /// <summary>The row's place in this start's history, counting from 1. Part of the key, so two writers cannot both
    /// add the same row.</summary>
    public int Sequence { get; set; }

    /// <summary>The state file's content, at most 64 KB. Empty on a reset.</summary>
    public string Content { get; set; } = "";

    /// <summary>When the row was written (UTC).</summary>
    public DateTime WrittenUtc { get; set; }

    /// <summary>The Director that reported it, or <c>person &lt;who&gt;</c> for a reset.</summary>
    public string WrittenBy { get; set; } = "";

    /// <summary>The trigger run id or scheduled run id whose report wrote it, or null for a reset.</summary>
    public string? RunRef { get; set; }

    /// <summary>True when a person reset the state.</summary>
    public bool Reset { get; set; }
}
