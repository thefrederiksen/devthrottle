using System.Net;

namespace CcDirectorClient.Voice;

/// <summary>
/// Raised by the voice-turn channel on a non-2xx Gateway response. Carries the HTTP
/// <see cref="StatusCode"/> so <see cref="VoiceTurnRunner"/> can classify the failure:
/// a 5xx is transient (re-poll after a backoff), a 404 means the cached turn expired
/// ("please resend"), and a 410 means the owning session has gone. A bare
/// <see cref="HttpRequestException"/> (no response at all - DNS/connect/reset) is treated
/// as transient because the carrier signal dropped, not the turn.
/// </summary>
public sealed class VoiceTurnHttpException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public VoiceTurnHttpException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>True for a 5xx server error - the Gateway is up enough to answer but
    /// momentarily faulted, so re-polling the same turn id is the right move.</summary>
    public bool IsServerError => (int)StatusCode >= 500 && (int)StatusCode <= 599;

    /// <summary>True for the 404 the Gateway returns once a cached turn has expired
    /// (10-minute TTL) or for an unknown turn id - a clean "please resend", not a crash.</summary>
    public bool IsExpired => StatusCode == HttpStatusCode.NotFound;

    /// <summary>True for the 410 the Gateway returns when the owning session has exited -
    /// terminal, stop immediately.</summary>
    public bool IsSessionGone => StatusCode == HttpStatusCode.Gone;
}
