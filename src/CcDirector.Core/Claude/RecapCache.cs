using System.Collections.Concurrent;

namespace CcDirector.Core.Claude;

/// <summary>
/// Process-wide in-memory cache of session recaps. Keyed by the Director's
/// session GUID. Survives until the Director process exits. Recaps are cheap
/// to regenerate (haiku side-call) so we do not persist them to disk.
/// </summary>
public static class RecapCache
{
    public sealed class Entry
    {
        public required string Recap { get; init; }
        public required DateTime GeneratedAt { get; init; }
        public required int AtTurnCount { get; init; }
        public required string Model { get; init; }
        public required long ElapsedMs { get; init; }
    }

    private static readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public static Entry? TryGet(Guid sessionId)
        => _entries.TryGetValue(sessionId, out var e) ? e : null;

    public static void Set(Guid sessionId, Entry entry)
        => _entries[sessionId] = entry;

    public static bool Remove(Guid sessionId)
        => _entries.TryRemove(sessionId, out _);
}
