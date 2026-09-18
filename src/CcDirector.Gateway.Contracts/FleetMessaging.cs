namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Pure helpers for fleet session-to-session messaging (issue #705). Kept free of I/O so they are
/// unit-testable in isolation.
///
/// Since the Message Load mission (16 September 2026) a fleet message is a queued record in the
/// receiver's inbox on the Gateway, read in full with <c>cc-devthrottle message inbox</c> - nothing
/// frames a message and types it into a session any more. The framing helper that did
/// (<c>BuildFramedMessage</c>, with its one-line collapse and its reply hint) had no caller left after
/// that change and was deleted on 17 September 2026, with its tests. What remains is the short handle
/// the Gateway's messaging log lines use.
/// </summary>
public static class FleetMessaging
{
    /// <summary>
    /// The short, human-friendly handle for a session: the first 8 characters of its GUID,
    /// matching the existing session-view convention. Empty input yields an empty handle.
    /// </summary>
    public static string ShortId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return "";
        return sessionId.Length <= 8 ? sessionId : sessionId.Substring(0, 8);
    }
}
