namespace CcDirector.Avalonia;

/// <summary>
/// THE DRAG RULE: a row reorders among its SIBLINGS and never crosses a parent boundary.
///
/// "Ownership is not arrangement" (the design, 14 September 2026). Dragging is how the user arranges
/// the rail; it is not how a session changes who it answers to. So a top-level row reorders among
/// top-level rows, a child reorders among the children of its own parent, and a drag that would land a
/// row in someone else's crew - or lift a child out of its crew - does NOTHING at all. It does not
/// snap to the nearest legal slot either: a silent relocation the user did not aim at is a worse
/// answer than a drag that visibly did not take.
///
/// Kept pure and out of the drop handler, like <c>GroupReorder</c>, so the invariant is provable
/// without a list box.
/// </summary>
public static class SessionRailDrag
{
    /// <summary>
    /// Where a drop lands in the FULL session list, or null when the drop crosses a parent boundary
    /// and must be refused.
    ///
    /// The answer is an insertion index into <paramref name="sessions"/> - the same shape
    /// <c>GroupReorder.MoveBlock</c> takes - because the rail's drag order IS that list's order: the
    /// roots render in list order and the children render in <c>SortOrder</c>, which the rail stamps
    /// from the list index. Moving the row to sit beside the right sibling therefore reorders the
    /// level it belongs to and leaves every other level alone.
    /// </summary>
    /// <param name="rows">The rows as rendered, from <see cref="SessionRailTree.Project"/>.</param>
    /// <param name="sessions">Every session on this Director, in the rail's drag order.</param>
    /// <param name="dragged">The session being dragged.</param>
    /// <param name="insertRowIndex">
    /// Where the pointer fell, as an insertion index into <paramref name="rows"/>: 0 is above the first
    /// row and <c>rows.Count</c> is below the last.
    /// </param>
    public static int? DropTarget(
        IReadOnlyList<SessionRailRow> rows,
        IReadOnlyList<SessionViewModel> sessions,
        SessionViewModel dragged,
        int insertRowIndex)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(dragged);

        var draggedRow = rows.FirstOrDefault(r => ReferenceEquals(r.Session, dragged));
        if (draggedRow is null) return null;

        var parent = draggedRow.Parent;
        var t = Math.Clamp(insertRowIndex, 0, rows.Count);

        if (!SlotAccepts(rows, t, parent)) return null;

        // Where among its own siblings the pointer asked for this row to go: how many siblings are
        // ABOVE the insertion point. The dragged row itself counts when it is above, which is exactly
        // what an insertion index means.
        var siblings = new List<SessionViewModel>();
        var position = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!ReferenceEquals(rows[i].Parent, parent)) continue;
            if (i < t) position++;
            siblings.Add(rows[i].Session);
        }

        if (siblings.Count == 0) return null;

        if (position < siblings.Count)
        {
            var anchor = siblings[position];
            // Dropped onto itself - a real drop, and the right answer is that nothing moves.
            if (ReferenceEquals(anchor, dragged)) return IndexOf(sessions, dragged);
            var index = IndexOf(sessions, anchor);
            return index < 0 ? null : index;
        }

        // Past the last sibling: land immediately after it.
        var lastIndex = IndexOf(sessions, siblings[^1]);
        return lastIndex < 0 ? null : lastIndex + 1;
    }

    /// <summary>
    /// Whether the slot at <paramref name="t"/> belongs to <paramref name="parent"/>'s level.
    ///
    /// The slot's level is read off the row ABOVE it, because that is what the user sees themselves
    /// dropping beneath. Above the first row the level is the top level. Directly beneath an OPEN crew
    /// row the level is that crew's - the slot is its first child position. Anywhere else it is the
    /// level of the row above.
    ///
    /// THEN THE SCOPES THAT END HERE. A slot at the bottom of a crew is also the slot at the bottom of
    /// that crew's parent, and of the top level, all at once - which is how a top-level row can be
    /// dropped after a whole three-level crew, and how a Manager can be dropped after the last of its
    /// own Workers. A scope ends at this slot only when the row BELOW is shallower than the slot is, so
    /// the walk upward stops at the depth of the next row: between two sessions of one crew, nothing
    /// ends, and a top-level row dropped there is refused rather than quietly relocated.
    ///
    /// None of this ever crosses a boundary. Every level the walk reaches is an ANCESTOR of the slot, so
    /// the row lands among its own siblings or the drag does nothing; no reading of a drop can move a
    /// session into someone else's crew or lift it out of its own.
    /// </summary>
    private static bool SlotAccepts(IReadOnlyList<SessionRailRow> rows, int t, SessionViewModel? parent)
    {
        if (t <= 0) return parent is null;

        var before = rows[t - 1];
        var opensACrew = before.HasCrew && before.IsExpanded;
        var scope = opensACrew ? before.Session : before.Parent;
        var scopeDepth = opensACrew ? before.Depth + 1 : before.Depth;
        var nextDepth = t < rows.Count ? rows[t].Depth : 0;

        while (true)
        {
            if (ReferenceEquals(scope, parent)) return true;
            if (scope is null || scopeDepth <= nextDepth) return false;
            scope = ParentOf(rows, scope);
            scopeDepth--;
        }
    }

    private static SessionViewModel? ParentOf(IReadOnlyList<SessionRailRow> rows, SessionViewModel session) =>
        rows.FirstOrDefault(r => ReferenceEquals(r.Session, session))?.Parent;

    /// <summary>Position of a session in the list BY IDENTITY. Never by equality: the rail holds one view
    /// model per session, and an equality comparer is one more thing that can answer this wrongly.</summary>
    private static int IndexOf(IReadOnlyList<SessionViewModel> sessions, SessionViewModel session)
    {
        for (var i = 0; i < sessions.Count; i++)
            if (ReferenceEquals(sessions[i], session)) return i;
        return -1;
    }
}
