using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>Which position the rail's two-position order switch is on.</summary>
public enum SessionRailOrder
{
    /// <summary>The drag order, as the rail has always opened. The Director's default.</summary>
    MyOrder,

    /// <summary>The Gateway's attention sections - needs you longest first, then working, then snoozed.</summary>
    Attention,
}

/// <summary>
/// ONE ROW OF THE RAIL. The list box is still a FLAT list - that is deliberate, so its selection, its
/// containers and its drag handle keep working exactly as they do today - and the tree is carried by
/// <see cref="Depth"/> and <see cref="Parent"/> on each row.
/// </summary>
/// <param name="Session">The session this row draws. Every session in the list appears in at most one row.</param>
/// <param name="Parent">
/// The session this one is DIRECTLY under, or null at the top level. This is the whole of the
/// drag rule: a row may only be reordered among rows with the same <see cref="Parent"/>.
/// </param>
/// <param name="Depth">0 at the top level, 1 for a child, 2 for a grandchild, and so on with no limit.</param>
/// <param name="HasCrew">True when this session supervises at least one other session in the list.</param>
/// <param name="IsExpanded">True when this crew is open, so its children are rows of their own below it.</param>
/// <param name="Crew">
/// EVERY session under this one, at EVERY level, depth-first in the crew's own order - not just the
/// direct children. This is the strip of small squares on a collapsed crew row, so collapsing a crew
/// hides no colour, and it is the same list the counts are taken from.
/// </param>
/// <param name="CrewLine">The crew line's words, straight from the shared fold. Empty when there is no crew.</param>
/// <param name="CrewAge">How long the crew has been going, from its oldest session. Empty when unknown.</param>
/// <param name="SectionTitle">
/// In attention order, the heading this row opens ("NEEDS YOU 2"), on the FIRST row of the section only.
/// Empty everywhere else, including in my order, which has no sections.
/// </param>
/// <param name="SectionIsNeedsYou">True when <see cref="SectionTitle"/> is the needs-you section, which is red.</param>
public sealed record SessionRailRow(
    SessionViewModel Session,
    SessionViewModel? Parent,
    int Depth,
    bool HasCrew,
    bool IsExpanded,
    IReadOnlyList<SessionViewModel> Crew,
    string CrewLine,
    string CrewAge,
    string SectionTitle,
    bool SectionIsNeedsYou);

/// <summary>
/// THE RAIL'S ROW PROJECTION: the session list as the ownership tree, flattened into rows.
///
/// It DECIDES NOTHING. Who is under whom, who is a root, what a crew's counts and age are, the exact
/// words of the crew line, and the attention sections all come from
/// <see cref="SessionTree"/> in CcDirector.Gateway.Contracts - the ONE fold the Cockpit and the phone
/// read through their TypeScript twin. This class walks that answer and hands the list box rows. A
/// second answer to any of those questions is the defect the whole mission exists to remove, so if you
/// find yourself computing a count or an order here, it belongs in the fold instead.
///
/// THE TRAP THIS CLASS IS WRITTEN AROUND. Four independent inspections of the same design in TypeScript
/// found the same defect twice: a renderer that STOPPED AT THE FIRST LEVEL. The fold recorded
/// Architect -> Manager -> Worker correctly, and the shell rendered only the root's direct children, so
/// the Manager got no chevron, the Worker rendered nowhere, and the Architect's crew line said "1 under
/// it" while two sessions were under it. <see cref="Project"/> therefore RECURSES - every row asks the
/// fold for its own children and its own crew, at any depth - and the crew counts come from
/// <see cref="SessionTree.DescendantsOf"/>, which is every level, never the direct children.
/// </summary>
public static class SessionRailTree
{
    /// <summary>
    /// The rows the list box renders, in order.
    ///
    /// A collapsed crew contributes ONE row carrying its whole crew as a summary. An expanded crew
    /// contributes its own row and then a row per session under it, each of which may itself be a
    /// collapsed or expanded crew.
    /// </summary>
    /// <param name="sessions">
    /// Every session on this Director, in the rail's own drag order. The tree is built over the WHOLE
    /// list, exactly as the fold requires - never per machine, never per mission.
    /// </param>
    /// <param name="order">Which position the order switch is on. Applied to the TOP LEVEL ONLY.</param>
    /// <param name="expandedCrewIds">The session ids of the crews the user has opened.</param>
    /// <param name="nowUtc">The rail's clock, passed in so the crew age ticks on the rail's existing timer.</param>
    public static IReadOnlyList<SessionRailRow> Project(
        IReadOnlyList<SessionViewModel> sessions,
        SessionRailOrder order,
        IReadOnlyCollection<string> expandedCrewIds,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(expandedCrewIds);

        var dtos = new List<SessionDto>(sessions.Count);
        var viewModelById = new Dictionary<string, SessionViewModel>(StringComparer.Ordinal);
        foreach (var vm in sessions)
        {
            var dto = vm.FoldInput;
            dtos.Add(dto);
            var id = (dto.SessionId ?? "").Trim();
            if (id.Length > 0) viewModelById[id] = vm;
        }

        var expanded = expandedCrewIds as ISet<string> ?? new HashSet<string>(expandedCrewIds, StringComparer.Ordinal);
        var tree = SessionTree.Build(dtos);
        var rows = new List<SessionRailRow>(sessions.Count);

        void Emit(SessionDto dto, SessionViewModel? parent, int depth, string sectionTitle, bool sectionIsNeedsYou)
        {
            var id = (dto.SessionId ?? "").Trim();
            if (!viewModelById.TryGetValue(id, out var vm)) return;

            var children = SessionTree.ChildrenOf(tree, dto);
            var hasCrew = children.Count > 0;
            var isExpanded = hasCrew && expanded.Contains(id);

            var crew = Array.Empty<SessionViewModel>() as IReadOnlyList<SessionViewModel>;
            var crewLine = "";
            var crewAge = "";
            if (hasCrew)
            {
                // EVERY level, not the direct children - this is the line the four TypeScript
                // inspections kept finding wrong, and the reason the counts and the strip of squares
                // are taken from the same list.
                var descendants = SessionTree.DescendantsOf(tree, dto);
                var summary = SessionTree.SummarizeCrew(dto, descendants.Select(d => d.Session));
                crewLine = SessionTree.CrewSummaryLine(summary);
                crewAge = SessionTree.CrewAge(summary, nowUtc);

                var strip = new List<SessionViewModel>(descendants.Count);
                foreach (var d in descendants)
                {
                    var childId = (d.Session.SessionId ?? "").Trim();
                    if (viewModelById.TryGetValue(childId, out var childVm)) strip.Add(childVm);
                }
                crew = strip;
            }

            rows.Add(new SessionRailRow(
                vm, parent, depth, hasCrew, isExpanded, crew, crewLine, crewAge, sectionTitle, sectionIsNeedsYou));

            if (!isExpanded) return;

            // THE RECURSION. A child that supervises sessions of its own gets its own chevron and its
            // own crew line, at any depth. Rendering only this level is the defect named in the class
            // comment above.
            foreach (var child in children) Emit(child, vm, depth + 1, "", false);
        }

        if (order == SessionRailOrder.MyOrder)
        {
            // The fold returns the roots IN THE CALLER'S ORDER, and the caller's order is the rail's
            // drag order - so my order is simply the roots as they came.
            foreach (var root in tree.Roots) Emit(root, null, 0, "", false);
            return rows;
        }

        foreach (var section in SessionTree.AttentionSections(tree.Roots))
        {
            var isNeedsYou = section.Key == SessionOrdering.TriageBucket.NeedsYou;
            // The count is the rows before the calm band: a calm row is listed under "Needs you" and never
            // counted in it. The desktop's display push carries no verdict state today, so its band is empty
            // and this equals the root count - but the heading counts the same thing on every surface.
            var heading = $"{section.Title.ToUpperInvariant()} {section.BandStart}";
            var first = true;
            foreach (var root in section.Roots)
            {
                Emit(root, null, 0, first ? heading : "", first && isNeedsYou);
                first = false;
            }
        }

        return rows;
    }
}
