using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's live repository model to the wire DTOs pushed to the Gateway and served by
/// the local relay. The fold happens HERE, once: worktree safety becomes a finished state string
/// every client renders verbatim.
/// </summary>
public static class RepositoryDtoMapper
{
    /// <summary>
    /// Builds the raw DTO and then applies the ONE shared repository-level fold
    /// (<see cref="FleetWorktreeFold.FoldRepositoryForServe"/>, ruling R2-3): a provisional
    /// entry's safe count folds to zero and its worktrees to "verifying". The Gateway applies
    /// the same fold at serve time, so a pre-fix Director's pushed shape cannot bypass it.
    /// </summary>
    public static RepoStatusDto Map(RepositoryStatus s, string directorId, string machineName)
        => FleetWorktreeFold.FoldRepositoryForServe(new RepoStatusDto
        {
            DirectorId = directorId,
            MachineName = machineName,
            Path = s.Path,
            Name = s.Name,
            RemoteUrl = s.RemoteUrl,
            Provider = s.Provider.ToString(),
            Org = s.Org,
            Branch = s.Branch,
            IsClean = s.IsClean,
            UncommittedCount = s.UncommittedCount,
            DirtySinceUtc = s.DirtySinceUtc,
            AheadCount = s.AheadCount,
            BehindCount = s.BehindCount,
            BehindMainCount = s.BehindMainCount,
            WorktreeCount = s.WorktreeCount,
            WorktreesSafeToReap = s.WorktreesSafeToReap,
            WorktreesInUse = s.WorktreesInUse,
            WorktreesNeedAttention = s.WorktreesNeedAttention,
            WorktreeBytes = s.WorktreeBytes,
            Provisional = s.Provisional,
            Worktrees = s.Worktrees.Select(Map).ToList(),
        });

    /// <summary>
    /// A repository this Director knows about but has NOT computed a status for: one from the machine's
    /// registered repository list that no watched folder covers, so the scan never reached it (the
    /// one-repository-list mission, "the registry reaches the Gateway").
    ///
    /// Everything except the identity is left at its default ON PURPOSE, and the row says so through
    /// <see cref="RepoStatusDto.StatusNotComputed"/> rather than leaving a reader to infer it from a
    /// blank branch. There is no status to fill in and no plausible value to invent: a repository
    /// nobody has measured is not clean, not dirty, not ahead and not behind, and writing any of those
    /// would be a fact the product made up. <c>DirectorHub.PushRepoSnapshot</c> keeps these rows away
    /// from the two Gateway consumers that report status, and hands them to the catalog, which wants a
    /// path and a name.
    ///
    /// The name comes from the registered entry, which is what the user sees on the desktop dialog and
    /// may have renamed by hand. Only when it is blank is one derived, through
    /// <see cref="RepositoryPaths.FolderName"/> rather than <c>Path.GetFileName</c>: the latter honours
    /// only the separator of the machine running it, and this string travels to a Linux container and
    /// on to two clients, so it is computed once here, from the path's own shape, and never recomputed
    /// downstream.
    /// </summary>
    public static RepoStatusDto IdentityOnly(string path, string? name, string directorId, string machineName)
        => new()
        {
            DirectorId = directorId,
            MachineName = machineName,
            Path = path,
            Name = string.IsNullOrWhiteSpace(name) ? RepositoryPaths.FolderName(path) : name.Trim(),
            Provider = RepoProvider.None.ToString(),
            StatusNotComputed = true,
        };

    public static WorktreeDto Map(WorktreeInfo w) => new()
    {
        Path = w.Path,
        Branch = w.Branch,
        State = StateString(w.Safety),
        Reason = w.Explanation,
        SessionLabels = w.OpenSessions.ToList(),
        SizeBytes = w.SizeBytes,
        LastActivityUtc = w.LastActivityUtc,
        AheadOfMain = w.AheadOfMain,
        BehindMain = w.BehindMain,
        DirtyFileCount = w.DirtyFileCount,
        IsDetachedHead = w.IsDetachedHead,
    };

    /// <summary>The folded state string. Every surface renders this; nothing re-derives it.</summary>
    public static string StateString(WorktreeSafety safety) => safety switch
    {
        WorktreeSafety.SafeToReap => "safe-to-reap",
        WorktreeSafety.InUseBySession => "in-use",
        _ => "needs-attention",
    };

    /// <summary>
    /// Flattens repository DTOs into fleet worktree rows (GET /worktrees). Delegates to the one
    /// shared fold in the contracts assembly, which fails closed for provisional repositories.
    /// </summary>
    public static List<FleetWorktreeDto> Flatten(IEnumerable<RepoStatusDto> repositories, double dataAgeSeconds = 0)
        => FleetWorktreeFold.Flatten(repositories, dataAgeSeconds);
}
