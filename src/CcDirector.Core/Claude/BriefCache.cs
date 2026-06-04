using System.Collections.Concurrent;

namespace CcDirector.Core.Claude;

/// <summary>
/// Process-wide in-memory cache of brief condensations, keyed by the Director's session
/// GUID and staleness-checked by transcript turn count (the <see cref="RecapCache"/>
/// pattern). The Brief renders on every session flip, so cache hits must be the norm:
/// condense once per completed turn, serve from memory after.
/// </summary>
public static class BriefCache
{
    public sealed class Entry
    {
        public required int AtTurnCount { get; init; }
        public required List<string> DidBullets { get; init; }
        public required string? NeedsYouVerbatim { get; init; }
        public required string Condenser { get; init; }
        public required DateTime GeneratedAt { get; init; }
    }

    private static readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>The cached condensation, or null when absent OR stale (turn count moved on).</summary>
    public static Entry? TryGetCurrent(Guid sessionId, int turnCount)
        => _entries.TryGetValue(sessionId, out var e) && e.AtTurnCount == turnCount ? e : null;

    public static void Set(Guid sessionId, Entry entry)
        => _entries[sessionId] = entry;

    public static bool Remove(Guid sessionId)
        => _entries.TryRemove(sessionId, out _);
}
