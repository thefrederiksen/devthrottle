namespace CcDirector.ControlApi;

/// <summary>
/// Holds dictation transcripts that were recovered after a client dropped its
/// <c>/dictate</c> WebSocket without a clean <c>stop</c> (mobile backgrounding,
/// tab blur, network blip). The audio was transcribed server-side but there was
/// no live socket to return it on, so it is parked here for the browser to pick
/// up when it reconnects - turning "your words were preserved in a log" into
/// "your words are waiting for you in the prompt".
///
/// In-memory and process-local on purpose: recovery is a "I just dropped, let me
/// reopen the page" flow against the same running Director, single-user tailnet.
/// Entries expire after <see cref="Freshness"/> so a stale recovery never
/// surprises the user hours later, and the list is capped so a burst of drops
/// cannot grow unbounded.
/// </summary>
internal static class RecoveredDictationStore
{
    private const int MaxEntries = 5;
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(10);
    private static readonly object _gate = new();
    private static readonly List<RecoveredDictation> _entries = new();

    /// <summary>Park a recovered transcript. No-op for empty text.</summary>
    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            _entries.Add(new RecoveredDictation(
                Guid.NewGuid().ToString("N"), text.Trim(), DateTime.UtcNow));
            PruneLocked();
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    /// <summary>Fresh (non-expired) recovered transcripts, oldest first.</summary>
    public static IReadOnlyList<RecoveredDictation> GetFresh()
    {
        lock (_gate)
        {
            PruneLocked();
            return _entries.ToList();
        }
    }

    /// <summary>Remove one entry by id. Returns true if it was present.</summary>
    public static bool Remove(string id)
    {
        lock (_gate)
        {
            PruneLocked();
            var i = _entries.FindIndex(e => e.Id == id);
            if (i < 0) return false;
            _entries.RemoveAt(i);
            return true;
        }
    }

    private static void PruneLocked()
    {
        var cutoff = DateTime.UtcNow - Freshness;
        _entries.RemoveAll(e => e.CreatedUtc < cutoff);
    }
}

/// <summary>One recovered dictation awaiting pickup by the browser.</summary>
internal sealed record RecoveredDictation(string Id, string Text, DateTime CreatedUtc);
