using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Reports;

namespace CcDirector.Gateway.Fleet;

/// <summary>The rule's answer: whether the session may be closed, and the sentence saying why not.</summary>
/// <param name="Allowed">True only when every fact needed says the work has landed.</param>
/// <param name="Refusal">Why not, in the owner's words; null when allowed, and null when there is no session at all.</param>
/// <param name="Evidence">When allowed, what the permission rests on, for the confirmation window.</param>
internal sealed record FleetCloseVerdict(bool Allowed, string? Refusal, string? Evidence)
{
    public static FleetCloseVerdict No(string why) => new(false, why, null);
}

/// <summary>
/// MAY THIS SESSION BE CLOSED FROM THE WALKTHROUGH? (the Fleet Manager mission, step 7.) One rule, asked twice: by the
/// walkthrough fold, to offer or withhold the button, and by the close route, again, before anything is stopped.
///
/// NEVER WITH UNLANDED WORK, AND "CANNOT TELL" IS A NO. Close is allowed only when the Gateway holds positive
/// evidence that nothing would be lost:
///   - the session's own Director says its working copy has no uncommitted file (<see cref="SessionDto.UncommittedCount"/>
///     is 0 - null is "not known" and refuses);
///   - the account holds a repository report (<see cref="StoredRepoState"/>, pushed by the same Director) that covers
///     the session's folder - the primary checkout or one of its worktrees, the longest match winning;
///   - that report was taken AFTER the session's last activity, so no commit can have been made since it was read;
///   - the tree was clean in that report, it is on a named branch, and that branch is contained in the default
///     branch the Director measured against (<see cref="RepoStateBranchDto.MergedIntoDefault"/> is true - the
///     Director measures against the remote's default branch, so "contained" means pushed and merged). A false is a
///     refusal that says how many commits are not in it; a null is a refusal that says it could not be told.
///
/// What the Assistant's delete_session did, for comparison: it asked for a spoken confirmation and deleted, with no
/// check of the work at all. This rule is the check it never had.
///
/// The repository reports are pushed every six hours, so a session that did anything since the last report is
/// refused - correctly, because the Gateway cannot tell. An inspection on demand is not built.
/// </summary>
internal static class FleetManagerCloseRule
{
    public static FleetCloseVerdict Decide(
        SessionDto? session,
        bool live,
        string? fleetManagerSessionId,
        TurnVerdictDto? latestVerdict,
        IReadOnlyList<StoredRepoState> repositories,
        TimeZoneInfo tz,
        DateTime nowUtc)
    {
        if (session is null || !live)
            return FleetCloseVerdict.No("Close is not offered: this session is not running, so there is nothing to close.");
        var name = string.IsNullOrWhiteSpace(session.Name) ? session.SessionId : session.Name!;
        if (fleetManagerSessionId is not null
            && string.Equals(session.SessionId, fleetManagerSessionId, StringComparison.OrdinalIgnoreCase))
            return FleetCloseVerdict.No("Close is not offered: this is the Fleet Manager itself. Restart or move it in Settings.");

        switch (session.UncommittedCount)
        {
            case null:
                return FleetCloseVerdict.No("Close is not offered: this session's computer has not said whether its "
                    + "working copy has uncommitted changes, so the Gateway cannot tell whether work would be lost.");
            case > 0:
                return FleetCloseVerdict.No($"Close is not offered: this session has {Plural(session.UncommittedCount.Value, "uncommitted file")}.");
        }

        var folder = Normalize(session.RepoPath);
        if (folder.Length == 0)
            return FleetCloseVerdict.No("Close is not offered: this session has not said which folder it works in, "
                + "so the Gateway cannot tell whether its work is pushed and merged.");

        var tree = FindTree(folder, session.DirectorId, repositories);
        if (tree is null)
            return FleetCloseVerdict.No("Close is not offered: the Gateway holds no report of this session's repository "
                + "from its computer, so it cannot tell whether its work is pushed and merged.");

        var (repo, branch, dirty, merged) = tree.Value;
        var inspected = FleetManagerPlacementFold.FormatWhen(repo.CollectedAtUtc, tz, nowUtc);
        var lastActivity = LastActivity(session, latestVerdict);
        if (lastActivity is null)
            return FleetCloseVerdict.No($"Close is not offered: the Gateway does not know when this session last worked, "
                + $"so it cannot tell whether the repository report from {inspected} is still true.");
        if (repo.CollectedAtUtc < lastActivity.Value)
            return FleetCloseVerdict.No($"Close is not offered: the repository was last inspected at {inspected}, before "
                + $"this session's last activity at {FleetManagerPlacementFold.FormatWhen(lastActivity.Value, tz, nowUtc)}, "
                + "so the Gateway cannot tell whether that work is pushed and merged.");
        if (dirty)
            return FleetCloseVerdict.No($"Close is not offered: its working copy had uncommitted changes when it was inspected at {inspected}.");
        if (branch is null)
            return FleetCloseVerdict.No("Close is not offered: its working copy is not on a named branch, so the Gateway "
                + "cannot tell whether its work is merged.");
        if (repo.DefaultBranch is null)
            return FleetCloseVerdict.No($"Close is not offered: the default branch of {repo.Name} could not be determined, "
                + $"so the Gateway cannot tell whether branch {branch} is merged.");

        var info = repo.Branches.FirstOrDefault(b => string.Equals(b.Name, branch, StringComparison.Ordinal));
        switch (merged)
        {
            case null:
                return FleetCloseVerdict.No($"Close is not offered: the Gateway could not tell whether branch {branch} is "
                    + $"contained in {repo.DefaultBranch}.");
            case false:
                var ahead = info?.CommitsAheadOfDefault ?? 0;
                return FleetCloseVerdict.No(ahead > 0
                    ? $"Close is not offered: branch {branch} has {Plural(ahead, "commit")} that {(ahead == 1 ? "is" : "are")} not in {repo.DefaultBranch}."
                    : $"Close is not offered: branch {branch} has work that is not in {repo.DefaultBranch}.");
        }

        return new FleetCloseVerdict(true, null,
            $"Its branch {branch} is fully in {repo.DefaultBranch} and its working copy was clean when it was inspected at "
            + $"{inspected}, after its last activity. Closing stops {name}; it cannot be undone.");
    }

    /// <summary>The latest moment the session could have changed its files: its last output, or the stop the Wingman read,
    /// whichever is later.</summary>
    private static DateTime? LastActivity(SessionDto session, TurnVerdictDto? verdict)
    {
        DateTime? last = session.LastActivityAt is { } a ? Utc(a) : null;
        if (verdict is not null && verdict.TurnEndObservedAtUtc.Year >= 2000)
        {
            var end = Utc(verdict.TurnEndObservedAtUtc);
            if (last is null || end > last) last = end;
        }
        return last;
    }

    /// <summary>The primary checkout or worktree of the session's own Director whose folder holds the session's folder,
    /// the longest match winning (a worktree can sit inside its repository's folder).</summary>
    private static (StoredRepoState Repo, string? Branch, bool Dirty, bool? Merged)? FindTree(
        string folder, string directorId, IReadOnlyList<StoredRepoState> repositories)
    {
        (StoredRepoState Repo, string? Branch, bool Dirty, bool? Merged)? best = null;
        var bestLength = -1;
        foreach (var repo in repositories.Where(r => string.Equals(r.DirectorId, directorId, StringComparison.OrdinalIgnoreCase)))
        {
            var primary = Normalize(repo.Path);
            if (Contains(primary, folder) && primary.Length > bestLength)
            {
                var current = repo.CurrentBranch;
                var merged = current is null
                    ? null
                    : repo.Branches.FirstOrDefault(b => string.Equals(b.Name, current, StringComparison.Ordinal))?.MergedIntoDefault;
                best = (repo, current, repo.IsDirty, merged);
                bestLength = primary.Length;
            }
            foreach (var wt in repo.Worktrees)
            {
                var path = Normalize(wt.Path);
                if (Contains(path, folder) && path.Length > bestLength)
                {
                    best = (repo, wt.Branch, wt.IsDirty, wt.Branch is null ? null : wt.BranchMergedIntoDefault);
                    bestLength = path.Length;
                }
            }
        }
        return best;
    }

    private static bool Contains(string root, string folder)
    {
        if (root.Length == 0) return false;
        var comparison = IsWindowsPath(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(root, folder, comparison)
               || folder.StartsWith(root + "/", comparison);
    }

    private static bool IsWindowsPath(string path) => path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]);

    private static string Normalize(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.Length > 1 && p.EndsWith('/') && !(p.Length == 3 && p[1] == ':')) p = p[..^1];
        return p;
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
