using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Durable store of <see cref="Mission"/> records (see
/// docs/new_architecture/fleet.html). A Mission is a first-class persisted
/// record that sessions attach to, so it MUST survive a Director restart - this store persists the whole
/// set to <c>missions.json</c> in the same director tool-config location as <see cref="SessionStateStore"/>
/// keeps <c>sessions.json</c>. Mirrors that store's JSON options + logging shape, but exposes a record API
/// (Create / Get / List / Delete) because a Mission is an addressable record, not a rebuilt-on-save list.
///
/// Every mutating call reads, mutates, and rewrites the file under a single lock so concurrent Control API
/// requests cannot interleave a half-written set.
///
/// PARTITIONED BY TENANT (devthrottle_internal issue #1039). One hosted Gateway process serves every
/// account from ONE of these files, so a mission carries the tenant that created it and every accessor
/// takes the tenant it is acting for. There is deliberately NO bare-id and NO unscoped accessor left on
/// this type: a caller cannot reach another tenant's record because there is no method that would return
/// one, rather than because each call site remembered to filter. The mission NAME is free text a person
/// typed - customer and project names - so an unpartitioned list is a disclosure of what other accounts
/// are working on, in their own words.
///
/// UNATTRIBUTED ROWS (<see cref="Mission.TenantId"/> null) are records written before missions carried an
/// owner. They cannot be attributed after the fact, so the store never guesses:
///  - Given an <c>adoptUnattributedAs</c> tenant, they are that tenant's. This is for a SINGLE-TENANT
///    install (self-host, the Director), where there is exactly one tenant by construction - so this is a
///    fact about the deployment, not an inference about the row.
///  - Given null (the hosted Gateway, where the file is shared), they belong to NOBODY: they match no
///    tenant, so they are never listed, never resolvable by id, and never deletable through this API.
///    They stay on disk - quarantined, not destroyed - so whoever holds the deployment can still inspect
///    or remove them out of band.
/// </summary>
public sealed class MissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();

    /// <summary>
    /// The tenant that owns rows written before missions carried an owner, or null to quarantine them.
    /// See the type remarks - this is a statement about the DEPLOYMENT (one tenant, or many sharing the
    /// file), decided once at construction where that is known, never inferred per row.
    /// </summary>
    private readonly TenantId? _adoptUnattributedAs;

    public string FilePath { get; }

    /// <param name="filePath">Where the set is persisted. Null uses the Director's tool-config location.</param>
    /// <param name="adoptUnattributedAs">
    /// The owner of unattributed rows. Pass <see cref="TenantId.Local"/> on a single-tenant install; pass
    /// null where more than one tenant shares the file, which quarantines them. It has no default on
    /// purpose: a store that serves several tenants must not inherit a single-tenant answer by omission.
    /// </param>
    public MissionStore(string? filePath, TenantId? adoptUnattributedAs)
    {
        _adoptUnattributedAs = adoptUnattributedAs;
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "missions.json");
        FileLog.Write($"[MissionStore] Initialized: FilePath={FilePath} " +
                      $"unattributedRows={(adoptUnattributedAs is { } t ? $"adopted as {t.ToLogString()}" : "quarantined")}");
    }

    /// <summary>
    /// Create and persist a new Mission with a freshly minted id, OWNED BY <paramref name="tenant"/> -
    /// which the caller resolves from its own authenticated identity, never from client input.
    /// <paramref name="missionName"/> is required (a blank name throws). Returns the created record.
    /// </summary>
    public Mission Create(TenantId tenant, string missionName)
    {
        RequireValid(tenant);
        if (string.IsNullOrWhiteSpace(missionName))
            throw new ArgumentException("missionName is required", nameof(missionName));

        FileLog.Write($"[MissionStore] Create: tenant={tenant.ToLogString()} name=\"{missionName}\"");

        var mission = new Mission
        {
            MissionId = Guid.NewGuid(),
            MissionName = missionName.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenant.Value,
        };

        lock (_lock)
        {
            var missions = LoadAll();
            missions.Add(mission);
            SaveAll(missions);
        }

        FileLog.Write($"[MissionStore] Create: created mission {mission.MissionId} for tenant {tenant.ToLogString()}");
        return mission;
    }

    /// <summary>
    /// Return <paramref name="tenant"/>'s Mission with the given id, or null when that tenant has no such
    /// mission. A mission another tenant owns is indistinguishable from one that does not exist, which is
    /// the point: the id alone reaches nothing.
    /// </summary>
    public Mission? Get(TenantId tenant, Guid missionId)
    {
        RequireValid(tenant);
        lock (_lock)
            return LoadAll().FirstOrDefault(m => m.MissionId == missionId && OwnedBy(m, tenant));
    }

    /// <summary>
    /// Return the Missions <paramref name="tenant"/> owns, oldest first.
    ///
    /// ACTIVE ONLY BY DEFAULT. A mission that has been completed or removed is history, and every caller
    /// that wants "what am I working on" wants it left out - which is all of them, until something asks for
    /// the archive. Pass <paramref name="state"/> to ask for one specific state, or
    /// <paramref name="includeEnded"/> to get everything.
    ///
    /// The default is the safe direction to be wrong in: a caller that has not been taught about states
    /// shows a shorter, correct list rather than a longer one full of finished work.
    /// </summary>
    /// <param name="state">One of <see cref="MissionStates"/> to return only that state, or null.</param>
    /// <param name="includeEnded">True to return every state. Ignored when <paramref name="state"/> is given.</param>
    public IReadOnlyList<Mission> List(TenantId tenant, string? state = null, bool includeEnded = false)
    {
        RequireValid(tenant);
        var wanted = MissionStates.Normalize(state);
        lock (_lock)
        {
            var mine = LoadAll().Where(m => OwnedBy(m, tenant));
            if (wanted is not null)
                mine = mine.Where(m => string.Equals(
                    MissionStates.Normalize(m.State) ?? MissionStates.Active, wanted, StringComparison.Ordinal));
            else if (!includeEnded)
                mine = mine.Where(m => m.IsActive);
            return mine.OrderBy(m => m.CreatedAt).ToList();
        }
    }

    /// <summary>
    /// Set (or clear) the WHY on <paramref name="tenant"/>'s Mission. A null/blank <paramref name="why"/>
    /// CLEARS it, returning the card to its "no why set" flag - there is no separate clear verb, exactly as
    /// the old note store worked. Returns the updated Mission, or null when this tenant has no such mission
    /// (indistinguishable from another tenant's, like every other accessor here).
    /// </summary>
    public Mission? SetWhy(TenantId tenant, Guid missionId, string? why, DateTimeOffset nowUtc)
    {
        RequireValid(tenant);
        var trimmed = (why ?? string.Empty).Trim();
        FileLog.Write($"[MissionStore] SetWhy: tenant={tenant.ToLogString()} mission={missionId} " +
                      $"cleared={trimmed.Length == 0}");
        lock (_lock)
        {
            var missions = LoadAll();
            var mission = missions.FirstOrDefault(m => m.MissionId == missionId && OwnedBy(m, tenant));
            if (mission is null)
                return null;

            mission.Why = trimmed;
            // A cleared WHY carries no "last set" time - the field is unset, not set-to-empty-at-a-moment.
            mission.WhyUpdatedAt = trimmed.Length == 0 ? null : nowUtc;
            SaveAll(missions);
            return mission;
        }
    }

    /// <summary>
    /// Rename <paramref name="tenant"/>'s Mission. Returns the updated Mission, or null when this tenant has
    /// no such mission - indistinguishable from another tenant's, like every accessor here.
    ///
    /// Renaming is SAFE because nothing keys off the name: sessions attach by <see cref="Mission.MissionId"/>,
    /// the Cockpit groups by it, and the WHY is a field on this record. That was not true until the WHY moved
    /// here - it used to live in a table keyed by the lower-cased name, and this method would have silently
    /// orphaned it. The name is display text, and this is the method that makes that literal.
    /// </summary>
    /// <exception cref="ArgumentException">The new name is null, empty, or whitespace.</exception>
    public Mission? Rename(TenantId tenant, Guid missionId, string missionName)
    {
        RequireValid(tenant);
        if (string.IsNullOrWhiteSpace(missionName))
            throw new ArgumentException("missionName is required", nameof(missionName));

        var trimmed = missionName.Trim();
        FileLog.Write($"[MissionStore] Rename: tenant={tenant.ToLogString()} mission={missionId} " +
                      $"name=\"{trimmed}\"");
        lock (_lock)
        {
            var missions = LoadAll();
            var mission = missions.FirstOrDefault(m => m.MissionId == missionId && OwnedBy(m, tenant));
            if (mission is null)
                return null;

            mission.MissionName = trimmed;
            SaveAll(missions);
            return mission;
        }
    }

    /// <summary>
    /// End (or reopen) <paramref name="tenant"/>'s Mission by setting its <see cref="Mission.State"/> to one
    /// of <see cref="MissionStates"/>. Returns the updated Mission, or null when this tenant has no such
    /// mission.
    ///
    /// REMOVED IS A SOFT DELETE - the record stays on disk. Two concrete reasons, not squeamishness: a
    /// mission number must never be reused, so the row has to keep holding its number; and sessions may
    /// still carry this mission id, so a vanished record would leave clients showing an attachment they
    /// cannot resolve. Actually erasing rows is a separate purge, not this verb.
    ///
    /// Setting the state it already has is a no-op that still returns the mission, so a caller repeating
    /// itself gets a success rather than a confusing failure.
    /// </summary>
    /// <exception cref="ArgumentException">The state is not one of the three.</exception>
    public Mission? SetState(TenantId tenant, Guid missionId, string state, DateTimeOffset nowUtc)
    {
        RequireValid(tenant);
        var normalized = MissionStates.Normalize(state)
            ?? throw new ArgumentException(
                $"state must be one of: {MissionStates.Active}, {MissionStates.Complete}, {MissionStates.Removed}",
                nameof(state));

        FileLog.Write($"[MissionStore] SetState: tenant={tenant.ToLogString()} mission={missionId} " +
                      $"state={normalized}");
        lock (_lock)
        {
            var missions = LoadAll();
            var mission = missions.FirstOrDefault(m => m.MissionId == missionId && OwnedBy(m, tenant));
            if (mission is null)
                return null;

            if (string.Equals(mission.State, normalized, StringComparison.Ordinal))
                return mission;

            mission.State = normalized;
            mission.StateChangedAt = nowUtc;
            SaveAll(missions);
            return mission;
        }
    }

    /// <summary>
    /// ONE-TIME MIGRATION: adopt WHYs that were written against the old name-keyed note store onto the
    /// Missions they belong to. <paramref name="whysByNormalizedName"/> maps a normalized (trimmed,
    /// lower-cased) mission name to its WHY. Returns how many Missions were filled in.
    ///
    /// TENANT-SCOPED BY CONSTRUCTION, and that is the whole reason this takes a tenant rather than a flat
    /// map of every note on the box. The notes were keyed by NAME, and a mission name is free text a person
    /// typed - customer names, project names. Matching notes to missions by name across the whole store
    /// would hand one account's stated reason for its work to another account that happened to name a
    /// mission the same thing. The caller groups notes by their owning tenant and calls this once per
    /// tenant; there is deliberately no bulk overload that could skip that step.
    ///
    /// IDEMPOTENT, and it never overwrites: a Mission that already has a WHY is left exactly as it is, so
    /// running this twice - or running it after somebody has set a WHY through the new path - cannot
    /// resurrect a stale one over a current one.
    ///
    /// A name matching SEVERAL missions fills all of them. That is not a merge bug; it is the faithful
    /// reproduction of what the old store did, because a single name-keyed note was already being shown on
    /// every card sharing that name. The migration's job is to preserve what was on screen, not to invent a
    /// resolution the old data never had.
    /// </summary>
    public int ImportWhys(TenantId tenant, IReadOnlyDictionary<string, string> whysByNormalizedName)
    {
        RequireValid(tenant);
        ArgumentNullException.ThrowIfNull(whysByNormalizedName);
        if (whysByNormalizedName.Count == 0)
            return 0;

        lock (_lock)
        {
            var missions = LoadAll();
            var filled = 0;
            foreach (var mission in missions)
            {
                if (!OwnedBy(mission, tenant))
                    continue;
                if (!string.IsNullOrWhiteSpace(mission.Why))
                    continue;

                var key = (mission.MissionName ?? string.Empty).Trim().ToLowerInvariant();
                if (!whysByNormalizedName.TryGetValue(key, out var why) || string.IsNullOrWhiteSpace(why))
                    continue;

                mission.Why = why.Trim();
                mission.WhyUpdatedAt = mission.CreatedAt;
                filled++;
            }

            if (filled > 0)
                SaveAll(missions);
            FileLog.Write($"[MissionStore] ImportWhys: tenant={tenant.ToLogString()} " +
                          $"candidates={whysByNormalizedName.Count} filled={filled}");
            return filled;
        }
    }

    /// <summary>
    /// Delete <paramref name="tenant"/>'s Mission with the given id. Returns true when a record was
    /// removed - false both when no such mission exists and when it belongs to someone else.
    /// </summary>
    public bool Delete(TenantId tenant, Guid missionId)
    {
        RequireValid(tenant);
        FileLog.Write($"[MissionStore] Delete: tenant={tenant.ToLogString()} mission={missionId}");
        lock (_lock)
        {
            var missions = LoadAll();
            var removed = missions.RemoveAll(m => m.MissionId == missionId && OwnedBy(m, tenant)) > 0;
            if (removed)
                SaveAll(missions);
            return removed;
        }
    }

    /// <summary>
    /// The one ownership test every accessor above shares, so scoping cannot differ between them. A row
    /// with an owner belongs to that owner; a row with none belongs to the adoption tenant, and to nobody
    /// when there is none.
    /// </summary>
    private bool OwnedBy(Mission mission, TenantId tenant)
    {
        var owner = string.IsNullOrWhiteSpace(mission.TenantId)
            ? _adoptUnattributedAs?.Value
            : mission.TenantId;
        return owner is not null && string.Equals(owner, tenant.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refuse a <c>default(TenantId)</c>. Every accessor is scoped by comparison against the caller's
    /// tenant, so an invalid tenant must fail loudly here rather than compare its null Value against a
    /// row and quietly decide something.
    /// </summary>
    private static void RequireValid(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A MissionStore call needs a valid tenant.", nameof(tenant));
    }

    /// <summary>Read the full set from disk. An absent file is an empty set; a corrupt file fails loudly.</summary>
    private List<Mission> LoadAll()
    {
        if (!File.Exists(FilePath))
            return new List<Mission>();

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<Mission>>(json, JsonOptions) ?? new List<Mission>();
    }

    /// <summary>Write the full set to disk, creating the directory when missing.</summary>
    private void SaveAll(List<Mission> missions)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine directory from path: {FilePath}");

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            FileLog.Write($"[MissionStore] SaveAll: created directory {dir}");
        }

        var serialized = JsonSerializer.Serialize(missions, JsonOptions);
        File.WriteAllText(FilePath, serialized);
        FileLog.Write($"[MissionStore] SaveAll: saved {missions.Count} mission(s)");
    }
}
