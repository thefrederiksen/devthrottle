using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Enumerates every worktree for a repository and computes each one's fail-closed
/// safe-to-reap verdict, exactly as issue #503 defines it. This is the detector: it
/// gathers the git facts and delegates the decision to <see cref="WorktreeSafetyEvaluator"/>.
/// The UI renders the result; it never re-derives a verdict.
/// </summary>
public sealed class WorktreeInventoryService
{
    private readonly GitCommandRunner _git;
    private readonly IMergedPullRequestProbe _pullRequestProbe;
    private readonly CcWorktreesPoolSlots _poolSlots;
    private readonly WorktreeMergeSignalCache? _signalCache;

    /// <param name="signalCache">Reuse each worktree's merge signals while its HEAD and origin/main are
    /// unchanged (<see cref="WorktreeMergeSignalCache"/>). Null - the default - computes every signal on
    /// every inventory; the worktree reaper relies on that and must never be given one.</param>
    public WorktreeInventoryService(
        GitCommandRunner? git = null,
        IMergedPullRequestProbe? pullRequestProbe = null,
        CcWorktreesPoolSlots? poolSlots = null,
        WorktreeMergeSignalCache? signalCache = null)
    {
        _git = git ?? new GitCommandRunner();
        _pullRequestProbe = pullRequestProbe ?? new NullMergedPullRequestProbe();
        _poolSlots = poolSlots ?? new CcWorktreesPoolSlots();
        _signalCache = signalCache;
    }

    /// <summary>
    /// Enumerates the worktrees of <paramref name="repositoryPath"/> and returns their verdicts.
    /// When <paramref name="fetchPrune"/> is true a <c>git fetch --prune</c> runs first so the
    /// origin-branch-gone signal (C2) is current. <paramref name="liveSessions"/> (already filtered
    /// to this machine and to genuinely-alive sessions) lets a git-safe worktree that a session is
    /// running in be classified "in use" rather than "safe to reap".
    /// </summary>
    /// <param name="unrefreshedRemotes">Remotes the CALLER tried and failed to refresh before asking
    /// (the reaper fetches for itself). Branches tracking one of them fail closed.</param>
    public async Task<WorktreeInventory> GetInventoryAsync(
        string repositoryPath, bool fetchPrune = true, IReadOnlyList<LiveSessionRef>? liveSessions = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? unrefreshedRemotes = null)
    {
        FileLog.Write($"[WorktreeInventoryService] GetInventoryAsync: repo={repositoryPath}, fetchPrune={fetchPrune}, liveSessions={liveSessions?.Count ?? 0}");
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return Failure(repositoryPath, $"repository path not found: {repositoryPath}");

            // Learn which origin branches were deleted (delete-branch-on-merge => gone == merged).
            var stale = new HashSet<string>(unrefreshedRemotes ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (fetchPrune)
            {
                // Fetch ORIGIN by name (inspection): a bare fetch follows the current branch's
                // upstream, which can be a different remote and leave origin/main stale.
                await _git.RunAsync(repositoryPath, new[] { "fetch", "--prune", "origin" }, ct);
                // And every other remote a branch tracks, because the upstream-gone verdict is read
                // from that remote's tracking ref. One that cannot be refreshed fails its branches closed.
                var others = await ConfiguredRemoteRefresh.PruneOtherRemotesAsync(_git, repositoryPath, "origin", ct);
                stale.UnionWith(others.Failed);
            }

            var (mainRef, mainCommitId) = await ResolveMainRefAsync(repositoryPath, ct);

            // Cached signals are read only when nothing about this inventory asks for fresh ones: an
            // explicit refresh just fetched (the tracking refs may have moved with no commit id
            // changing), and a remote that could not be refreshed must fail its branches closed now.
            bool reuseSignals = _signalCache != null && !fetchPrune && stale.Count == 0;
            int reused = 0;
            var sessionsByPath = BuildSessionMap(liveSessions);

            var listResult = await _git.RunAsync(repositoryPath, new[] { "worktree", "list", "--porcelain" }, ct);
            if (!listResult.Success)
                return Failure(repositoryPath, $"git worktree list failed: {listResult.Error}");

            // Which directories belong to a cc-worktrees pool, read ONCE for the whole inventory from
            // that tool's own records. Records that exist and cannot be read THROW: "I could not tell"
            // must never reach the verdict looking like "none of them", because the verdict decides
            // what may be deleted.
            IReadOnlyList<string> poolSlotDirectories;
            try
            {
                poolSlotDirectories = _poolSlots.RecordedSlotDirectories();
            }
            catch (CcWorktreesStateUnreadableException ex)
            {
                return Failure(repositoryPath, $"could not tell which worktrees belong to a cc-worktrees pool: {ex.Message}");
            }

            var rawEntries = WorktreeListParser.Parse(listResult.Output);
            var worktrees = new List<WorktreeInfo>();
            bool primaryAssigned = false;

            foreach (var entry in rawEntries)
            {
                if (entry.IsBare)
                    continue; // a bare repository has no primary checkout to protect or reap

                // Git lists the main working tree first; it is the primary checkout.
                bool isPrimary = !primaryAssigned;
                primaryAssigned = true;

                var (info, wasReused) = await BuildInfoAsync(repositoryPath, entry, isPrimary, mainRef, mainCommitId, reuseSignals, sessionsByPath, poolSlotDirectories, stale, ct);
                worktrees.Add(info);
                if (wasReused)
                    reused++;
            }
            _signalCache?.KeepOnly(repositoryPath, rawEntries.Select(e => e.Path));

            var safeCount = worktrees.Count(w => w.Safety == WorktreeSafety.SafeToReap);
            FileLog.Write($"[WorktreeInventoryService] inventory: {worktrees.Count} worktrees, {safeCount} safe to reap, merge signals reused for {reused}");

            return new WorktreeInventory
            {
                RepositoryPath = repositoryPath,
                Worktrees = worktrees,
                Success = true,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeInventoryService] GetInventoryAsync FAILED: {ex.Message}");
            return Failure(repositoryPath, ex.Message);
        }
    }

    private async Task<(WorktreeInfo Info, bool Reused)> BuildInfoAsync(string repositoryPath, RawWorktreeEntry entry, bool isPrimary, string? mainRef, string? mainCommitId, bool reuseSignals, IReadOnlyDictionary<string, List<string>> sessionsByPath, IReadOnlyList<string> poolSlotDirectories, IReadOnlyCollection<string> unrefreshedRemotes, CancellationToken ct)
    {
        // A cc-worktrees pool slot, by either of that tool's own markers: its records, or the layout it
        // creates. Decided before any git question, because the answer does not depend on one.
        bool isPoolSlot = !isPrimary
            && (CcWorktreesPoolSlots.HasSlotLayout(entry.Path)
                || CcWorktreesPoolSlots.IsInside(entry.Path, poolSlotDirectories));

        // Cleanliness is measured inside the worktree itself - on EVERY inventory, because a working-tree
        // edit changes it without moving any commit.
        var statusResult = await _git.RunAsync(entry.Path, new[] { "status", "--porcelain" }, ct);
        int dirtyCount = statusResult.Success
            ? statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
        bool isClean = statusResult.Success && dirtyCount == 0;

        // Everything else depends only on the worktree's commit, its branch and origin/main, so it is
        // reused while none of those moved (plan step 7d). A worktree with no commit yet is never cached.
        WorktreeMergeSignalCache.Key? key = string.IsNullOrEmpty(entry.Head)
            ? null
            : new WorktreeMergeSignalCache.Key(entry.Head, entry.Branch, entry.IsDetached, mainCommitId);
        var signals = reuseSignals && key != null ? _signalCache!.TryGet(repositoryPath, entry.Path, key) : null;
        bool reused = signals != null;
        bool signalsOk = true;
        if (signals == null)
        {
            (signals, signalsOk) = await ComputeSignalsAsync(repositoryPath, entry, isPrimary, mainRef, unrefreshedRemotes, ct);
            // Only a fully successful inspection is remembered: a fail-closed answer is recomputed.
            if (signalsOk && key != null)
                _signalCache?.Store(repositoryPath, entry.Path, key, signals);
        }

        // Every merge signal needs origin/main; if we could not resolve it, fail closed.
        bool inspectionOk = statusResult.Success && mainRef != null && signalsOk;

        // A live session sitting in this worktree (matched by full path) - only meaningful for a
        // non-primary worktree that would otherwise be safe.
        var openSessions = sessionsByPath.TryGetValue(WorktreeReaperService.NormalizePath(entry.Path), out var labels)
            ? labels
            : new List<string>();

        var facts = new WorktreeFacts
        {
            IsPrimary = isPrimary,
            IsDetachedHead = entry.IsDetached,
            IsClean = isClean,
            PullRequestMerged = signals.PullRequestMerged,
            OriginBranchGone = signals.OriginGone,
            ContainedInMain = signals.ContainedInMain,
            DetachedHeadIsAncestorOfMain = signals.DetachedAncestor,
            InspectionSucceeded = inspectionOk,
            HasLiveSession = !isPrimary && openSessions.Count > 0,
            IsCcWorktreesPoolSlot = isPoolSlot,
        };

        var verdict = WorktreeSafetyEvaluator.Evaluate(facts);
        var lastActivity = GetLastActivityUtc(entry.Path, signals);

        var info = new WorktreeInfo
        {
            Path = entry.Path,
            Branch = entry.Branch,
            HeadCommit = entry.Head,
            IsPrimary = isPrimary,
            IsDetachedHead = entry.IsDetached,
            IsClean = isClean,
            DirtyFileCount = dirtyCount,
            AheadOfMain = signals.Ahead,
            BehindMain = signals.Behind,
            HasOpenPullRequest = signals.HasOpenPullRequest,
            LastActivityUtc = lastActivity,
            OpenSessions = openSessions,
            Safety = verdict.Safety,
            Reason = verdict.Reason,
            Explanation = verdict.Explanation,
        };
        return (info, reused);
    }

    /// <summary>
    /// Asks git every question whose answer depends only on the worktree's commit, its branch and
    /// origin/main: the merge signals (C1 to C3), ahead and behind, and the git half of the last
    /// activity. The flag is false when any merge question could not be answered - the caller fails closed.
    /// </summary>
    private async Task<(WorktreeMergeSignalCache.Signals Signals, bool Ok)> ComputeSignalsAsync(string repositoryPath, RawWorktreeEntry entry, bool isPrimary, string? mainRef, IReadOnlyCollection<string> unrefreshedRemotes, CancellationToken ct)
    {
        bool ok = true;
        bool prMerged = false, hasOpenPr = false, originGone = false, containedInMain = false, detachedAncestor = false;
        int ahead = 0, behind = 0;

        if (mainRef != null && !isPrimary)
        {
            if (entry.IsDetached)
            {
                // Detached HEAD: safe only if its commit is an ancestor of origin/main.
                var ancestor = await _git.RunAsync(repositoryPath, new[] { "merge-base", "--is-ancestor", entry.Head, mainRef }, ct);
                // exit 0 => ancestor, exit 1 => not an ancestor. Any other exit is a real error.
                ok &= ancestor.ExitCode is 0 or 1;
                detachedAncestor = ancestor.ExitCode == 0;
            }
            else if (entry.Branch != null)
            {
                // C2: has the CONFIGURED upstream been deleted (after the prune above)? "Gone" only
                // means "merged" when the branch had a configured upstream in the first place - a
                // never-pushed branch has no upstream, and treating that absence as proof-of-merge
                // would mark unpushed work safe to delete. The probe asks the configured remote for
                // the configured ref name, both of which can differ from origin/<local-name>.
                var upstream = await ConfiguredUpstreamProbe.ProbeAsync(_git, repositoryPath, entry.Branch, ct, unrefreshedRemotes);
                ok &= upstream.InspectionSucceeded;
                originGone = upstream.HasConfiguredUpstream && upstream.UpstreamGone;

                // C3: does the branch add anything origin/main lacks? git cherry marks such commits with '+'.
                var cherry = await _git.RunAsync(repositoryPath, new[] { "cherry", mainRef, entry.Branch }, ct);
                ok &= cherry.Success;
                containedInMain = cherry.Success && !HasUniqueCommits(cherry.Output);

                // Ahead/behind counts for the needs-attention display.
                var counts = await _git.RunAsync(repositoryPath, new[] { "rev-list", "--left-right", "--count", $"{mainRef}...{entry.Branch}" }, ct);
                (behind, ahead) = ParseLeftRightCount(counts.Output);

                // C1: authoritative pull-request-merged signal (optional).
                prMerged = await _pullRequestProbe.IsBranchMergedAsync(repositoryPath, entry.Branch, ct);
                hasOpenPr = await _pullRequestProbe.HasOpenPullRequestAsync(repositoryPath, entry.Branch, ct);
            }
        }

        var (reflogPath, lastCommitUtc) = await ReadActivityFromGitAsync(entry.Path, ct);
        var signals = new WorktreeMergeSignalCache.Signals(detachedAncestor, originGone, containedInMain, ahead, behind, prMerged, hasOpenPr, reflogPath, lastCommitUtc);
        return (signals, ok);
    }

    /// <summary>
    /// The git half of "last activity": where the worktree's HEAD reflog lives, and the last commit's
    /// committer date. The HEAD reflog updates on commit/checkout/reset but NOT on a plain status, so
    /// it is a clean signal (unlike the index, which our own status scan can touch).
    /// </summary>
    private async Task<(string? ReflogPath, DateTime? LastCommitUtc)> ReadActivityFromGitAsync(string worktreePath, CancellationToken ct)
    {
        string? reflog = null;
        var reflogPath = await _git.RunAsync(worktreePath, new[] { "rev-parse", "--git-path", "logs/HEAD" }, ct);
        if (reflogPath.Success)
            reflog = ResolveWorktreeRelativePath(worktreePath, reflogPath.Output.Trim());

        DateTime? lastCommit = null;
        var commit = await _git.RunAsync(worktreePath, new[] { "log", "-1", "--format=%ct" }, ct);
        if (commit.Success && long.TryParse(commit.Output.Trim(), out var unixSeconds))
            lastCommit = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

        return (reflog, lastCommit);
    }

    /// <summary>
    /// Best-effort "last activity" timestamp: the most recent of the worktree's last commit, its
    /// top-level folder modification time, and its HEAD reflog. The two file times are read fresh on
    /// every inventory (a stat, not a process); the git answers come from <paramref name="signals"/>.
    /// Only informational - deleting these folders removes real source directories, so a human wants
    /// to see how recently one was touched. Returns null if nothing can be read.
    /// </summary>
    private static DateTime? GetLastActivityUtc(string worktreePath, WorktreeMergeSignalCache.Signals signals)
    {
        DateTime? best = null;

        // Top-level folder modification time (files added/removed/renamed at the root, e.g. builds).
        try
        {
            if (Directory.Exists(worktreePath))
                best = Max(best, Directory.GetLastWriteTimeUtc(worktreePath));
        }
        catch { /* unreadable - fall through to the git signals */ }

        try
        {
            if (signals.ReflogPath != null && File.Exists(signals.ReflogPath))
                best = Max(best, File.GetLastWriteTimeUtc(signals.ReflogPath));
        }
        catch { /* ignore */ }

        if (signals.LastCommitUtc is { } lastCommit)
            best = Max(best, lastCommit);

        return best;
    }

    private static DateTime Max(DateTime? current, DateTime candidate) =>
        current is null || candidate > current.Value ? candidate : current.Value;

    private static string? ResolveWorktreeRelativePath(string worktreePath, string gitPath)
    {
        if (string.IsNullOrWhiteSpace(gitPath))
            return null;
        try
        {
            return Path.IsPathRooted(gitPath) ? gitPath : Path.GetFullPath(Path.Combine(worktreePath, gitPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Groups the live-session refs by normalized worktree path, so a worktree lookup is O(1).
    /// Multiple sessions can sit in the same directory, so the value is a list of labels.
    /// </summary>
    private static IReadOnlyDictionary<string, List<string>> BuildSessionMap(IReadOnlyList<LiveSessionRef>? liveSessions)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (liveSessions == null)
            return map;

        foreach (var s in liveSessions)
        {
            if (string.IsNullOrWhiteSpace(s.RepoPath))
                continue;
            var key = WorktreeReaperService.NormalizePath(s.RepoPath);
            if (!map.TryGetValue(key, out var labels))
                map[key] = labels = new List<string>();
            labels.Add(string.IsNullOrWhiteSpace(s.Label) ? s.RepoPath : s.Label);
        }
        return map;
    }

    /// <summary>True if <c>git cherry</c> output contains a commit the upstream lacks (a line starting with '+').</summary>
    private static bool HasUniqueCommits(string cherryOutput) =>
        cherryOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith('+'));

    /// <summary>
    /// Parses <c>git rev-list --left-right --count A...B</c> output ("&lt;left&gt;\t&lt;right&gt;").
    /// Left is commits in A (origin/main) not B, i.e. how far the branch is behind; right is
    /// commits in B not A, i.e. how far the branch is ahead. Returns (behind, ahead).
    /// </summary>
    private static (int Behind, int Ahead) ParseLeftRightCount(string output)
    {
        var parts = output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var left) && int.TryParse(parts[1], out var right))
            return (left, right);
        return (0, 0);
    }

    /// <summary>The main ref's name and the commit id it points at (the id is part of the signal cache key).</summary>
    private async Task<(string? Name, string? CommitId)> ResolveMainRefAsync(string repositoryPath, CancellationToken ct)
    {
        var main = await _git.RunAsync(repositoryPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (main.Success && !string.IsNullOrWhiteSpace(main.Output))
            return ("origin/main", main.Output.Trim());

        var master = await _git.RunAsync(repositoryPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (master.Success && !string.IsNullOrWhiteSpace(master.Output))
            return ("origin/master", master.Output.Trim());

        FileLog.Write($"[WorktreeInventoryService] could not resolve origin/main or origin/master in {repositoryPath}");
        return (null, null);
    }

    private static WorktreeInventory Failure(string repositoryPath, string error)
    {
        FileLog.Write($"[WorktreeInventoryService] inventory FAILED: {error}");
        return new WorktreeInventory { RepositoryPath = repositoryPath, Success = false, Error = error };
    }
}
