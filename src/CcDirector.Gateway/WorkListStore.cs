using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// The fleet's in-memory named work-list store (issue #273, child of #270). A named work list is
/// an ordered list of structured item refs (<see cref="WorkListItemRef"/>) plus a single-consumer
/// claim. This object is what the product skill writes to, the Cockpit views, and the queue runner
/// drains - so it exists before any of those.
///
/// The store keeps order + the structured refs + the single-consumer assignment ONLY. It never
/// stores item status (consumers read status from the source themselves) and it never rejects a
/// source (runnability is the queue runner's concern). Persistence across Gateway restart is OUT
/// for v1 (#270 roadmap), so this lives purely in process memory, guarded by a single lock.
/// </summary>
public sealed class WorkListStore
{
    private readonly object _gate = new();

    // Name -> list. Case-insensitive names so "Backlog" and "backlog" address the same list.
    private readonly Dictionary<string, WorkListDto> _lists =
        new(StringComparer.OrdinalIgnoreCase);

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

            list.Consumer = null;
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
}
