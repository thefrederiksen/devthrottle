using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Per-session queue of prompts the user wants to send later.
/// Items can be reordered, removed, or sent in any order.
/// </summary>
public sealed class PromptQueue
{
    private readonly List<PromptQueueItem> _items = new();

    /// <summary>Fires on any mutation (add, remove, reorder, clear, load).</summary>
    public event Action? OnQueueChanged;

    /// <summary>Read-only view of queued items in order.</summary>
    public IReadOnlyList<PromptQueueItem> Items => _items.AsReadOnly();

    public int Count => _items.Count;

    public bool HasItems => _items.Count > 0;

    /// <summary>Add a new prompt to the end of the queue.</summary>
    public PromptQueueItem Enqueue(string text)
    {
        FileLog.Write($"[PromptQueue] Enqueue: text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\"");
        var item = new PromptQueueItem { Text = text };
        _items.Add(item);
        OnQueueChanged?.Invoke();
        return item;
    }

    /// <summary>Remove a specific item by ID. No-op if not found.</summary>
    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;

        FileLog.Write($"[PromptQueue] Remove: id={id}");
        _items.RemoveAt(index);
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Move an item one position up (toward index 0). No-op if already first or not found.</summary>
    public bool MoveUp(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index <= 0) return false;

        FileLog.Write($"[PromptQueue] MoveUp: id={id}, from={index} to={index - 1}");
        (_items[index], _items[index - 1]) = (_items[index - 1], _items[index]);
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Move an item one position down (toward end). No-op if already last or not found.</summary>
    public bool MoveDown(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || index >= _items.Count - 1) return false;

        FileLog.Write($"[PromptQueue] MoveDown: id={id}, from={index} to={index + 1}");
        (_items[index], _items[index + 1]) = (_items[index + 1], _items[index]);
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Replace the text of an existing item. No-op if not found or unchanged.</summary>
    public bool UpdateText(Guid id, string text)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        if (item.Text == text) return false;

        FileLog.Write($"[PromptQueue] UpdateText: id={id}, length={text.Length}");
        item.Text = text;
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Remove all items from the queue.</summary>
    public void Clear()
    {
        if (_items.Count == 0) return;
        FileLog.Write($"[PromptQueue] Clear: removing {_items.Count} item(s)");
        _items.Clear();
        OnQueueChanged?.Invoke();
    }

    /// <summary>Replace queue contents from persisted data. Used during session restore.</summary>
    public void LoadFrom(IEnumerable<PromptQueueItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        FileLog.Write($"[PromptQueue] LoadFrom: loaded {_items.Count} item(s)");
        OnQueueChanged?.Invoke();
    }

    /// <summary>Find an item by ID.</summary>
    public PromptQueueItem? FindById(Guid id) => _items.FirstOrDefault(i => i.Id == id);
}
