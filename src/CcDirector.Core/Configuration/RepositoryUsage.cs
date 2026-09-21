namespace CcDirector.Core.Configuration;

/// <summary>
/// Which repository a session counts as a use of.
///
/// ONE RULE, BECAUSE TWO CATALOGUES READ IT. The Director's own
/// <see cref="RepositoryRegistry"/> records it through <see cref="RepositoryUsageRecorder"/>, and the
/// Gateway's known-repository catalogue records it from the pushed session. Those two lists decide
/// the order of the same New Session screen on three different clients, so a second copy of this
/// rule would eventually disagree with the first about which repository was used last - which is the
/// exact defect this is part of fixing.
/// </summary>
public static class RepositoryUsage
{
    /// <summary>
    /// The repository a session is a use of: the one it was STARTED IN, which is not always the one
    /// it runs in.
    ///
    /// A session holding a pooled worktree runs in a throwaway slot, and that slot is what its
    /// repository path reports - every reader of a session's repository path means "where the session
    /// is". Nobody picks a slot out of a repository list, and the slot is deleted when the pool takes
    /// it back, so the repository a use should be recorded against is the one the slot came out of.
    ///
    /// <para><b>AND A WORKTREE IS NOT A REPOSITORY. USING A WORKTREE OF <c>devthrottle</c> IS USING
    /// <c>devthrottle</c></b> (the one-repository-list mission, "a worktree is not a repository"). The
    /// pooled slot above was only the first instance of it: every agent session on this fleet runs in a
    /// git worktree, and each one used to become its own row. Measured against the live Gateway on 20
    /// September 2026, one Windows machine's list served 559 repositories, 110 of which were live
    /// worktrees of four repositories, and thirteen of the top twenty rows - the part a person reads -
    /// were worktrees. The owner's words, looking at it: "here you are showing the work trees. We
    /// should only be showing the repos."</para>
    ///
    /// <para><b>The order of the three is the order of what is certain.</b> A pooled slot's repository
    /// is a fact the pool recorded and it needs no disk; the worktree's repository is a fact this
    /// machine read off the disk at session creation; the session's own folder is what is left when
    /// neither applies, and it is what this rule answered before either existed. A worktree whose
    /// repository could not be proved to exist arrives here as a null
    /// <paramref name="primaryRepoPath"/> and is recorded as itself, because this rule never guesses
    /// which repository a folder belongs to - see
    /// <see cref="Git.LinkedWorktree.ParentRepositoryOf"/>, which is the only thing allowed to
    /// answer it and runs only on the machine that owns the path.</para>
    ///
    /// Returns null when none of the three is present, and the caller then records nothing rather than
    /// inventing an entry.
    /// </summary>
    /// <param name="repoPath">Where the session is: the session's own repository path.</param>
    /// <param name="pooledWorktreeRepo">The repository whose pool the session's slot came from, or
    /// null when the session is not in a pooled worktree (the default, and almost every session).</param>
    /// <param name="primaryRepoPath">The repository <paramref name="repoPath"/> is a linked worktree
    /// of, as the owning machine resolved it at session creation
    /// (<c>Session.PrimaryRepoPath</c>, <c>SessionDto.PrimaryRepoPath</c>) - or null when the session
    /// is in a repository proper, when the worktree's repository could not be proved, or when the
    /// session came from a Director that predates this field.</param>
    /// <remarks>There is deliberately NO default for
    /// <paramref name="primaryRepoPath"/>. A defaulted parameter would let a call site that has the
    /// answer omit it and still compile, and the catalogue would record a worktree again with nothing
    /// to say so. With no default the compiler is the guard: every caller of this rule must state what
    /// it knows.</remarks>
    public static string? StartedIn(string? repoPath, string? pooledWorktreeRepo, string? primaryRepoPath)
    {
        if (!string.IsNullOrWhiteSpace(pooledWorktreeRepo))
            return pooledWorktreeRepo.Trim();

        if (!string.IsNullOrWhiteSpace(primaryRepoPath))
            return primaryRepoPath.Trim();

        return string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();
    }
}
