namespace CcDirector.Cockpit.Services;

/// <summary>
/// The per-item status badge the Cockpit Lists view renders (issue #275). It is DERIVED, never
/// stored: for a github item it is computed live from the issue's GitHub <c>flow:*</c> label (the
/// single source of truth - the work-list object carries no status). Non-github items have no
/// flow label and render as <see cref="Queued"/>.
/// </summary>
public enum WorkItemStatus
{
    /// <summary>No flow label / flow:ready-dev, or a non-github source: waiting to be drained.</summary>
    Queued,

    /// <summary>An implementation loop is actively on this item (flow:ready-qa or transient flow:qa-failed).</summary>
    Running,

    /// <summary>flow:done - the item finished and merged.</summary>
    Done,

    /// <summary>flow:needs-human - the loop escalated; a person must act.</summary>
    NeedsHuman,

    /// <summary>A runner-recorded failure signal (child 3) - the drain failed.</summary>
    Failed,

    /// <summary>
    /// The status could not be derived (e.g. GitHub unreachable, or GITHUB_TOKEN not configured).
    /// Surfaced explicitly so the row never silently shows a wrong "queued" - no-fallback rule.
    /// </summary>
    Unknown,
}

/// <summary>
/// The GitHub-derived view of one work-list item (issue #275): its display title plus the status
/// derived from its <c>flow:*</c> label. For non-github items only <see cref="Status"/> (Queued) is
/// meaningful; <see cref="Title"/> is null and the row shows the bare id.
/// </summary>
public sealed record WorkItemInfo(string? Title, WorkItemStatus Status, string? Detail);
