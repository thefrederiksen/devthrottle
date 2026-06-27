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
    /// <param name="includeReplyHint">True for cc-send (a one-way message: tell the recipient how to
    /// reply with cc-send). FALSE for cc-ask: the asker is already waiting and reads the answer from
    /// the target's output, so the recipient must answer DIRECTLY - a "reply with cc-send" hint makes
    /// it try to cc-send back instead of answering, which the ask flow then misses.</param>
    public static string BuildFramedMessage(string? fromSessionId, string? fromName, string fromMachine, string text, bool includeReplyHint = true)
    {
        var shortId = ShortId(fromSessionId);

        string header;
        if (!string.IsNullOrWhiteSpace(fromName))
            header = $"[message from {fromName} ({fromMachine}), id {shortId}]";
        else if (!string.IsNullOrWhiteSpace(shortId))
            header = $"[message from session {shortId} ({fromMachine})]";
        else
            header = "[message from another session]";

        // Single line. A fleet message is a short notification/question, and the delivery layer routes
        // ANY multi-line text through an @-temp-file reference (see LargeInputHandler's line-break rule).
        // Some agents (e.g. Pi) do not expand that reference in their composer, so they would see the
        // file path instead of the message. Collapsing the frame to one line keeps it under the
        // line-break and length thresholds, so it is typed INLINE and every agent receives the actual
        // text. Genuinely long messages (over the length threshold) still take the file path.
        var oneLine = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        var reply = includeReplyHint && !string.IsNullOrWhiteSpace(shortId)
            ? $"  (to reply: cc-send {shortId} \"<your reply>\")"
            : "";

        return $"{header} {oneLine}{reply}";
    }
}
