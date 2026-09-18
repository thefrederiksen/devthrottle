using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>The result of removing one worktree, including its proof-of-safety for the audit log.</summary>
public sealed class ReapOutcome
{
    public string Path { get; init; } = "";
    public string? Branch { get; init; }

    /// <summary>Why this worktree was safe to remove (logged for the audit trail).</summary>
    public string Proof { get; init; } = "";

    /// <summary>True only when the worktree was fully removed (deregistered and the folder deleted).</summary>
    public bool Removed { get; init; }

    /// <summary>Set when the folder could not be fully deleted (e.g. Windows-locked build outputs).</summary>
    public string? Leftover { get; init; }

    public string? Error { get; init; }

    public static ReapOutcome RemovedOk(WorktreeInfo w) =>
        new() { Path = WorktreeReaperService.NormalizePath(w.Path), Branch = w.Branch, Proof = w.Explanation, Removed = true };

    public static ReapOutcome LeftBehind(WorktreeInfo w) =>
        new()
        {
            Path = WorktreeReaperService.NormalizePath(w.Path),
            Branch = w.Branch,
            Proof = w.Explanation,
            Removed = false,
            Leftover = WorktreeReaperService.NormalizePath(w.Path),
            Error = "folder could not be fully deleted (files are locked)",
        };

    public static ReapOutcome Failed(WorktreeInfo w, string error) =>
        new() { Path = WorktreeReaperService.NormalizePath(w.Path), Branch = w.Branch, Proof = w.Explanation, Removed = false, Error = error };
}

/// <summary>The result of a reap run: what was removed, what folders remain, and what was skipped.</summary>
public sealed class ReapResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ReapOutcome> Outcomes { get; init; } = Array.Empty<ReapOutcome>();

    /// <summary>Folders that could not be fully deleted - reported exactly rather than claimed removed.</summary>
    public IReadOnlyList<string> Leftovers { get; init; } = Array.Empty<string>();

    /// <summary>Safe worktrees deliberately NOT removed because a live session is using them.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public string? Error { get; init; }

    public int RemovedCount => Outcomes.Count(o => o.Removed);

    public static ReapResult Failure(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Removes worktrees that are provably safe to reap. It re-runs the detector immediately before
/// acting (state can change between the scan and the click), removes ONLY the safe set, protects
/// the primary checkout and any worktree a live session is using, and - crucially - reports any
/// folder it could not physically delete rather than claiming success. It only ever removes
/// worktrees; it never merges, rebases, force-pushes, or deletes origin branches.
/// </summary>
public sealed class WorktreeReaperService
{
    /// <summary>
    /// A worktree touched within this window is held back from reaping (owner's suggestion): a live
    /// session working in a worktree keeps touching files, so a recent last-activity is a cheap,
    /// roster-independent signal that something may be using it. This is a defense-in-depth layer on
    /// top of the authoritative roster - it does not replace it (an idle session writes nothing, so
    /// only the roster protects that case).
    /// </summary>
    public static readonly TimeSpan ActivityCoolingOff = TimeSpan.FromMinutes(10);

    private readonly WorktreeInventoryService _inventory;
    private readonly GitCommandRunner _git;
    private readonly Func<DateTime> _utcNow;
    private readonly WorktreeReservationStore _reservations;
    private readonly WorktreeLeftoverStore _leftovers;
    private readonly CcWorktreesPoolSlots _poolSlots;

    public WorktreeReaperService(
        WorktreeInventoryService? inventory = null,
        GitCommandRunner? git = null,
        Func<DateTime>? utcNow = null,
        WorktreeReservationStore? reservations = null,
        WorktreeLeftoverStore? leftovers = null,
        CcWorktreesPoolSlots? poolSlots = null)
    {
        _git = git ?? new GitCommandRunner();
        _poolSlots = poolSlots ?? new CcWorktreesPoolSlots();
        _inventory = inventory ?? new WorktreeInventoryService(_git, poolSlots: _poolSlots);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _reservations = reservations ?? new WorktreeReservationStore();
        _leftovers = leftovers ?? new WorktreeLeftoverStore();
    }

    /// <summary>
    /// Re-checks safety and removes every currently safe-to-reap worktree, holding back any that a
    /// live session is running in.
    ///
    /// FAIL CLOSED (issue 516). Removing a worktree from under a running session is destructive, so
    /// this boundary must positively confirm which worktrees are in use before it acts. It obtains
    /// the authoritative live-session roster from <paramref name="liveSessionsProvider"/> AFTER the
    /// fetch below - as late as possible before acting, so a session that started during the (slow)
    /// fetch is still seen - and feeds it into the safety recompute so an in-use worktree is
    /// classified out of the safe set at the destructive step, not merely caught by a set frozen
    /// earlier. If the roster cannot be read (no provider, an exception, or a null result) the reap
    /// ABORTS and removes nothing. There is deliberately no "best effort with no sessions" path: a
    /// roster failure that silently became an empty protected set is exactly the hazard this closes.
    /// </summary>
    public async Task<ReapResult> ReapAsync(
        string repositoryPath,
        Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? liveSessionsProvider,
        IReadOnlySet<string>? approvedPaths = null,
        CancellationToken ct = default)
    {
        FileLog.Write($"[WorktreeReaperService] ReapAsync: repo={repositoryPath}");
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return ReapResult.Failure($"repository path not found: {repositoryPath}");

            if (liveSessionsProvider is null)
                return ReapResult.Failure("cannot confirm which worktrees are in use by live sessions - reap aborted");

            // Refresh remote-tracking refs first so the merge signals are current. A FAILED fetch
            // means the merge proof (contained-in-origin/main, origin-branch-gone) would be computed
            // against STALE remote state - remote main may have been rewritten to drop the very
            // commits a worktree carries. This is a destructive path, so a failed fetch ABORTS the
            // reap rather than acting on stale signals (issue 516 / inspection).
            // Fetch ORIGIN by name (inspection): a bare "git fetch --prune" fetches the CURRENT
            // branch's configured upstream, which can be a different remote (e.g. "upstream"), leaving
            // origin/main - the ref the containment proof uses - stale. Naming origin refreshes the
            // exact ref the proof trusts.
            var fetch = await _git.RunAsync(repositoryPath, new[] { "fetch", "--prune", "origin" }, ct);
            if (!fetch.Success)
            {
                FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - git fetch failed, remote state is stale: {fetch.Error.Trim()}");
                return ReapResult.Failure($"could not refresh remote state (git fetch failed) - reap aborted so nothing is removed on stale merge signals: {fetch.Error.Trim()}");
            }

            // ...THEN read the authoritative live-session roster. Any failure aborts - never a silent
            // fall-through to "no sessions". Cancellation is the caller superseding us and propagates.
            IReadOnlyList<LiveSessionRef> liveSessions;
            try
            {
                liveSessions = await liveSessionsProvider(ct)
                    ?? throw new InvalidOperationException("the live-session source returned no result");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - could not confirm live sessions: {ex.Message}");
                return ReapResult.Failure($"could not confirm which worktrees are in use by live sessions - reap aborted: {ex.Message}");
            }

            // Recompute safety WITH the roster in hand (never session-blind). fetchPrune is false
            // because we already pruned above; doing so before reading the roster would have frozen
            // it too early.
            var inventory = await _inventory.GetInventoryAsync(repositoryPath, fetchPrune: false, liveSessions, ct);
            if (!inventory.Success)
                return ReapResult.Failure($"could not enumerate worktrees: {inventory.Error}");

            // The roster is re-read INSIDE the loop, immediately before each removal (see below), so
            // a session that started during the slow inventory - or after an earlier worktree's
            // removal - is still seen. This is the tightest practical check-to-remove window.

            // The owner-approved set from the confirmation, normalized for comparison. Null means
            // no confirmation gate (a non-interactive caller reaps every currently-safe worktree).
            var approvedNormalized = approvedPaths is null
                ? null
                : new HashSet<string>(approvedPaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);

            // Retry folders a PAST reap deregistered but could not physically delete (a locked build
            // output). git no longer lists them as worktrees, so no inventory can rediscover them -
            // without this they leak forever despite the UI promising a retry (inspection round 4). The
            // retry re-reads reservations and re-checks git registration under the lock (round 5).
            await RetryPersistedLeftovers(repositoryPath, ct);

            var outcomes = new List<ReapOutcome>();
            // Worktrees held back because a live session is running in them - reported so the user
            // sees they were deliberately spared.
            var skipped = inventory.InUseBySession.Select(w => NormalizePath(w.Path)).ToList();

            // Builds the final result, ALWAYS carrying the outcomes accumulated so far (inspection): a
            // mid-loop abort must still report what it already removed, never a bare failure that hides
            // a completed destructive delete.
            ReapResult BuildResult(string? error)
            {
                var lo = outcomes.Where(o => o.Leftover != null).Select(o => o.Leftover!).ToList();
                return new ReapResult
                {
                    Success = error is null && outcomes.All(o => o.Removed),
                    Outcomes = outcomes,
                    Leftovers = lo,
                    Skipped = skipped.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Error = error,
                };
            }

            foreach (var worktree in inventory.SafeToReap)
            {
                var normalized = NormalizePath(worktree.Path);

                // Act ONLY on worktrees the owner approved at the confirmation (issue 516). A
                // worktree that became safe AFTER the confirmation opened was never shown or
                // approved, so it must not be removed here - it appears on the next refresh for the
                // owner to approve deliberately.
                if (approvedNormalized != null && !approvedNormalized.Contains(normalized))
                {
                    FileLog.Write($"[WorktreeReaperService] SKIP (not in the owner-approved set): {worktree.Path}");
                    continue;
                }

                // Re-read the AUTHORITATIVE roster IMMEDIATELY before removing THIS worktree
                // (inspection): a session that started after the inventory, or after an earlier
                // worktree in this loop was removed, is seen here. Same fail-closed rule - a roster
                // failure aborts the whole reap. KNOWN RESIDUAL: a sub-git-operation window remains
                // between this check and git's own removal; closing it fully needs a reservation the
                // session-launch path shares with the reaper, which is follow-up work (QA report).
                IReadOnlyList<LiveSessionRef> rosterNow;
                try
                {
                    rosterNow = await liveSessionsProvider(ct)
                        ?? throw new InvalidOperationException("the live-session source returned no result");
                }
                catch (OperationCanceledException)
                {
                    // The caller superseded us. Preserve the record of what was already removed
                    // (inspection round 5) - a bare cancellation that discarded completed destructive
                    // deletes would hide them. Prune with a non-cancellable token so cleanup still runs.
                    FileLog.Write($"[WorktreeReaperService] ReapAsync cancelled before re-confirming live sessions for {worktree.Path}");
                    await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);
                    return BuildResult($"reap cancelled after removing {outcomes.Count(o => o.Removed)} worktree(s)");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - could not re-confirm live sessions before removing {worktree.Path}: {ex.Message}");
                    // Preserve what was already removed (inspection): prune, then report the outcomes
                    // so far plus the abort reason - never discard the record of a completed delete.
                    await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);
                    return BuildResult($"reap aborted after removing {outcomes.Count(o => o.Removed)} worktree(s) - could not re-confirm live sessions before removing {worktree.Path}: {ex.Message}");
                }
                if (RosterProtects(normalized, rosterNow))
                {
                    FileLog.Write($"[WorktreeReaperService] SKIP (a live session is using it): {worktree.Path}");
                    skipped.Add(normalized);
                    continue;
                }

                // Cooling-off (owner's suggestion): hold back a worktree touched within the last
                // few minutes. A live session working here keeps touching files, so recent activity
                // is a cheap, roster-independent guard against deleting one that is in use - covering
                // the split-second where a just-started session is not on the roster yet. Cheap, so
                // done before taking the lock.
                if (worktree.LastActivityUtc is { } activity && _utcNow() - activity < ActivityCoolingOff)
                {
                    FileLog.Write($"[WorktreeReaperService] SKIP (touched within the cooling-off window - may be in use): {worktree.Path}");
                    skipped.Add(normalized);
                    continue;
                }

                // THE HARD GUARANTEE (inspection round 5). Enter the machine-wide critical section that
                // serializes a session-launch reservation write against this reservation check PLUS the
                // destructive removal. Either a session reserved this worktree before we read (we see it
                // and skip) or it cannot reserve until after we finish removing (it then launches into
                // an already-gone worktree, which fails - correct). There is no check-to-remove gap.
                try
                {
                    using var crit = _reservations.EnterCriticalSection();

                    // Re-read reservations INSIDE the lock, as late as possible. A read failure is
                    // "cannot tell", not "none": it FAILS CLOSED and aborts the reap.
                    IReadOnlySet<string> reservedNow;
                    try
                    {
                        reservedNow = _reservations.LiveReservedPaths();
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - could not read live reservations before removing {worktree.Path}: {ex.Message}");
                        await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);
                        return BuildResult($"reap aborted after removing {outcomes.Count(o => o.Removed)} worktree(s) - could not confirm live reservations before removing {worktree.Path}: {ex.Message}");
                    }

                    if (reservedNow.Any(p => PathCoversWorktree(normalized, p)))
                    {
                        FileLog.Write($"[WorktreeReaperService] SKIP (a live session reservation covers it): {worktree.Path}");
                        skipped.Add(normalized);
                        continue;
                    }

                    // A cc-worktrees pool slot is never removed here, whatever the inventory concluded.
                    // The inventory already classifies one out of the safe set; this is the second,
                    // independent check at the destructive step itself, for the same reason the roster
                    // and the reservations are re-read here - the decision and the act are not the same
                    // moment, and this is the moment that cannot be undone. Records that exist and
                    // cannot be read abort the reap rather than letting "cannot tell" pass as "no".
                    bool ownedByPool;
                    try
                    {
                        ownedByPool = _poolSlots.Owns(worktree.Path);
                    }
                    catch (CcWorktreesStateUnreadableException ex)
                    {
                        FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - could not tell whether {worktree.Path} belongs to a cc-worktrees pool: {ex.Message}");
                        await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);
                        return BuildResult($"reap aborted after removing {outcomes.Count(o => o.Removed)} worktree(s) - could not tell whether {worktree.Path} belongs to a cc-worktrees pool: {ex.Message}");
                    }
                    if (ownedByPool)
                    {
                        FileLog.Write($"[WorktreeReaperService] SKIP (a cc-worktrees pool slot - that tool owns it): {worktree.Path}");
                        skipped.Add(normalized);
                        continue;
                    }

                    outcomes.Add(await RemoveOneAsync(repositoryPath, worktree, ct));
                }
                catch (OperationCanceledException)
                {
                    FileLog.Write($"[WorktreeReaperService] ReapAsync cancelled around removing {worktree.Path}");
                    await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);
                    return BuildResult($"reap cancelled after removing {outcomes.Count(o => o.Removed)} worktree(s)");
                }
                catch (TimeoutException ex)
                {
                    FileLog.Write($"[WorktreeReaperService] ReapAsync aborted - could not acquire the worktree-reap lock before removing {worktree.Path}: {ex.Message}");
                    return BuildResult($"reap aborted after removing {outcomes.Count(o => o.Removed)} worktree(s) - could not acquire the worktree-reap lock before removing {worktree.Path}: {ex.Message}");
                }
            }

            // One prune at the end clears any admin entries whose directories are now gone. Non-cancellable
            // (inspection round 6): a cancel here would throw AFTER destructive removals completed and
            // discard the outcome record; the removals are already done, so this cleanup must finish.
            await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);

            var result = BuildResult(null);
            FileLog.Write($"[WorktreeReaperService] reaped {result.RemovedCount}/{outcomes.Count}, leftovers={result.Leftovers.Count}, skipped={result.Skipped.Count}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReaperService] ReapAsync FAILED: {ex.Message}");
            return ReapResult.Failure(ex.Message);
        }
    }

    private async Task<ReapOutcome> RemoveOneAsync(string repositoryPath, WorktreeInfo worktree, CancellationToken ct)
    {
        FileLog.Write($"[WorktreeReaperService] remove: path={worktree.Path}, branch={worktree.Branch}, proof={worktree.Reason} ({worktree.Explanation})");

        // Re-verify cleanliness before asking git to remove the worktree.
        var status = await _git.RunAsync(worktree.Path, new[] { "status", "--porcelain" }, ct);
        if (!status.Success)
            return ReapOutcome.Failed(worktree, $"could not verify cleanliness: {status.Error}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            return ReapOutcome.Failed(worktree, "worktree became dirty after the safety scan - not removed");

        // From here the operation is destructive and must run to completion - a cancellation that
        // killed git mid-remove would leave a deregistered, half-erased worktree that no inventory
        // and no leftover record could recover (inspection round 5). So the removal and every git call
        // after it use CancellationToken.None; cancellation is honoured only BETWEEN worktrees.
        var remove = await _git.RunAsync(repositoryPath, new[] { "worktree", "remove", worktree.Path }, CancellationToken.None);

        // Cleanly removed: git deregistered the worktree and the folder is gone.
        if (remove.Success && !Directory.Exists(worktree.Path))
            return ReapOutcome.RemovedOk(worktree);

        // The folder is still here. Two very different situations look identical at this point and
        // must NOT be confused (issue 516):
        //   (a) git REFUSED the whole operation - there is an unavoidable window between the status
        //       check above and this removal, and a file created in it makes git correctly refuse
        //       the now-dirty worktree. git leaves the worktree fully REGISTERED and touches
        //       nothing. Force-deleting here would discard that new content.
        //   (b) git PROCEEDED (the worktree was clean by git's own check), DEREGISTERED the
        //       worktree, and deleted most of it, but could not delete a locked build output. Only
        //       leftover files remain - all committed or ignored - and finishing the delete is safe.
        // The discriminator is whether git still lists this path as a worktree: (a) keeps it
        // registered, (b) removes the registration. We fail closed on any inability to tell.
        var list = await _git.RunAsync(repositoryPath, new[] { "worktree", "list", "--porcelain" }, CancellationToken.None);
        if (!list.Success)
        {
            FileLog.Write($"[WorktreeReaperService] could not enumerate worktrees to confirm removal of {worktree.Path}: {list.Error.Trim()} - left in place");
            return ReapOutcome.Failed(worktree, $"could not confirm the worktree was removed - left in place: {list.Error.Trim()}");
        }
        var normalizedTarget = NormalizePath(worktree.Path);
        bool stillRegistered = WorktreeListParser.Parse(list.Output)
            .Any(e => string.Equals(NormalizePath(e.Path), normalizedTarget, StringComparison.OrdinalIgnoreCase));

        if (stillRegistered)
        {
            // Case (a): git REFUSED and kept the worktree registered. git refuses for a REASON - the
            // worktree turned dirty in the window, it is protected by "git worktree lock", it holds a
            // submodule, and so on. We RESPECT git's refusal and never force-delete a worktree git
            // still owns (inspection: this must not bypass "git worktree lock"). Re-check only to give
            // the clearest message; the outcome is "left untouched" either way.
            var recheck = await _git.RunAsync(worktree.Path, new[] { "status", "--porcelain" }, CancellationToken.None);
            if (recheck.Success && !string.IsNullOrWhiteSpace(recheck.Output))
            {
                FileLog.Write($"[WorktreeReaperService] {worktree.Path} became dirty in the remove window - left in place, not force-deleted");
                return ReapOutcome.Failed(worktree, "worktree became dirty after the safety scan - not removed");
            }
            FileLog.Write($"[WorktreeReaperService] git refused to remove {worktree.Path} (still registered) - left untouched, not force-deleted: {remove.Error.Trim()}");
            return ReapOutcome.Failed(worktree, $"git refused to remove the worktree and it is left untouched: {remove.Error.Trim()}");
        }

        // git DEREGISTERED the worktree (it proceeded past its own clean check) but could not delete
        // every file - only leftover ignored/build outputs remain, and finishing the physical delete
        // loses nothing that was tracked or uncommitted.
        FileLog.Write($"[WorktreeReaperService] folder remains after git deregistered {worktree.Path} (git success={remove.Success}); finishing physical delete");
        var normalizedLeft = NormalizePath(worktree.Path);
        // Stamp a marker file INTO the folder to give it a stable identity (inspection round 6): a later
        // retry deletes only a folder that still carries our marker, so if the owner clears this orphan
        // and creates NEW data (or git registers a new worktree) at the same pathname, the retry sees no
        // marker and never touches it. Record the leftover BEFORE the physical delete (round 5) so a
        // crash mid-delete still leaves a retry record.
        TryWriteLeftoverMarker(worktree.Path);
        _leftovers.Add(normalizedLeft, repositoryPath);
        TryDeleteDirectory(worktree.Path);
        await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, CancellationToken.None);

        if (!Directory.Exists(worktree.Path))
        {
            _leftovers.Remove(normalizedLeft); // fully deleted - nothing to retry
            return ReapOutcome.RemovedOk(worktree);
        }

        // The folder survived a partial delete (a locked file remains). The recursive delete may have
        // removed the marker, so re-assert it on the surviving folder for the next retry.
        TryWriteLeftoverMarker(worktree.Path);
        FileLog.Write($"[WorktreeReaperService] LEFTOVER (locked files remain): {worktree.Path}");
        return ReapOutcome.LeftBehind(worktree);
    }

    /// <summary>The stable-identity marker the reaper stamps into a leftover folder. A retry deletes a
    /// leftover ONLY while this file is present, so a folder the owner later recreated at the same path
    /// (which will not carry it) is never deleted.</summary>
    private const string LeftoverMarkerName = ".ccd-worktree-leftover";

    private static void TryWriteLeftoverMarker(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                File.WriteAllText(Path.Combine(folder, LeftoverMarkerName), "cc-director worktree leftover - safe to delete\n");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReaperService] could not stamp leftover marker in {folder}: {ex.Message}");
        }
    }

    private static bool HasLeftoverMarker(string folder)
    {
        try { return File.Exists(Path.Combine(folder, LeftoverMarkerName)); }
        catch { return false; }
    }

    /// <summary>
    /// Retry the physical delete of folders a past reap deregistered but could not fully remove, for
    /// the repository being reaped. A candidate is deleted ONLY when, under the machine-wide lock, it
    /// still carries our leftover MARKER (inspection round 6) - the stable identity that proves it is
    /// the very orphan we recorded and not a folder the owner recreated, or a new worktree git
    /// registered, at the same pathname. A leftover with no marker (recreated), one a live reservation
    /// covers (in use), or one already gone is dropped or left alone rather than deleted. Those orphans
    /// hold only ignored/build outputs, so finishing the delete loses nothing tracked. A lock or
    /// reservation read failure skips the retry entirely rather than deleting blind.
    /// </summary>
    private Task RetryPersistedLeftovers(string repositoryPath, CancellationToken ct)
    {
        var repoNorm = NormalizePath(repositoryPath);
        var mine = _leftovers.All()
            .Where(r => string.Equals(NormalizePath(r.RepositoryPath), repoNorm, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mine.Count == 0)
            return Task.CompletedTask;

        IDisposable crit;
        try { crit = _reservations.EnterCriticalSection(); }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReaperService] leftover retry skipped - could not acquire the reap lock: {ex.Message}");
            return Task.CompletedTask;
        }
        using (crit)
        {
            IReadOnlySet<string> reservedNow;
            try { reservedNow = _reservations.LiveReservedPaths(); }
            catch (Exception ex)
            {
                FileLog.Write($"[WorktreeReaperService] leftover retry skipped - could not read live reservations: {ex.Message}");
                return Task.CompletedTask;
            }

            foreach (var leftover in mine)
            {
                ct.ThrowIfCancellationRequested();
                var path = leftover.Path;

                if (!Directory.Exists(path))
                {
                    _leftovers.Remove(path); // already gone - a prior retry finished it, or the owner did
                    continue;
                }
                if (reservedNow.Any(p => PathCoversWorktree(path, p)))
                    continue; // a live session (re)entered it - leave it entirely alone
                if (IsPoolSlotOrUnknown(path))
                    continue; // cc-worktrees owns it, or this machine cannot say it does not
                if (!HasLeftoverMarker(path))
                {
                    // The folder at this path is no longer OUR orphan - the owner recreated it, or git
                    // registered a new worktree here. Never delete it; drop the stale record.
                    FileLog.Write($"[WorktreeReaperService] leftover {path} no longer carries our marker - not deleting; dropping the stale record");
                    _leftovers.Remove(path);
                    continue;
                }

                TryDeleteDirectory(path);
                if (!Directory.Exists(path))
                {
                    FileLog.Write($"[WorktreeReaperService] retried leftover deleted (lock cleared): {path}");
                    _leftovers.Remove(path);
                }
                else
                {
                    TryWriteLeftoverMarker(path); // a locked file remained and may have taken the marker - re-assert it
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="path"/> belongs to a cc-worktrees pool - and TRUE when this machine's
    /// cc-worktrees records exist and cannot be read, because the caller is about to delete a folder
    /// and "I could not tell" is not "no". The doubtful case is logged so a machine whose records are
    /// broken says so rather than quietly stopping the retry forever.
    /// </summary>
    private bool IsPoolSlotOrUnknown(string path)
    {
        try
        {
            if (!_poolSlots.Owns(path))
                return false;
            FileLog.Write($"[WorktreeReaperService] leftover {path} is a cc-worktrees pool slot - not deleting");
            return true;
        }
        catch (CcWorktreesStateUnreadableException ex)
        {
            FileLog.Write($"[WorktreeReaperService] leftover {path} not deleted - could not tell whether it belongs to a cc-worktrees pool: {ex.Message}");
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // Best effort - locked files remain and are reported as a leftover, not swallowed as success.
            FileLog.Write($"[WorktreeReaperService] physical delete incomplete for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the live-session roster protects <paramref name="normalizedWorktree"/>: a session
    /// whose working directory IS the worktree, or is ANY subdirectory of it, holds it in use
    /// (inspection - a session running in W\src, not just W, must protect W). KNOWN RESIDUAL: paths
    /// are compared after Path.GetFullPath, which does NOT resolve a junction or symlink alias of the
    /// worktree; a session reaching it through such an alias is not matched (documented in the QA
    /// report as follow-up).
    /// </summary>
    private static bool RosterProtects(string normalizedWorktree, IReadOnlyList<LiveSessionRef> roster)
    {
        foreach (var s in roster)
        {
            if (string.IsNullOrWhiteSpace(s.RepoPath))
                continue;
            if (PathCoversWorktree(normalizedWorktree, NormalizePath(s.RepoPath)))
                return true;
        }
        return false;
    }

    /// <summary>True when a session working directory <paramref name="normalizedSessionPath"/> is the
    /// worktree itself or any subdirectory of it - either way the session is running inside the
    /// worktree and it must not be reaped. Both paths must already be normalized.</summary>
    private static bool PathCoversWorktree(string normalizedWorktree, string normalizedSessionPath)
        => string.Equals(normalizedSessionPath, normalizedWorktree, StringComparison.OrdinalIgnoreCase)
           || normalizedSessionPath.StartsWith(normalizedWorktree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonical, trailing-separator-trimmed form used to compare worktree paths across git and the OS.
    /// When the path EXISTS it is resolved to its real filesystem path (inspection round 5): a session
    /// reaching a worktree through a junction or symlink alias (e.g. D:\aliases\W\src -> D:\repos\W)
    /// would otherwise normalize to the alias while git lists the real target, so neither the roster
    /// nor the reservation guard would match and the live worktree could be reaped. Resolving both
    /// sides to the real path closes that alias bypass. A path that does not exist (or cannot be
    /// opened) falls back to lexical Path.GetFullPath.
    /// </summary>
    public static string NormalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var real = TryResolveFinalPath(full) ?? full;
            return real.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    /// <summary>
    /// Resolve <paramref name="fullPath"/> to its real filesystem path, following junctions and
    /// symlinks anywhere in the path (Windows GetFinalPathNameByHandle). Returns null when the path
    /// does not exist or cannot be opened, or on any non-Windows platform, so the caller falls back to
    /// the lexical path.
    /// </summary>
    private static string? TryResolveFinalPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a DIRECTORY handle.
        const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read | write | delete
        const uint OPEN_EXISTING = 3;
        var handle = CreateFileW(fullPath, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
            if (len == 0)
                return null;
            if (len > sb.Capacity)
            {
                sb.EnsureCapacity((int)len + 1);
                len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
                if (len == 0)
                    return null;
            }
            var result = sb.ToString();
            // GetFinalPathNameByHandle returns the \\?\ (or \\?\UNC\) extended-length prefix; strip it
            // so the result matches ordinary Path.GetFullPath output for comparison.
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + result.Substring(8);
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                return result.Substring(4);
            return result;
        }
        finally
        {
            handle.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, System.Text.StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
}
