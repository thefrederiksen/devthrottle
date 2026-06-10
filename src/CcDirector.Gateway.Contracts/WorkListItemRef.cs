namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A structured reference to one work item in a named work list (issue #273, child of #270).
/// The store keeps these in order; it never interprets or stores item status - consumers read
/// status from the source themselves (e.g. GitHub labels).
///
/// Item-ref JSON shape (v1): <c>{ "source": "github", "id": "262", "area": "Gateway" }</c>.
/// </summary>
public sealed class WorkListItemRef
{
    /// <summary>
    /// The source system the item lives in. v1 enum: <c>github</c> (the only RUNNABLE source -
    /// the queue runner in #274 drives only github items), plus <c>devops</c> and <c>jira</c>
    /// which are accepted, stored, ordered, and displayed but not yet drained. The store does not
    /// reject any source; runnability is the queue runner's concern, not the store's.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The item identifier WITHIN its source. A string (not an int) so it holds non-numeric keys
    /// such as a Jira key (e.g. <c>"CCD-44"</c>) as well as numeric ones (e.g. <c>"262"</c>).
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Optional free-text grouping label for display (e.g. Gateway / Core / Installer / Cockpit).
    /// Pure display metadata; the store does not interpret it.
    /// </summary>
    public string? Area { get; set; }
}
