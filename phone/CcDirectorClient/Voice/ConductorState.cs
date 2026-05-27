namespace CcDirectorClient.Voice;

/// <summary>
/// Turn-taking state for the all-sessions conductor. Holds the ordered queue of
/// sessions that need the user and a cursor at the one currently being handled.
/// Pure logic, no MAUI/Android dependency, so it is unit tested off-device.
///
/// Locked behavior: nothing auto-advances. The UI reads the <see cref="Current"/>
/// session aloud, then waits; the user either replies to that session or presses
/// Next, which calls <see cref="Advance"/>. <see cref="Update"/> refreshes the
/// queue from a freshly polled roster while keeping the cursor on the same
/// session when it still needs attention, so a background poll never yanks the
/// user off the session they are mid-conversation with.
/// </summary>
public sealed class ConductorState
{
    private readonly List<SessionInfo> _queue = new();
    private int _index;

    // When true (the FIFO voice mode), sessions the user has put on hold are dropped
    // from the queue. The all-sessions conductor leaves this false so its rotation is
    // unchanged. Set once at construction; the queue is rebuilt with it on every Update.
    private readonly bool _excludeHeld;

    /// <summary>
    /// Create a conductor queue. Pass <paramref name="excludeHeld"/> true for FIFO voice
    /// mode so parked (on-hold) sessions are skipped; the default false preserves the
    /// original all-sessions conductor behavior.
    /// </summary>
    public ConductorState(bool excludeHeld = false) => _excludeHeld = excludeHeld;

    /// <summary>Number of sessions currently needing the user.</summary>
    public int Count => _queue.Count;

    /// <summary>True when at least one session needs the user.</summary>
    public bool HasWork => _queue.Count > 0;

    /// <summary>The session currently being handled, or null when nothing needs the user.</summary>
    public SessionInfo? Current
        => _queue.Count == 0 ? null : _queue[Math.Clamp(_index, 0, _queue.Count - 1)];

    /// <summary>A read-only snapshot of the queue, in rotation order.</summary>
    public IReadOnlyList<SessionInfo> Queue => _queue;

    /// <summary>
    /// Rebuild the queue from a freshly polled roster (the caller passes the full
    /// roster; this applies the needs-you filter). The cursor stays on the
    /// session it was on when that session still needs attention; otherwise it is
    /// clamped into range so <see cref="Current"/> points at a real entry.
    /// Returns the session now under the cursor (may differ if the prior one was
    /// resolved), or null when nothing needs the user.
    /// </summary>
    public SessionInfo? Update(IEnumerable<SessionInfo> roster)
    {
        var priorId = Current?.SessionId;

        _queue.Clear();
        _queue.AddRange(SessionFilter.AttentionQueue(roster, _excludeHeld));

        if (_queue.Count == 0)
        {
            _index = 0;
        }
        else if (priorId is not null)
        {
            var keep = _queue.FindIndex(s => string.Equals(s.SessionId, priorId, StringComparison.OrdinalIgnoreCase));
            _index = keep >= 0 ? keep : Math.Clamp(_index, 0, _queue.Count - 1);
        }
        else
        {
            _index = 0;
        }

        ClientLog.Write($"[ConductorState] Update: queue={_queue.Count}, current={Current?.DisplayName ?? "(none)"}");
        return Current;
    }

    /// <summary>
    /// Move the cursor to the next session in the queue (round-robin wrap), and
    /// return it. No-op returning null when the queue is empty. Called only on an
    /// explicit Next - the conductor never advances on its own.
    /// </summary>
    public SessionInfo? Advance()
    {
        if (_queue.Count == 0) { _index = 0; return null; }
        _index = (_index + 1) % _queue.Count;
        ClientLog.Write($"[ConductorState] Advance: current={Current?.DisplayName}");
        return Current;
    }
}
