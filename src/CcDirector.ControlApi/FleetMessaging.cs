namespace CcDirector.ControlApi;

/// <summary>
/// Pure helpers for fleet session-to-session messaging (issue #705). Kept free of I/O so the
/// framing format is unit-testable in isolation. The Director stamps the sender header from a
/// session's own record; the calling agent never supplies its own display identity.
/// </summary>
internal static class FleetMessaging
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

    /// <summary>
    /// Wrap a message with a sender header so the recipient knows who sent it and how to reply.
    /// The sender name and machine are resolved by the Director (not the caller); a sender whose
    /// id or name is unknown is framed generically and without a reply line.
    /// </summary>
    /// <param name="fromSessionId">The sender's GUID, or null/empty when unknown.</param>
    /// <param name="fromName">The sender's display name resolved by the Director, or null when unknown.</param>
    /// <param name="fromMachine">The sender's machine name.</param>
    /// <param name="text">The message body.</param>
    public static string BuildFramedMessage(string? fromSessionId, string? fromName, string fromMachine, string text)
    {
        var shortId = ShortId(fromSessionId);

        string header;
        if (!string.IsNullOrWhiteSpace(fromName))
            header = $"[message from {fromName} ({fromMachine}), id {shortId}]";
        else if (!string.IsNullOrWhiteSpace(shortId))
            header = $"[message from session {shortId} ({fromMachine})]";
        else
            header = "[message from another session]";

        var reply = !string.IsNullOrWhiteSpace(shortId)
            ? $"\n\n(to reply: cc-send {shortId} \"<your reply>\")"
            : "";

        return $"{header}\n{text}{reply}";
    }
}
