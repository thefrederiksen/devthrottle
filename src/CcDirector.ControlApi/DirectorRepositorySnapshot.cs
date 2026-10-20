using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// THE ONE REPOSITORY LIST THIS DIRECTOR PUSHES UP THE TUNNEL (devthrottle#4301).
///
/// A Director has ONE repository list: its registry (<see cref="RepositoryRegistry"/>,
/// <c>config/director/repositories.json</c> under the instance home). The root-folder scan
/// (<see cref="RepositoryMonitor"/>) feeds it - every repository the scan finds is added, and nothing is ever
/// removed by the scan (<see cref="RepositoryRegistry.FollowScan"/>) - and Browse feeds it. Until then the
/// Director held two lists and combined them here, in the New Session dialog, and nowhere else, so the factory
/// seat start, which read the registry alone, refused a repository every screen showed.
///
/// So this no longer combines two sources. The registry is the list; the scan only supplies the STATUS for the
/// rows it computed one for. A registry entry the scan has no status for goes up identity-only, marked
/// <see cref="RepoStatusDto.StatusNotComputed"/>.
///
/// <para><b>Why it is still done on the Director and pushed once.</b> The Gateway reconciles a Director's push
/// against what it already holds for that Director: rows never opened and no longer in the push are removed.
/// One list, pushed once, reconciled once.</para>
///
/// <para>It is a pure function over its arguments - no monitor, no registry, no clock - so its rules are
/// testable without a machine to scan.</para>
/// </summary>
public static class DirectorRepositorySnapshot
{
    /// <summary>
    /// The one list this Director pushes: every registry entry, carrying the scan's status where the scan computed
    /// one for that folder.
    ///
    /// <para><b>Until <paramref name="scanHasCompleted"/> is true this returns the scan's rows and nothing
    /// else</b>, exactly as before - see <see cref="RepositoryMonitor.HasCompletedAScan"/>. The Gateway reads a
    /// push with no unverified entry in it as a COMPLETE statement of what this Director knows and reconciles
    /// against it, so a push made before the first scan had settled must not read as "everything is gone". The
    /// scan's rows at that moment are the warm-start cache, provisional, and a provisional row suspends
    /// reconciliation.</para>
    ///
    /// <para><b>Matching a registry entry to its scanned row.</b> Paths are compared through
    /// <see cref="WorktreeReaperService.NormalizePath"/>, the scan's own key, which resolves a junction, a symbolic
    /// link or a short name to the same folder. This runs on the Director, the machine that owns these paths, so
    /// resolving them against the filesystem is allowed here; the Gateway uses its own
    /// <c>KnownRepositoryStore.NormalizePathKey</c>. A registry holding one folder written two ways goes up once.</para>
    ///
    /// <para><b>The registry's own last-used time is deliberately NOT sent.</b> The last-used time is the
    /// Gateway's, observed from every session start on every surface.</para>
    /// </summary>
    /// <param name="scanned">The root-folder scan's model, as <see cref="RepositoryMonitor.Snapshot"/>
    /// returns it - used only for status.</param>
    /// <param name="registered">The machine's one repository list, as <see cref="RepositoryRegistry.Repositories"/>
    /// returns it.</param>
    /// <param name="scanHasCompleted"><see cref="RepositoryMonitor.HasCompletedAScan"/>.</param>
    /// <param name="rootFolders">What currently exists under the registered root folders, from
    /// <see cref="DirectorRootFolders"/>, or null/empty when this Director has nothing to say about them. It rides
    /// on the FIRST row - see <see cref="RepoStatusDto.RootFolders"/>.</param>
    /// <param name="directorId">The pushing Director.</param>
    /// <param name="machineName">The machine this Director runs on.</param>
    public static List<RepoStatusDto> Union(
        IReadOnlyList<RepositoryStatus>? scanned,
        IReadOnlyList<RepositoryConfig>? registered,
        bool scanHasCompleted,
        IReadOnlyList<RootFolderListingDto>? rootFolders,
        string directorId,
        string machineName)
    {
        var scannedRows = scanned ?? Array.Empty<RepositoryStatus>();
        if (!scanHasCompleted)
            return Stamp(scannedRows.Select(status => RepositoryDtoMapper.Map(status, directorId, machineName)).ToList(),
                rootFolders);

        var statusByFolder = new Dictionary<string, RepositoryStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in scannedRows)
            if (!string.IsNullOrWhiteSpace(status.Path))
                statusByFolder.TryAdd(WorktreeReaperService.NormalizePath(status.Path), status);

        var rows = new List<RepoStatusDto>();
        var pushed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registered ?? Array.Empty<RepositoryConfig>())
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue; // the path IS the identity - a pathless entry cannot be keyed
            var key = WorktreeReaperService.NormalizePath(entry.Path);
            if (!pushed.Add(key))
                continue; // one folder written two ways in the registry goes up once
            rows.Add(statusByFolder.TryGetValue(key, out var status)
                ? RepositoryDtoMapper.Map(status, directorId, machineName)
                : RepositoryDtoMapper.IdentityOnly(entry.Path, entry.Name, directorId, machineName));
        }

        return Stamp(rows, rootFolders);
    }

    /// <summary>
    /// Put the root-folder listing on the first row of the push. Nothing when there is no listing to
    /// carry, and nothing when there is no row to carry it - an empty push is the one the Gateway
    /// already refuses to reconcile from, so it has nothing to authorise.
    /// </summary>
    private static List<RepoStatusDto> Stamp(
        List<RepoStatusDto> rows, IReadOnlyList<RootFolderListingDto>? rootFolders)
    {
        if (rows.Count > 0 && rootFolders is { Count: > 0 })
            rows[0].RootFolders = rootFolders.ToList();
        return rows;
    }
}
