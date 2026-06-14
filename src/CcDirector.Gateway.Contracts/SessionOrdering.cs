namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Shared client-side policy for how a roster of <see cref="SessionDto"/> is ordered and
/// triaged. Lives next to the DTO so every client (Cockpit today, others later) agrees on
/// the rules instead of each re-implementing them, and so the rules are unit-testable
/// without spinning up a UI.
/// </summary>
public static class SessionOrdering
{
    /// <summary>
    /// The stable "desktop order": honor the owning Director's <see cref="SessionDto.SortOrder"/>
    /// (the user-controlled, drag-to-reorder, persisted order), then <see cref="SessionDto.CreatedAt"/>
    /// as a deterministic tie-break. The tie-break is also the only signal when a Director predates
    /// SortOrder (every session reports 0). This is what keeps a session in a fixed slot instead of
    /// reshuffling as its name or activity state changes.
    /// </summary>
    public static IReadOnlyList<SessionDto> InDesktopOrder(IEnumerable<SessionDto> sessions) =>
        sessions.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt).ToList();

    /// <summary>Triage priority bucket for the "needs-you-first" view.</summary>
    public enum TriageBucket
    {
        /// <summary>Wants the user now (effective color "red"), and not parked.</summary>
        NeedsYou = 0,
        /// <summary>Anything else that isn't parked.</summary>
        Active = 1,
        /// <summary>Parked by the user or the agent (<see cref="SessionDto.OnHold"/>) - sinks to the bottom.</summary>
        OnHold = 2,
    }

    /// <summary>
    /// True while the session must present as "the wingman is reading": the Gateway's brief
    /// agent has the finished turn queued or in flight (<see cref="SessionDto.BriefingState"/>
    /// "Briefing") AND the raw turn-end color is red. While a NEW turn is already running
    /// (blue) the stale in-flight brief is irrelevant - raw activity wins, no chip.
    /// </summary>
    public static bool IsBriefing(SessionDto s) =>
        s.BriefingState == "Briefing" && string.Equals(s.StatusColor, "red", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True while a user-initiated "I am lost - explain" deep dive runs for the session
    /// (issue #217). Unlike <see cref="IsBriefing"/> there is NO raw-activity gate: the
    /// user pressed the button just now, so the orange must show regardless of whether
    /// the session is working, quiet, or red - suppressing it (the original red-gated
    /// implementation) left the rail blue while the brief pane said "explaining", the
    /// exact cross-surface contradiction issue #196 forbids.
    /// </summary>
    public static bool IsExplaining(SessionDto s) =>
        s.BriefingState == "Explaining";

    /// <summary>
    /// The ONE effective status color every client renders and triages on (issue #196).
    /// The Director stamps the raw <see cref="SessionDto.StatusColor"/> (it no longer knows
    /// about briefing since #187 moved the pipeline to the Gateway), and the Gateway stamps
    /// <see cref="SessionDto.BriefingState"/> on top. Folding the two HERE - instead of in
    /// each view - is what keeps the dot, the "wingman reading..." chip, and the triage
    /// bucket atomic: while the wingman reads a finished turn the session IS yellow; while
    /// a user-requested deep dive runs it IS orange (issue #217); red ("needs you") may
    /// only appear after the brief or report lands.
    /// </summary>
    public static string EffectiveColor(SessionDto s) =>
        s.OnHold ? "grey"
        : IsExplaining(s) ? "orange"
        : IsBriefing(s) ? "yellow"
        : s.StatusColor;

    /// <summary>
    /// Classify a session for triage. On-hold takes precedence over color: a parked session sinks
    /// to the bottom even if it would otherwise be "needs you", because the user has explicitly
    /// deferred it. Uses <see cref="EffectiveColor"/>, NOT the raw Director color: a session the
    /// wingman is still reading stays in Active until the brief lands, instead of flopping into
    /// NEEDS YOU mid-brief and possibly back out (issue #196).
    /// </summary>
    public static TriageBucket Classify(SessionDto s) =>
        s.OnHold ? TriageBucket.OnHold
        : EffectiveColor(s) == "red" ? TriageBucket.NeedsYou
        : TriageBucket.Active;

    /// <summary>All sessions in a given triage bucket, in desktop order.</summary>
    public static IReadOnlyList<SessionDto> InBucket(IEnumerable<SessionDto> sessions, TriageBucket bucket) =>
        InDesktopOrder(sessions.Where(s => Classify(s) == bucket));

    /// <summary>
    /// The display label for the "(no repo)" group: sessions whose <see cref="SessionDto.RepoPath"/>
    /// is empty (and that carry no <see cref="SessionDto.RemoteRepo"/>). Rendered last in the
    /// by-repo view (issue #219).
    /// </summary>
    public const string NoRepoGroup = "(no repo)";

    /// <summary>
    /// One repository's group in the by-repo rail view (issue #219): the display name (the repo's
    /// short name) plus its sessions in desktop order. <see cref="IsNoRepo"/> marks the trailing
    /// catch-all group for repo-less sessions.
    /// </summary>
    public sealed record RepoGroup(string Name, IReadOnlyList<SessionDto> Sessions, bool IsNoRepo);

    /// <summary>
    /// The repo-identity decision for the by-repo view (issue #219). Same repo regardless of where
    /// it is checked out: prefer the remote (<see cref="SessionDto.RemoteRepo"/>, normalized - trimmed,
    /// trailing ".git" dropped) so the SAME repo on two machines coalesces under one header; fall back
    /// to the leaf folder name of <see cref="SessionDto.RepoPath"/> when there is no remote. Returns
    /// null when the session has neither (it belongs in the "(no repo)" group). The returned value is
    /// the human-facing group name; grouping is case-insensitive (see <see cref="InRepoGroups"/>).
    /// </summary>
    public static string? RepoName(SessionDto s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var remote = NormalizeRemote(s.RemoteRepo);
        if (!string.IsNullOrEmpty(remote))
            return LeafName(remote);

        if (!string.IsNullOrWhiteSpace(s.RepoPath))
            return LeafName(s.RepoPath.Trim());

        return null;
    }

    /// <summary>
    /// Group a session roster by repository for the by-repo rail view (issue #219): one group per
    /// distinct repo (case-insensitive on the <see cref="RepoName"/>), named-repo groups sorted
    /// alphabetically (case-insensitive), then a single "(no repo)" group last for sessions with no
    /// repo. Sessions within each group are in <see cref="InDesktopOrder"/> so a row holds its slot
    /// and never reshuffles when only its status color changes. Sessions for the same repo on
    /// different machines/Directors land in ONE group (the key ignores machine/Director identity).
    /// </summary>
    public static IReadOnlyList<RepoGroup> InRepoGroups(IEnumerable<SessionDto> sessions)
    {
        if (sessions is null) throw new ArgumentNullException(nameof(sessions));

        var named = sessions
            .Where(s => RepoName(s) is not null)
            // GroupBy on the case-insensitive name so "cc-director" and "CC-Director" coalesce; the
            // group's display name is the first session's RepoName (stable under desktop order).
            .GroupBy(s => RepoName(s), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RepoGroup(
                RepoName(InDesktopOrder(g)[0]) ?? g.Key ?? "",
                InDesktopOrder(g),
                IsNoRepo: false))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var noRepo = InDesktopOrder(sessions.Where(s => RepoName(s) is null));
        if (noRepo.Count > 0)
            named.Add(new RepoGroup(NoRepoGroup, noRepo, IsNoRepo: true));

        return named;
    }

    /// <summary>Normalize a remote-repo slug for grouping: trim, then drop a single trailing ".git".
    /// Empty/whitespace yields "".</summary>
    private static string NormalizeRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "";
        var trimmed = remote.Trim();
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        return trimmed;
    }

    /// <summary>The leaf segment of a repo identifier: the last non-empty part after splitting on
    /// both path separators (so "owner/repo" -> "repo" and "D:\ReposFred\cc-director" -> "cc-director").
    /// Returns the whole input when it has no separators.</summary>
    private static string LeafName(string value)
    {
        var parts = value.Split('/', '\\');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(parts[i]))
                return parts[i];
        }
        return value;
    }
}
