using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Events;

/// <summary>
/// The minimal Phase-1 observable sink for the doorbell event vocabulary (issue #330):
/// a per-director capped ring of received events (newest kept, oldest dropped) plus one
/// structured log line per event. This is deliberately NOT the Phase-3 event hub - no
/// subscriptions, no push, no persistence; just enough that "the Gateway received
/// session-created/session-exited/prompt-detected" is provable from the outside
/// (GET /directors/{id}/events) and from the Gateway log.
/// </summary>
public sealed class DirectorEventLog
{
    /// <summary>Ring capacity per director - enough to debug a busy Director without growing unbounded.</summary>
    public const int MaxEventsPerDirector = 200;

    private readonly ConcurrentDictionary<string, Queue<DirectorEventDto>> _rings = new();

    /// <summary>Record one received event and write the structured log line.</summary>
    public void Record(string directorId, string sessionId, string eventName, string state)
    {
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrEmpty(eventName))
            throw new ArgumentException("eventName is required", nameof(eventName));

        var dto = new DirectorEventDto
        {
            ReceivedAt = DateTime.UtcNow,
            SessionId = sessionId,
            Event = eventName,
            State = state,
        };

        var ring = _rings.GetOrAdd(directorId, _ => new Queue<DirectorEventDto>());
        lock (ring)
        {
            ring.Enqueue(dto);
            while (ring.Count > MaxEventsPerDirector)
                ring.Dequeue();
        }

        // The structured line the issue's acceptance criteria can be proven against.
        FileLog.Write($"[DirectorEvents] director={directorId} session={sessionId} event={eventName} state={state}");
    }

    /// <summary>Snapshot of a director's recorded events, oldest first. Empty when none.</summary>
    public IReadOnlyList<DirectorEventDto> For(string directorId)
    {
        if (string.IsNullOrEmpty(directorId) || !_rings.TryGetValue(directorId, out var ring))
            return Array.Empty<DirectorEventDto>();
        lock (ring)
        {
            return ring.ToList();
        }
    }
}
