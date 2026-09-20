using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// The THIRD observer on the repository snapshot a Director already pushes up the tunnel (the
/// one-repository-list mission, phase 2). It sits beside <see cref="Streaming.PushedRepositoryStore"/>
/// and <see cref="Streaming.RepoHistoryStore"/> on the one accepted push in
/// <c>DirectorHub.PushRepoSnapshot</c>, and folds it into the durable catalog as the DISCOVERED half:
/// repositories found under a registered root folder that nobody has ever opened.
///
/// It is a THIRD OBSERVER and not a fourth FEED, deliberately. The Director's root-folder scan already
/// has a route to the Gateway - <c>ControlApiHost.SnapshotRepositories</c> maps
/// <c>RepositoryMonitor.Snapshot()</c> and <c>WireRepositoryPush</c> pushes it, debounced, on every
/// upsert, removal and completed scan, plus the ten-second reseed. A second pusher on a second cadence
/// would be two feeds describing one machine, and two feeds describing one machine disagree - which is
/// the defect this whole mission exists to end.
///
/// What it adds is DURABILITY. <see cref="Streaming.PushedRepositoryStore"/> holds the same snapshot in
/// memory, per Director, and returns nothing once that Director has been offline a while;
/// <see cref="Streaming.RepoHistoryStore"/> keeps daily rows for the morning report. Neither is a
/// machine-keyed catalogue that survives the Director going away, which is exactly when the Cockpit and
/// the phone still need the list.
/// </summary>
public sealed class DiscoveredRepositoryObserver
{
    private readonly KnownRepositoryStore _catalog;
    private readonly Func<TenantId, string, string?> _machineName;

    /// <summary>
    /// What was last folded for each <c>tenant|directorId</c>, so an unchanged re-push costs nothing at
    /// all - not even a read. A Director re-pushes its whole repository snapshot on a ten-second reseed,
    /// and on a hosted Gateway that is every Director of every account; going to the database each time to
    /// discover that nothing moved is pure noisy-neighbour cost. The same device
    /// <see cref="Streaming.RepoHistoryStore"/> uses to skip its write, and
    /// <see cref="SessionHistoryRecorder"/> to skip its reconcile query.
    ///
    /// It can only ever SKIP work, never change what a fold does, and it is lost on restart - so a Gateway
    /// that comes up cold folds the next push in full.
    /// </summary>
    private readonly ConcurrentDictionary<string, FoldedSnapshot> _folded = new(StringComparer.Ordinal);

    /// <summary>The signature of the last folded snapshot, and when it was folded.</summary>
    private sealed record FoldedSnapshot(string Signature, DateTime AtUtc);

    /// <summary>
    /// <paramref name="machineName"/> must resolve the machine from the DIRECTOR REGISTRATION, because
    /// that is what the read side looks rows up by: <c>GET /directors/{id}/known-repositories</c> resolves
    /// the owned Director and reads <c>director.MachineName</c>. A writer that used the machine name in the
    /// pushed payload instead would write rows under a name that may differ from the registration's, and
    /// those rows would exist while no screen ever showed them. One source, both ends.
    /// </summary>
    public DiscoveredRepositoryObserver(KnownRepositoryStore catalog, Func<TenantId, string, string?> machineName)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
    }

    /// <summary>
    /// Fold one ACCEPTED repository snapshot into the catalog.
    ///
    /// <paramref name="directorId"/> is the Director BOUND to the pushing connection, never a Director id
    /// carried in the payload: it is the ownership and reconciliation scope of every row written here.
    ///
    /// THE RECONCILIATION GUARD, copied from <see cref="Streaming.RepoHistoryStore.ObserveSnapshot"/>
    /// which already paid for it. The snapshot is a Director's full current view, so a REAL observation
    /// also reconciles - that is how removing a root folder takes effect. But an EMPTY push and an
    /// ALL-PROVISIONAL push both look exactly like "every repository was removed", and neither is: a cold
    /// start before the first live scan pushes nothing, and a warm-cache push carries only entries the
    /// Director has not re-verified yet. A MIXED push - some entries verified, others still warming up -
    /// is a partial view, and reconciling from it would delete a repository that had simply not finished
    /// warming up. So reconciliation runs only when at least one entry was verified AND no entry anywhere
    /// in the push was provisional. The accepted cost is that a genuinely emptied root folder is not
    /// cleared until the next complete observation; keeping a stale row beats erasing a true one.
    ///
    /// <para><b>AND THE PUSH NOW ALSO CARRIES WHAT EXISTS UNDER THE ROOT FOLDERS</b> (the
    /// one-repository-list mission, "the catalogue forgets"), which is what lets the catalog forget a
    /// repository whose folder has gone rather than growing for ever. It rides on one row of the same
    /// push - one feed, not a second one - and everything that makes acting on it safe is on
    /// <see cref="KnownRepositoryStore.ObserveDiscovered"/> and
    /// <see cref="RootFolderListingDto"/>.</para>
    /// </summary>
    public void ObserveSnapshot(TenantId tenant, string directorId, IReadOnlyList<RepoStatusDto> repositories,
        DateTime? seenUtc = null)
    {
        if (string.IsNullOrWhiteSpace(directorId))
        {
            FileLog.Write("[DiscoveredRepositoryObserver] ignored - no bound Director id");
            return; // the Director id is the ownership and reconciliation scope
        }
        if (repositories is null)
            throw new ArgumentNullException(nameof(repositories));

        var machine = _machineName(tenant, directorId);
        if (string.IsNullOrWhiteSpace(machine))
        {
            // The registration is the only source the read side agrees with, so there is no second-best
            // machine name to fall back on: a row written under the payload's name would be unreadable.
            FileLog.Write($"[DiscoveredRepositoryObserver] ignored - the registration for {directorId} reports no machine name");
            return;
        }

        var found = new List<DiscoveredRepository>(repositories.Count);
        var worktrees = new List<WorktreeOfRepository>();
        var sawProvisional = false;
        List<RootFolderListingDto>? listings = null;
        foreach (var repository in repositories)
        {
            // WHAT EXISTS UNDER THIS DIRECTOR'S ROOT FOLDERS, taken from the FIRST row that carries it -
            // it is a push-level fact and the Director stamps it on one row, because the hub method's
            // signature cannot gain a parameter without breaking every Director in the field. Taking the
            // first non-null found ANYWHERE in the set rather than reading row zero means no re-ordering
            // or filtering of the push can lose it. A Director that predates this carries none, and a
            // push with none forgets nothing.
            //
            // It is read BEFORE the provisional filter on purpose: a directory listing is not a status,
            // so it is not made unverified by sitting beside warm-start rows. It still cannot cause a
            // removal in that push, because reconciliation is refused outright when anything was
            // provisional.
            listings ??= repository.RootFolders;

            if (repository.Provisional)
            {
                sawProvisional = true;
                continue; // unverified warm-start data never enters the catalog
            }
            if (string.IsNullOrWhiteSpace(repository.Path))
                continue; // the path IS the identity - a pathless row cannot be keyed
            found.Add(new DiscoveredRepository(repository.Path, repository.Name ?? ""));

            // A WORKTREE IS NOT A REPOSITORY (the one-repository-list mission). The Director's scan
            // already computes every scanned repository's worktrees with git, on the machine that holds
            // the disk, and they already ride this push - so the statement "that folder is a worktree of
            // this repository" is here for the taking, and the catalogue can fold the worktree's row
            // into the repository's. No new field, no new push, no new endpoint and no new tunnel verb:
            // one feed, as every piece of this mission since phase 2.
            //
            // Taken only from a row that is NOT provisional - the filter above has already dropped those
            // - because a warm-start row's worktree list is whatever was last cached rather than what git
            // says now, and this is a destructive operation.
            foreach (var worktree in repository.Worktrees ?? new List<WorktreeDto>())
            {
                if (!string.IsNullOrWhiteSpace(worktree.Path))
                    worktrees.Add(new WorktreeOfRepository(worktree.Path, repository.Path));
            }
        }

        var rootFolders = (listings ?? new List<RootFolderListingDto>())
            .Where(listing => !string.IsNullOrWhiteSpace(listing.Path))
            .Select(listing => new WatchedRootFolder(
                listing.Path,
                (IReadOnlyList<string>)(listing.ChildPaths ?? new List<string>())))
            .ToList();

        var reconcile = found.Count > 0 && !sawProvisional;
        var seen = seenUtc ?? DateTime.UtcNow;

        // An empty push after the provisional filter has nothing to insert and - by the guard above -
        // nothing to reconcile either, so it is answered here rather than with a database read that could
        // only ever conclude the same thing.
        if (found.Count == 0)
        {
            FileLog.Write($"[DiscoveredRepositoryObserver] nothing to fold: director={directorId} machine={machine} "
                          + $"provisional={sawProvisional} pushed={repositories.Count}");
            return;
        }

        // Skip an identical re-push whose last-seen stamp is not due yet. The signature covers everything
        // this fold writes - the machine, every path, and every name - so a push that would change a row
        // can never match it.
        var key = $"{tenant.Value}|{directorId}";
        var signature = Signature(machine, found, rootFolders, worktrees);
        if (_folded.TryGetValue(key, out var last)
            && string.Equals(last.Signature, signature, StringComparison.Ordinal)
            && seen - last.AtUtc < KnownRepositoryStore.LastSeenFreshnessInterval)
            return;

        _catalog.ObserveDiscovered(tenant, machine, directorId, found, rootFolders, worktrees, seen, reconcile);
        _folded[key] = new FoldedSnapshot(signature, seen);
    }

    /// <summary>
    /// Everything one fold writes, in one string: the machine it writes under, each repository's
    /// normalized path key with the name that rides beside it, and each covered root folder with
    /// everything the Director saw beside it. Ordered by the key, because a scan publishes in whatever
    /// order it finished and the same set in a different order is the same set.
    ///
    /// The root folders are in here because they now decide REMOVALS: a folder deleted under a watched
    /// root changes nothing about the pushed repositories - the scan never reported it - so a signature
    /// that covered only the repositories would skip the very push that was supposed to forget it. The
    /// worktrees are in here for the same reason and with the same limit stated plainly: a worktree
    /// APPEARING or DISAPPEARING defeats the skip, but a row that only the CATALOGUE gained - a session
    /// started by a Director too old to resolve its own worktree - changes nothing in the push, so its
    /// collapse waits for the next push that differs, or for the reseed a reconnect brings.
    /// </summary>
    private static string Signature(string machine, IReadOnlyList<DiscoveredRepository> found,
        IReadOnlyList<WatchedRootFolder> rootFolders, IReadOnlyList<WorktreeOfRepository> worktrees)
        => string.Join('\n', found
            .Select(repository => (Key: KnownRepositoryStore.NormalizePathKey(repository.Path), repository.Name))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}\t{entry.Name}")
            .Concat(rootFolders
                .Select(root => string.Join('\t', (root.ChildPaths ?? Array.Empty<string>())
                    .Select(KnownRepositoryStore.NormalizePathKey)
                    .OrderBy(child => child, StringComparer.Ordinal)
                    .Prepend(KnownRepositoryStore.NormalizePathKey(root.Path))))
                .OrderBy(line => line, StringComparer.Ordinal))
            .Concat(worktrees
                .Select(worktree => NormalizePair(worktree))
                .OrderBy(line => line, StringComparer.Ordinal))
            .Prepend(machine));

    private static string NormalizePair(WorktreeOfRepository worktree)
        => KnownRepositoryStore.NormalizePathKey(worktree.Path) + '\u0001'
           + KnownRepositoryStore.NormalizePathKey(worktree.RepositoryPath);
}
