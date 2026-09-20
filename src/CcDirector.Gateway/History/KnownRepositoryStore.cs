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
/// The durable catalog of repositories the Gateway knows about, grouped by tenant and machine. Session
/// history is intentionally retained for only ninety days; this catalog is not part of that sweep.
///
/// It holds BOTH halves of the one repository list (the one-repository-list mission, phase 2) in one
/// table: <see cref="Observe"/> writes the USED half from a session observation, and
/// <see cref="ObserveDiscovered"/> writes the DISCOVERED half from a Director's root-folder scan. A
/// discovered row carries NO last-used time, and the two halves never write each other's facts - see
/// the remarks on <see cref="ObserveDiscovered"/>.
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
    /// Every path here goes through <see cref="NormalizePathKey"/>, which decides Windows-ness from the
    /// PATH'S OWN SHAPE. The Gateway is a Linux container holding paths written by Windows and macOS
    /// machines and is never the machine a path describes, so nothing here may ask the host what a path
    /// separator is or what a path's leaf is called. The NAME rides in with the observation, computed by
    /// the Director on the machine that owns the path; the Gateway never recomputes it.
    /// </summary>
    public bool ObserveDiscovered(TenantId tenant, string machineName, string directorId,
        IReadOnlyList<DiscoveredRepository> found, DateTime seenUtc, bool reconcile)
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

        var changed = false;
        var inserted = 0;
        var removed = 0;
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

            foreach (var (pathKey, repository) in snapshot)
            {
                if (!existingByPath.TryGetValue(pathKey, out var matches))
                {
                    context.KnownRepositories.Add(new KnownRepositoryEntity
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
                    });
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

            // Removing a root folder takes effect HERE, and nowhere else. Scoped to (this tenant, this
            // machine, this Director, no last-used time), so a used repository and another Director's
            // findings both survive it.
            if (reconcile)
            {
                var stale = rows
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
            }

            if (changed)
                context.SaveChanges();
        }

        FileLog.Write($"[KnownRepositoryStore] ObserveDiscovered: tenant={tenant.ToLogString()} machine={machine} "
                      + $"director={reporter} found={snapshot.Count} inserted={inserted} removed={removed} "
                      + $"reconcile={reconcile} changed={changed}");
        return changed;
    }

    /// <summary>
    /// Read every USED repository retained for one machine, newest first. There is deliberately no result
    /// cap: the mobile client needs to search the complete catalog rather than a hidden recent subset.
    ///
    /// Rows with no last-used time - the DISCOVERED half - are deliberately NOT served here. Phase 2 of the
    /// one-repository-list mission STORES the root-folder scan; phase 3 owns serving the union and its
    /// order, and until it lands the phone reads exactly what it read before.
    ///
    /// READ THIS BEFORE MOVING THE SORT INTO THE DATABASE. The rows are materialized with ToList() and
    /// ordered IN MEMORY, and once the last-used time is nullable that is load-bearing rather than
    /// incidental. C# and PostgreSQL disagree about where a null goes in a descending sort:
    ///
    ///   OrderByDescending on a DateTime? puts null LAST  - never-opened beneath everything used, which is
    ///                                                      what this mission's goal 2 asks for.
    ///   PostgreSQL ORDER BY ... DESC puts NULLS FIRST    - every never-opened repository at the TOP of
    ///                                                      every screen, the exact inversion of it.
    ///
    /// And the disagreement is INVISIBLE to this repository's database tests, which is the dangerous part:
    /// SQLite sorts nulls as smallest, so its DESC puts them LAST and agrees with C#. Both were run rather
    /// than remembered. A sort pushed into SQL would therefore pass every test here and invert the list on
    /// the hosted Gateway alone. If phase 3 does move it, it must say NULLS LAST explicitly.
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

        var result = rows
            .Where(row => row.LastUsedUtc is not null)
            .Where(row => string.Equals(
                NormalizeMachineKey(row.MachineName), machineKey, StringComparison.Ordinal))
            .GroupBy(row => NormalizePathKey(row.Path), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.LastUsedUtc).First())
            .OrderByDescending(row => row.LastUsedUtc)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => new KnownRepositoryDto
            {
                Name = row.Name,
                Path = row.Path,
                // Non-null by the filter above: the discovered half is not served in phase 2.
                LastUsed = row.LastUsedUtc ?? default,
            })
            .ToList();

        FileLog.Write($"[KnownRepositoryStore] ReadForMachine: tenant={tenant.ToLogString()} machine={machine} count={result.Count}");
        return result;
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
