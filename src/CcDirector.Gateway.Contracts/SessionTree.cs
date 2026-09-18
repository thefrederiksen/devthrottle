namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE OWNERSHIP TREE - one fold, shared by the Director rail, the Cockpit and the phone.
///
/// The session list is ALWAYS the ownership tree (owner ruling, 2026-09-14 - the Session List Views
/// design in devthrottle_internal/docs/design/session-list-views/). Any session that started another
/// session is a parent; the sessions it started sit UNDER it, collapsed by default. A session with no
/// children is an ordinary top-level row, so for most sessions nothing changes.
///
/// The relationship is <see cref="SessionDto.ControllerSessionId"/> - the supervisor a session answers
/// to. It is NOT Mission: the owner was explicit, "anything that has a parent and a child". A child
/// nests only while its supervisor is in the same list. When the supervisor is gone (killed, or on a
/// machine the roster no longer carries) the child becomes a top-level row again, which is exactly when
/// it goes red on its own (issue #2826) and must be seen.
///
/// THIS IS A PORT, NOT A SECOND DESIGN. The rules shipped first in TypeScript
/// (packages/client-core/src/sessions/tree.ts, pull request 2852, four inspection rounds) and this file
/// must agree with that one case for case. The two are held together mechanically by the shared fixture
/// file packages/client-core/src/sessions/tree-agreement.json: the C# answers are asserted against it by
/// CcDirector.StateAgreementCheck.TreeAgreement and the TypeScript answers by tree.agreement.test.ts, so
/// neither language can change an answer on its own without going red.
///
/// WHAT IS NOT HERE. Expanded-or-collapsed is remembered per crew by each SHELL, in its own store (the
/// browser's localStorage, the Director's persisted rail state). It is not a fold answer and there is
/// nothing to agree about, so it is deliberately absent from this file.
/// </summary>
public static class SessionTree
{
    /// <summary>
    /// The tree: the top-level rows, and the sessions under each session by its id.
    /// </summary>
    /// <param name="Roots">
    /// Every session whose supervisor is absent from the list, IN THE CALLER'S ORDER - so the caller
    /// decides my-order versus attention by ordering the roots it gets back.
    /// </param>
    /// <param name="ChildrenBySessionId">
    /// Session id -> the sessions it supervises, in desktop order. A session with none is ABSENT from
    /// the dictionary rather than present with an empty list; read it through <see cref="ChildrenOf"/>.
    /// </param>
    public sealed record Tree(
        IReadOnlyList<SessionDto> Roots,
        IReadOnlyDictionary<string, IReadOnlyList<SessionDto>> ChildrenBySessionId);

    /// <summary>
    /// One session under a root at some depth (1 = a direct child), for a shell that flattens a crew.
    /// <paramref name="Parent"/> is the session it is DIRECTLY under - not the root - so a shell that
    /// flattens the crew can still answer per-edge questions (is this one on another machine than the
    /// session above it?).
    /// </summary>
    public sealed record Descendant(SessionDto Session, SessionDto Parent, int Depth);

    /// <summary>
    /// What a collapsed crew row must carry so that collapsing hides nothing that matters: how many
    /// sessions are under it - at EVERY level, not just the direct children - and what each is doing, by
    /// the Gateway's own triage bucket (<see cref="SessionOrdering.Classify"/>).
    ///
    /// <see cref="NeedsYou"/> is zero for every live crew - a child with a live supervisor never goes red
    /// (issue #2826) - and turns non-zero only when the supervisor has died and its sessions have
    /// surfaced. That is exactly when it must be seen, so the count is always carried, never omitted when
    /// zero.
    /// </summary>
    /// <param name="Since">
    /// The earliest <see cref="SessionDto.CreatedAt"/> across the root and the sessions under it, as UTC;
    /// null when none of them carries a usable stamp.
    /// </param>
    public sealed record CrewSummary(int Count, int NeedsYou, int Working, int Stopped, DateTime? Since);

    /// <summary>One attention section: its bucket, its heading, the roots in it, and where its calm band starts.</summary>
    /// <param name="BandStart">
    /// The index in <see cref="Roots"/> of the first row of the calm band - the rows the Wingman judged a report
    /// (<see cref="SessionOrdering.IsInCalmBand"/>), listed after every red row of the needs-you section and not
    /// counted in it. Equal to the number of roots when there is no band, which is always so for the other two
    /// sections. The rows that need you are exactly the first <c>BandStart</c> roots, and a heading counts those.
    /// </param>
    public sealed record AttentionSection(
        SessionOrdering.TriageBucket Key,
        string Title,
        IReadOnlyList<SessionDto> Roots,
        int BandStart);

    /// <summary>
    /// A <see cref="SessionDto.CreatedAt"/> before this instant is not a real creation stamp - it is a
    /// default or a zero that survived a serializer - and is ignored when ageing a crew.
    ///
    /// It is the C# face of the TypeScript guard, which drops a createdAt that will not parse OR that
    /// lands before 2000. C# has no "will not parse" case here (the wire field is a DateTime, so an
    /// unparseable value never becomes one), and the shape it does produce is default(DateTime) - which
    /// this threshold catches, and which is why the two languages still answer the same on the
    /// nothing-parses fixture.
    /// </summary>
    private static readonly DateTime NotACreationStamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Nest every session under its supervisor when that supervisor is in the same list. Roots keep the
    /// order of <paramref name="sessions"/>; children are in desktop order under their parent. Ownership
    /// can go more than one level deep (an Architect's Manager's Workers), and every level is kept.
    ///
    /// THE CROSS-DIRECTOR RULE, stated once for every shell and both orders: the tree is built over the
    /// WHOLE list, never per machine. A session may supervise a session on another machine (the owner
    /// named at spawn may live on any Director, and the Gateway resolves liveness fleet-wide), so a
    /// child nests under its parent wherever the parent lives, and a child on another machine says so on
    /// its own row (see <see cref="IsOnAnotherMachine"/>). A view that groups by machine groups the ROOTS.
    ///
    /// EVERY SESSION RENDERS EXACTLY ONCE. A malformed ownership loop (a supervises b supervises a) puts
    /// neither in the roots and would hide both; every session not reachable from a root is promoted to a
    /// root instead, the same invariant the Fleet Map keeps.
    /// </summary>
    public static Tree Build(IEnumerable<SessionDto> sessions)
    {
        var list = sessions as IReadOnlyList<SessionDto> ?? sessions.ToList();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list)
        {
            var id = IdOf(s);
            if (id.Length > 0) ids.Add(id);
        }

        var childrenOf = new Dictionary<string, List<SessionDto>>(StringComparer.Ordinal);
        var roots = new List<SessionDto>();
        foreach (var s in list)
        {
            var sup = SupervisorOf(s);
            // A session that names itself as its own supervisor is malformed; it stays a root rather than
            // vanishing into a loop nobody can expand.
            //
            // THE SELF-COMPARISON IS NOT PROVEN AND IS NOT LOAD-BEARING. Deleting it was watched, and NO
            // test went red: a self-loop is a loop of one, so the promotion pass below reaches exactly the
            // same answer by a longer road. It is kept because it matches the TypeScript line for line and
            // because reaching the right answer through the malformed-input path is luck, not design - but
            // a reader must not take it for a rule the suite is holding down. The rule the suite IS holding
            // down is the promotion pass.
            if (sup.Length > 0 && ids.Contains(sup) && !string.Equals(sup, IdOf(s), StringComparison.Ordinal))
            {
                if (childrenOf.TryGetValue(sup, out var kids)) kids.Add(s);
                else childrenOf[sup] = new List<SessionDto> { s };
            }
            else
            {
                roots.Add(s);
            }
        }

        // Promote every member of an ownership loop (a supervises b supervises a) to a root, so each
        // renders exactly once. A session that merely hangs off a loop stays under its parent.
        var byId = new Dictionary<string, SessionDto>(StringComparer.Ordinal);
        foreach (var s in list) byId[IdOf(s)] = s;

        var reached = new HashSet<string>(StringComparer.Ordinal);

        void Walk(SessionDto s)
        {
            var id = IdOf(s);
            if (!reached.Add(id)) return;
            if (childrenOf.TryGetValue(id, out var kids))
                foreach (var k in kids.ToList())
                    Walk(k);
        }

        foreach (var r in roots.ToList()) Walk(r);

        void DetachFromParent(SessionDto s)
        {
            var sup = SupervisorOf(s);
            if (childrenOf.TryGetValue(sup, out var siblings))
                childrenOf[sup] = siblings.Where(x => !ReferenceEquals(x, s)).ToList();
        }

        foreach (var s in list)
        {
            if (reached.Contains(IdOf(s))) continue;

            // Follow the supervisor chain until it revisits itself: that is the loop.
            var path = new List<SessionDto>();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            SessionDto? cur = s;
            while (cur is not null && !reached.Contains(IdOf(cur)) && !at.ContainsKey(IdOf(cur)))
            {
                at[IdOf(cur)] = path.Count;
                path.Add(cur);
                cur = byId.TryGetValue(SupervisorOf(cur), out var up) ? up : null;
            }

            // The chain can also run into a session that is ALREADY REACHED, and that is not a loop - it
            // is a tail hanging off one. `at` does not hold that session, so there is nothing to promote.
            int? loopStart = null;
            if (cur is not null && at.TryGetValue(IdOf(cur), out var startIndex)) loopStart = startIndex;

            var promoted = loopStart is null
                ? new List<SessionDto>()
                : path.Skip(loopStart.Value).ToList();

            foreach (var m in promoted) DetachFromParent(m);
            foreach (var m in promoted)
            {
                roots.Add(m);
                Walk(m);
            }

            // Whatever led into the loop (or into an already-reached session) is under it and now reached.
            foreach (var m in path) Walk(m);
        }

        var finalChildren = new Dictionary<string, IReadOnlyList<SessionDto>>(StringComparer.Ordinal);
        foreach (var pair in childrenOf)
        {
            if (pair.Value.Count == 0) continue;
            finalChildren[pair.Key] = SessionOrdering.InDesktopOrder(pair.Value);
        }

        return new Tree(roots, finalChildren);
    }

    /// <summary>The sessions directly under a session, or an empty list when it has none.</summary>
    public static IReadOnlyList<SessionDto> ChildrenOf(Tree tree, SessionDto root) =>
        tree.ChildrenBySessionId.TryGetValue(IdOf(root), out var kids) ? kids : Array.Empty<SessionDto>();

    /// <summary>
    /// Everything under a session, depth-first in desktop order at each level, with its depth. This is
    /// what a crew IS - not just the direct children - so the crew summary and a flattened crew list both
    /// read it and cannot disagree about who is under whom.
    /// </summary>
    public static IReadOnlyList<Descendant> DescendantsOf(Tree tree, SessionDto root)
    {
        var output = new List<Descendant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Walk(SessionDto s, int depth)
        {
            foreach (var k in ChildrenOf(tree, s))
            {
                if (!seen.Add(IdOf(k))) continue;
                output.Add(new Descendant(k, s, depth));
                Walk(k, depth + 1);
            }
        }

        Walk(root, 1);
        return output;
    }

    /// <summary>
    /// True when a child lives on a different Director from its parent - its row must then say where it
    /// is. Falls to the machine name only when either side carries no Director id, because a row that
    /// cannot name its Director can still name its machine.
    /// </summary>
    public static bool IsOnAnotherMachine(SessionDto parent, SessionDto child)
    {
        var a = (parent.DirectorId ?? "").Trim();
        var b = (child.DirectorId ?? "").Trim();
        if (a.Length > 0 && b.Length > 0) return !string.Equals(a, b, StringComparison.Ordinal);
        return !string.Equals(
            (parent.MachineName ?? "").Trim(),
            (child.MachineName ?? "").Trim(),
            StringComparison.Ordinal);
    }

    /// <summary>The crew word for a session that needs a person.</summary>
    public const string CrewStateNeedsYou = "needs-you";

    /// <summary>The crew word for a session that is not stopped.</summary>
    public const string CrewStateWorking = "working";

    /// <summary>The crew word for a session that has stopped and is quiet (parked, or supervised and done).</summary>
    public const string CrewStateStopped = "stopped";

    /// <summary>
    /// ONE session's word in its crew line: <see cref="CrewStateNeedsYou"/>, <see cref="CrewStateWorking"/> or
    /// <see cref="CrewStateStopped"/>. The crew line counts these words, and the Fleet Manager's digest prints
    /// them per session, so the two cannot disagree about what a session under someone is doing.
    /// </summary>
    public static string CrewState(SessionDto s) => SessionOrdering.Classify(s) switch
    {
        SessionOrdering.TriageBucket.NeedsYou => CrewStateNeedsYou,
        SessionOrdering.TriageBucket.OnHold => CrewStateStopped,
        _ => CrewStateWorking,
    };

    /// <summary>
    /// The crew summary for a collapsed row. Pass EVERY session under the root -
    /// <c>DescendantsOf(tree, root).Select(d =&gt; d.Session)</c> - not just the direct children: a crew
    /// is what is under it at every level, and counting one level would let a collapsed row hide a
    /// surfaced red grandchild.
    /// </summary>
    public static CrewSummary SummarizeCrew(SessionDto root, IEnumerable<SessionDto> crew)
    {
        var kids = crew as IReadOnlyList<SessionDto> ?? crew.ToList();

        var needsYou = kids.Count(s => CrewState(s) == CrewStateNeedsYou);
        var working = kids.Count(s => CrewState(s) == CrewStateWorking);
        var stopped = kids.Count(s => CrewState(s) == CrewStateStopped);

        DateTime? since = null;
        foreach (var s in kids.Prepend(root))
        {
            var created = AsUtc(s.CreatedAt);
            if (created < NotACreationStamp) continue;
            if (since is null || created < since.Value) since = created;
        }

        return new CrewSummary(kids.Count, needsYou, working, stopped, since);
    }

    /// <summary>
    /// The crew line's words: "9 under it: 4 working, 5 stopped, 0 need you". ONE formatter, so the
    /// Director rail, the Cockpit and the phone cannot word the same crew three different ways - which is
    /// the defect this mission exists to stop.
    /// </summary>
    public static string CrewSummaryLine(CrewSummary summary) =>
        $"{summary.Count} under it: {summary.Working} working, {summary.Stopped} stopped, {summary.NeedsYou} need you";

    /// <summary>
    /// How long the crew has been going, from its oldest session: "5h 29m". Empty when unknown.
    /// <paramref name="now"/> is passed in so the caller's ticking clock drives it and a test is
    /// deterministic.
    /// </summary>
    public static string CrewAge(CrewSummary summary, DateTime now)
    {
        if (summary.Since is not { } since) return "";
        var elapsed = AsUtc(now) - since;
        return DurationFrom(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
    }

    /// <summary>
    /// The duration ladder the crew age climbs: "0m" -> "12m" -> "1h 4m" -> "2d 3h". The port of
    /// <c>durationFromMs</c> in packages/client-core/src/sessions/waiting.ts.
    ///
    /// IT IS NOT THE RAIL'S OTHER TWO LADDERS, and that is deliberate on both sides. The desktop's
    /// "waiting 11m" and "wakes in 3h 48m" climb their own ladders in SessionViewModel, and so do their
    /// TypeScript counterparts. Porting those is not this fold's job; folding them together would change
    /// what three shipped labels say.
    /// </summary>
    public static string DurationFrom(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var totalMinutes = (long)Math.Floor(span.TotalMinutes);

        var days = totalMinutes / 1440;
        var hours = (totalMinutes % 1440) / 60;
        var minutes = totalMinutes % 60;

        if (days >= 1) return $"{days}d {hours}h";
        if (hours >= 1) return $"{hours}h {minutes}m";
        return $"{minutes}m";
    }

    /// <summary>
    /// The attention order, as sections: "Needs you" as a waiting line (longest wait on top), then
    /// "Working" in desktop order, then "Snoozed" in desktop order. Empty sections are omitted.
    ///
    /// APPLIED TO TOP-LEVEL ROWS ONLY - pass the tree's roots, never the whole list. Children never
    /// re-sort: a child with a live supervisor never goes red, so there is nothing for attention to
    /// reorder under a parent.
    /// </summary>
    public static IReadOnlyList<AttentionSection> AttentionSections(IEnumerable<SessionDto> roots)
    {
        var list = roots as IReadOnlyList<SessionDto> ?? roots.ToList();

        // The waiting line carries the calm band after its reds, so the reds are exactly its leading rows.
        var waiting = SessionOrdering.InWaitingOrder(list);
        var needsYouCount = waiting.Count(s => SessionOrdering.Classify(s) == SessionOrdering.TriageBucket.NeedsYou);
        // A calm row's bucket is Active, but it is listed in the band and is not working, so it is not listed
        // under "Working" as well.
        var active = SessionOrdering.InBucket(
            list.Where(s => !SessionOrdering.IsInCalmBand(s)), SessionOrdering.TriageBucket.Active);
        var onHold = SessionOrdering.InBucket(list, SessionOrdering.TriageBucket.OnHold);

        var sections = new[]
        {
            new AttentionSection(SessionOrdering.TriageBucket.NeedsYou, "Needs you", waiting, needsYouCount),
            new AttentionSection(SessionOrdering.TriageBucket.Active, "Working", active, active.Count),
            new AttentionSection(SessionOrdering.TriageBucket.OnHold, "Snoozed", onHold, onHold.Count),
        };

        return sections.Where(s => s.Roots.Count > 0).ToList();
    }

    /// <summary>The session's own id, trimmed - the key every map in this file is built on.</summary>
    private static string IdOf(SessionDto s) => (s.SessionId ?? "").Trim();

    /// <summary>The id of the session this one answers to, trimmed; empty when it answers to nobody.</summary>
    private static string SupervisorOf(SessionDto s) => (s.ControllerSessionId ?? "").Trim();

    /// <summary>
    /// A wire timestamp as UTC. A stamp that arrived without a kind is READ AS UTC rather than as local
    /// time: every clock on this wire is the Gateway's, stamped UTC, and guessing local would shift a
    /// crew's age by the machine's offset.
    /// </summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
