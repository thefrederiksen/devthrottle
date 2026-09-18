namespace CcDirector.Core.Git;

/// <summary>
/// The two states a worktree can be in from the reaper's point of view.
/// The verdict is computed once, on the service side, and the UI only renders it
/// (the "dumb client" rule in CLAUDE.md).
/// </summary>
public enum WorktreeSafety
{
    /// <summary>Work is provably on origin/main and the tree is clean - safe to remove mechanically.</summary>
    SafeToReap,

    /// <summary>
    /// Git-safe (work on origin/main, tree clean) BUT a live session is running in it. Never offered
    /// for reap while the session is open - deleting it would pull the ground out from under the session.
    /// </summary>
    InUseBySession,

    /// <summary>Carries unmerged commits, uncommitted content, or is the primary checkout - never auto-removed.</summary>
    NeedsAttention,
}

/// <summary>
/// The specific signal that decided a worktree's safety verdict, so the UI can show a
/// one-line reason and the reaper can log its proof-of-safety.
/// </summary>
public enum WorktreeSafetyReason
{
    // --- Safe-to-reap reasons (each is sufficient proof the work reached origin/main) ---

    /// <summary>C1: the branch's pull request is merged (authoritative; recognises squash merges).</summary>
    PullRequestMerged,

    /// <summary>C2: the origin branch no longer exists after a prune - with delete-branch-on-merge, gone means merged.</summary>
    OriginBranchGone,

    /// <summary>C3: <c>git cherry</c> reports the branch adds nothing origin/main lacks.</summary>
    ContainedInMain,

    /// <summary>Detached-HEAD case: its HEAD commit is an ancestor of origin/main.</summary>
    DetachedHeadAncestorOfMain,

    /// <summary>Git-safe, but a live session is running in the worktree - held back from reaping.</summary>
    LiveSessionOpen,

    // --- Needs-attention reasons (never auto-removed) ---

    /// <summary>
    /// The directory is a slot in a cc-worktrees pool. It belongs to that tool, which holds the lease
    /// on it and is the only thing that knows whether the work in it has landed - so the Director
    /// never removes it, whatever git says about it.
    /// </summary>
    CcWorktreesPoolSlot,

    /// <summary>The repository's primary checkout - never removed.</summary>
    PrimaryCheckout,

    /// <summary>The tree has modified, staged, or untracked content - deleting would lose work.</summary>
    UncommittedChanges,

    /// <summary>No merge signal could be established - fail closed, treat as stranded.</summary>
    NotProvenMerged,

    /// <summary>A required git probe failed, so safety could not be proven - fail closed.</summary>
    InspectionFailed,
}

/// <summary>
/// The plain facts about a worktree, gathered from git, that feed the pure
/// <see cref="WorktreeSafetyEvaluator"/>. Keeping the facts separate from the git
/// calls lets the fail-closed decision be unit-tested exhaustively without a repository.
/// </summary>
public sealed record WorktreeFacts
{
    /// <summary>True when this is the repository's main working tree (guardrail A).</summary>
    public bool IsPrimary { get; init; }

    /// <summary>True when the worktree is on a detached HEAD rather than a branch.</summary>
    public bool IsDetachedHead { get; init; }

    /// <summary>True when <c>git status --porcelain</c> is empty (no content at all).</summary>
    public bool IsClean { get; init; }

    /// <summary>C1: the branch has a merged pull request.</summary>
    public bool PullRequestMerged { get; init; }

    /// <summary>C2: the origin branch was deleted after a prune.</summary>
    public bool OriginBranchGone { get; init; }

    /// <summary>C3: <c>git cherry</c> is clean - the branch adds nothing origin/main lacks.</summary>
    public bool ContainedInMain { get; init; }

    /// <summary>Detached-HEAD case: the HEAD commit is an ancestor of origin/main.</summary>
    public bool DetachedHeadIsAncestorOfMain { get; init; }

    /// <summary>
    /// False when a required git probe failed (e.g. origin/main could not be resolved),
    /// forcing a fail-closed verdict rather than a guess.
    /// </summary>
    public bool InspectionSucceeded { get; init; } = true;

    /// <summary>True when a live session's working directory is this worktree - it is held back from reaping.</summary>
    public bool HasLiveSession { get; init; }

    /// <summary>
    /// True when this directory is a slot in a cc-worktrees pool (or the Director could not establish
    /// that it is not - see <see cref="CcWorktreesPoolSlots"/>). A pool slot is never the Director's to
    /// remove: cc-worktrees owns it and holds the lease, and the proof that its commits reached the
    /// remote is the tool's, not this evaluator's.
    /// </summary>
    public bool IsCcWorktreesPoolSlot { get; init; }
}

/// <summary>
/// A live session's working directory plus a short human label, used to detect which worktrees are
/// currently occupied by an open session. The caller supplies these (already filtered to this machine
/// and to genuinely-alive sessions); the detector only matches paths.
/// </summary>
public sealed class LiveSessionRef
{
    public string RepoPath { get; init; } = "";

    /// <summary>A short label for the UI, e.g. "ERP KB Builder (#109)".</summary>
    public string Label { get; init; } = "";
}

/// <summary>The evaluator's decision: the safety bucket, the deciding reason, and a one-line explanation.</summary>
public sealed class WorktreeVerdict
{
    public WorktreeSafety Safety { get; init; }
    public WorktreeSafetyReason Reason { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>
/// A raw entry parsed from <c>git worktree list --porcelain</c>, before any safety reasoning.
/// </summary>
public sealed class RawWorktreeEntry
{
    public string Path { get; init; } = "";
    public string Head { get; init; } = "";

    /// <summary>The short branch name, or null for a detached HEAD.</summary>
    public string? Branch { get; init; }

    public bool IsDetached { get; init; }
    public bool IsBare { get; init; }
}

/// <summary>
/// The full, display-ready record for one worktree: its identity, its dirty/ahead/behind
/// counts, and the computed safety verdict. The UI renders these verbatim. A record so services
/// can derive enriched copies (e.g. stamping the measured size) without mutation.
/// </summary>
public sealed record WorktreeInfo
{
    public string Path { get; init; } = "";

    /// <summary>The short branch name, or null for a detached HEAD.</summary>
    public string? Branch { get; init; }

    public string HeadCommit { get; init; } = "";
    public bool IsPrimary { get; init; }
    public bool IsDetachedHead { get; init; }
    public bool IsClean { get; init; }

    /// <summary>Number of modified/staged/untracked entries reported by <c>git status --porcelain</c>.</summary>
    public int DirtyFileCount { get; init; }

    public int AheadOfMain { get; init; }
    public int BehindMain { get; init; }
    public bool HasOpenPullRequest { get; init; }

    /// <summary>
    /// Best-effort "last activity" for this worktree (UTC): the most recent of its last commit,
    /// its top-level folder change, and its HEAD reflog. Shown so a human can judge whether a
    /// worktree is still being worked in before removing it. Null when it cannot be determined.
    /// </summary>
    public DateTime? LastActivityUtc { get; init; }

    /// <summary>Labels of the live sessions running in this worktree (empty when none).</summary>
    public IReadOnlyList<string> OpenSessions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Size of the worktree folder on disk in bytes, or null when not yet measured. Measured with a
    /// cached walk (re-measured only when the worktree's last activity changes) because this is what
    /// "reap and reclaim N GB" is quoted from.
    /// </summary>
    public long? SizeBytes { get; init; }

    public WorktreeSafety Safety { get; init; }
    public WorktreeSafetyReason Reason { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>
/// The result of enumerating a repository's worktrees: the full list plus success/error,
/// with convenience projections into the safe-to-reap and needs-attention groups the UI shows.
/// </summary>
public sealed class WorktreeInventory
{
    public string RepositoryPath { get; init; } = "";
    public IReadOnlyList<WorktreeInfo> Worktrees { get; init; } = Array.Empty<WorktreeInfo>();
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>The worktrees proven safe to reap right now - what the badge counts and the button removes.</summary>
    public IReadOnlyList<WorktreeInfo> SafeToReap =>
        Worktrees.Where(w => w.Safety == WorktreeSafety.SafeToReap).ToList();

    /// <summary>Git-safe worktrees that a live session is currently running in - shown, but never reaped.</summary>
    public IReadOnlyList<WorktreeInfo> InUseBySession =>
        Worktrees.Where(w => w.Safety == WorktreeSafety.InUseBySession).ToList();

    /// <summary>Stranded or dirty worktrees a human or agent must decide about (excludes the primary checkout).</summary>
    public IReadOnlyList<WorktreeInfo> NeedsAttention =>
        Worktrees.Where(w => w.Safety == WorktreeSafety.NeedsAttention && !w.IsPrimary).ToList();

    /// <summary>The badge count: how many worktrees can be reaped now.</summary>
    public int SafeToReapCount => Worktrees.Count(w => w.Safety == WorktreeSafety.SafeToReap);
}
