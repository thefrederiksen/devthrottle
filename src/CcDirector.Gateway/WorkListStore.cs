using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// The fleet's named work-list store (issue #273, child of #270; persistent since #301). A named
/// work list is an ordered list of structured item refs (<see cref="WorkListItemRef"/>) plus a
/// single-consumer claim. This object is what the product skill writes to, the Cockpit views, and
/// the queue runner drains - so it exists before any of those.
///
/// The store keeps order + the structured refs + the single-consumer assignment ONLY. It never
/// stores item status (consumers read status from the source themselves) and it never rejects a
/// source (runnability is the queue runner's concern).
///
/// PERSISTENCE (issue #301, keyvault.json precedent): the whole store lives in ONE plain JSON
/// file at the path the constructor receives (production: worklists.json in the Gateway data
/// dir). Every mutation writes through immediately with an atomic temp-file + rename, so a crash
/// mid-write can never half-truncate the store. On construction the file is loaded back:
///   - missing file  = empty store + a log line (the normal first boot), never an error;
///   - corrupt file  = the bytes are QUARANTINED to "&lt;path&gt;.corrupt-&lt;stamp&gt;" (preserved
///     for the operator, never silently overwritten), an explicit error is logged, and the store
///     starts empty so the Gateway still boots;
///   - a persisted consumer claim is BY DEFINITION stale after a restart (the claiming runner
///     died with the Gateway), so it is released on load with a log line naming the list and the
///     dead consumer token, and the released state is persisted immediately.
/// </summary>
public sealed class WorkListStore
{
    private readonly object _gate = new();
    private readonly string _path;

    // Name -> list. Case-insensitive names so "Backlog" and "backlog" address the same list.
    private readonly Dictionary<string, WorkListDto> _lists =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">
    /// The JSON file the store persists to. REQUIRED so no caller can silently land on the real
    /// user's file: production (<see cref="GatewayHost"/>) passes worklists.json in the Gateway
    /// data dir; tests pass an isolated temp path.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public WorkListStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>The outcome of a single-consumer claim attempt.</summary>
    public enum ClaimResult
    {
        /// <summary>The claim was granted; the token is the active consumer.</summary>
        Granted,

        /// <summary>The list is already claimed by another consumer; the claim is refused.</summary>
        AlreadyClaimed,

        /// <summary>No list with that name exists.</summary>
        NoSuchList,
    }

    /// <summary>
    /// Create a named list. Returns false if a list with that name already exists (the caller maps
    /// that to a conflict); the existing list is left untouched.
    /// </summary>
    /// <exception cref="ArgumentException">The name is null/empty/whitespace.</exception>
    public bool Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("list name is required", nameof(name));

        lock (_gate)
        {
            if (_lists.ContainsKey(name))
            {
                FileLog.Write($"[WorkListStore] Create: name={name} already exists");
                return false;
            }

            _lists[name] = new WorkListDto { Name = name };
            Save();
            FileLog.Write($"[WorkListStore] Create: name={name}");
            return true;
        }
    }

    /// <summary>All named lists, name-sorted. Each is a defensive copy (callers never mutate the store).</summary>
    public IReadOnlyList<WorkListDto> ListAll()
    {
        lock (_gate)
            return _lists.Values
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Copy)
                .ToList();
    }

    /// <summary>One list by name as a defensive copy, or null if absent.</summary>
    public WorkListDto? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
            return _lists.TryGetValue(name, out var list) ? Copy(list) : null;
    }

    /// <summary>
    /// Append one structured item ref to the end of the named list. Returns false if no list with
    /// that name exists. The source is NOT validated against any enum here - any source is stored
    /// (mixed sources coexist in one ordered list).
    /// </summary>
    /// <exception cref="ArgumentNullException">The ref is null.</exception>
    /// <exception cref="ArgumentException">The ref's source or id is null/empty/whitespace.</exception>
    public bool AppendItem(string name, WorkListItemRef item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.Source))
            throw new ArgumentException("item source is required", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("item id is required", nameof(item));

        lock (_gate)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                FileLog.Write($"[WorkListStore] AppendItem: no such list name={name}");
                return false;
            }

            list.Items.Add(new WorkListItemRef { Source = item.Source, Id = item.Id, Area = item.Area });
            Save();
            FileLog.Write($"[WorkListStore] AppendItem: name={name}, source={item.Source}, id={item.Id}, count={list.Items.Count}");
            return true;
        }
    }

    /// <summary>
    /// Replace the named list's items with the supplied ordered array (reorder). Returns false if
    /// no list with that name exists. The new array is taken verbatim as the new order.
    /// </summary>
    /// <exception cref="ArgumentNullException">The items array is null.</exception>
    /// <exception cref="ArgumentException">Any ref has a null/empty source or id.</exception>
    public bool Reorder(string name, IReadOnlyList<WorkListItemRef> items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));
        foreach (var item in items)
        {
            if (item is null)
                throw new ArgumentException("an item ref in the array is null", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Source))
                throw new ArgumentException("an item ref is missing its source", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Id))
                throw new ArgumentException("an item ref is missing its id", nameof(items));
        }

        lock (_gate)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                FileLog.Write($"[WorkListStore] Reorder: no such list name={name}");
                return false;
            }

            list.Items = items
                .Select(i => new WorkListItemRef { Source = i.Source, Id = i.Id, Area = i.Area })
                .ToList();
            Save();
            FileLog.Write($"[WorkListStore] Reorder: name={name}, count={list.Items.Count}");
            return true;
        }
    }

    /// <summary>
    /// Remove the one item addressed by source + id (case-insensitive on source, exact on id) from
    /// the named list, preserving the relative order of the rest. Returns true if an item was
    /// removed, false if no list with that name exists or no item matched.
    /// </summary>
    public bool RemoveItem(string name, string source, string id)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                FileLog.Write($"[WorkListStore] RemoveItem: no such list name={name}");
                return false;
            }

            var removed = list.Items.RemoveAll(i =>
                string.Equals(i.Source, source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Id, id, StringComparison.Ordinal));
            if (removed > 0)
                Save();
            FileLog.Write($"[WorkListStore] RemoveItem: name={name}, source={source}, id={id}, removed={removed}");
            return removed > 0;
        }
    }

    /// <summary>
    /// Claim the single draining consumer for the named list. Granted only when the list is
    /// currently unclaimed; a claim on an already-claimed list is refused. The supplied token
    /// becomes the active consumer on success.
    /// </summary>
    /// <exception cref="ArgumentException">The token is null/empty/whitespace.</exception>
    public ClaimResult Claim(string name, string consumerToken)
    {
        if (string.IsNullOrWhiteSpace(consumerToken))
            throw new ArgumentException("consumer token is required", nameof(consumerToken));

        lock (_gate)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                FileLog.Write($"[WorkListStore] Claim: no such list name={name}");
                return ClaimResult.NoSuchList;
            }

            if (!string.IsNullOrEmpty(list.Consumer))
            {
                FileLog.Write($"[WorkListStore] Claim: name={name} already claimed");
                return ClaimResult.AlreadyClaimed;
            }

            list.Consumer = consumerToken;
            Save();
            FileLog.Write($"[WorkListStore] Claim: name={name} granted");
            return ClaimResult.Granted;
        }
    }

    /// <summary>
    /// Release the consumer claim on the named list, after which a new claim succeeds. Returns
    /// false if no list with that name exists; releasing an already-unclaimed list is a no-op
    /// that returns true (the post-condition - no consumer - holds).
    /// </summary>
    public bool Release(string name)
    {
        lock (_gate)
        {
            if (!_lists.TryGetValue(name, out var list))
            {
                FileLog.Write($"[WorkListStore] Release: no such list name={name}");
                return false;
            }

            if (list.Consumer is not null)
            {
                list.Consumer = null;
                Save();
            }
            FileLog.Write($"[WorkListStore] Release: name={name}");
            return true;
        }
    }

    private static WorkListDto Copy(WorkListDto list) => new()
    {
        Name = list.Name,
        Consumer = list.Consumer,
        Items = list.Items
            .Select(i => new WorkListItemRef { Source = i.Source, Id = i.Id, Area = i.Area })
            .ToList(),
    };

    // ---- persistence (issue #301) ------------------------------------------------------------

    /// <summary>The on-disk shape: one document holding every named list.</summary>
    private sealed class StoreFile
    {
        public List<WorkListDto> Lists { get; set; } = new();
    }

    /// <summary>
    /// Load the store file written by a previous Gateway run. Called once from the constructor.
    /// Missing file = the normal first boot (empty store, logged). A corrupt file is quarantined
    /// (renamed next to the original with a timestamp suffix) so its bytes are preserved for the
    /// operator and never silently overwritten by the next write-through; the store then starts
    /// empty so the Gateway still boots. Stale consumer claims are released here - a persisted
    /// claim's runner died with the Gateway - and the released state is persisted immediately.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[WorkListStore] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            // "null" is valid JSON, so deserialization succeeds but yields no document - the
            // file carries no usable store. Same recovery as a parse failure: preserve + start empty.
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        var staleClaims = 0;
        foreach (var list in parsed.Lists)
        {
            if (string.IsNullOrWhiteSpace(list.Name))
            {
                Quarantine("a persisted list has an empty name");
                _lists.Clear();
                return;
            }

            // A persisted claim is by definition stale after a restart: the claiming runner died
            // with the Gateway (or belongs to a session that may no longer exist). Release it so a
            // new runner can re-claim and continue from the persisted order (issue #301 policy).
            if (!string.IsNullOrEmpty(list.Consumer))
            {
                FileLog.Write($"[WorkListStore] Load: released stale claim on list={list.Name}, deadConsumer={list.Consumer} (claims do not survive a Gateway restart)");
                list.Consumer = null;
                staleClaims++;
            }

            _lists[list.Name] = list;
        }

        FileLog.Write($"[WorkListStore] Load: restored {_lists.Count} list(s) from {_path}, staleClaimsReleased={staleClaims}");

        // Persist the released-claim state immediately so a crash before the next mutation
        // does not resurrect a dead consumer on the following boot.
        if (staleClaims > 0)
            Save();
    }

    /// <summary>
    /// Preserve an unreadable store file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly.
    /// The original path is then free for the next write-through; the operator can inspect or
    /// hand-restore the quarantined bytes. The move is not allowed to fail silently: if even the
    /// quarantine fails, the exception propagates and the Gateway does not start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[WorkListStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty. Operator action: inspect the quarantined file to recover lists.");
    }

    /// <summary>
    /// Write-through: serialize the whole store and atomically replace the file (temp + rename),
    /// so a concurrent reader or a crash mid-write never sees a half-written store. Called inside
    /// the lock by every mutation. A failed save is a LOGGED error that propagates (the caller's
    /// request fails loudly) - never a silent skip.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile
            {
                Lists = _lists.Values
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorkListStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
