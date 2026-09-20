using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Reports;

/// <summary>
/// Turns the stored repo-state snapshots (issue #2118) into the morning report's hygiene rows - the
/// stale-worktree and unmerged-branch recommendations that are the flagship content of the daily email.
///
/// THIS IS WHERE A WRONG ANSWER COSTS THE OWNER WORK, so every rule below is conservative in the same
/// direction: when the Gateway cannot tell, it says nothing rather than something.
///
///  - A worktree is only SAFE TO REMOVE when it is old, clean, unoccupied, AND its branch is provably
///    merged into the default branch. A worktree whose merged-ness is UNKNOWN (no detectable default
///    branch, or a failed inspection) is reported in the not-safe item - it is honest to say "this is old"
///    and dishonest to say "this is finished".
///  - An age needs an observed timestamp. A worktree with neither a last-activity time nor a tip commit
///    date is skipped entirely: an unknown age is not a small age.
///  - A DIRTY worktree is never listed as removable at all. Uncommitted work is the one thing that exists
///    nowhere else.
///  - An OCCUPIED worktree (a live session working in it) is never listed. That is not stale, that is
///    someone's desk.
///  - A branch counts as unmerged only on a definite <c>false</c>, never on a null.
/// </summary>
internal static class RepoHygieneFold
{
    /// <summary>How old a worktree's last activity must be before it is worth mentioning at all.</summary>
    public const double StaleWorktreeDays = 7;

    /// <summary>How old a branch's tip must be before an unmerged branch is worth mentioning. Anything
    /// younger is simply work in progress, and reporting it would make the email noise.</summary>
    public const double UnmergedBranchHours = 24;

    /// <summary>The hygiene rows for one tenant's stored repositories, or an EMPTY list when there are
    /// none. The caller decides what an empty list means: with no snapshots at all the sections are absent
    /// from the report entirely (the honesty rule), which is not the same as "everything is tidy".</summary>
    public static List<MorningAttentionItemDto> Items(IReadOnlyList<StoredRepoState> repositories, DateTime nowUtc)
    {
        var items = new List<MorningAttentionItemDto>();
        if (repositories is null || repositories.Count == 0)
            return items;

        foreach (var repo in repositories)
        {
            items.AddRange(WorktreeItems(repo, nowUtc));
            var branches = UnmergedBranchItem(repo, nowUtc);
            if (branches is not null)
                items.Add(branches);
        }

        FileLog.Write($"[RepoHygieneFold] Items: {repositories.Count} repositories -> {items.Count} hygiene rows");
        return items;
    }

    private static IEnumerable<MorningAttentionItemDto> WorktreeItems(StoredRepoState repo, DateTime nowUtc)
    {
        var stale = repo.Worktrees
            .Where(w => !w.HasLiveSession && !w.IsDirty)
            .Select(w => (Worktree: w, AgeDays: AgeDays(w.LastActivityUtc ?? w.TipCommitUtc, nowUtc)))
            .Where(x => x.AgeDays is > StaleWorktreeDays)
            .ToList();

        if (stale.Count == 0)
            yield break;

        // Two items, never one: "six worktrees, four of them safe to remove" is a sentence a person acts on
        // wrongly. The safe ones and the not-safe ones are different recommendations and get different rows.
        var safe = stale.Where(x => x.Worktree.BranchMergedIntoDefault == true).ToList();
        var notSafe = stale.Where(x => x.Worktree.BranchMergedIntoDefault != true).ToList();

        if (safe.Count > 0)
            yield return Item(repo, safe, safeToRemove: true);
        if (notSafe.Count > 0)
            yield return Item(repo, notSafe, safeToRemove: false);
    }

    private static StaleWorktreesAttentionDto Item(
        StoredRepoState repo, List<(RepoStateWorktreeDto Worktree, double? AgeDays)> group, bool safeToRemove)
        => new()
        {
            Repo = string.IsNullOrWhiteSpace(repo.Name) ? repo.Path : repo.Name,
            Count = group.Count,
            // BASE NAMES only - the email does not need, and should not carry, the owner's directory layout.
            Worktrees = group
                .Select(x => BaseName(x.Worktree.Path))
                .Where(n => n.Length > 0)
                .ToList(),
            OldestAgeDays = Math.Round(group.Max(x => x.AgeDays!.Value), 1),
            SafeToRemove = safeToRemove,
        };

    private static UnmergedBranchesAttentionDto? UnmergedBranchItem(StoredRepoState repo, DateTime nowUtc)
    {
        // The default branch's own short name, so "main" is not reported as an unmerged branch of itself.
        var defaultShort = ShortBranchName(repo.DefaultBranch);

        var candidates = repo.Branches
            .Where(b => !NameMatches(b.Name, defaultShort))
            .Where(b => !NameMatches(b.Name, repo.CurrentBranch))
            .Select(b => (Branch: b, AgeDays: AgeDays(b.TipCommitUtc, nowUtc)))
            .ToList();

        var branches = candidates
            .Where(x => x.Branch.MergedIntoDefault == false)   // a definite false; a null is "not determined"
            .Where(x => x.AgeDays is not null && x.AgeDays > UnmergedBranchHours / 24.0)
            .OrderByDescending(x => x.AgeDays)                 // oldest first
            .ToList();

        // What could not be placed either way: merge state never determined, or unmerged with no tip date
        // to age it by. Still never NAMED as unmerged work - nobody established that it is - but COUNTED,
        // because dropping it made the count in the email read as the whole truth (#3124). A definite
        // merged branch and a branch touched within the day are known quantities, not unknowns.
        var undetermined = candidates.Count(x =>
            x.Branch.MergedIntoDefault is null ||
            (x.Branch.MergedIntoDefault == false && x.AgeDays is null));

        if (branches.Count == 0 && undetermined == 0)
            return null;

        return new UnmergedBranchesAttentionDto
        {
            Repo = string.IsNullOrWhiteSpace(repo.Name) ? repo.Path : repo.Name,
            Branches = branches.Select(x => new UnmergedBranchDto
            {
                Name = x.Branch.Name,
                AgeDays = Math.Round(x.AgeDays!.Value, 1),
                Commits = x.Branch.CommitsAheadOfDefault,
            }).ToList(),
            Undetermined = undetermined,
        };
    }

    /// <summary>Days between <paramref name="at"/> and now, or NULL when there is no observed timestamp.
    /// An unknown age is never treated as zero and never as large - it is simply not reported.</summary>
    private static double? AgeDays(DateTime? at, DateTime nowUtc)
    {
        if (at is not { } when)
            return null;
        var days = (nowUtc - DateTime.SpecifyKind(when, DateTimeKind.Utc)).TotalDays;
        return days < 0 ? 0 : days;
    }

    /// <summary>"origin/main" -> "main". Null stays null.</summary>
    private static string? ShortBranchName(string? refName)
    {
        if (string.IsNullOrWhiteSpace(refName))
            return null;
        var trimmed = refName.Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool NameMatches(string name, string? other)
        => !string.IsNullOrWhiteSpace(other) && string.Equals(name, other, StringComparison.Ordinal);

    /// <summary>The final path segment, whichever separator the pushing machine used.</summary>
    private static string BaseName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }
}
