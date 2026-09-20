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
        var sawProvisional = false;
        foreach (var repository in repositories)
        {
            if (repository.Provisional)
            {
                sawProvisional = true;
                continue; // unverified warm-start data never enters the catalog
            }
            if (string.IsNullOrWhiteSpace(repository.Path))
                continue; // the path IS the identity - a pathless row cannot be keyed
            found.Add(new DiscoveredRepository(repository.Path, repository.Name ?? ""));
        }

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
        var signature = Signature(machine, found);
        if (_folded.TryGetValue(key, out var last)
            && string.Equals(last.Signature, signature, StringComparison.Ordinal)
            && seen - last.AtUtc < KnownRepositoryStore.LastSeenFreshnessInterval)
            return;

        _catalog.ObserveDiscovered(tenant, machine, directorId, found, seen, reconcile);
        _folded[key] = new FoldedSnapshot(signature, seen);
    }

    /// <summary>
    /// Everything one fold writes, in one string: the machine it writes under, and each repository's
    /// normalized path key with the name that rides beside it. Ordered by the key, because a scan
    /// publishes in whatever order it finished and the same set in a different order is the same set.
    /// </summary>
    private static string Signature(string machine, IReadOnlyList<DiscoveredRepository> found)
        => string.Join('\n', found
            .Select(repository => (Key: KnownRepositoryStore.NormalizePathKey(repository.Path), repository.Name))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}\t{entry.Name}")
            .Prepend(machine));
}
