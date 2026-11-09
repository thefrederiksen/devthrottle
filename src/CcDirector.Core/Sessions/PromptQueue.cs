using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Per-session queue of prompts the user wants to send later.
/// Items can be reordered, removed, or sent in any order.
///
/// EVERY READ AND EVERY WRITE IS UNDER ONE LOCK, AND <see cref="Items"/> IS A SNAPSHOT (issue #4815).
///
/// This queue has two sets of callers that are not the same thread: the desktop - the queue panel, the
/// queue button, the expanded editor - and the tunnel command surface, whose eight <c>queue-*</c> verbs
/// arrive from the Gateway. Until 9 November 2026 the whole state was a bare <see cref="List{T}"/> with no
/// lock at all, and <c>Items</c> handed out <c>AsReadOnly()</c> over that live list: a caller enumerating it
/// while another thread added an item threw <see cref="InvalidOperationException"/>, and two concurrent
/// writes could lose an item, move the wrong one, or leave the list torn.
///
/// It had not bitten, for a reason that was never a design: every queue verb is fully synchronous, so the
/// Director's single SignalR command delivery loop serialised them completely - two of them could not run at
/// once however fast the Gateway sent them. The #4490 change took verbs off that loop, and the queue verbs
/// were kept on it naming this issue as the reason. That constraint was a lock standing in costume; the lock
/// is here now, so the queue is safe against the desktop racing the tunnel as well, which the loop never
/// covered.
///
/// WHY <see cref="OnQueueChanged"/> IS RAISED OUTSIDE THE LOCK. Its one subscriber is a view model, which
/// reaches the user-interface thread; a handler that blocked while this lock was held would hold it for the
/// length of a dispatch, and a handler that read the queue back from another thread would wait on the thread
/// it was raised from. The event is raised after the critical section, so a handler may do anything it likes,
/// including calling straight back in.
/// </summary>
public sealed class PromptQueue
{
    private readonly object _gate = new();
    private readonly List<PromptQueueItem> _items = new();

    /// <summary>Fires on any mutation (add, remove, reorder, clear, load). Raised outside the lock.</summary>
    public event Action? OnQueueChanged;

    /// <summary>
    /// The queued items in order, as a SNAPSHOT taken under the lock - not a view over the live list. A
    /// caller may hold it, enumerate it slowly, or hand it to a dialog; what it will never do is change
    /// under them or throw because another thread wrote to the queue (issue #4815).
    /// </summary>
    public IReadOnlyList<PromptQueueItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    public bool HasItems { get { lock (_gate) return _items.Count > 0; } }

    /// <summary>Add a new prompt to the end of the queue.</summary>
    public PromptQueueItem Enqueue(string text)
    {
        FileLog.Write($"[PromptQueue] Enqueue: text=\"{(text.Length > 60 ? text[..60] + "..." : text)}\"");
        var item = new PromptQueueItem { Text = text };
        lock (_gate) _items.Add(item);
        OnQueueChanged?.Invoke();
        return item;
    }

    /// <summary>
    /// Remove a specific item by ID. No-op if not found. The same claim <see cref="TryTake"/> makes, for a
    /// caller that does not want the text back - one find-and-remove serves both, and each still writes its
    /// own log line, because a remove and a take are different intentions to whoever reads the log.
    /// </summary>
    public bool Remove(Guid id)
    {
        if (!Took(id, out _)) return false;

        FileLog.Write($"[PromptQueue] Remove: id={id}");
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Move an item one position up (toward index 0). No-op if already first or not found.</summary>
    public bool MoveUp(Guid id)
    {
        int index;
        lock (_gate)
        {
            index = _items.FindIndex(i => i.Id == id);
            if (index <= 0) return false;
            (_items[index], _items[index - 1]) = (_items[index - 1], _items[index]);
        }

        FileLog.Write($"[PromptQueue] MoveUp: id={id}, from={index} to={index - 1}");
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Move an item one position down (toward end). No-op if already last or not found.</summary>
    public bool MoveDown(Guid id)
    {
        int index;
        lock (_gate)
        {
            index = _items.FindIndex(i => i.Id == id);
            if (index < 0 || index >= _items.Count - 1) return false;
            (_items[index], _items[index + 1]) = (_items[index + 1], _items[index]);
        }

        FileLog.Write($"[PromptQueue] MoveDown: id={id}, from={index} to={index + 1}");
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Replace the text of an existing item. No-op if not found or unchanged.</summary>
    public bool UpdateText(Guid id, string text)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return false;
            if (item.Text == text) return false;
            item.Text = text;
        }

        FileLog.Write($"[PromptQueue] UpdateText: id={id}, length={text.Length}");
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>Remove all items from the queue.</summary>
    public void Clear()
    {
        int removed;
        lock (_gate)
        {
            if (_items.Count == 0) return;
            removed = _items.Count;
            _items.Clear();
        }

        FileLog.Write($"[PromptQueue] Clear: removing {removed} item(s)");
        OnQueueChanged?.Invoke();
    }

    /// <summary>Replace queue contents from persisted data. Used during session restore.</summary>
    public void LoadFrom(IEnumerable<PromptQueueItem> items)
    {
        int loaded;
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(items);
            loaded = _items.Count;
        }

        FileLog.Write($"[PromptQueue] LoadFrom: loaded {loaded} item(s)");
        OnQueueChanged?.Invoke();
    }

    /// <summary>
    /// Take one item out of the queue and hand back the text it held, under ONE lock - so the removal IS the
    /// claim on it (found reviewing #4815).
    ///
    /// WHY THIS EXISTS AND FindById-THEN-Remove DOES NOT DO. <c>queue-send</c> used to find the item, read
    /// its text, call <see cref="Remove"/> and throw the answer away, then send. Two callers that are not
    /// the same thread - the queue panel on the desktop and the <c>queue-send</c> verb from the Gateway -
    /// could both find the same item and both act on it: the prompt typed into the agent twice, or sent once
    /// and left sitting in the composer as well. One of the two <see cref="Remove"/> calls answered false
    /// and nobody was listening. Only the caller this returns true to may act.
    /// </summary>
    public bool TryTake(Guid id, out string text)
    {
        if (!Took(id, out text)) return false;

        FileLog.Write($"[PromptQueue] TryTake: id={id}, length={text.Length}");
        OnQueueChanged?.Invoke();
        return true;
    }

    /// <summary>The claim itself, under the lock and saying nothing. The two callers above each raise the
    /// event and write their own line outside it.</summary>
    private bool Took(Guid id, out string text)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                text = "";
                return false;
            }
            text = _items[index].Text;
            _items.RemoveAt(index);
            return true;
        }
    }

    /// <summary>Find an item by ID.</summary>
    public PromptQueueItem? FindById(Guid id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }
}
