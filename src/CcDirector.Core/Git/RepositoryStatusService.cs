using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Computes the at-a-glance status of on-disk repositories for the Repository screen. It composes
/// the pieces that already exist - remote URL, sync (ahead/behind), uncommitted count, and the
/// worktree triage - into one <see cref="RepositoryStatus"/> per repo. Reads only; never fetches by
/// default (uses the last-known remote-tracking refs), so a whole-machine scan stays fast.
/// </summary>
public sealed class RepositoryStatusService
{
    private readonly GitCommandRunner _git;
    private readonly GitStatusProvider _status;
    private readonly GitSyncStatusProvider _sync;
    private readonly WorktreeInventoryService _worktrees;

    public RepositoryStatusService(
        GitCommandRunner? git = null,
        GitStatusProvider? status = null,
        GitSyncStatusProvider? sync = null,
        WorktreeInventoryService? worktrees = null)
    {
        _git = git ?? new GitCommandRunner();
        _status = status ?? new GitStatusProvider();
        _sync = sync ?? new GitSyncStatusProvider();
        _worktrees = worktrees ?? new WorktreeInventoryService();
    }

    /// <summary>
    /// Enumerates the git repositories directly under each root directory and returns each one's
    /// status. A worktree (which carries a <c>.git</c> file, not a folder) is not a repository here -
    /// only real checkouts are returned; a repo's worktrees are summarised on its own status.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryStatus>> ScanAsync(
        IEnumerable<string> rootPaths,
        IReadOnlyList<LiveSessionRef>? liveSessions = null,
        CancellationToken ct = default)
    {
        FileLog.Write("[RepositoryStatusService] ScanAsync");
        var results = new List<RepositoryStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            foreach (var (_, path) in RemoteRepoProvider.ScanLocalRepos(root))
            {
                if (!seen.Add(WorktreeReaperService.NormalizePath(path)))
                    continue; // a repo reachable from two roots is listed once
                results.Add(await GetStatusAsync(path, liveSessions, fetchPrune: false, ct));
            }
        }

        FileLog.Write($"[RepositoryStatusService] ScanAsync: {results.Count} repositories");
        return results;
    }

    /// <summary>
    /// Builds the status of a single repository. <paramref name="fetchPrune"/> is off by default so a
    /// scan does not hit the network per repo; pass true for an explicit refresh of one repo.
    /// </summary>
    public async Task<RepositoryStatus> GetStatusAsync(
        string repoPath,
        IReadOnlyList<LiveSessionRef>? liveSessions = null,
        bool fetchPrune = false,
        CancellationToken ct = default)
    {
        FileLog.Write($"[RepositoryStatusService] GetStatusAsync: {repoPath}");
        try
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return new RepositoryStatus { Path = repoPath, Success = false, Error = $"repository path not found: {repoPath}" };

            var remoteUrl = await ReadOriginUrlAsync(repoPath, ct);
            var (provider, org) = ClassifyRemote(remoteUrl);

            var sync = await _sync.GetSyncStatusAsync(repoPath, ct);
            var count = await _status.GetCountAsync(repoPath, ct);
            var inventory = await _worktrees.GetInventoryAsync(repoPath, fetchPrune, liveSessions, ct);

            // Fail closed (issue 516): if ANY probe failed, this row is UNKNOWN, never a
            // non-provisional "verified clean" that fleet reporting and recommendations would trust.
            if (!sync.Success || !count.Success || !inventory.Success)
            {
                var reason = !sync.Success ? (sync.Error ?? "sync status probe failed")
                    : !count.Success ? "could not read the uncommitted count"
                    : (inventory.Error ?? "worktree inventory failed");
                FileLog.Write($"[RepositoryStatusService] GetStatusAsync: a git probe failed for {repoPath} - reporting unknown: {reason}");
                return new RepositoryStatus
                {
                    Path = repoPath,
                    Name = System.IO.Path.GetFileName(repoPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                    RemoteUrl = remoteUrl,
                    Provider = provider,
                    Org = org,
                    Success = false,
                    Error = reason,
                };
            }

            var uncommitted = count.Count;

            var safe = inventory.SafeToReapCount;
            var inUse = inventory.InUseBySession.Count;
            var needs = inventory.NeedsAttention.Count;

            // Carry the full (non-primary) worktree records on the status, with measured sizes -
            // every surface renders from this one model, and "reap N GB" is quoted from it.
            var worktrees = inventory.Worktrees
                .Where(w => !w.IsPrimary)
                .Select(w => w with { SizeBytes = MeasureWorktreeBytes(w, ct) })
                .ToList();

            return new RepositoryStatus
            {
                Path = repoPath,
                Name = System.IO.Path.GetFileName(repoPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                RemoteUrl = remoteUrl,
                Provider = provider,
                Org = org,
                Branch = sync.BranchName,
                IsDetachedHead = sync.IsDetachedHead,
                HasUpstream = sync.HasUpstream,
                AheadCount = sync.AheadCount,
                BehindCount = sync.BehindCount,
                BehindMainCount = sync.BehindMainCount,
                IsClean = uncommitted == 0,
                UncommittedCount = uncommitted,
                WorktreeCount = safe + inUse + needs,
                WorktreesSafeToReap = safe,
                WorktreesInUse = inUse,
                WorktreesNeedAttention = needs,
                Worktrees = worktrees,
                WorktreeBytes = worktrees.Sum(w => w.SizeBytes ?? 0),
                Success = true,
            };
        }
        catch (OperationCanceledException)
        {
            // A cancelled compute was superseded - the caller owns that outcome; never fold it
            // into a failure status.
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryStatusService] GetStatusAsync FAILED: {ex.Message}");
            return new RepositoryStatus { Path = repoPath, Success = false, Error = ex.Message };
        }
    }

    // Size measurements are a full directory walk, so they are cached per worktree and re-measured
    // only when the worktree's last activity changes. Process-wide: the monitor, the per-session
    // tab, and the watcher all share one measurement.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime? Stamp, long Bytes)> SizeCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Measures a worktree's on-disk size, honoring cancellation DURING the walk: a superseded
    /// compute stops immediately and stores nothing. Deliberately no file caps or byte budgets -
    /// a silently truncated size would be a lie; either the walk finishes or it is cancelled.
    ///
    /// KNOWN LIMITATION (inspection round 2, ruling R2-10 - accepted, no further code change):
    /// cancellation is honored once per enumerated file, but a single blocked filesystem call
    /// INSIDE Directory.EnumerateFiles - a stalled enumeration advancing to its next result, a
    /// blocked metadata read - cannot be interrupted by any token. Bounding it would require a
    /// timeout, and a timeout that silently truncates a size is exactly the lie the no-fallback
    /// rule forbids. The compute runs on a background thread, so a blocked walk delays model
    /// freshness for that repository; it never blocks the user interface.
    /// </summary>
    internal static long? MeasureWorktreeBytes(WorktreeInfo w, CancellationToken ct)
    {
        try
        {
            var key = WorktreeReaperService.NormalizePath(w.Path);
            if (SizeCache.TryGetValue(key, out var hit) && hit.Stamp == w.LastActivityUtc)
                return hit.Bytes;

            if (!Directory.Exists(w.Path))
                return null;

            long bytes = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions/symlinks
            };
            foreach (var file in Directory.EnumerateFiles(w.Path, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { bytes += new FileInfo(file).Length; }
                catch { /* transient file - skip */ }
            }

            SizeCache[key] = (w.LastActivityUtc, bytes);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw; // a partial measurement is never stored or returned
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryStatusService] MeasureWorktreeBytes failed for {w.Path} FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Evicts every cached size whose worktree path is not in <paramref name="worktreePathsSeen"/>.
    /// Called by the monitor after each completed scan, so entries for reaped, moved, or deleted
    /// worktrees do not accumulate forever in the process-wide cache.
    /// </summary>
    internal static void EvictSizeCacheExcept(IEnumerable<string> worktreePathsSeen)
    {
        var keep = new HashSet<string>(
            worktreePathsSeen.Select(WorktreeReaperService.NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        int evicted = 0;
        foreach (var key in SizeCache.Keys)
        {
            if (!keep.Contains(key) && SizeCache.TryRemove(key, out _))
                evicted++;
        }
        if (evicted > 0)
            FileLog.Write($"[RepositoryStatusService] size cache: evicted {evicted} entr{(evicted == 1 ? "y" : "ies")} for worktrees no longer present");
    }

    /// <summary>Test probe: whether a size measurement is cached for this worktree path.</summary>
    internal static bool SizeCacheContains(string worktreePath)
        => SizeCache.ContainsKey(WorktreeReaperService.NormalizePath(worktreePath));

    /// <summary>Classifies an origin remote URL into a provider and its owner/org. Pure and testable.</summary>
    internal static (RepoProvider Provider, string? Org) ClassifyRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return (RepoProvider.None, null);

        var gh = GitHubUrls.TryGitHubOwnerRepo(remoteUrl);
        if (gh is { } g)
            return (RepoProvider.GitHub, g.Owner);

        var az = GitHubUrls.TryAzureDevOpsOrgRepo(remoteUrl);
        if (az is { } a)
            return (RepoProvider.AzureDevOps, a.Org);

        return (RepoProvider.Other, null);
    }

    private async Task<string?> ReadOriginUrlAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, new[] { "remote", "get-url", "origin" }, ct);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }
}
