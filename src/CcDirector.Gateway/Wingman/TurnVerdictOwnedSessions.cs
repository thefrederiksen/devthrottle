using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the Wingman needs to know about the sessions a session owns (owner ruling, 2026-09-15): how many are
/// working, stopped and needing a person - the row's own crew line counts, from the same fold - how many are ALIVE,
/// and the latest moment any of them last wrote to its terminal, which is when the last one stopped once none is
/// working.
/// </summary>
/// <param name="Live">How many of them are ALIVE: in the fresh roster, not exited or crashed, and not snoozed.
/// This is the count the carrying-on clock stops on, and it is deliberately not <paramref name="Working"/>:
/// Working comes from terminal silence (the Director flips a session to waiting after ten quiet seconds), so a
/// session inside one long command that prints nothing reads as stopped while its turn is still open. Issue 2992:
/// a Worker eight minutes into a silent test run turned its Architect red.</param>
/// <param name="LastActivityAtUtc">The latest <see cref="SessionDto.LastActivityAt"/> across the owned sessions,
/// in UTC, or null when none carries one.</param>
public sealed record OwnedSessionsFacts(int Working, int Live, int Stopped, int NeedYou, DateTime? LastActivityAtUtc);

/// <summary>
/// The owned sessions of one session, read as a pure function over a roster - the shape and the reason of
/// <see cref="TurnVerdictHeldCheck"/>: the roster arrives with every role and liveness answer nulled by the push
/// store, so roles are resolved across the WHOLE roster first, then the ownership tree is built and the crew
/// under the session is summarised by <see cref="SessionTree.SummarizeCrew"/>, the fold the row's crew line
/// ("5 under it: 3 working") prints from.
///
/// "Owned" is every level: a Manager's Workers are an Architect's too.
/// </summary>
public static class TurnVerdictOwnedSessions
{
    /// <summary>The facts, or null when the session is not in the roster or owns no session.</summary>
    public static OwnedSessionsFacts? For(IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var sessions = roster.Select(r => r.Session).Where(s => s is not null).ToList();
        FleetRoleResolver.Stamp(sessions);

        var root = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (root is null) return null;

        var tree = SessionTree.Build(sessions);
        var owned = SessionTree.DescendantsOf(tree, root).Select(d => d.Session).ToList();
        if (owned.Count == 0) return null;

        var crew = SessionTree.SummarizeCrew(root, owned);
        DateTime? last = null;
        foreach (var s in owned)
        {
            if (s.LastActivityAt is not { } at) continue;
            var utc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
            if (last is null || utc > last.Value) last = utc;
        }

        return new OwnedSessionsFacts(crew.Working, owned.Count(IsLive), crew.Stopped, crew.NeedsYou, last);
    }

    /// <summary>
    /// Is this owned session ALIVE? It is in the roster the caller passed - the Gateway passes the FRESH roster, so
    /// a session whose stream has gone quiet past the freshness horizon is not in it at all - and it has neither
    /// exited nor crashed nor been snoozed.
    ///
    /// LIVENESS IS NOT ACTIVITY, and that is the whole point of it. Nothing here reads
    /// <see cref="SessionDto.ActivityState"/> for "Working", because that field answers "has the terminal printed
    /// in the last ten seconds", which a session inside a long silent command answers no to while its turn is
    /// still open (issue 2992).
    /// </summary>
    internal static bool IsLive(SessionDto s)
        => !s.Crashed
           && !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)
           && !s.OnHold;
}
