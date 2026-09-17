namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Pure helpers for fleet session-to-session messaging (issue #705). Kept free of I/O so the
/// framing format is unit-testable in isolation. The SERVER stamps the sender header from the
/// sender's own session record; the calling agent never supplies its own display identity.
///
/// It lives HERE, in the shared contracts, since the Remove-the-network-port mission's phase 2. It
/// used to be internal to the Director's Control API, because the command line reached the fleet
/// through its own Director and the Director framed every message on the way past. The tools now
/// call the Gateway directly, so the Gateway has to frame - and a second copy of this format on the
/// Gateway side would be two definitions of what a fleet message LOOKS LIKE, drifting apart by
/// default. One definition, two callers, no drift.
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

    /// <summary>
    /// Wrap a message with a sender header so the recipient knows who sent it and how to reply.
    /// The sender name and machine are resolved by the server (not the caller); a sender whose
    /// id or name is unknown is framed generically and without a reply line.
    /// </summary>
    /// <param name="fromSessionId">The sender's GUID, or null/empty when unknown.</param>
    /// <param name="fromName">The sender's display name resolved by the server, or null when unknown.</param>
    /// <param name="fromMachine">The sender's machine name.</param>
    /// <param name="text">The message body.</param>
    /// <param name="includeReplyHint">True for a one-way message: tell the recipient how to
    /// reply with cc-devthrottle. FALSE for message ask: the asker is already waiting and reads the answer from
    /// the target's output, so the recipient must answer DIRECTLY - a reply-command hint makes
    /// it try to send a separate reply instead of answering, which the ask flow then misses.</param>
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
            ? $"  (to reply: cc-devthrottle message send {shortId} \"<your reply>\")"
            : "";

        return $"Message {header} {oneLine}{reply}";
    }

    /// <summary>The header this frame opens with. Everything before the sender is fixed, so it is written once.
    /// </summary>
    private const string FramePrefix = "Message [message from ";

    /// <summary>The reply hint's opening, used to find and remove it again.</summary>
    private const string ReplyHintOpening = "  (to reply: cc-devthrottle message send ";

    /// <summary>The reply hint's ending, which pins the tail so a body cannot be mistaken for one.</summary>
    private const string ReplyHintEnding = " \"<your reply>\")";

    /// <summary>
    /// Read a message back out of OUR OWN frame: is this text one session talking to another, who sent it, and
    /// what did they actually say?
    ///
    /// THE POINT IS THAT IT IS OUR OWN FRAME, and that is what makes this a fact rather than a guess. The Wingman
    /// tab's Now view says what a working session was last asked and by whom, and the only honest way to name the
    /// asker is to recognise a string this very file wrote. Anything looser - a message that merely looks like a
    /// notification, a heuristic on the first few words - would put a name in front of the owner that nobody
    /// verified. A message that does not parse is simply not claimed to be from anyone.
    ///
    /// <see cref="BuildFramedMessage"/> writes three header forms and optionally a reply hint; all four
    /// combinations are read here, and a round-trip test pins them to the builder rather than to a copy of its
    /// format.
    /// </summary>
    /// <param name="message">The text as the recipient received it.</param>
    /// <param name="frame">The sender's display name (null when the frame carried none, which two of the three
    /// header forms do) and the message with the frame removed.</param>
    /// <returns>True when this is a framed fleet message.</returns>
    public static bool TryParseFrame(string? message, out FleetMessageFrame frame)
    {
        frame = default;
        if (string.IsNullOrEmpty(message) || !message.StartsWith(FramePrefix, StringComparison.Ordinal))
            return false;

        var headerEnd = message.IndexOf(']', FramePrefix.Length);
        if (headerEnd < 0) return false;

        // The header's own contents, between "[message from " and "]".
        var inside = message.Substring(FramePrefix.Length, headerEnd - FramePrefix.Length);
        var body = message.Substring(headerEnd + 1).TrimStart();

        // The reply hint is removed only when the WHOLE tail is one - the opening, an identifier, and the exact
        // ending. A body that merely contains the words is left alone.
        var hint = body.LastIndexOf(ReplyHintOpening, StringComparison.Ordinal);
        if (hint >= 0 && body.EndsWith(ReplyHintEnding, StringComparison.Ordinal))
        {
            var idStart = hint + ReplyHintOpening.Length;
            var idLength = body.Length - ReplyHintEnding.Length - idStart;
            // An identifier with no space in it, which is what ShortId produces.
            if (idLength > 0 && body.IndexOf(' ', idStart, idLength) < 0)
                body = body.Substring(0, hint);
        }

        frame = new FleetMessageFrame(SenderNameIn(inside), body);
        return true;
    }

    /// <summary>
    /// The sender's display name out of the header's contents, or null when the header carries none.
    ///
    /// Only the "{name} ({machine}), id {shortId}" form names a sender. "session {shortId} ({machine})" names an
    /// identifier, and "another session" names nothing at all - in both of those the answer is null, because a
    /// short identifier is not a name and putting one where a name goes is exactly the guess this avoids.
    /// </summary>
    private static string? SenderNameIn(string inside)
    {
        if (inside.StartsWith("session ", StringComparison.Ordinal)) return null;
        if (string.Equals(inside, "another session", StringComparison.Ordinal)) return null;

        // The name runs up to " (" - the machine's opening bracket. A name is free text, so the LAST one is taken:
        // the machine and the identifier that follow it contain no further " (".
        var machine = inside.LastIndexOf(" (", StringComparison.Ordinal);
        if (machine <= 0) return null;

        var name = inside.Substring(0, machine).Trim();
        return name.Length == 0 ? null : name;
    }
}

/// <summary>One fleet message read back out of its own frame - see <see cref="FleetMessaging.TryParseFrame"/>.
/// </summary>
/// <param name="SenderName">The sender's display name, or null when the frame carried none.</param>
/// <param name="Text">The message with the frame and the reply hint removed.</param>
public readonly record struct FleetMessageFrame(string? SenderName, string Text);
