namespace CcDirector.Gateway.Util;

/// <summary>
/// A mutual-exclusion gate per KEY that can be waited for without holding a thread.
///
/// WHY (deploy outage, measured 9 November 2026). Three stores - triggers, skill placement and the activity ledger -
/// each serialised their writes with one process-wide <c>lock</c> and did synchronous database work inside it. When
/// every Director reconnected after a deploy and single database commands took 24 to 30 seconds, every waiting request
/// sat BLOCKED on that lock holding a thread-pool worker. The pool ran dry (209 queued work items, threads injected one
/// every two seconds), and the Gateway could not answer even its health check for 70 seconds.
///
/// The locks protect database rows (a read-decide-write of a trigger's claim, an insert-if-absent), so the writes they
/// cover must stay serialised - releasing the lock before the database work would change the answers. What changes is
/// HOW a caller waits, and WHAT it waits for:
///
///  - <see cref="EnterAsync"/> waits by awaiting: a waiter holds no thread, so a slow database command can no longer
///    starve the pool however many callers queue behind it.
///  - The gate is KEYED (per account, or per account and Director): writes that never touched the same rows - two
///    accounts - no longer queue behind each other at all.
///
/// <see cref="Enter"/> is the blocking twin for the store methods that are still synchronous; it serialises against
/// <see cref="EnterAsync"/> on the same key exactly as the old lock did, and costs a thread while it waits, as the old
/// lock did.
///
/// An entry exists only while someone holds or waits for its key, so the gate does not grow with the number of keys
/// ever seen.
/// </summary>
public sealed class KeyedAsyncGate
{
    private readonly object _entries = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    /// <summary>How many keys are held or waited for right now. For tests.</summary>
    internal int ActiveKeys
    {
        get { lock (_entries) return _byKey.Count; }
    }

    /// <summary>Wait for <paramref name="key"/> without holding a thread. Dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = Acquire(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseUse(key, entry);
            throw;
        }
        return new Holder(this, key, entry);
    }

    /// <summary>Wait for <paramref name="key"/>, blocking the calling thread. Dispose the result to release it.</summary>
    public IDisposable Enter(string key)
    {
        var entry = Acquire(key);
        entry.Semaphore.Wait();
        return new Holder(this, key, entry);
    }

    private Entry Acquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_entries)
        {
            if (!_byKey.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _byKey[key] = entry;
            }
            entry.Users++;
            return entry;
        }
    }

    private void ReleaseUse(string key, Entry entry)
    {
        lock (_entries)
        {
            entry.Users--;
            if (entry.Users == 0)
                _byKey.Remove(key);
        }
    }

    private sealed class Holder : IDisposable
    {
        private readonly KeyedAsyncGate _gate;
        private readonly string _key;
        private Entry? _entry;

        public Holder(KeyedAsyncGate gate, string key, Entry entry)
        {
            _gate = gate;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            entry.Semaphore.Release();
            _gate.ReleaseUse(_key, entry);
        }
    }
}
