using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Remembers each worktree's merge signals between inventories, so a working-tree edit - which
/// changes neither a commit nor origin/main - costs one <c>git status</c> per worktree instead of the
/// whole set of merge questions (plan step 7d). Before this, every recompute of a repository asked
/// <c>merge-base</c>, <c>cherry</c>, <c>rev-list</c>, the upstream probe, the pull-request probe and
/// two last-activity questions of every worktree again: about 126,000 git launches in 14 hours on
/// the owner's machine, almost all of them answering exactly what the previous inventory had.
///
/// An entry is reused only when ALL of these still match what it was computed from:
/// - the worktree's HEAD commit id (a commit, reset or checkout there moves it);
/// - its branch name and detached state (a checkout of another branch at the same commit changes
///   which upstream the upstream-gone signal reads);
/// - the origin/main commit id (a fetch that moves origin/main changes containment and counts);
/// - and it is younger than <see cref="DefaultMaxAge"/>.
///
/// The age limit exists because two signals can change with neither commit id moving: the
/// pull-request probe (a pull request opened or merged on the hosting provider) and the upstream
/// tracking ref (a fetch that pruned the branch's upstream). Five minutes matches the watcher's
/// periodic reconciliation, so those are re-read on the same cadence the full rescan already had.
///
/// Only a fully successful inspection is stored - a fail-closed answer is never reused - and the
/// cache is only ever read on the display paths (the repository monitor). The worktree reaper does
/// not use one: its verdicts decide what is deleted and are always computed fresh.
/// </summary>
public sealed class WorktreeMergeSignalCache
{
    /// <summary>How long an entry may be reused even when both commit ids still match.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _maxAge;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _byRepository = new(StringComparer.OrdinalIgnoreCase);

    public WorktreeMergeSignalCache(TimeSpan? maxAge = null, Func<DateTime>? utcNow = null)
    {
        _maxAge = maxAge ?? DefaultMaxAge;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>What an entry was computed from. Reused only when every field is equal.</summary>
    internal sealed record Key(string Head, string? Branch, bool IsDetached, string? MainCommitId);

    /// <summary>
    /// A worktree's merge signals and the git-derived half of its last-activity time (the reflog's
    /// location and the last commit's date; the file times themselves are read fresh every time).
    /// </summary>
    internal sealed record Signals(
        bool DetachedAncestor,
        bool OriginGone,
        bool ContainedInMain,
        int Ahead,
        int Behind,
        bool PullRequestMerged,
        bool HasOpenPullRequest,
        string? ReflogPath,
        DateTime? LastCommitUtc);

    private sealed record Entry(Key Key, Signals Signals, DateTime StoredUtc);

    /// <summary>The stored signals for a worktree when its key still matches and the entry is young enough; otherwise null.</summary>
    internal Signals? TryGet(string repositoryPath, string worktreePath, Key key)
    {
        lock (_gate)
        {
            if (!_byRepository.TryGetValue(WorktreeReaperService.NormalizePath(repositoryPath), out var worktrees))
                return null;
            if (!worktrees.TryGetValue(WorktreeReaperService.NormalizePath(worktreePath), out var entry))
                return null;
            if (entry.Key != key)
                return null;
            if (_utcNow() - entry.StoredUtc >= _maxAge)
                return null;
            return entry.Signals;
        }
    }

    internal void Store(string repositoryPath, string worktreePath, Key key, Signals signals)
    {
        lock (_gate)
        {
            var repoKey = WorktreeReaperService.NormalizePath(repositoryPath);
            if (!_byRepository.TryGetValue(repoKey, out var worktrees))
                _byRepository[repoKey] = worktrees = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            worktrees[WorktreeReaperService.NormalizePath(worktreePath)] = new Entry(key, signals, _utcNow());
        }
    }

    /// <summary>Drops the entries of worktrees the repository no longer lists, so the cache holds only what exists.</summary>
    internal void KeepOnly(string repositoryPath, IEnumerable<string> worktreePaths)
    {
        lock (_gate)
        {
            if (!_byRepository.TryGetValue(WorktreeReaperService.NormalizePath(repositoryPath), out var worktrees))
                return;
            var keep = worktreePaths.Select(WorktreeReaperService.NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var gone = worktrees.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var k in gone)
                worktrees.Remove(k);
            if (gone.Count > 0)
                FileLog.Write($"[WorktreeMergeSignalCache] dropped {gone.Count} entries for worktrees no longer listed in {repositoryPath}");
        }
    }
}
