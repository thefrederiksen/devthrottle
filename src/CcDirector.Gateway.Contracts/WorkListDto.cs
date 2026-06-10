namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A named work list as it travels over the Gateway REST surface (issue #273, child of #270):
/// a name + an ordered list of structured item refs + the single-consumer claim. This is the
/// object the product skill writes to, the Cockpit views, and the queue runner drains.
///
/// It deliberately carries NO item-status field - the list stores order, the structured refs,
/// and the consumer assignment ONLY. Consumers read item status from the source themselves.
/// </summary>
public sealed class WorkListDto
{
    /// <summary>The list's unique name (its address in the REST surface).</summary>
    public string Name { get; set; } = "";

    /// <summary>The ordered item references. Order is preserved across mixed sources.</summary>
    public List<WorkListItemRef> Items { get; set; } = new();

    /// <summary>
    /// The single active draining consumer's claim token, or null when the list is unclaimed.
    /// A list has at most one active consumer; claiming an already-claimed list is refused.
    /// </summary>
    public string? Consumer { get; set; }
}
