namespace CcDirectorClient.Voice;

/// <summary>
/// The poll loop hit its overall deadline (aligned with the Gateway job TTL) without the turn
/// reaching a terminal stage. Distinct from a transient drop (which is retried) - this means the
/// turn genuinely did not finish in time, so the caller surfaces it as a clean "please try again".
/// </summary>
public sealed class VoiceTurnTimeoutException : Exception
{
    public VoiceTurnTimeoutException(string message) : base(message) { }
}

/// <summary>
/// The Gateway returned 404 for a turn id we had already submitted: the cached job expired
/// (10-minute TTL) or is unknown. A clean terminal "please resend" - never an unhandled crash.
/// </summary>
public sealed class VoiceTurnExpiredException : Exception
{
    public VoiceTurnExpiredException(string message) : base(message) { }
}
