using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// EVERYTHING ONE DIRECTOR KNOWS ABOUT THE REPOSITORIES ON ITS MACHINE, as one list, for the one push
/// that already goes up the tunnel (the one-repository-list mission, "the registry reaches the
/// Gateway").
///
/// A Director knows about a repository in two ways, and until now only one of them left the machine:
///
/// - the ROOT-FOLDER SCAN (<see cref="RepositoryMonitor"/>), which walks the folders the user asked
///   DevThrottle to watch and computes a full status for everything it finds. This is what
///   <c>ControlApiHost.SnapshotRepositories</c> pushed, and only this.
/// - the REGISTERED LIST (<see cref="RepositoryRegistry"/>, <c>config/director/repositories.json</c>),
///   which a person built by hand, one Browse at a time. The desktop New Session dialog has always
///   shown the union of the two (<c>NewSessionDialog.BuildRepositoryList</c>).
///
/// So a repository that was added to a Director by hand, has not been used since the Gateway started
/// recording, and does not sit under any watched folder existed ONLY in that Director's local file. The
/// Gateway had never heard of it, which means it was missing from the Cockpit and the phone - and once
/// the Director's own dialog reads the Gateway list, it would have gone missing from the one screen
/// that shows it correctly today.
///
/// <para><b>THE UNION IS DONE HERE, ON THE DIRECTOR, AND THAT IS THE WHOLE DESIGN DECISION.</b> The
/// alternative - a second observation at the Gateway, carrying the registry separately - was rejected,
/// and not for tidiness. The Gateway reconciles a Director's repository push against what it already
/// holds for that Director: rows that have never been opened and are no longer in the push are removed,
/// which is how un-watching a folder takes effect. Two observations describing one machine would
/// therefore each reconcile the other's rows away - the scan's push would delete the registry's rows,
/// the registry's push would delete the scan's, and which survived would depend on which arrived last.
/// Two feeds describing one machine disagree, and that is the exact defect this mission exists to end.
/// One list, pushed once, reconciled once.</para>
///
/// <para>It is a pure function over its arguments - no monitor, no registry, no clock, no environment -
/// because it is where the union's rules live and those rules have to be testable without a machine to
/// scan. The same reason phase 3 made the Gateway's ordering a pure function over a materialized list.
/// </para>
/// </summary>
public static class DirectorRepositorySnapshot
{
    /// <summary>
    /// The one list this Director pushes: every repository the scan computed a status for, plus every
    /// registered repository the scan did not reach, the latter marked
    /// <see cref="RepoStatusDto.StatusNotComputed"/>.
    ///
    /// <para><b>A registered repository is added only once <paramref name="scanHasCompleted"/> is
    /// true</b> - see <see cref="RepositoryMonitor.HasCompletedAScan"/> for the full reason. In short:
    /// the Gateway reads a push with no unverified entry in it as a COMPLETE statement of what this
    /// Director knows and reconciles against it, so a push made before the first scan had run, carrying
    /// the registry and nothing else, would read as "every repository under every watched folder has
    /// gone away". Until the scan has settled, this returns exactly what it always returned, and the
    /// Gateway's existing guard - a push with nothing in it reconciles nothing - covers it. The cost is
    /// that a hand-added repository reaches the Gateway on the first push after the first scan finishes
    /// rather than on the first push of all.</para>
    ///
    /// <para><b>The de-duplication.</b> A repository can be in the registry AND under a watched folder -
    /// on a developer's machine that is the normal case, not the exception. The scanned row wins,
    /// because it is the one carrying a status; the registered entry is dropped as a duplicate rather
    /// than added beside it. Paths are compared through
    /// <see cref="WorktreeReaperService.NormalizePath"/>, which is what <see cref="RepositoryMonitor"/>
    /// itself keys its model by: the question being asked is "is this path already in the scan", and the
    /// only comparison that can answer it truthfully is the scan's own. This runs on the Director, which
    /// IS the machine that owns these paths, so resolving them against the filesystem - which is how a
    /// junction, a symbolic link or a short name is seen to be the same folder - is both allowed and
    /// correct here. It would not be on the Gateway, and nothing here is reused there: the Gateway
    /// compares paths with <c>KnownRepositoryStore.NormalizePathKey</c>, which decides Windows-ness from
    /// the path's own shape because it is a Linux container holding paths from Windows and macOS
    /// machines. Two comparisons, two different questions, and neither one is a copy of the other's
    /// rule.</para>
    ///
    /// <para><b>The registry's own last-used time is deliberately NOT sent, and never will be.</b> A
    /// registered entry carries a <c>LastUsed</c> stamp written by this Director, and pushing it would
    /// undo the first thing this mission did: the last-used time is the Gateway's, observed from every
    /// session start on every surface, and the local file stopped being the thing that decides the
    /// order. A repository this Director's file remembers but the Gateway has never seen used therefore
    /// arrives as never-opened and sits at the bottom of the list until it is next used - which is the
    /// mission's decision, recorded in its section 4, and not an oversight here.</para>
    /// </summary>
    /// <param name="scanned">The root-folder scan's model, as <see cref="RepositoryMonitor.Snapshot"/>
    /// returns it.</param>
    /// <param name="registered">The machine's registered repository list, as
    /// <see cref="RepositoryRegistry.Repositories"/> returns it.</param>
    /// <param name="scanHasCompleted"><see cref="RepositoryMonitor.HasCompletedAScan"/>.</param>
    /// <param name="directorId">The pushing Director.</param>
    /// <param name="machineName">The machine this Director runs on.</param>
    public static List<RepoStatusDto> Union(
        IReadOnlyList<RepositoryStatus>? scanned,
        IReadOnlyList<RepositoryConfig>? registered,
        bool scanHasCompleted,
        string directorId,
        string machineName)
    {
        var rows = (scanned ?? Array.Empty<RepositoryStatus>())
            .Select(status => RepositoryDtoMapper.Map(status, directorId, machineName))
            .ToList();

        if (registered is null || registered.Count == 0 || !scanHasCompleted)
            return rows;

        // Seeded from the scanned rows so a registered repository the scan already found is dropped
        // rather than pushed twice; Add then answers both questions at once - "was it scanned" and "have
        // I already taken this registered entry", the second of which matters because one machine's
        // registry can hold the same folder written two ways.
        var alreadyKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (!string.IsNullOrWhiteSpace(row.Path))
                alreadyKnown.Add(WorktreeReaperService.NormalizePath(row.Path));

        foreach (var entry in registered)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue; // the path IS the identity - a pathless entry cannot be keyed
            if (!alreadyKnown.Add(WorktreeReaperService.NormalizePath(entry.Path)))
                continue; // the scan already has it, with a real status
            rows.Add(RepositoryDtoMapper.IdentityOnly(entry.Path, entry.Name, directorId, machineName));
        }

        return rows;
    }
}
