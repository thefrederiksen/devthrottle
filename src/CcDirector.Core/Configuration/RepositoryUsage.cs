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
    /// Returns null when neither value is present, and the caller then records nothing rather than
    /// inventing an entry.
    /// </summary>
    /// <param name="repoPath">Where the session is: the session's own repository path.</param>
    /// <param name="pooledWorktreeRepo">The repository whose pool the session's slot came from, or
    /// null when the session is not in a pooled worktree (the default, and almost every session).</param>
    public static string? StartedIn(string? repoPath, string? pooledWorktreeRepo)
    {
        if (!string.IsNullOrWhiteSpace(pooledWorktreeRepo))
            return pooledWorktreeRepo.Trim();

        return string.IsNullOrWhiteSpace(repoPath) ? null : repoPath.Trim();
    }
}
