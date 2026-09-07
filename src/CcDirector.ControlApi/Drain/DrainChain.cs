using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// One seat's place in the reporting chain: who it reports to, who reports to it, and how deep it sits.
/// </summary>
/// <param name="SessionId">The seat's session id.</param>
/// <param name="Name">The seat's name, for the log and the report.</param>
/// <param name="ReportsTo">The session id of the seat this one reports to, resolved to a seat that is
/// actually in this workspace. Null for a head.</param>
/// <param name="Subordinates">The session ids of the seats that report DIRECTLY to this one.</param>
/// <param name="Depth">0 for a head, 1 for its direct subordinates, and so on.</param>
public sealed record DrainChainNode(
    string SessionId,
    string Name,
    string? ReportsTo,
    IReadOnlyList<string> Subordinates,
    int Depth);

/// <summary>
/// The reporting chain of one captured Director, built from the seats alone.
///
/// THIS IS THE FIRST OF THE FIVE BEHAVIOURS THE HAND-RUN GOT RIGHT BY ATTENTION (issue #2723).
/// A Director can hold twenty sessions. Messaging twenty sessions is twenty interrupts arriving at once
/// and nothing that can act on any of them. Messaging the CHAIN - each mission's most senior seat, and
/// each standalone session - covered seventeen sessions with seven messages in the first real drain, and
/// every senior seat drained its own subordinates and reported once.
///
/// Two facts build the tree and neither is guessed from a name:
///
///  - <see cref="WorkspaceSeat.ReportsTo"/>, the CONTROLLER, which is the live reporting relationship;
///  - <see cref="WorkspaceSeat.ParentSessionId"/>, which is who SPAWNED it - history rather than control,
///    and the fallback when a seat has no controller.
///
/// A parent that is not itself a seat in this workspace is not a parent for this purpose: it lives on
/// another Director (or has already gone), so the seat has nobody here to report through and is a head.
/// That is what makes the head list correct rather than merely plausible.
/// </summary>
public sealed class DrainChain
{
    private readonly Dictionary<string, DrainChainNode> _nodes;

    private DrainChain(
        Dictionary<string, DrainChainNode> nodes,
        IReadOnlyList<string> heads,
        IReadOnlyList<string> closeOrder,
        IReadOnlyList<string> cycles)
    {
        _nodes = nodes;
        Heads = heads;
        CloseOrder = closeOrder;
        SeatsInCycles = cycles;
    }

    /// <summary>The seats to MESSAGE: every seat with no senior inside this workspace. Each one drains its
    /// own subtree and reports once.</summary>
    public IReadOnlyList<string> Heads { get; }

    /// <summary>Every seat, deepest first - the order a leaf-first close walks. This is an ORDER, not the
    /// guarantee: the guarantee is <see cref="CanClose"/>, which refuses a seat whose subordinates are
    /// still open however the caller sequenced its work.</summary>
    public IReadOnlyList<string> CloseOrder { get; }

    /// <summary>Seats whose reporting chain loops back on itself. They are treated as heads so the drain
    /// still reaches them, and they are NAMED rather than silently absorbed - a cycle in the chain is a
    /// real fault in the fleet's records and somebody has to see it.</summary>
    public IReadOnlyList<string> SeatsInCycles { get; }

    /// <summary>Every seat in the chain, by session id.</summary>
    public IReadOnlyCollection<DrainChainNode> Nodes => _nodes.Values;

    /// <summary>One seat's node, or null when the workspace has no such seat.</summary>
    /// <param name="sessionId">The seat's session id.</param>
    public DrainChainNode? Node(string sessionId)
        => sessionId is not null && _nodes.TryGetValue(sessionId, out var n) ? n : null;

    /// <summary>
    /// THE LEAF-FIRST GATE. True only when every seat reporting directly to this one is already closed.
    ///
    /// Closing a seat while its subordinates still owe a document takes away the seat they were told to
    /// report to, and their work reaches nobody. The first real drain nearly got this wrong: an Architect
    /// was ready to close while two of its Workers were still writing.
    /// </summary>
    /// <param name="sessionId">The seat being considered for close.</param>
    /// <param name="closedSessionIds">The seats already verified genuinely gone.</param>
    public bool CanClose(string sessionId, ISet<string> closedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(closedSessionIds);
        var node = Node(sessionId);
        if (node is null) return false;
        foreach (var sub in node.Subordinates)
            if (!closedSessionIds.Contains(sub))
                return false;
        return true;
    }

    /// <summary>Every seat below this one, at any depth. Used to check a "covered" claim: a seat may only
    /// be accounted for by a document from a seat it actually reports through.</summary>
    /// <param name="sessionId">The senior seat.</param>
    public IReadOnlyList<string> Descendants(string sessionId)
    {
        var result = new List<string>();
        var node = Node(sessionId);
        if (node is null) return result;

        var stack = new Stack<string>(node.Subordinates);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            result.Add(id);
            var child = Node(id);
            if (child is null) continue;
            foreach (var sub in child.Subordinates) stack.Push(sub);
        }
        return result;
    }

    /// <summary>
    /// Build the chain from a workspace's seats.
    /// </summary>
    /// <param name="seats">The seats. A seat with no session id cannot be reported to or closed, so it is
    /// left out of the chain entirely rather than given an empty key.</param>
    public static DrainChain Build(IEnumerable<WorkspaceSeat>? seats)
    {
        var list = (seats ?? Array.Empty<WorkspaceSeat>())
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.SessionId))
            .ToList();

        var byId = new Dictionary<string, WorkspaceSeat>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list) byId[s.SessionId!] = s;

        // Resolve each seat's senior: the controller when it is a seat here, else the spawning parent when
        // IT is a seat here, else none. A seat is never its own senior.
        // A SEAT THAT HAS A CONTROLLER HAS A CONTROLLER, wherever it lives. The spawning parent is the
        // fallback only when there is NO controller at all - not when the controller happens to be
        // elsewhere. Attaching such a seat to its spawner would put it under a seat that does not control
        // it: it would never be messaged directly, and a session with no authority over it would be
        // expected to collect its handover. So a seat controlled from another Director is a head here,
        // which is the whole point of resolving against the seats actually present.
        //
        // (This used to fall back to the parent whenever the controller was not in the workspace, while
        // the comment above said the opposite. The comment was right and the code was wrong.)
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list)
        {
            var id = s.SessionId!;
            string? senior = null;
            var hasController = !string.IsNullOrWhiteSpace(s.ReportsTo)
                                && !string.Equals(s.ReportsTo, id, StringComparison.OrdinalIgnoreCase);

            if (hasController)
            {
                if (byId.ContainsKey(s.ReportsTo!)) senior = byId[s.ReportsTo!].SessionId;
            }
            else if (!string.IsNullOrWhiteSpace(s.ParentSessionId)
                     && byId.ContainsKey(s.ParentSessionId!)
                     && !string.Equals(s.ParentSessionId, id, StringComparison.OrdinalIgnoreCase))
            {
                senior = byId[s.ParentSessionId!].SessionId;
            }
            parent[id] = senior;
        }

        // A cycle would make the close walk never terminate and would hide a whole subtree from the
        // message step. Break it at the seat that closes the loop, and say which seats were involved.
        var cycles = new List<string>();
        foreach (var s in list)
        {
            var id = s.SessionId!;
            var walk = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            var cursor = parent[id];
            while (cursor is not null)
            {
                if (!walk.Add(cursor))
                {
                    parent[id] = null;
                    cycles.Add(id);
                    break;
                }
                cursor = parent.TryGetValue(cursor, out var next) ? next : null;
            }
        }

        var subordinates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list) subordinates[s.SessionId!] = new List<string>();
        foreach (var s in list)
        {
            var senior = parent[s.SessionId!];
            if (senior is not null) subordinates[senior].Add(s.SessionId!);
        }

        // Depth is measured by walking up, which is safe now every cycle is broken.
        int DepthOf(string id)
        {
            var depth = 0;
            var cursor = parent[id];
            while (cursor is not null && depth < list.Count + 1)
            {
                depth++;
                cursor = parent.TryGetValue(cursor, out var next) ? next : null;
            }
            return depth;
        }

        var nodes = new Dictionary<string, DrainChainNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list)
        {
            var id = s.SessionId!;
            nodes[id] = new DrainChainNode(id, s.Name ?? "", parent[id], subordinates[id], DepthOf(id));
        }

        var order = list.Select(s => s.SessionId!).ToList();
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++) position[order[i]] = i;

        var heads = order.Where(id => nodes[id].ReportsTo is null).ToList();

        var closeOrder = order
            .OrderByDescending(id => nodes[id].Depth)
            .ThenBy(id => position[id])
            .ToList();

        return new DrainChain(nodes, heads, closeOrder, cycles);
    }
}
