namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of <c>POST /sessions/{sid}/message</c> - one session writing one message into another session's
/// inbox (Remove-the-network-port mission, phase 2; queued since the Message Load mission).
///
/// THERE IS NO SENDER FIELD, AND ITS ABSENCE IS THE POINT. The sender is the session whose key
/// authenticated the request, read from the authenticated identity and never from this body. The
/// Director's loopback predecessor took a <c>fromSessionId</c> from the body, which was safe only
/// because the only thing that could reach that port was a process on the same machine. This route
/// is reachable by anything holding a session key, so a caller-supplied sender would let one agent
/// send a message wearing another agent's name - and the recipient would have no way to tell.
/// </summary>
public sealed class FleetMessageRequest
{
    /// <summary>The message body, exactly as it should be read. It may span many lines: it is written to the
    /// recipient's inbox and never typed into a terminal.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// "message" (the default) or "report" - a worker telling its supervisor what it did. Any other value is
    /// refused. The kinds a broadcast or a system notice carry are decided by the Gateway, never here.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// RETIRED with <c>message ask</c> (the Message Load mission, ruling 10). It is still read so that an
    /// older command line asking to wait is REFUSED with a sentence rather than silently queued and answered
    /// with an empty "answer". No agent waits for another agent any more.
    /// </summary>
    public bool WaitForIdle { get; set; }
}

/// <summary>
/// What <c>POST /sessions/{sid}/message</c> answers, and one row of what <c>POST /fleet/broadcast</c>
/// answers (the Message Load mission, slice 1).
///
/// "QUEUED", NEVER "DELIVERED". The message is written to the recipient's inbox; the recipient reads it when
/// it is next free. Nothing here claims the recipient has seen it.
/// </summary>
public sealed class FleetMessageSendResponse
{
    /// <summary>"queued" (written to the inbox), "duplicate" (dropped: an identical message is already
    /// waiting unread), or "refused" (not written; <see cref="Error"/> says why).</summary>
    public string Status { get; set; } = "";

    /// <summary>The session the message was for.</summary>
    public string RecipientSessionId { get; set; } = "";

    /// <summary>The written message's id, when <see cref="Status"/> is "queued".</summary>
    public string? MessageId { get; set; }

    /// <summary>Why nothing was queued, when <see cref="Status"/> is "refused". Named <c>error</c> on the wire
    /// so every client that reads a refusal sentence from <c>error</c> reads this one.</summary>
    public string? Error { get; set; }

    /// <summary>A sentence about an outcome that is not a refusal - why a duplicate was dropped.</summary>
    public string? Note { get; set; }
}

/// <summary>What <c>POST /fleet/broadcast</c> answers (the Message Load mission, slice 1).</summary>
public sealed class FleetBroadcastResponse
{
    /// <summary>One row per recipient the broadcast was considered for.</summary>
    public List<FleetMessageSendResponse> Results { get; set; } = new();

    /// <summary>True when the whole broadcast was refused before any recipient was considered.</summary>
    public bool Denied { get; set; }

    /// <summary>Why, when <see cref="Denied"/> is true.</summary>
    public string? DeniedReason { get; set; }

    /// <summary>A note about an accepted broadcast that reached nobody - "You have no workers to message."</summary>
    public string? Warning { get; set; }
}

/// <summary>One message as the recipient reads it from <c>GET /fleet/inbox</c>.</summary>
public sealed class FleetInboxMessageDto
{
    public string MessageId { get; set; } = "";

    /// <summary>The sending session's id, or null for a notice from the Gateway itself.</summary>
    public string? FromSessionId { get; set; }

    /// <summary>The sender's name when it sent, or null.</summary>
    public string? FromName { get; set; }

    /// <summary>The sender's machine when it sent, or null.</summary>
    public string? FromMachine { get; set; }

    /// <summary>message, report, team, everyone or system.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The full text, exactly as sent.</summary>
    public string Text { get; set; } = "";

    public DateTime SentAtUtc { get; set; }

    /// <summary>When it was read. For a message returned as unread this is the moment of this read.</summary>
    public DateTime? ReadAtUtc { get; set; }
}

/// <summary>What <c>GET /fleet/inbox</c> answers: the calling session's own inbox.</summary>
public sealed class FleetInboxResponse
{
    /// <summary>The session whose inbox this is - always the caller.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>How many messages were unread before this read. Every one of them is in <see cref="Unread"/>
    /// and is now marked read.</summary>
    public int UnreadCount { get; set; }

    /// <summary>The messages that were unread, oldest first, in full.</summary>
    public List<FleetInboxMessageDto> Unread { get; set; } = new();

    /// <summary>Messages read earlier in the last 24 hours, newest first, at most 200 - only when the caller
    /// asked with <c>all=true</c>. This is how a read whose answer was lost is recovered.</summary>
    public List<FleetInboxMessageDto> Recent { get; set; } = new();

    /// <summary>How many messages were read in the last 24 hours, before the 200-row cap. Zero unless the caller
    /// asked with <c>all=true</c>.</summary>
    public int RecentTotal { get; set; }

    /// <summary>True when <see cref="RecentTotal"/> is more than <see cref="Recent"/> holds - the answer is the
    /// newest 200 of them.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Body of <c>POST /fleet/broadcast</c> - one message to each of the sender's own WORKERS (the fleet's
/// "message send all"; the Message Load mission narrowed it from the sender's team).
///
/// Like <see cref="FleetMessageRequest"/> it carries no sender: the workers are the roster rows that name
/// the AUTHENTICATED session as their controller, so the recipient list can only ever be about the session
/// whose key made the call.
///
/// It is a DIFFERENT TYPE from the Director's <see cref="FleetBroadcastRequest"/> rather than a reuse of
/// it, and the difference is the whole point: that one carries a caller-supplied FromSessionId, which is
/// exactly the field this route must not have. Reusing it would leave a sender field on the wire that the
/// Gateway silently ignores - a caller setting it would believe it had taken effect.
/// </summary>
public sealed class FleetTeamBroadcastRequest
{
    /// <summary>The message body. Each worker's inbox gets one copy, with the sender recorded beside it.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Reach the whole ACCOUNT rather than the sender's workers. Refused unless <see cref="Reason"/> and a
    /// valid human-issued <see cref="GrantId"/> accompany it - an agent cannot mint its own grant, so
    /// this cannot become the default way to talk to the fleet.
    /// </summary>
    public bool Everyone { get; set; }

    /// <summary>Why this message needs to reach beyond the sender's workers. Required with <see cref="Everyone"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>The human-issued broadcast grant authorizing <see cref="Everyone"/>.</summary>
    public string? GrantId { get; set; }
}

/// <summary>
/// The doorbell verb (the Message Load mission, slice 2), spelled once for the Gateway that sends it and the
/// Director that answers it. The Gateway asks; the Director - the only party that can see the terminal -
/// decides whether it is safe to type the one doorbell line, and answers <see cref="FleetRingOutcomes.Rung"/>
/// or <see cref="FleetRingOutcomes.Deferred"/> with a reason.
/// </summary>
public static class FleetDoorbellVerbs
{
    /// <summary>Ask the owning Director to ring one session's doorbell. Payload: <see cref="FleetRingRequest"/>.</summary>
    public const string Ring = "ring";
}

/// <summary>Payload of the <c>ring</c> verb.</summary>
public sealed class FleetRingRequest
{
    /// <summary>How many messages wait unread in the session's inbox. The doorbell line says this number and
    /// nothing else about the messages - their text is never typed.</summary>
    public int UnreadCount { get; set; }
}

/// <summary>The two answers to <c>ring</c>.</summary>
public static class FleetRingOutcomes
{
    /// <summary>The doorbell line was typed and submitted.</summary>
    public const string Rung = "rung";

    /// <summary>Nothing was typed; <see cref="FleetRingResponse.Reason"/> says why. The Gateway asks again later.</summary>
    public const string Deferred = "deferred";
}

/// <summary>Why a Director deferred a ring. One code per reason so the Gateway log can be counted.</summary>
public static class FleetRingDeferReasons
{
    /// <summary>The agent is in the middle of a turn.</summary>
    public const string Working = "working";

    /// <summary>The composer holds text - most likely the owner's unsent words.</summary>
    public const string ComposerHoldsText = "composer-holds-text";

    /// <summary>An interactive menu or dialog owns the terminal (issue 2842); a typed line would pick an option.</summary>
    public const string MenuOpen = "menu-open";

    /// <summary>The session has exited or failed. Nothing is ever typed into it.</summary>
    public const string Exited = "exited";

    /// <summary>The screen could not be read, or its layout is not one the check recognises. Unknown is never
    /// treated as empty.</summary>
    public const string ScreenUnreadable = "screen-unreadable";
}

/// <summary>The Director's answer to <c>ring</c>.</summary>
public sealed class FleetRingResponse
{
    /// <summary>One of <see cref="FleetRingOutcomes"/>.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>One of <see cref="FleetRingDeferReasons"/> when deferred; empty when rung.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The same reason as a sentence, for the log.</summary>
    public string Detail { get; set; } = "";
}
