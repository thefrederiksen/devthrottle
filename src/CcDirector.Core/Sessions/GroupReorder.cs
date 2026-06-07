namespace CcDirector.Core.Sessions;

/// <summary>
/// The pure list-reorder math behind "a group moves as one unit" (issue #225). Kept out of
/// the desktop drag handler so the invariants - a group never splits, members keep their
/// internal order, and a drop never lands inside another group - are unit-testable without a
/// ListBox. The desktop handler supplies the items, a group-id selector, the dragged index,
/// and the raw drop index (from container geometry); this returns the new order.
/// </summary>
public static class GroupReorder
{
    /// <summary>
    /// Move the BLOCK containing <paramref name="draggedIndex"/> (the whole group when that
    /// item is a member, else just itself) to <paramref name="rawTarget"/>, snapping the
    /// insertion point out of any OTHER group's interior. Returns the reordered items.
    /// Pure - no mutation of the input.
    /// </summary>
    /// <param name="rawTarget">Desired insertion index in 0..Count (insert before the item
    /// currently there; Count = append at the end).</param>
    public static List<T> MoveBlock<T>(IReadOnlyList<T> items, Func<T, Guid?> groupOf, int draggedIndex, int rawTarget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(groupOf);
        if (draggedIndex < 0 || draggedIndex >= items.Count)
            return new List<T>(items);

        // 1. The dragged block: contiguous run sharing the dragged item's group id.
        var gid = groupOf(items[draggedIndex]);
        int start = draggedIndex, end = draggedIndex;
        if (gid is not null)
        {
            while (start > 0 && groupOf(items[start - 1]) == gid) start--;
            while (end < items.Count - 1 && groupOf(items[end + 1]) == gid) end++;
        }

        // 2. Snap the target out of another group's interior (same non-null id on both
        //    sides, and not our own group) -> just past that group's last member.
        int target = Math.Clamp(rawTarget, 0, items.Count);
        if (target > 0 && target < items.Count)
        {
            var here = groupOf(items[target]);
            var prev = groupOf(items[target - 1]);
            if (here is not null && here == prev && here != gid)
            {
                int e2 = target;
                while (e2 < items.Count - 1 && groupOf(items[e2 + 1]) == here) e2++;
                target = e2 + 1;
            }
        }

        // 3. Dropping the block onto itself (target within or immediately after the block) -
        //    no change. Returning a copy keeps callers simple.
        if (target >= start && target <= end + 1)
            return new List<T>(items);

        // 4. Lift the block, reinsert it before the anchor (the item originally at target).
        var anchor = target < items.Count ? items[target] : default;
        var block = new List<T>();
        for (int i = start; i <= end; i++) block.Add(items[i]);
        var rest = new List<T>();
        for (int i = 0; i < items.Count; i++)
            if (i < start || i > end) rest.Add(items[i]);

        int insertAt = anchor is null ? rest.Count : rest.IndexOf(anchor);
        if (insertAt < 0) insertAt = rest.Count;
        rest.InsertRange(insertAt, block);
        return rest;
    }
}
