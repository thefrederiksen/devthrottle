using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.History;

/// <summary>
/// Folds what the Gateway HOLDS of one session's conversation into the <see cref="SessionHistoryDto"/> the
/// Chat screen renders (the turn-push mission, phase 2). Before this, Chat asked the owning Director for
/// the conversation every 2.5 seconds and the Director re-parsed the whole transcript for each poll; now
/// the Director pushes it once and this reads rows.
///
/// THE JOB THIS DOES BEYOND COPYING ROWS. "There are no messages" has several causes and they are not the
/// same news to the person looking at an empty screen:
///
///   this session is not known here     - nothing has ever arrived about it; the id may be wrong.
///   this agent keeps no conversation   - nothing will ever appear, and that is not a fault.
///   that computer has not checked in   - it cannot send anything until it reconnects.
///   that computer cannot send it       - its DevThrottle is too old to push conversations. Update it.
///   nothing has been sent yet          - the Director has it and has not pushed it; wait a moment.
///   the conversation has not started   - the session is here and has simply not spoken.
///
/// The client used to guess between two of these from a boolean and show "Waiting for the conversation to
/// start..." for all the rest, which is the sentence that makes a person sit and wait for something that is
/// never coming. So the sentence is decided HERE and rendered verbatim, like every other display verdict in
/// this product (CLAUDE.md rule 7). The client keeps exactly one line of its own - the one about its OWN
/// filters hiding messages - because the Gateway cannot know what a reader has filtered out.
///
/// WHAT "CONNECTED" MEANS HERE, precisely. It is the same freshness lease the rest of the product reads a
/// Director's presence through: a Director counts as connected while its last push is younger than the
/// stale-after window. So a machine that dropped a moment ago still reads as connected until that window
/// expires, and the screen can be up to that long out of date about it. That is deliberate - the roster is
/// built to ride out reconnect blips rather than flap - and it is written down here because a sentence
/// about someone's computer being offline should not quietly mean something narrower than it says.
/// </summary>
public static class SessionConversationFold
{
    /// <summary>Shown while a session that has arrived simply has not said anything yet.</summary>
    public const string NotStartedText = "Waiting for the conversation to start...";

    /// <summary>Shown for an agent that keeps no readable conversation at all.</summary>
    public const string UnsupportedText = "History is not available for this agent yet.";

    /// <summary>Shown when the owning computer is connected and pushing, but this session's conversation has
    /// not arrived yet. Self-heals within a minute: the Director's safety sweep pushes what it missed.</summary>
    public const string NotPushedYetText = "This session's computer has not sent its conversation yet. It should appear in a moment.";

    /// <summary>Shown when the owning computer is connected but its DevThrottle is too old to send
    /// conversations. Nothing will arrive until it is updated, so the sentence says so rather than asking
    /// the reader to keep waiting.</summary>
    public const string DirectorTooOldText = "This session's computer is running an older DevThrottle that does not send conversations. Update it to read the chat here.";

    /// <summary>
    /// Shown when the owning computer has not checked in recently. Two kinds of precision here, both from
    /// review:
    ///
    /// It is worded to be true of BOTH shapes it reaches - a session with nothing stored at all, and one
    /// whose stored conversation is empty - because saying "nothing arrived" would contradict the head this
    /// same response ships.
    ///
    /// And it says NOT CHECKED IN, not "offline", because that is the whole of what this Gateway knows: the
    /// Director's last push is older than the freshness window. The machine may be running perfectly with a
    /// dropped connection. The status key below is still the shorthand <c>director-offline</c> - that is a
    /// machine-readable label for logs - but the sentence a person reads must not claim more than was
    /// observed.
    /// </summary>
    public const string DirectorOfflineText = "This session's computer has not checked in recently, and no conversation has been stored for it.";

    /// <summary>Shown when the Gateway has never heard of the session at all - not in the roster, nothing
    /// stored. Distinct from "offline", which is about a session it DOES know.</summary>
    public const string UnknownSessionText = "This session is not known here. It may have been deleted, or belong to another account.";

    /// <summary>Shown ABOVE a stored conversation whose computer has not checked in recently. The words on
    /// screen are real; what is not true any more is that they are current. "Until it reconnects" rather
    /// than "until that computer is back", for the same reason as <see cref="DirectorOfflineText"/>: a
    /// dropped connection is what was observed, not a machine that is switched off.</summary>
    public const string FrozenOfflineNotice = "This session's computer has not checked in recently. This is the conversation as it last reached here - nothing new will arrive, and anything you send will not go through, until it reconnects.";

    /// <summary>Shown above a stored conversation whose computer is checking in but cannot send new turns.
    /// It says "the last turn stored" rather than "the last turn it sent", because the rows may have come
    /// from an earlier Director on that session - ownership can change (found in review).</summary>
    public const string FrozenTooOldNotice = "This session's computer is running an older DevThrottle that does not send conversations, so what is shown here stops at the last turn stored. Update it to keep the chat current.";

    /// <param name="sessionId">The session being read.</param>
    /// <param name="head">The stored head row, or null when nothing has ever been pushed for this session.</param>
    /// <param name="messages">The stored messages, in order. Empty when the head exists but no turn has arrived.</param>
    /// <param name="directorId">The Director that owns the session, or null when it is not located.</param>
    /// <param name="sessionKnown">The Gateway has heard of this session at all - it is in the roster (fresh
    /// or stale) or has stored rows. False means the id names nothing here, which is a different answer from
    /// a session whose computer is merely away.</param>
    /// <param name="directorConnected">Whether that Director's last push is inside the freshness window.</param>
    /// <param name="directorPushesTurns">Whether that Director told this Gateway it sends conversations.
    /// False for a Director too old to have the feature - which is why "nothing stored" is not always the
    /// same news.</param>
    public static SessionHistoryDto Fold(
        string sessionId,
        SessionTurnHeadEntity? head,
        List<HistoryMessageDto> messages,
        string? directorId,
        bool sessionKnown,
        bool directorConnected,
        bool directorPushesTurns)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var dto = Fold(sessionId, head, messages.Count, directorId, sessionKnown, directorConnected, directorPushesTurns);
        dto.Messages = messages;
        return dto;
    }

    /// <summary>
    /// The same fold for a conversation of <paramref name="messageCount"/> messages that are not built yet (issue
    /// #4387): every field but <c>messages</c>, which is left empty for the caller to fill with the part it sends.
    /// The verdicts depend on whether there are any messages, never on what they say.
    /// </summary>
    public static SessionHistoryDto Fold(
        string sessionId,
        SessionTurnHeadEntity? head,
        int messageCount,
        string? directorId,
        bool sessionKnown,
        bool directorConnected,
        bool directorPushesTurns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageCount);
        var dto = new SessionHistoryDto
        {
            SessionId = sessionId,
            // The LIVE owner first, so the identifier in the response is the one the status below is about.
            // Preferring the head's would let the response name the Director that wrote the rows while the
            // sentence described the one that owns the session now (found in review).
            DirectorId = directorId ?? head?.DirectorId ?? "",
            Agent = head?.Agent ?? "",
            IsSupported = head?.IsSupported ?? true,
            IsRawText = head?.IsRawText ?? false,
            HistoryState = head?.HistoryState,
            Messages = new List<HistoryMessageDto>(),
        };

        // STORED CONTENT WINS OVER EVERYTHING. This is the whole point of holding the conversation: it stays
        // readable when the machine that produced it is not.
        //
        // But readable is not the same as CURRENT, and the screen must not pretend otherwise. A conversation
        // whose computer is away looks exactly like a live one: same bubbles, no hint that it stopped an
        // hour ago. A reader types into it and waits for an answer that cannot come, because the agent that
        // would answer is not running. So a frozen conversation carries a notice above it saying so - the
        // same defect this mission exists to kill, one screen over, and it does NOT replace the content the
        // way an empty-screen sentence does.
        if (messageCount > 0)
        {
            dto.Status = "ok";
            dto.StaleNotice = !directorConnected ? FrozenOfflineNotice
                            : !directorPushesTurns ? FrozenTooOldNotice
                            : null;
            return dto;
        }

        // An agent that keeps no conversation is next, ABOVE the connectivity answers, even though "that
        // computer is offline" is the more actionable-sounding sentence. Nothing about this session will
        // ever produce a conversation, online or off, so pointing the reader at their machine would send
        // them to fix something that is not the reason.
        if (head is not null && !head.IsSupported)
        {
            dto.Status = "unsupported";
            dto.EmptyText = UnsupportedText;
            return dto;
        }

        // Nothing to show. Now the reasons, most-final first - and note that these apply to a session with a
        // stored-but-empty head just as much as to one with no head at all. An earlier version stopped at
        // "the conversation has not started" the moment a head existed, so a session whose computer went
        // away after registering sat on that sentence forever (found in review).
        if (!sessionKnown)
        {
            dto.Status = "unknown-session";
            dto.EmptyText = UnknownSessionText;
            dto.Error = dto.EmptyText;
            return dto;
        }
        if (!directorConnected)
        {
            dto.Status = "director-offline";
            dto.EmptyText = DirectorOfflineText;
            dto.Error = dto.EmptyText;
            return dto;
        }
        if (!directorPushesTurns)
        {
            dto.Status = "director-too-old";
            dto.EmptyText = DirectorTooOldText;
            dto.Error = dto.EmptyText;
            return dto;
        }
        if (head is null)
        {
            dto.Status = "not-pushed-yet";
            dto.EmptyText = NotPushedYetText;
            dto.Error = dto.EmptyText;
            return dto;
        }

        // Known, connected, pushing, and its conversation has arrived - carrying nothing. The session really
        // has not spoken yet, and this is the only arm where telling the reader to wait is honest.
        dto.Status = "ok";
        dto.EmptyText = NotStartedText;
        return dto;
    }
}
