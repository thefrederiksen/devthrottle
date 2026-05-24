using System.Collections.Concurrent;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Global per-session floor between ANY two Wingman LLM calls. The terminal-state
/// detector and the turn summariser both fire on the same quiet edge; without a
/// shared gate a flappy session (bursty startup output, a redraw storm) could trigger
/// LLM call after LLM call and rack up tokens. This guarantees the model is asked at
/// most once per <see cref="MinGap"/> per session - cheap byte-activity state changes
/// stay free and can happen as often as they like; only the expensive calls are gated.
/// </summary>
public static class WingmanLlmThrottle
{
    /// <summary>Minimum time between LLM calls for one session.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<Guid, long> _lastTicks = new();

    /// <summary>
    /// Returns true and records "now" if at least <see cref="MinGap"/> has elapsed since
    /// the last acquired call for this session; false otherwise (the caller must NOT make
    /// the LLM call). Thread-safe under concurrent quiet-edge handlers.
    /// </summary>
    public static bool TryAcquire(Guid sessionId)
    {
        var now = DateTime.UtcNow.Ticks;
        while (true)
        {
            var last = _lastTicks.GetOrAdd(sessionId, 0L);
            if (now - last < MinGap.Ticks)
                return false;
            if (_lastTicks.TryUpdate(sessionId, now, last))
                return true;
            // Lost the race with another handler; re-read and re-check.
        }
    }

    /// <summary>Forget a session (called when it is removed) so the map doesn't grow forever.</summary>
    public static void Forget(Guid sessionId) => _lastTicks.TryRemove(sessionId, out _);
}
