using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Voice;

/// <summary>
/// One async voice turn tracked by the Gateway (issue #376). The submit endpoint creates a job,
/// a background task drives the Director's SSE voice-turn endpoint and mirrors each stage event
/// into the job, and the poll endpoint reads it - so a phone that loses signal mid-turn collects
/// the cached result on reconnect instead of restarting the turn.
///
/// Stage vocabulary (mirrors the Director's SSE events, see
/// docs/architecture/gateway/VOICE_TURN_ARCHITECTURE.md): submitted, transcribing, transcript,
/// waiting, thinking, summarizing, reply (terminal), error (terminal).
///
/// Thread safety: the background task writes and any number of poll requests read concurrently,
/// so every mutation and the snapshot read are serialized on one private lock. Readers always
/// consume <see cref="Snapshot"/> - never the fields piecemeal - so a poll can never observe a
/// half-applied reply event.
/// </summary>
public sealed class TurnJob
{
    private readonly object _lock = new();
    private string _stage = "submitted";
    private string? _transcript;
    private string? _summary;
    private string? _audioBase64;
    private string? _errorMessage;

    /// <summary>UUID identifying this turn; the poll route key.</summary>
    public string TurnId { get; }

    /// <summary>The session this turn targets; polls for a different session 404.</summary>
    public string SessionId { get; }

    /// <summary>When the job was created (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>When the job expires (UTC); reads after this point behave as not-found.</summary>
    public DateTime ExpiresAt { get; private set; }

    public TurnJob(string turnId, string sessionId, DateTime createdAtUtc, TimeSpan ttl)
    {
        if (string.IsNullOrEmpty(turnId))
            throw new ArgumentException("turnId is required", nameof(turnId));
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        TurnId = turnId;
        SessionId = sessionId;
        CreatedAt = createdAtUtc;
        ExpiresAt = createdAtUtc + ttl;
    }

    /// <summary>Record an in-progress stage event (transcribing/waiting/thinking/summarizing).</summary>
    public void SetStage(string stage)
    {
        if (string.IsNullOrEmpty(stage)) return;
        lock (_lock) { _stage = stage; }
    }

    /// <summary>Record the transcript event: the audio was transcribed to <paramref name="transcript"/>.</summary>
    public void SetTranscript(string? transcript)
    {
        lock (_lock)
        {
            _stage = "transcript";
            _transcript = transcript ?? "";
        }
    }

    /// <summary>Record the terminal reply event. Fields are coerced to non-null so a completed
    /// poll always carries both (audioBase64 may be empty when no TTS key is configured).</summary>
    public void SetReply(string? summary, string? audioBase64)
    {
        lock (_lock)
        {
            _stage = "reply";
            _summary = summary ?? "";
            _audioBase64 = audioBase64 ?? "";
        }
    }

    /// <summary>Record the terminal error outcome (Director error event, unreachable Director,
    /// non-2xx answer, or a stream that ended without a reply).</summary>
    public void SetError(string message)
    {
        lock (_lock)
        {
            _stage = "error";
            _errorMessage = string.IsNullOrEmpty(message) ? "unknown error" : message;
        }
    }

    /// <summary>Consistent point-in-time read of the job for the poll endpoint.</summary>
    public TurnJobSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new TurnJobSnapshot(_stage, _transcript, _summary, _audioBase64, _errorMessage);
        }
    }

    /// <summary>
    /// TEST SEAM: rewrite the creation time (and the derived expiry) so a test can simulate a
    /// job created in the past without waiting out the real TTL.
    /// </summary>
    internal void OverrideCreatedAtForTest(DateTime createdAtUtc, TimeSpan ttl)
    {
        lock (_lock)
        {
            CreatedAt = createdAtUtc;
            ExpiresAt = createdAtUtc + ttl;
        }
    }
}

/// <summary>Point-in-time view of a <see cref="TurnJob"/> (one lock acquisition per poll).</summary>
public readonly record struct TurnJobSnapshot(
    string Stage, string? Transcript, string? Summary, string? AudioBase64, string? ErrorMessage);

/// <summary>
/// In-memory store of async voice-turn jobs keyed by UUID turn_id (issue #376). 10-minute TTL;
/// expiry is checked lazily on every read and swept opportunistically on create, so no timer
/// thread is needed. In-memory by design - a Gateway restart drops in-flight turns, and the
/// phone simply re-submits (the same contract as the rest of the Gateway's in-memory state).
/// </summary>
public sealed class GatewayTurnJobStore
{
    /// <summary>How long a completed (or in-flight) turn result stays pollable.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, TurnJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Create and register a new job for <paramref name="sessionId"/> (stage=submitted).</summary>
    public TurnJob Create(string sessionId)
    {
        SweepExpired();
        var job = new TurnJob(Guid.NewGuid().ToString(), sessionId, DateTime.UtcNow, Ttl);
        _jobs[job.TurnId] = job;
        FileLog.Write($"[GatewayTurnJobStore] Create: turnId={job.TurnId}, sid={sessionId}, expiresAt={job.ExpiresAt:O}");
        return job;
    }

    /// <summary>
    /// The job for <paramref name="turnId"/>, or null when unknown or expired. An expired job is
    /// removed on this read (lazy expiry) so the caller's 404 is also the cleanup.
    /// </summary>
    public TurnJob? Get(string turnId)
    {
        if (string.IsNullOrEmpty(turnId)) return null;
        if (!_jobs.TryGetValue(turnId, out var job)) return null;
        if (job.ExpiresAt <= DateTime.UtcNow)
        {
            _jobs.TryRemove(turnId, out _);
            FileLog.Write($"[GatewayTurnJobStore] Get: turnId={turnId} expired (created {job.CreatedAt:O}); removed");
            return null;
        }
        return job;
    }

    /// <summary>Drop every expired job. Called on each create so the dictionary stays bounded
    /// by the number of turns submitted within one TTL window.</summary>
    private void SweepExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.ExpiresAt <= now)
                _jobs.TryRemove(kvp.Key, out _);
        }
    }
}
