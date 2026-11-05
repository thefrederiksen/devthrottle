using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>One local branch with its sync state and the fail-closed safe-delete verdict.</summary>
public sealed record BranchInfo
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }

    /// <summary>True when the branch is checked out in some linked worktree (never deletable).</summary>
    public bool CheckedOutInWorktree { get; init; }

    /// <summary>The commit the branch pointed at when the verdict was computed - deletion binds to it.</summary>
    public string TipCommit { get; init; } = "";

    public int AheadOfMain { get; init; }
    public int BehindMain { get; init; }
    public bool OriginBranchExists { get; init; }
    public DateTime? LastCommitUtc { get; init; }

    public bool SafeToDelete { get; init; }
    public string Explanation { get; init; } = "";

    /// <summary>
    /// Whether every commit on this branch is already contained in the default branch (issue #2118).
    /// NULL means NOT DETERMINED - there was no detectable default branch, or the containment inspection
    /// failed - and it must never be read as either true or false.
    ///
    /// DELIBERATELY NARROWER THAN <see cref="SafeToDelete"/>, and the two must not be substituted for one
    /// another. Safe-to-delete is a DISPOSAL verdict that also accepts "the origin branch is gone" and "the
    /// pull request merged", and refuses whenever the branch is checked out. Containment is a plain
    /// statement about commits. The morning report needs the plain statement - a branch checked out in a
    /// worktree is exactly the case it wants to report on, and safe-to-delete says false about it for a
    /// reason that has nothing to do with whether the work is merged.
    /// </summary>
    public bool? MergedIntoDefault { get; init; }
}

/// <summary>
/// A repository's branch inventory plus the default branch every verdict in it was measured against
/// (issue #2118). The default branch is carried WITH the branches, rather than resolved a second time by
/// the caller, because a caller that resolved it independently could disagree with the service that
/// computed the containment - and a "merged into main" claim measured against a different ref than the
/// caller believes is exactly the kind of confidently-wrong statement the morning report must not make.
/// A NULL <see cref="DefaultBranch"/> means it was undetectable, and then every branch's
/// <see cref="BranchInfo.MergedIntoDefault"/> is null too.
/// </summary>
public sealed record BranchInventory
{
    /// <summary>The ref every containment verdict was measured against (e.g. "origin/main"), or null.</summary>
    public string? DefaultBranch { get; init; }

    public IReadOnlyList<BranchInfo> Branches { get; init; } = Array.Empty<BranchInfo>();
}

/// <summary>
/// The pure safe-delete decision for a local branch. Deleting a local branch makes its commits
/// unreachable if they are not held elsewhere, so this is fail-closed: deletable ONLY when the
/// merge is POSITIVELY confirmed - a merged pull request, or the commits contained in origin/main -
/// and never the current branch, never one checked out in a worktree. No signal = not deletable.
///
/// Policy split (issue 516, blocker: a deleted remote branch is NOT proof of merge). Unlike worktree
/// removal - which leaves the branch ref in place, so the commits stay reachable - branch deletion
/// removes the last local reference to the commits. A missing remote branch does not prove those
/// commits reached origin/main: a pull request can be closed without merging and its head deleted,
/// or the remote ref can be deleted directly. So the origin-branch-gone signal, which remains
/// sufficient for reaping a clean worktree (see <see cref="WorktreeSafetyEvaluator"/>), is NOT
/// sufficient here. It is accepted only as a helping hand toward a clearer refusal message.
/// </summary>
public static class BranchSafetyEvaluator
{
    public static (bool Safe, string Explanation) Evaluate(
        bool isCurrent,
        bool checkedOutInWorktree,
        bool inspectionSucceeded,
        bool pullRequestMerged,
        bool originBranchGone,
        bool containedInMain)
    {
        if (isCurrent)
            return (false, "The branch you are on - switch away before deleting.");
        if (checkedOutInWorktree)
            return (false, "Checked out in a worktree - remove the worktree first.");
        if (!inspectionSucceeded)
            return (false, "Could not inspect this branch - treated as unsafe.");
        if (pullRequestMerged)
            return (true, "Pull request merged.");
        if (containedInMain)
            return (true, "All commits already contained in origin/main.");
        if (originBranchGone)
            // Deliberately NOT safe (issue 516). A gone remote branch does not prove a merge, and
            // deleting the local branch would strand its commits. Say so plainly.
            return (false, "Remote branch is gone, but its commits are not proven to be in origin/main - deleting could strand them. Confirm the merge first.");
        return (false, "Has commits not proven to be in origin/main.");
    }
}

/// <summary>
/// Lists local branches with sync state and the safe-delete verdict, and deletes branches that
/// verdict allows - re-verifying at the moment of deletion (state can change between render and
/// click). Reuses the same git signals as the worktree detector.
/// </summary>
public sealed class GitBranchService
{
    /// <summary>git's "expected old value" meaning "the ref must not exist" - makes a restore
    /// create-only, so a concurrently recreated branch is never overwritten (ruling R3-1).</summary>
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    private readonly GitCommandRunner _git;
    private readonly IMergedPullRequestProbe _pullRequestProbe;

    public GitBranchService(GitCommandRunner? git = null, IMergedPullRequestProbe? pullRequestProbe = null)
    {
        _git = git ?? new GitCommandRunner();
        _pullRequestProbe = pullRequestProbe ?? new NullMergedPullRequestProbe();
    }

    public async Task<IReadOnlyList<BranchInfo>> ListAsync(string repoPath, CancellationToken ct = default)
        => (await ListInventoryAsync(repoPath, ct)).Branches;

    /// <summary>
    /// The branch list AND the default branch it was measured against (issue #2118). <see cref="ListAsync"/>
    /// is this method's branches; nothing computes a branch verdict twice.
    /// </summary>
    public async Task<BranchInventory> ListInventoryAsync(string repoPath, CancellationToken ct = default)
    {
        FileLog.Write($"[GitBranchService] ListInventoryAsync: {repoPath}");
        var results = new List<BranchInfo>();

        var mainRef = await ResolveMainRefAsync(repoPath, ct);

        // Branch list with current marker, upstream, and last commit date in one call.
        var list = await _git.RunAsync(repoPath, new[]
        {
            "for-each-ref", "refs/heads",
            "--format=%(HEAD)%09%(refname:short)%09%(committerdate:unix)%09%(objectname)"
        }, ct);
        if (!list.Success)
            return new BranchInventory { DefaultBranch = mainRef, Branches = results };

        // Branches held by linked worktrees (never deletable from under them).
        var worktreeBranches = await BranchesCheckedOutInWorktreesAsync(repoPath, ct);

        // Every branch's configured upstream and whether its remote branch is gone, read for the
        // whole repository in two git processes rather than three per branch.
        var upstreams = await ConfiguredUpstreamProbe.ReadAsync(_git, repoPath, ct);

        foreach (var raw in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length < 4)
                continue;
            bool isCurrent = parts[0].Trim() == "*";
            var name = parts[1].Trim();
            DateTime? lastCommit = long.TryParse(parts[2].Trim(), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;
            var tip = parts[3].Trim();

            bool inspectionOk = mainRef != null;
            bool originGone = false, containedInMain = false, prMerged = false;
            int ahead = 0, behind = 0;
            // Null until the containment inspection actually succeeds (issue #2118). With no default branch
            // there is nothing to be contained in, and a failed `git cherry` proves nothing either way - so
            // both stay "not determined" rather than collapsing to the false that `containedInMain` holds.
            bool? mergedIntoDefault = null;

            if (mainRef != null)
            {
                // "Upstream gone" only proves a merge when the branch HAD a configured upstream -
                // a never-pushed branch's absence from the remote proves nothing (same rule as
                // worktrees). The probe asks the configured remote for the configured ref name,
                // both of which can differ from origin/<local-name>.
                var upstream = upstreams.Verdict(name);
                inspectionOk &= upstream.InspectionSucceeded;
                originGone = upstream.HasConfiguredUpstream && upstream.UpstreamGone;

                var cherry = await _git.RunAsync(repoPath, new[] { "cherry", mainRef, name }, ct);
                inspectionOk &= cherry.Success;
                containedInMain = cherry.Success && !cherry.Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(l => l.TrimStart().StartsWith('+'));
                if (cherry.Success)
                    mergedIntoDefault = containedInMain;

                var counts = await _git.RunAsync(repoPath, new[] { "rev-list", "--left-right", "--count", $"{mainRef}...{name}" }, ct);
                var nums = counts.Output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 2 && int.TryParse(nums[0], out var l) && int.TryParse(nums[1], out var r))
                    (behind, ahead) = (l, r);

                prMerged = await _pullRequestProbe.IsBranchMergedAsync(repoPath, name, ct);
            }

            bool inWorktree = worktreeBranches.Contains(name);
            var (safe, why) = BranchSafetyEvaluator.Evaluate(isCurrent, inWorktree, inspectionOk, prMerged, originGone, containedInMain);

            results.Add(new BranchInfo
            {
                Name = name,
                IsCurrent = isCurrent,
                CheckedOutInWorktree = inWorktree,
                TipCommit = tip,
                AheadOfMain = ahead,
                BehindMain = behind,
                OriginBranchExists = !originGone && mainRef != null,
                LastCommitUtc = lastCommit,
                SafeToDelete = safe,
                Explanation = why,
                MergedIntoDefault = mergedIntoDefault,
            });
        }

        return new BranchInventory { DefaultBranch = mainRef, Branches = results };
    }

    /// <summary>
    /// Deletes a branch AFTER re-verifying its safe-delete verdict right now. Fail closed: any
    /// verdict other than safe refuses, with the reason. The deletion binds to the verified tip:
    /// if a concurrent commit moves the branch between the verdict and the delete, git refuses
    /// and the branch survives.
    /// </summary>
    public async Task<(bool Deleted, string Message)> DeleteIfSafeAsync(string repoPath, string branch, CancellationToken ct = default)
    {
        FileLog.Write($"[GitBranchService] DeleteIfSafeAsync: {branch} in {repoPath}");

        // Refresh remote state before proving the merge (inspection): the containment check runs
        // `git cherry` against the LOCAL origin/main ref, which can be stale - remote main may have
        // been rewritten to drop the very commits this branch carries. A failed fetch means the
        // merge signals cannot be trusted, so this destructive path ABORTS rather than deleting a
        // branch on stale containment and stranding its commits.
        // Fetch ORIGIN by name (inspection): a bare "git fetch --prune" fetches the current branch's
        // configured upstream, which can be a different remote, leaving origin/main - the ref the
        // containment check (git cherry origin/main) uses - stale.
        var fetch = await _git.RunAsync(repoPath, new[] { "fetch", "--prune", "origin" }, ct);
        if (!fetch.Success)
            return (false, $"could not refresh remote state (git fetch failed) - not deleted so nothing is stranded on stale merge signals: {fetch.Error.Trim()}");

        var current = (await ListAsync(repoPath, ct)).FirstOrDefault(b => b.Name == branch);
        if (current is null)
            return (false, $"branch not found: {branch}");
        if (!current.SafeToDelete)
            return (false, current.Explanation);
        if (string.IsNullOrWhiteSpace(current.TipCommit))
            return (false, $"could not read the verified tip of {branch} - not deleted");

        return await DeleteAtVerifiedTipAsync(repoPath, branch, current.TipCommit, current.Explanation, ct);
    }

    /// <summary>
    /// The atomic delete: <c>git update-ref -d refs/heads/&lt;name&gt; &lt;verified-sha&gt;</c>.
    /// Git refuses when the ref no longer points at the verified commit, which closes the
    /// time-of-check to time-of-use window a plain force delete leaves open.
    ///
    /// update-ref binds the TIP but - unlike <c>git branch -D</c> - it does NOT refuse to delete
    /// a branch that is checked out. A checkout into a worktree can land between verification and
    /// deletion, which would leave that worktree with a broken symbolic HEAD. Compensating
    /// post-check: immediately after a successful delete the worktrees are listed; if any worktree
    /// HEAD still references the deleted branch, the ref is RESTORED at the verified sha (we hold
    /// the exact commit, so restoration is lossless) and the delete is reported as refused. The
    /// restore is CREATE-ONLY (ruling R3-1, expected old value of forty zeros): a branch another
    /// process recreated in the window stands untouched, and the outcome reports that a branch of
    /// the same name has since appeared.
    ///
    /// The destructive act and its compensation are ONE non-cancellable unit (rulings R3-2 and
    /// R4-1): the caller's cancellation applies BEFORE the delete only - the token is checked
    /// immediately before the delete is issued, and the delete command itself plus everything
    /// after it run on <see cref="CancellationToken.None"/>. A cancellable process wait could
    /// throw AFTER the child git process already deleted the ref, concealing the completed
    /// mutation and skipping compensation. The whole post-delete phase is additionally wrapped
    /// so that ANY exception inside compensation still attempts the create-only restore on the
    /// way out before the exception propagates - no exit path can leave the ref missing without
    /// having proven the delete safe.
    ///
    /// On a confirmed delete the branch's config section is cleaned up, as <c>git branch -D</c>
    /// would have done - but only after confirming the ref is still absent at that moment, so a
    /// branch another process just recreated does not lose ITS configuration. The residual
    /// millisecond window between that check and the removal is accepted and documented: worst
    /// case a just-recreated branch loses its tracking config - annoying, never destructive to
    /// commits.
    /// </summary>
    internal async Task<(bool Deleted, string Message)> DeleteAtVerifiedTipAsync(
        string repoPath, string branch, string verifiedSha, string explanation, CancellationToken ct = default)
    {
        // The caller's cancellation applies BEFORE the destructive act only (ruling R4-1): it
        // is honored here and never again. The delete command itself runs on
        // CancellationToken.None - a cancellable process wait can throw AFTER the child git
        // process already deleted the ref, which would conceal the completed mutation and skip
        // every compensation step below.
        ct.ThrowIfCancellationRequested();

        // The DELETE COMMAND ITSELF sits inside a recovery boundary (round-5 finding): the
        // process layer or an injected runner can throw AFTER the child git process already
        // deleted the ref, which would otherwise bypass every compensation step. The
        // create-only restore is safe in both directions - if the delete never happened the
        // ref still exists and git refuses the create - so the restore-on-the-way-out covers
        // "threw before mutation" and "threw after mutation" without knowing which occurred.
        //
        // The REFUSED-delete path sits OUTSIDE every recovery boundary (round-6 finding): a
        // refusal means git verified the ref did NOT match the verified sha and mutated
        // nothing - a restore attempt there could RECREATE a branch some other process had
        // legitimately deleted in full. The refusal is reported between the two boundaries,
        // where no exception - not even a throwing log line - can reroute it into a restore.
        //
        // Everything from the delete onward is a NON-CANCELLABLE phase (rulings R3-2, R4-1):
        // the worktree listing, the restore decision, and the config cleanup all run on
        // CancellationToken.None, because honoring the caller's token after the destructive
        // step could skip the worktree check and the restore.
        GitCommandResult del;
        try
        {
            del = await _git.RunAsync(repoPath, new[] { "update-ref", "-d", $"refs/heads/{branch}", verifiedSha }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await RestoreOnTheWayOutAsync(repoPath, branch, verifiedSha, ex);
            throw;
        }

        if (!del.Success)
        {
            FileLog.Write($"[GitBranchService] delete refused for {branch}: ref no longer at {verifiedSha}: {del.Error.Trim()}");
            return (false, $"branch moved since it was verified - not deleted");
        }

        try
        {
            return await CompensateAfterDeleteAsync(repoPath, branch, verifiedSha, explanation);
        }
        catch (Exception ex)
        {
            await RestoreOnTheWayOutAsync(repoPath, branch, verifiedSha, ex);
            throw;
        }
    }

    /// <summary>
    /// The restore attempted when the delete or its compensation ESCAPES with an exception
    /// (ruling R4-1): the delete was never proven safe, so the ref must not stay missing.
    /// Create-only, so a concurrently recreated branch is never overwritten - and harmless
    /// when the delete never actually happened, because the still-existing ref refuses the
    /// create. Never throws: the caller rethrows the ORIGINAL exception right after this,
    /// and nothing here - not the restore, not a log line - may replace it (round-5
    /// finding: logging in this path is SafeLog on purpose).
    /// </summary>
    private async Task RestoreOnTheWayOutAsync(string repoPath, string branch, string verifiedSha, Exception cause)
    {
        // The restore attempt comes FIRST (round-7 finding): no diagnostic may precede it,
        // because even building the log string evaluates the cause's Message property - which
        // is VIRTUAL and can itself throw - before SafeLog could suppress anything. All
        // logging here is best-effort and after the fact, with message extraction guarded.
        try
        {
            var restore = await _git.RunAsync(repoPath, new[] { "update-ref", $"refs/heads/{branch}", verifiedSha, ZeroSha }, CancellationToken.None);
            SafeLog(restore.Success
                ? $"[GitBranchService] restore-on-the-way-out: {branch} restored at {verifiedSha} after: {SafeMessage(cause)}"
                : $"[GitBranchService] restore-on-the-way-out for {branch} was refused (the ref may already exist again): {restore.Error.Trim()} - initiating failure: {SafeMessage(cause)}");
        }
        catch (Exception restoreEx)
        {
            SafeLog($"[GitBranchService] restore-on-the-way-out for {branch} itself failed - recreate the ref with: git update-ref refs/heads/{branch} {verifiedSha}: {SafeMessage(restoreEx)} - initiating failure: {SafeMessage(cause)}");
        }
    }

    /// <summary>Message extraction that cannot throw: Exception.Message is virtual, and an
    /// override that throws must not be able to derail the recovery path it is describing.</summary>
    private static string SafeMessage(Exception ex)
    {
        try { return ex.Message; }
        catch { return ex.GetType().Name; }
    }

    /// <summary>
    /// Logging inside the destructive-recovery path must never be the reason recovery fails:
    /// FileLog.Write can throw during a concurrent logger shutdown, which would skip the
    /// restore or replace the original exception with a logging failure. Best-effort by
    /// design HERE AND ONLY HERE - everywhere else a logging failure surfaces normally.
    /// </summary>
    private static void SafeLog(string message)
    {
        try { FileLog.Write(message); }
        catch { }
    }

    /// <summary>
    /// The post-delete compensation phase (rulings R2-1, R3-1, R3-2, R4-1): worktree check,
    /// create-only restore decision, and config cleanup. Runs entirely on
    /// <see cref="CancellationToken.None"/>; the caller wraps it so any exception thrown here
    /// still attempts the create-only restore before propagating.
    /// </summary>
    private async Task<(bool Deleted, string Message)> CompensateAfterDeleteAsync(
        string repoPath, string branch, string verifiedSha, string explanation)
    {
        // The compensating post-check (ruling R2-1). Includes the FIRST entry too: the primary
        // checkout is just as broken by losing its checked-out branch as a linked worktree is.
        var wtList = await _git.RunAsync(repoPath, new[] { "worktree", "list", "--porcelain" }, CancellationToken.None);
        bool mustRestore;
        string refusal;
        if (!wtList.Success)
        {
            // Cannot PROVE no worktree holds the branch - fail closed and put the ref back.
            FileLog.Write($"[GitBranchService] worktree list failed after deleting {branch} - restoring: {wtList.Error.Trim()}");
            mustRestore = true;
            refusal = "could not verify the worktrees after deletion - the branch was restored, not deleted";
        }
        else
        {
            mustRestore = WorktreeListParser.Parse(wtList.Output).Any(e => e.Branch == branch);
            refusal = "branch is checked out in a worktree - restored, not deleted";
        }
        if (mustRestore)
        {
            // CREATE-ONLY restore (ruling R3-1): the expected-old-value of forty zeros makes git
            // refuse when the ref exists again. Another process can recreate the branch at a NEW
            // commit between our delete and this restore - an unconditional restore would silently
            // rewind that new branch to the verified sha and discard its tip.
            var restore = await _git.RunAsync(repoPath, new[] { "update-ref", $"refs/heads/{branch}", verifiedSha, ZeroSha }, CancellationToken.None);
            if (!restore.Success)
            {
                // Two distinct outcomes hide behind a refused create: either the ref exists again
                // (the concurrent recreation - the new branch stands, and the worktree HEAD is
                // valid again by that very fact), or the restore genuinely failed (loud error).
                var recreated = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}" }, CancellationToken.None);
                if (recreated.Success)
                {
                    FileLog.Write($"[GitBranchService] {branch} was recreated by another process after deletion - the new branch stands, not restored");
                    return (true, $"deleted {branch} ({explanation}) - a branch of the same name has since appeared and was left untouched");
                }
                // Restoration failing is a real error and must be loud: the commit still exists,
                // and the exact command to recreate the ref is part of the message.
                FileLog.Write($"[GitBranchService] RESTORE FAILED for {branch} at {verifiedSha}: {restore.Error.Trim()}");
                return (false, $"branch is checked out in a worktree and restoring it failed - run: git update-ref refs/heads/{branch} {verifiedSha}");
            }
            FileLog.Write($"[GitBranchService] {branch} is checked out in a worktree - restored at {verifiedSha}, not deleted");
            return (false, refusal);
        }

        // Config cleanup only when the ref is confirmed absent RIGHT NOW - another process may
        // have recreated the branch after our delete, and the new branch's config is not ours.
        var refCheck = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}" }, CancellationToken.None);
        if (refCheck.Success)
        {
            FileLog.Write($"[GitBranchService] {branch} was recreated after deletion - leaving the new branch's config in place");
            return (true, $"deleted {branch} ({explanation})");
        }

        // "rev-parse --verify --quiet" exits 1 for a missing ref - ONLY that exact outcome
        // proves absence (ruling R4-2). Any other failure - a transient error, a repository
        // problem - proves nothing, and cleaning up on it could strip a live branch's tracking
        // configuration. The stale section is inert; a wrong cleanup is not, so skip.
        if (refCheck.ExitCode != 1)
        {
            FileLog.Write($"[GitBranchService] ref probe for {branch} failed with exit {refCheck.ExitCode} (not the missing-ref outcome) - leaving the config section alone: {refCheck.Error.Trim()}");
            return (true, $"deleted {branch} ({explanation})");
        }

        // git branch -D removes the branch's config section; update-ref does not, so clean it up.
        // A branch without any config has no section - that outcome is expected, not an error.
        var cleanup = await _git.RunAsync(repoPath, new[] { "config", "--remove-section", $"branch.{branch}" }, CancellationToken.None);
        if (!cleanup.Success && !cleanup.Error.Contains("no such section", StringComparison.OrdinalIgnoreCase))
            FileLog.Write($"[GitBranchService] config cleanup for {branch} reported: {cleanup.Error.Trim()}");

        return (true, $"deleted {branch} ({explanation})");
    }

    private async Task<HashSet<string>> BranchesCheckedOutInWorktreesAsync(string repoPath, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = await _git.RunAsync(repoPath, new[] { "worktree", "list", "--porcelain" }, ct);
        if (!list.Success)
            return set;
        bool first = true; // the first entry is the primary checkout - its branch is "current", not worktree-held
        foreach (var entry in WorktreeListParser.Parse(list.Output))
        {
            if (first) { first = false; continue; }
            if (entry.Branch != null)
                set.Add(entry.Branch);
        }
        return set;
    }

    private async Task<string?> ResolveMainRefAsync(string repoPath, CancellationToken ct)
    {
        var main = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (main.Success && !string.IsNullOrWhiteSpace(main.Output))
            return "origin/main";
        var master = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (master.Success && !string.IsNullOrWhiteSpace(master.Output))
            return "origin/master";
        return null;
    }
}
