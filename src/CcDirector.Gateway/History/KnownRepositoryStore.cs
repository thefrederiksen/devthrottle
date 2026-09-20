using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.History;

/// <summary>One repository a Director reported under one of its registered root folders. The NAME is the
/// one the Director computed on the machine that owns the path - the Gateway never recomputes it.</summary>
public readonly record struct DiscoveredRepository(string Path, string Name);

/// <summary>
/// One registered root folder a Director could positively LIST, and the full path of every direct child
/// folder that existed under it at that moment - git repository or not (the one-repository-list mission,
/// "the catalogue forgets").
///
/// THE PRESENCE OF ONE OF THESE IS THE PERMISSION TO FORGET a used repository under that root, so a root
/// the Director could not read is never one of these. See <see cref="RootFolderListingDto"/> for why the
/// Director's root-folder SCAN cannot answer this question and a plain directory listing has to.
/// </summary>
public readonly record struct WatchedRootFolder(string Path, IReadOnlyList<string> ChildPaths);

/// <summary>
/// ONE POSITIVE STATEMENT BY A DIRECTOR: the folder at <paramref name="Path"/> is a linked git WORKTREE
/// of the repository at <paramref name="RepositoryPath"/>, on the machine that holds the disk (the
/// one-repository-list mission, "a worktree is not a repository").
///
/// It is what lets the catalogue COLLAPSE a worktree's row into its repository's. Only the Director can
/// say it - the answer is written inside the folder, and the Gateway is a Linux container holding paths
/// pushed up by Windows and macOS Directors and is never the machine a path describes. It comes from the
/// Director's own root-folder scan, which computes every scanned repository's worktrees with git, and
/// rides the repository push that already carries them.
/// </summary>
public readonly record struct WorktreeOfRepository(string Path, string RepositoryPath);

/// <summary>
/// The durable catalog of repositories the Gateway knows about, grouped by tenant and machine. Session
/// history is intentionally retained for only ninety days; this catalog is not part of that sweep.
///
/// It holds BOTH halves of the one repository list (the one-repository-list mission, phase 2) in one
/// table: <see cref="Observe"/> writes the USED half from a session observation, and
/// <see cref="ObserveDiscovered"/> writes the DISCOVERED half from a Director's root-folder scan. A
/// discovered row carries NO last-used time, and the two halves never write each other's facts - see
/// the remarks on <see cref="ObserveDiscovered"/>.
///
/// <see cref="ReadForMachine"/> serves that one table as ONE LIST in ONE ORDER (phase 3), so that no
/// client sorts for itself. The order is decided in <see cref="OrderOneList"/> and nowhere else.
/// </summary>
public sealed class KnownRepositoryStore
{
    public const int MaxIdentityChars = 1024;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public KnownRepositoryStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Insert or refresh one observed repository. Older observations never move the timestamp or display
    /// facts backwards. Returns true when the durable row changed.
    /// </summary>
    public bool Observe(TenantId tenant, string machineName, string path, string? name, DateTime lastUsedUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));

        var machine = Required(machineName, nameof(machineName));
        var repositoryPath = Required(path, nameof(path));
        var machineKey = NormalizeMachineKey(machine);
        var pathKey = NormalizePathKey(repositoryPath);
        var candidateMachineKeys = CandidateMachineKeys(machine);
        var candidatePathKeys = CandidatePathKeys(repositoryPath);
        var used = DateTime.SpecifyKind(lastUsedUtc.ToUniversalTime(), DateTimeKind.Utc);
        var displayName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        if (displayName.Length > MaxIdentityChars)
            throw new ArgumentException($"The repository name exceeds {MaxIdentityChars} characters.", nameof(name));

        var changed = false;
        lock (_gate)
        {
            using var context = _db.CreateContext(tenant);

            // Current rows use the machine index and filter PathKey in the database, so an ordinary observe
            // does not materialize the machine catalog. The compatibility fallback is machine-scoped and only
            // runs when the original migration left an exact, unnormalized PathKey. Both queries share the
            // machine candidates used by ReadForMachine.
            var candidates = context.KnownRepositories
                .Where(row => candidateMachineKeys.Contains(row.MachineKey)
                              && candidatePathKeys.Contains(row.PathKey))
                .ToList()
                .Where(row => string.Equals(
                    NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal))
                .ToList();
            var existing = candidates.FirstOrDefault(row => string.Equals(
                NormalizePathKey(row.Path), pathKey, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = context.KnownRepositories
                    .Where(row => candidateMachineKeys.Contains(row.MachineKey))
                    .ToList()
                    .FirstOrDefault(row =>
                        string.Equals(NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal)
                        && string.Equals(NormalizePathKey(row.Path), pathKey, StringComparison.Ordinal));
            }

            if (existing is null)
            {
                context.KnownRepositories.Add(new KnownRepositoryEntity
                {
                    TenantId = tenant.Value,
                    MachineKey = machineKey,
                    PathKey = pathKey,
                    MachineName = machine,
                    Path = repositoryPath,
                    Name = displayName,
                    LastUsedUtc = used,
                });
                changed = true;
            }
            else
            {
                // Repair compatibility keys opportunistically, but reads never depend on this repair: a
                // retired machine with an original migration row remains readable without another observe.
                if (!string.Equals(existing.MachineKey, machineKey, StringComparison.Ordinal))
                {
                    existing.MachineKey = machineKey;
                    changed = true;
                }
                if (!string.Equals(existing.PathKey, pathKey, StringComparison.Ordinal))
                {
                    existing.PathKey = pathKey;
                    changed = true;
                }

                // A row with NO last-used time is a discovered repository being opened for the first
                // time: it GAINS the time and stays the same row, which is the whole reason the two
                // halves share one table. Its discovered facts are kept, because the Director that
                // found it still finds it.
                if (existing.LastUsedUtc is not { } previous || used > previous)
                {
                    existing.MachineName = machine;
                    existing.Path = repositoryPath;
                    if (displayName.Length > 0)
                        existing.Name = displayName;
                    existing.LastUsedUtc = used;
                    changed = true;
                }
                else if (used == previous && existing.Name.Length == 0 && displayName.Length > 0)
                {
                    existing.Name = displayName;
                    changed = true;
                }
            }

            if (changed)
                context.SaveChanges();
        }

        FileLog.Write($"[KnownRepositoryStore] Observe: tenant={tenant.ToLogString()} machine={machine} path={repositoryPath} changed={changed}");
        return changed;
    }

    /// <summary>
    /// How far the last-seen stamp of a discovered row is allowed to fall behind before a push rewrites
    /// it. A Director re-pushes its whole repository snapshot on a ten-second reseed, and a stamp
    /// refreshed on every one of those would be a database write per Director every ten seconds for a
    /// fact nothing reads to the second - the noisy-neighbour cost <see cref="Streaming.RepoHistoryStore"/>
    /// already refused for the same reason. The accepted cost is that "last seen" is accurate to within
    /// this interval.
    /// </summary>
    public static readonly TimeSpan LastSeenFreshnessInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Apply one Director's FULL root-folder scan for one machine: the DISCOVERED half of the catalog.
    /// Returns true when the durable rows changed.
    ///
    /// <paramref name="machineName"/> must come from the Director REGISTRATION, because that is what the
    /// read side looks rows up by (<c>GET /directors/{id}/known-repositories</c> reads
    /// <c>director.MachineName</c>). Rows written under any other spelling of the machine exist and no
    /// screen ever shows them. <paramref name="directorId"/> must be the id BOUND to the pushing
    /// connection, never one carried in a payload - it is the reconciliation scope below.
    ///
    /// What it does, and the three invariants that make it safe to run over a live catalog:
    /// <list type="number">
    ///   <item>A repository in the snapshot with no row yet is INSERTED with NO last-used time. Null is
    ///     "found but never opened".</item>
    ///   <item>A row that already has a last-used time is LEFT ALONE - not refreshed, not re-stamped, not
    ///     claimed. The used half is untouchable from here: a discovered observation never creates, moves
    ///     or clears a last-used time. A repository that is used keeps its place in the order however
    ///     often it is found again.</item>
    ///   <item>A never-opened row belongs to the Director that reported it, and another Director reporting
    ///     the same path leaves it alone. Two Directors on one machine therefore cannot rewrite or delete
    ///     each other's rows. The cost, deliberately taken: if the owning Director stops reporting a path
    ///     another one still finds, the row is removed here and the other Director's next push inserts it
    ///     again under its own id.</item>
    /// </list>
    ///
    /// <paramref name="reconcile"/> is how removing a root folder takes effect: never-opened rows for THIS
    /// Director that are absent from the snapshot are removed. It is the caller's decision because only the
    /// caller can tell a real observation from a cold start - see
    /// <see cref="DiscoveredRepositoryObserver"/>, which mirrors the rule
    /// <see cref="Streaming.RepoHistoryStore.ObserveSnapshot"/> already pays for: an empty or
    /// all-provisional push must never be mistaken for "every repository was removed".
    ///
    /// <para><b>AND THE CATALOGUE FORGETS A FOLDER THAT NO LONGER EXISTS</b> (the one-repository-list
    /// mission, "the catalogue forgets"). Until this, nothing ever removed a row that had been USED, so a
    /// folder created, worked in and deleted stayed in every screen's list for ever: measured against the
    /// live Gateway on 20 September 2026, one machine's catalogue held 90 repositories of which 76 no
    /// longer existed. <paramref name="rootFolders"/> is what makes removing one safe. A used row is
    /// forgotten ONLY when all four of these hold:</para>
    /// <list type="number">
    ///   <item>this is a real observation (<paramref name="reconcile"/>), so never on an empty push, never
    ///     on an all-provisional one, and never from a Director that has gone quiet - a Director that
    ///     says nothing removes nothing;</item>
    ///   <item>its folder's PARENT is one of the roots in <paramref name="rootFolders"/> - a root this
    ///     Director could positively read just now. <b>A root the Director CANNOT list is omitted from
    ///     that set entirely, so nothing beneath it is ever forgotten</b>: an unmounted disk, an
    ///     unreadable folder and a root that has stopped being watched each mean "I know nothing here"
    ///     and never "nothing is here". This is a destructive operation, so it acts only on what it can
    ///     positively prove is disposable;</item>
    ///   <item>its path is in neither the snapshot nor that root's child listing, so the Director has
    ///     positively said the folder is not there; and</item>
    ///   <item>nothing about which Director owns it, because a used row has no owner - which is why the
    ///     root, and not ownership, is the scope. Another Director on the same machine reports its OWN
    ///     roots, so it can only ever forget what is under those.</item>
    /// </list>
    /// <para><b>The child listing is load-bearing and is not the same as the snapshot.</b> The snapshot is
    /// the root-folder SCAN, which reports a direct child only when its <c>.git</c> is a DIRECTORY - so a
    /// git worktree has never been in it. On the machine measured above, ELEVEN of the fourteen surviving
    /// repositories were worktrees, and forgetting rows that were merely absent from the snapshot would
    /// have deleted all eleven live folders.</para>
    /// <para><b>What is lost is the LAST-ACCESS TIME, and it does not come back.</b> A forgotten row is
    /// DELETED, not hidden, and the last-access time is this mission's whole signal. If the folder
    /// returns - a worktree re-made under the same name - its history does NOT return with it: it comes
    /// back as never-opened and sits at the bottom of the list until it is next used. Nothing else is
    /// lost, because the row holds no other fact a screen reads. That is the accepted cost of a list that
    /// describes the disk.</para>
    ///
    /// <para><b>AND A WORKTREE IS NOT A REPOSITORY: ITS ROW IS COLLAPSED INTO ITS REPOSITORY'S</b> (the
    /// one-repository-list mission, "a worktree is not a repository"). Every agent session on this fleet
    /// runs in a git worktree, so every worktree that ever hosted one became its own row: measured
    /// against the live Gateway on 20 September 2026, one Windows machine's list served 559 repositories,
    /// of which 110 were live worktrees of four repositories, and thirteen of its top twenty rows - the
    /// part a person reads - were worktrees. The owner, reading it: "here you are showing the work trees.
    /// We should only be showing the repos."</para>
    /// <para><b>THE SAFETY PROPERTY, in the words it was ruled in: the Gateway collapses a row only on a
    /// POSITIVE OBSERVATION FROM THE DIRECTOR that the folder is a worktree of a named parent; SILENCE IS
    /// NEVER PERMISSION.</b> <paramref name="worktrees"/> carries those statements and nothing else. A
    /// folder the Director said nothing about is not touched, whatever its name, wherever it sits and
    /// however long it has been in the catalogue - so an unresolvable worktree, a folder that is gone, a
    /// bare repository's worktree, a git submodule (which IS a repository) and a plain folder are all
    /// left exactly as they are. It is the same direction the forgetting rule above fails in: act only on
    /// what can positively be proved, and enumerate what to CHANGE rather than what to skip.</para>
    /// <para>Three further conditions hold it shut: it runs only on a real observation
    /// (<paramref name="reconcile"/>), so never on an empty push, never on an all-provisional one and
    /// never from a Director that has gone quiet; the named repository must itself be in THIS push's
    /// snapshot, so a statement about a repository the Director is not currently reporting collapses
    /// nothing; and it is scoped to this machine like everything else here.</para>
    /// <para><b>What it does, and it is the one place a discovered observation moves a last-used
    /// time.</b> The repository's row takes the NEWER of its own last-used time and the worktree's, and
    /// the worktree's row is deleted. That is not an exception to invariant 2 so much as the meaning of
    /// the rule: using a worktree of a repository IS using the repository, so the repository's most
    /// recent use is the true answer. Nothing else moves - the worktree's row holds no other fact a
    /// screen reads - and, as with the forgetting rule, there is no undo and no tombstone.</para>
    ///
    /// Every path here goes through <see cref="NormalizePathKey"/>, which decides Windows-ness from the
    /// PATH'S OWN SHAPE. The Gateway is a Linux container holding paths written by Windows and macOS
    /// machines and is never the machine a path describes, so nothing here may ask the host what a path
    /// separator is or what a path's leaf is called. The NAME rides in with the observation, computed by
    /// the Director on the machine that owns the path; the Gateway never recomputes it.
    /// </summary>
    public bool ObserveDiscovered(TenantId tenant, string machineName, string directorId,
        IReadOnlyList<DiscoveredRepository> found, IReadOnlyList<WatchedRootFolder>? rootFolders,
        IReadOnlyList<WorktreeOfRepository>? worktrees, DateTime seenUtc, bool reconcile)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        if (found is null)
            throw new ArgumentNullException(nameof(found));

        var machine = Required(machineName, nameof(machineName));
        var reporter = Required(directorId, nameof(directorId));
        var machineKey = NormalizeMachineKey(machine);
        var candidateMachineKeys = CandidateMachineKeys(machine);
        var seen = DateTime.SpecifyKind(seenUtc.ToUniversalTime(), DateTimeKind.Utc);

        // Normalize the snapshot once. A row this loop drops is dropped from the RECONCILE set too, which
        // is why the set is built here rather than read twice: a row that cannot be keyed must not be able
        // to delete the row it would have matched. Over-long and pathless rows are skipped and logged
        // rather than thrown, because one unusable row in a bulk observation must not lose the other
        // thirty - unlike Observe, whose caller is a single session start.
        var snapshot = new Dictionary<string, DiscoveredRepository>(StringComparer.Ordinal);
        foreach (var repository in found)
        {
            if (string.IsNullOrWhiteSpace(repository.Path))
            {
                FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: a row without a path was ignored (name={repository.Name})");
                continue;
            }
            var repositoryPath = repository.Path.Trim();
            if (repositoryPath.Length > MaxIdentityChars)
            {
                FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: a path longer than {MaxIdentityChars} characters was ignored");
                continue;
            }
            var displayName = string.IsNullOrWhiteSpace(repository.Name) ? "" : repository.Name.Trim();
            if (displayName.Length > MaxIdentityChars)
            {
                FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: a name longer than {MaxIdentityChars} characters was dropped for {repositoryPath}");
                displayName = "";
            }
            snapshot[NormalizePathKey(repositoryPath)] = new DiscoveredRepository(repositoryPath, displayName);
        }

        // The roots this Director could positively read, and everything it saw beside each of them, keyed
        // the one way this class keys every path. A root with no usable path is dropped rather than
        // matched against nothing.
        var coveredRootKeys = new HashSet<string>(StringComparer.Ordinal);
        var stillThere = new HashSet<string>(snapshot.Keys, StringComparer.Ordinal);
        foreach (var root in rootFolders ?? Array.Empty<WatchedRootFolder>())
        {
            if (string.IsNullOrWhiteSpace(root.Path))
                continue;
            coveredRootKeys.Add(NormalizePathKey(root.Path.Trim()));
            foreach (var child in root.ChildPaths ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(child))
                    stillThere.Add(NormalizePathKey(child.Trim()));
            }
        }

        var changed = false;
        var inserted = 0;
        var removed = 0;
        var forgotten = 0;
        var collapsed = 0;
        lock (_gate)
        {
            using var context = _db.CreateContext(tenant);

            // The machine's whole catalog, read through the same machine candidates ReadForMachine uses so
            // a row written under a legacy key is found rather than duplicated. Materialized because the
            // normalized comparison below is not a database expression.
            var rows = context.KnownRepositories
                .Where(row => candidateMachineKeys.Contains(row.MachineKey))
                .ToList()
                .Where(row => string.Equals(
                    NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal))
                .ToList();
            var existingByPath = rows
                .GroupBy(row => NormalizePathKey(row.Path), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var insertedByPath = new Dictionary<string, KnownRepositoryEntity>(StringComparer.Ordinal);
            var collapsedAway = new HashSet<KnownRepositoryEntity>();

            foreach (var (pathKey, repository) in snapshot)
            {
                if (!existingByPath.TryGetValue(pathKey, out var matches))
                {
                    var added = new KnownRepositoryEntity
                    {
                        TenantId = tenant.Value,
                        MachineKey = machineKey,
                        PathKey = pathKey,
                        MachineName = machine,
                        Path = repository.Path,
                        Name = repository.Name,
                        LastUsedUtc = null,
                        DiscoveredByDirectorId = reporter,
                        LastSeenUtc = seen,
                    };
                    context.KnownRepositories.Add(added);
                    // Remembered so the collapse below can find a repository that was inserted moments
                    // ago: a worktree's row must be able to fold into a repository this very push is the
                    // first to report.
                    insertedByPath[pathKey] = added;
                    changed = true;
                    inserted++;
                    continue;
                }

                // Invariant 2: any row for this path that has been used is untouchable from here.
                if (matches.Any(row => row.LastUsedUtc is not null))
                    continue;

                // Invariant 3: a never-opened row belongs to the Director that reported it.
                var mine = matches.FirstOrDefault(row => string.Equals(
                    row.DiscoveredByDirectorId, reporter, StringComparison.OrdinalIgnoreCase));
                if (mine is null)
                    continue;

                if (mine.LastSeenUtc is not { } lastSeen || seen - lastSeen >= LastSeenFreshnessInterval)
                {
                    mine.LastSeenUtc = seen;
                    changed = true;
                }
                // The Director's own facts, carried: its spelling of the path, and the name it computed on
                // the machine that owns that path. A blank name never overwrites one already held.
                if (repository.Name.Length > 0 && !string.Equals(mine.Name, repository.Name, StringComparison.Ordinal))
                {
                    mine.Name = repository.Name;
                    changed = true;
                }
                if (!string.Equals(mine.Path, repository.Path, StringComparison.Ordinal))
                {
                    mine.Path = repository.Path;
                    changed = true;
                }
                if (!string.Equals(mine.MachineName, machine, StringComparison.Ordinal))
                {
                    mine.MachineName = machine;
                    changed = true;
                }
                if (!string.Equals(mine.MachineKey, machineKey, StringComparison.Ordinal))
                {
                    mine.MachineKey = machineKey;
                    changed = true;
                }
                if (!string.Equals(mine.PathKey, pathKey, StringComparison.Ordinal))
                {
                    mine.PathKey = pathKey;
                    changed = true;
                }
            }

            // A WORKTREE IS NOT A REPOSITORY: ITS ROW FOLDS INTO ITS REPOSITORY'S, keeping the newer of
            // the two last-used times. Everything that makes this safe is in the remarks above; the one
            // that lives here is that ONLY a path the Director positively named as a worktree of a named
            // repository is touched, and that the repository must be in THIS push's snapshot - silence
            // is never permission.
            //
            // IT RUNS BEFORE THE FORGETTING BELOW, on purpose. A worktree folder that has gone could be
            // reached by both rules, and they do different things with it: this one MOVES its last-used
            // time onto the repository, the other DELETES it and the time with it. Doing this first means
            // a row that can be accounted for is accounted for, and only what nobody claims is forgotten.
            // That is the keep-leaning order.
            if (reconcile && worktrees is { Count: > 0 })
            {
                foreach (var worktree in worktrees)
                {
                    if (string.IsNullOrWhiteSpace(worktree.Path) || string.IsNullOrWhiteSpace(worktree.RepositoryPath))
                        continue;
                    var childKey = NormalizePathKey(worktree.Path.Trim());
                    var repositoryKey = NormalizePathKey(worktree.RepositoryPath.Trim());
                    if (string.Equals(childKey, repositoryKey, StringComparison.Ordinal))
                        continue; // a repository is not a worktree of itself

                    // The named repository must be one this push reports. A statement about a repository
                    // the Director is not currently reporting has nothing to fold into, and inventing a
                    // row for it here would be the Gateway deciding something it cannot see.
                    if (!snapshot.ContainsKey(repositoryKey))
                        continue;

                    var children = existingByPath.TryGetValue(childKey, out var childRows) ? childRows : null;
                    if (children is null || children.Count == 0)
                        continue; // nothing in the catalogue for this worktree - nothing to collapse

                    var repositoryRow = existingByPath.TryGetValue(repositoryKey, out var repositoryRows)
                        ? repositoryRows.OrderByDescending(row => row.LastUsedUtc).First()
                        : insertedByPath.GetValueOrDefault(repositoryKey);
                    if (repositoryRow is null || collapsedAway.Contains(repositoryRow))
                        continue;

                    var newest = children.Max(row => row.LastUsedUtc);
                    if (newest is { } used && (repositoryRow.LastUsedUtc is not { } already || used > already))
                    {
                        repositoryRow.LastUsedUtc = used;
                        changed = true;
                    }

                    foreach (var child in children)
                    {
                        if (!collapsedAway.Add(child))
                            continue;
                        context.KnownRepositories.Remove(child);
                        changed = true;
                        collapsed++;
                        FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: collapsing {child.Path} into "
                                      + $"{repositoryRow.Path} - {reporter} reports it is a worktree of it");
                    }
                }
            }

            // Removing a root folder takes effect HERE, and nowhere else. Scoped to (this tenant, this
            // machine, this Director, no last-used time), so a used repository and another Director's
            // findings both survive it.
            if (reconcile)
            {
                var stale = rows
                    .Where(row => !collapsedAway.Contains(row))
                    .Where(row => row.LastUsedUtc is null
                                  && string.Equals(row.DiscoveredByDirectorId, reporter, StringComparison.OrdinalIgnoreCase)
                                  && !snapshot.ContainsKey(NormalizePathKey(row.Path)))
                    .ToList();
                foreach (var row in stale)
                    context.KnownRepositories.Remove(row);
                if (stale.Count > 0)
                {
                    changed = true;
                    removed = stale.Count;
                }

                // AND THE FOLDER THAT NO LONGER EXISTS IS FORGOTTEN, used or not (the one-repository-list
                // mission, "the catalogue forgets"). Everything that makes this safe is in the four
                // conditions on the remarks above; the two that live here are that the row's PARENT is a
                // root this Director could read just now, and that the Director listed that root without
                // it. A row whose parent is not a covered root - another machine's layout, a folder
                // outside every watched root, a root this Director does not watch or could not read - is
                // not even looked at.
                //
                // The parent is compared, not a prefix, because a root folder's scan and its listing both
                // reach exactly one level: a repository two levels down was never reported by this root
                // and this root may not speak for it. If that ever changes, this under-claims and keeps a
                // row that could have gone, which is the direction to fail in.
                var gone = rows
                    .Where(row => !collapsedAway.Contains(row))
                    .Where(row => row.LastUsedUtc is not null)
                    .Where(row =>
                    {
                        var key = NormalizePathKey(row.Path);
                        return coveredRootKeys.Contains(ParentPathKey(key)) && !stillThere.Contains(key);
                    })
                    .ToList();
                foreach (var row in gone)
                    context.KnownRepositories.Remove(row);
                if (gone.Count > 0)
                {
                    changed = true;
                    forgotten = gone.Count;
                    foreach (var row in gone)
                        FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: forgetting {row.Path} - "
                                      + $"{reporter} listed its root folder and it was not there");
                }
            }

            if (changed)
                context.SaveChanges();
        }

        FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: tenant={tenant.ToLogString()} machine={machine} "
                      + $"director={reporter} found={snapshot.Count} inserted={inserted} removed={removed} "
                      + $"forgotten={forgotten} collapsed={collapsed} roots={coveredRootKeys.Count} "
                      + $"worktrees={worktrees?.Count ?? 0} reconcile={reconcile} changed={changed}");
        return changed;
    }

    /// <summary>
    /// Read the ONE repository list for one machine, already in the order every screen shows it (the
    /// one-repository-list mission, phase 3): most recently used first, with never-opened repositories
    /// beneath everything that has been used. There is deliberately no result cap - a client needs to
    /// search the complete catalog rather than a hidden recent subset.
    ///
    /// IT SERVES BOTH HALVES, AND THE ORDER IS THE RULING. A row with a last-used time has been opened; a
    /// row without one was found by a Director under a registered root folder and never opened, and says
    /// so on the wire in <see cref="KnownRepositoryDto.NeverOpened"/> rather than leaving a client to
    /// decide what an absent date means. This is Critical Rule 7 (CLAUDE.md) applied to a list instead of
    /// a verdict: the Gateway rules once, here, and no client sorts for itself or invents a different
    /// answer.
    ///
    /// READ THIS BEFORE MOVING THE SORT INTO THE DATABASE. THE SORT IS DELIBERATELY IN C#, over rows that
    /// have already been materialized, and phase 3 kept it there on purpose. C# and PostgreSQL disagree
    /// about where a null goes in a descending sort:
    ///
    ///   OrderByDescending on a DateTime? puts null LAST  - never-opened beneath everything used, which is
    ///                                                      what this mission's goal 2 asks for.
    ///   PostgreSQL ORDER BY ... DESC puts NULLS FIRST    - every never-opened repository at the TOP of
    ///                                                      every screen, the exact inversion of it.
    ///
    /// And the disagreement is INVISIBLE to this repository's database tests, which is the dangerous part:
    /// SQLite sorts nulls as smallest, so its DESC puts them LAST and agrees with C#. Both were run rather
    /// than remembered. A sort pushed into SQL would therefore pass every test here and invert the list on
    /// the hosted Gateway alone, which is the one place nobody can test before shipping. If a later change
    /// does move it, it must say NULLS LAST explicitly.
    ///
    /// The ordering itself lives in <see cref="OrderOneList"/>, which takes a materialized list rather
    /// than a query for exactly that reason, and is proved to produce the same order whatever order the
    /// rows are handed to it in - including the nulls-first order PostgreSQL would hand over.
    /// </summary>
    public IReadOnlyList<KnownRepositoryDto> ReadForMachine(TenantId tenant, string machineName)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid tenant is required.", nameof(tenant));
        var machine = Required(machineName, nameof(machineName));
        var machineKey = NormalizeMachineKey(machine);
        var candidateMachineKeys = CandidateMachineKeys(machine);

        List<KnownRepositoryEntity> rows;
        lock (_gate)
        {
            using var context = _db.CreateContext(tenant);
            rows = context.KnownRepositories.AsNoTracking()
                .Where(row => candidateMachineKeys.Contains(row.MachineKey))
                .ToList();
        }

        var result = OrderOneList(rows, machineKey);
        var neverOpened = result.Count(row => row.NeverOpened);
        FileLog.Write($"[KnownRepositoryStore] ReadForMachine: tenant={tenant.ToLogString()} machine={machine} "
                      + $"count={result.Count} neverOpened={neverOpened}");
        return result;
    }

    /// <summary>
    /// THE ONE ORDER, decided in one place, over rows that are already in memory.
    ///
    /// It takes an <see cref="IReadOnlyList{T}"/> and not a query on purpose: the order a client sees must
    /// be decided here and not by whichever database the Gateway happens to be running on, and the two
    /// disagree about nulls in a way no test in this repository can catch (see the remarks on
    /// <see cref="ReadForMachine"/>). Because this is a pure function over a materialized list, it can be
    /// handed rows in the exact nulls-first order PostgreSQL would produce and proved to re-order them.
    ///
    /// The rules, in order:
    /// <list type="number">
    ///   <item>Only rows whose machine name normalizes to this machine - the candidate-key query above is
    ///     deliberately wider than the answer, so a row written under a legacy key is found rather than
    ///     missed, and this is where it is narrowed.</item>
    ///   <item>One row per repository. Two rows can share a path when the machine name was written with
    ///     different spellings, and the USED one wins - a repository that has been opened never loses its
    ///     place in the order to a discovered duplicate.</item>
    ///   <item>Each row is given THE NAME A PERSON CAN TELL APART - see below. It happens before the
    ///     ordering, because the list is sorted by the name that is shown.</item>
    ///   <item>Most recently used first; never-opened beneath everything used.</item>
    ///   <item>Within a tie - and every never-opened row ties with every other - by name and then by path,
    ///     so the list is a total order. Two clients reading the same catalog see the same list, and a
    ///     screen does not reshuffle between reads.</item>
    /// </list>
    ///
    /// <para><b>THE NAME (the one-repository-list mission, "the catalogue forgets").</b> A used row's
    /// stored name is whatever the session carried - in practice the repository's GitHub slug, or nothing
    /// at all - and a Director's push can never correct it, because a used row is untouchable from
    /// <see cref="ObserveDiscovered"/>. Measured against the live Gateway on 20 September 2026: one
    /// machine's 90 rows carried THREE distinct names between them - 66 said
    /// <c>thefrederiksen/devthrottle</c>, 6 said <c>thefrederiksen/devthrottle_internal</c>, and 18 said
    /// nothing - across ninety different folders. So the Gateway serves the folder name from the path when
    /// the stored name is blank, or when it is shared with another row and therefore tells the two apart
    /// from nothing.</para>
    ///
    /// <para>It is done HERE, in the one fold, and never in a client: Critical Rule 7 (CLAUDE.md). A
    /// client that decided for itself when a name was worth showing would decide differently from the next
    /// client, and three screens showing one machine would disagree again - which is the defect this
    /// mission exists to end. The name comes from <see cref="RepositoryPaths.FolderName"/>, which reads
    /// the path's own shape: the Gateway is a Linux container holding paths written by Windows and macOS
    /// machines, and <c>Path.GetFileName</c> handed a Windows path on Linux returns the whole path.</para>
    ///
    /// <para>A name that is unique keeps its stored spelling, which is the ruling as given: a slug that
    /// distinguishes the row is a good name for it. A path with no folder name in it at all keeps whatever
    /// it had, because an empty name is not an improvement on a poor one.</para>
    /// </summary>
    internal static IReadOnlyList<KnownRepositoryDto> OrderOneList(
        IReadOnlyList<KnownRepositoryEntity> rows, string machineKey)
    {
        var deduplicated = rows
            .Where(row => string.Equals(
                NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal))
            .GroupBy(row => NormalizePathKey(row.Path), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.LastUsedUtc).First())
            .ToList();

        // How many rows each stored name would have to speak for. A name held by more than one row cannot
        // tell them apart, so none of them keeps it. Blank names are not counted here because they are
        // replaced whether they are shared or not.
        var timesUsed = deduplicated
            .Select(row => (row.Name ?? "").Trim())
            .Where(name => name.Length > 0)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return deduplicated
            .Select(row => new KnownRepositoryDto
            {
                Name = DisplayName(row, timesUsed),
                Path = row.Path,
                LastUsed = row.LastUsedUtc,
                // Stamped here and nowhere else, so the flag and the time can never disagree.
                NeverOpened = row.LastUsedUtc is null,
            })
            .OrderByDescending(row => row.LastUsed)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The name this row is served under: the stored one when it tells this row apart from every other,
    /// and otherwise the folder the path ends in. See the remarks on <see cref="OrderOneList"/>.
    /// </summary>
    private static string DisplayName(KnownRepositoryEntity row, IReadOnlyDictionary<string, int> timesUsed)
    {
        var stored = (row.Name ?? "").Trim();
        if (stored.Length > 0 && timesUsed.TryGetValue(stored, out var count) && count == 1)
            return stored;

        var folder = RepositoryPaths.FolderName(row.Path);
        return folder.Length > 0 ? folder : stored;
    }

    internal static string NormalizeMachineKey(string machineName) =>
        machineName.Trim().ToUpperInvariant();

    internal static string LegacyAsciiMachineKey(string machineName)
    {
        var chars = machineName.Trim().ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'a' and <= 'z')
                chars[index] = (char)(chars[index] - ('a' - 'A'));
        }
        return new string(chars);
    }

    internal static string NormalizePathKey(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            // Keep a Windows drive root (for example C:/) intact even though a repository is not normally
            // registered at the root.
            if (normalized.Length == 3 && char.IsLetter(normalized[0]) && normalized[1] == ':')
                break;
            normalized = normalized[..^1];
        }

        var isWindowsPath = (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
                            || normalized.StartsWith("//", StringComparison.Ordinal);
        return isWindowsPath ? normalized.ToUpperInvariant() : normalized;
    }

    /// <summary>
    /// The folder one normalized path key sits directly in, as a key - or an empty string when the key has
    /// no parent (a bare name, or a root). It takes a key rather than a path because
    /// <see cref="NormalizePathKey"/> has already done every decision that could go wrong: both separators
    /// are now one, a trailing one is gone, and a Windows path is upper-cased from its OWN shape rather
    /// than from whatever machine the Gateway happens to be. All that is left is to cut at the last
    /// separator, which is the same answer on every operating system.
    ///
    /// A key with no separator answers empty, and empty never matches a covered root because a root with
    /// no usable path is dropped before the comparison. The two roots that are not simply "everything
    /// before the last separator" are spelled out: the POSIX root is <c>/</c> and not the empty string,
    /// and a Windows drive root is <c>C:/</c> and not <c>C:</c> - which is the spelling
    /// <see cref="NormalizePathKey"/> keeps for a drive root, so the two agree.
    /// </summary>
    internal static string ParentPathKey(string pathKey)
    {
        var cut = pathKey.LastIndexOf('/');
        if (cut < 0)
            return "";
        if (cut == 0)
            return "/";
        var parent = pathKey[..cut];
        return parent.Length == 2 && char.IsLetter(parent[0]) && parent[1] == ':' ? parent + "/" : parent;
    }

    private static List<string> CandidateMachineKeys(string machineName) =>
        new[]
        {
            NormalizeMachineKey(machineName),
            LegacyAsciiMachineKey(machineName),
            machineName.Trim(),
        }.Distinct(StringComparer.Ordinal).ToList();

    private static List<string> CandidatePathKeys(string path)
    {
        var trimmed = path.Trim();
        return new[]
        {
            NormalizePathKey(trimmed),
            trimmed,
            trimmed.Replace('\\', '/'),
            trimmed.Replace('/', '\\'),
        }.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdentityChars)
            throw new ArgumentException($"The value exceeds {MaxIdentityChars} characters.", parameterName);
        return trimmed;
    }
}
