namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One workspace in the <c>workspaces</c> table (issue #2722): a named set of seats, authored by hand or
/// captured from a running Director.
///
/// It lives on the GATEWAY rather than on the machine because it must be readable when that machine is
/// down and editable from another computer - the machine whose fleet it describes is the machine that is
/// about to be restarted.
///
/// The document is stored as JSON in <see cref="DocumentJson"/> rather than spread across columns, and
/// that is a deliberate choice, not a shortcut. A workspace is a RECORD: its shape is the drain index's
/// shape, it grew three whole blocks (the launcher update, the restart mechanism, what actually came
/// back) DURING the first real run, and it will grow again as the remaining phases of issue #2719 learn
/// things. Columns would make each of those a migration; the value of a record is that a later reader
/// gets the whole of what was written, including fields the schema did not anticipate.
///
/// The head columns beside it are a projection FOR LISTING ONLY, derived from the document in exactly one
/// place - the store's upsert - so the two cannot drift apart. Nothing reads them to decide anything.
/// </summary>
public sealed class WorkspaceEntity : TenantScopedEntity
{
    /// <summary>The workspace's slug id ("morning-fleet"), minted by the author and validated in code.
    /// Part of the composite primary key with <c>tenant_id</c>, so two tenants may use the same slug.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name, projected from the document for the list.</summary>
    public string Name { get; set; } = "";

    /// <summary>"authored" or "captured", projected from the document for the list.</summary>
    public string Origin { get; set; } = "";

    /// <summary>The machine a captured workspace came from, projected for the list. Null when authored.</summary>
    public string? Machine { get; set; }

    /// <summary>The Director a captured workspace came from, projected for the list. Null when authored.</summary>
    public string? DirectorId { get; set; }

    /// <summary>The captured Director's display name, projected for the list. Null when authored.</summary>
    public string? DirectorName { get; set; }

    /// <summary>Where the run got to ("draining", "restored", ...), projected for the list. Null when
    /// authored, which is not the record of a run.</summary>
    public string? Outcome { get; set; }

    /// <summary>What the workspace is for, projected for the list.</summary>
    public string? Description { get; set; }

    /// <summary>How many seats the document holds, projected so the list does not parse every document.</summary>
    public int SeatCount { get; set; }

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the row was last written (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>The whole workspace document as JSON. The source of truth; every other column is a
    /// projection of it.</summary>
    public string DocumentJson { get; set; } = "";
}
