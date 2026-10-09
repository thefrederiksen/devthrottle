using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Git;

/// <summary>
/// Builds the Director's repo-state snapshots (issue #2118) - branches and worktrees per registered
/// repository - for the periodic push to the Gateway. This is the one hygiene feed the morning report
/// cannot get from any Gateway-side store: the Gateway knows a repository exists, but only the machine
/// holding the checkout can enumerate its branches and worktrees.
///
/// IT COLLECTS NAMES, PATHS, COUNTS AND DATES, AND NOTHING ELSE. No file contents, no diffs, no commit
/// messages ever enter a snapshot - see <see cref="RepoStateSnapshotDto"/> for why that is a boundary and
/// not a preference. Everything here comes from git plumbing already used elsewhere in the Director
/// (<see cref="GitBranchService"/>, <see cref="WorktreeInventoryService"/>), so there is no second, subtly
/// different notion of "merged" in the product.
///
/// IT NEVER GUESSES. A repository whose default branch cannot be resolved records a null default branch
/// and null merged-ness on every branch and worktree; a repository whose enumeration fails is OMITTED from
/// the batch rather than pushed as an empty one. An empty snapshot and a failed one look identical on the
/// receiving side, and the report would read the failure as "this repository is perfectly clean".
/// </summary>
public sealed class RepoStateSnapshotCollector
{
    private readonly GitBranchService _branches;
    private readonly WorktreeInventoryService _worktrees;
    private readonly Func<DateTime> _utcNow;

    public RepoStateSnapshotCollector(
        GitBranchService? branches = null,
        WorktreeInventoryService? worktrees = null,
        Func<DateTime>? utcNow = null)
    {
        _branches = branches ?? new GitBranchService();
        _worktrees = worktrees ?? new WorktreeInventoryService();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Collect a snapshot for every repository in <paramref name="repositories"/>. One repository's failure
    /// is isolated: it is logged and OMITTED, and the rest of the batch is still collected.
    /// </summary>
    /// <param name="liveSessions">Sessions running on this machine, so a worktree a session is working in
    /// is reported as occupied instead of as an abandoned one.</param>
    public async Task<List<RepoStateSnapshotDto>> CollectAsync(
        IEnumerable<RepositoryConfig> repositories,
        IReadOnlyList<LiveSessionRef>? liveSessions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        var snapshots = new List<RepoStateSnapshotDto>();
        foreach (var repo in repositories)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                FileLog.Write($"[RepoStateSnapshotCollector] skipped a repository whose path is missing: {repo.Path}");
                continue;
            }
            if (!IsInsideGitWorkingTree(repo.Path))
            {
                // A session can be started in a plain folder, and that folder is registered (#3669). It has no
                // repository state to report; running git on it only produced an error report every cycle.
                FileLog.Write($"[RepoStateSnapshotCollector] skipped a registered folder that is not a git repository: {repo.Path}");
                continue;
            }

            try
            {
                snapshots.Add(await CollectOneAsync(repo, liveSessions, ct));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolated per repository, and OMITTED rather than pushed empty: a pushed empty snapshot
                // would read downstream as "no branches, no worktrees, nothing to do here" - a clean bill
                // of health invented out of a failure.
                FileLog.Write($"[RepoStateSnapshotCollector] CollectOne FAILED for {repo.Path}, omitting it from the batch: {ex.Message}");
            }
        }

        FileLog.Write($"[RepoStateSnapshotCollector] CollectAsync: collected {snapshots.Count} repository snapshots");
        return snapshots;
    }

    /// <summary>True when the folder or one of its parents holds a <c>.git</c> directory or file - the presence
    /// git itself looks for. A <c>.git</c> that exists but is broken still goes to git and still fails loudly.</summary>
    private static bool IsInsideGitWorkingTree(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
                return true;
        }
        return false;
    }

    private async Task<RepoStateSnapshotDto> CollectOneAsync(
        RepositoryConfig repo, IReadOnlyList<LiveSessionRef>? liveSessions, CancellationToken ct)
    {
        // ONE branch inventory per repository, and the worktree merged-ness is read back out of it rather
        // than computed a second way. Two independent notions of "merged" in one snapshot would eventually
        // disagree, and the report would show a worktree as safe to remove whose branch it also lists as
        // unmerged - in the same email.
        var inventory = await _branches.ListInventoryAsync(repo.Path, ct);
        var byBranch = inventory.Branches.ToDictionary(b => b.Name, StringComparer.Ordinal);

        // fetchPrune: false - the collector runs on a timer in the background and must not mutate the
        // owner's repositories or spend network time on their behalf. The origin-gone signal it would
        // refresh is not one this snapshot carries.
        var worktrees = await _worktrees.GetInventoryAsync(repo.Path, fetchPrune: false, liveSessions, ct);
        if (!worktrees.Success)
            throw new InvalidOperationException(
                $"the worktree enumeration failed: {worktrees.Error ?? "no reason reported"}");

        var primary = worktrees.Worktrees.FirstOrDefault(w => w.IsPrimary);
        var current = inventory.Branches.FirstOrDefault(b => b.IsCurrent);

        return new RepoStateSnapshotDto
        {
            Name = repo.Name,
            Path = repo.Path,
            CollectedAtUtc = _utcNow(),
            DefaultBranch = inventory.DefaultBranch,
            CurrentBranch = current?.Name,
            IsDirty = primary is not null && !primary.IsClean,
            Branches = inventory.Branches.Select(b => new RepoStateBranchDto
            {
                Name = b.Name,
                TipCommitUtc = b.LastCommitUtc,
                CommitsAheadOfDefault = b.AheadOfMain,
                MergedIntoDefault = b.MergedIntoDefault,
                CheckedOut = b.IsCurrent || b.CheckedOutInWorktree,
            }).ToList(),
            Worktrees = worktrees.Worktrees
                .Where(w => !w.IsPrimary)   // the primary checkout is the repository, not one of its worktrees
                .Select(w => new RepoStateWorktreeDto
                {
                    Path = w.Path,
                    Branch = w.Branch,
                    TipCommitUtc = w.Branch is not null && byBranch.TryGetValue(w.Branch, out var wb)
                        ? wb.LastCommitUtc
                        : null,
                    LastActivityUtc = w.LastActivityUtc,
                    IsDirty = !w.IsClean,
                    BranchMergedIntoDefault = w.Branch is not null && byBranch.TryGetValue(w.Branch, out var mb)
                        ? mb.MergedIntoDefault
                        : null,
                    HasLiveSession = w.OpenSessions.Count > 0,
                }).ToList(),
        };
    }
}
