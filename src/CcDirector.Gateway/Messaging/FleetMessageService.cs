using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Messaging;

/// <summary>One end of a message, as the roster described it at the moment of sending.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ControllerSessionId">The session that started it (its supervisor), or null.</param>
/// <param name="Name">Its roster name, or null.</param>
/// <param name="Machine">Its machine, or null.</param>
public sealed record FleetParty(string SessionId, string? ControllerSessionId, string? Name, string? Machine);

/// <summary>The answer to one send, and the status code the route answers it with.</summary>
public readonly record struct FleetSendOutcome(FleetMessageSendResponse Response, FleetMessageOutcome Outcome)
{
    /// <summary>The HTTP status for this outcome. A queued message and a dropped duplicate are both 200 -
    /// neither is a failure. A relationship refusal is 403, a rate refusal 429, a bad text 400.</summary>
    public int StatusCode => Outcome switch
    {
        FleetMessageOutcome.Queued => StatusCodes.Status200OK,
        FleetMessageOutcome.DuplicateDropped => StatusCodes.Status200OK,
        FleetMessageOutcome.RefusedNotRelated => StatusCodes.Status403Forbidden,
        FleetMessageOutcome.RefusedHourlyLimit => StatusCodes.Status429TooManyRequests,
        FleetMessageOutcome.RefusedRecipientSpacing => StatusCodes.Status429TooManyRequests,
        FleetMessageOutcome.RefusedReplyTarget => StatusCodes.Status403Forbidden,
        FleetMessageOutcome.RefusedAlreadyReplied => StatusCodes.Status409Conflict,
        FleetMessageOutcome.RefusedUnknownMessage => StatusCodes.Status404NotFound,
        FleetMessageOutcome.RefusedNoReplyWanted => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}

/// <summary>
/// Sending and reading fleet messages (the Message Load mission, slice 1). The routes resolve WHO - the
/// sender from its session key, the recipient from the roster - and hand the two parties here; this class
/// decides, writes, reads and logs. Nothing in it is typed into a terminal: the record is the delivery.
///
/// EVERY SEND, REFUSAL AND READ LEAVES ONE LOG LINE, with the sender, the recipient, the kind and the
/// outcome, so "why did my message not arrive" is answerable from the Gateway log alone. The text itself is
/// never logged - only its length - because a message can carry anything an agent chose to write.
/// </summary>
public sealed class FleetMessageService
{
    private readonly FleetMessageStore _store;
    private readonly FleetMessageLimits _limits;
    private readonly Func<DateTime> _clock;

    public FleetMessageService(FleetMessageStore store, FleetMessageLimits? limits = null, Func<DateTime>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _limits = limits ?? FleetMessageLimits.Default;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The limits this service enforces.</summary>
    public FleetMessageLimits Limits => _limits;

    /// <summary>
    /// Decide one message and, when the policy allows it, write it into the recipient's inbox.
    /// </summary>
    /// <param name="tenant">The account both parties are in.</param>
    /// <param name="sender">The sending session, or null for a notice from the Gateway itself.</param>
    /// <param name="recipient">The session the message is for.</param>
    /// <param name="text">The full text.</param>
    /// <param name="kind">One of <see cref="FleetMessageKinds"/>.</param>
    /// <param name="exemption">Why, if at all, the relationship and rate rules are waived.</param>
    /// <param name="replyWithin">When set, the sender wants a reply within this long (slice 3): it gets a
    /// correlation id and a deadline, and the sender gets one no-reply notice if the deadline passes unanswered.
    /// Must be inside <see cref="FleetMessageLimits.MinReplyWindow"/> and <see cref="FleetMessageLimits.MaxReplyWindow"/>
    /// (see <see cref="TryReplyWindow"/>).</param>
    public FleetSendOutcome Send(
        TenantId tenant,
        FleetParty? sender,
        FleetParty recipient,
        string text,
        string kind,
        FleetMessageExemption exemption = FleetMessageExemption.None,
        TimeSpan? replyWithin = null)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (sender is null && exemption != FleetMessageExemption.System)
            throw new ArgumentException("Only a system notice has no sender.", nameof(sender));
        if (kind == FleetMessageKinds.Reply)
            throw new ArgumentException("A reply is sent with Reply, which names the message it answers.", nameof(kind));
        if (replyWithin is { } w && (sender is null || w < _limits.MinReplyWindow || w > _limits.MaxReplyWindow))
            throw new ArgumentOutOfRangeException(nameof(replyWithin), replyWithin,
                "A reply may be asked for only by a session, and only within the reply window limits.");

        var now = _clock();
        var draft = new FleetMessageDraft(recipient.SessionId, sender?.SessionId, sender?.Name, sender?.Machine, kind, text ?? "",
            ReplyByUtc: replyWithin is { } within ? now + within : null);
        FleetMessageHistory seen = default;
        var (verdict, written) = _store.TryEnqueue(tenant, draft, now, _limits.SenderWindow, history =>
        {
            seen = history;
            return FleetMessagePolicy.Decide(new FleetMessageAttempt(
                SenderSessionId: sender?.SessionId,
                SenderControllerSessionId: sender?.ControllerSessionId,
                RecipientSessionId: recipient.SessionId,
                RecipientControllerSessionId: recipient.ControllerSessionId,
                Text: text ?? "",
                NowUtc: now,
                SentBySenderInWindow: history.SentBySenderInWindow,
                LastSentToRecipientUtc: history.LastSentToRecipientUtc,
                RecipientHasUnreadDuplicate: history.RecipientHasUnreadDuplicate,
                Exemption: exemption,
                Kind: kind), _limits);
        });

        var response = new FleetMessageSendResponse { RecipientSessionId = recipient.SessionId };
        var from = sender is null ? "gateway" : Short(sender.SessionId);
        var len = (text ?? "").Length;
        switch (verdict.Outcome)
        {
            case FleetMessageOutcome.Queued:
                response.Status = "queued";
                response.MessageId = written!.MessageId;
                response.CorrelationId = written.CorrelationId;
                response.ReplyByUtc = written.ReplyByUtc;
                FileLog.Write($"[FleetMessageService] Send QUEUED: from={from} to={Short(recipient.SessionId)} kind={kind} exemption={exemption} id={written.MessageId} correlation={written.CorrelationId ?? "(none)"} len={len}");
                break;
            case FleetMessageOutcome.DuplicateDropped:
                response.Status = "duplicate";
                response.Note = verdict.Reason;
                // The copy already waiting is the one that will be read - and answered, if it asked - so its ids are
                // the ones the sender needs.
                response.MessageId = seen.DuplicateMessageId;
                response.CorrelationId = seen.DuplicateCorrelationId;
                FileLog.Write($"[FleetMessageService] Send DUPLICATE dropped: from={from} to={Short(recipient.SessionId)} kind={kind} waiting={seen.DuplicateMessageId} len={len}");
                break;
            default:
                response.Status = "refused";
                response.Error = verdict.Reason;
                FileLog.Write($"[FleetMessageService] Send REFUSED ({verdict.Outcome}): from={from} to={Short(recipient.SessionId)} kind={kind} len={len}");
                break;
        }
        return new FleetSendOutcome(response, verdict.Outcome);
    }

    /// <summary>
    /// Turn the minutes a caller asked for into a reply window (slice 3). Null minutes: the default,
    /// <see cref="FleetMessageLimits.DefaultReplyWindow"/>. Returns false, with the sentence the caller reads, when
    /// the minutes are outside <see cref="FleetMessageLimits.MinReplyWindow"/> and
    /// <see cref="FleetMessageLimits.MaxReplyWindow"/>.
    /// </summary>
    public bool TryReplyWindow(int? minutes, out TimeSpan window, out string error)
    {
        window = minutes is { } m ? TimeSpan.FromMinutes(m) : _limits.DefaultReplyWindow;
        error = "";
        if (window >= _limits.MinReplyWindow && window <= _limits.MaxReplyWindow) return true;
        error = $"replyByMinutes must be between {(int)_limits.MinReplyWindow.TotalMinutes} and " +
                $"{(int)_limits.MaxReplyWindow.TotalMinutes}; {minutes} was given.";
        return false;
    }

    /// <summary>
    /// Answer a message that asked for a reply (slice 3, ruling 10). <paramref name="id"/> is the message's
    /// correlation id or its message id. The reply goes into the inbox of the session that SENT the original,
    /// whatever the relationship between the two; only the session the original was sent to may send it; it is
    /// held to the text rules, the duplicate rule and one reply per question, and not to the rate limits. A reply
    /// after the deadline is written like any other.
    /// </summary>
    /// <param name="tenant">The account.</param>
    /// <param name="replier">The replying session, from its session key and the roster.</param>
    /// <param name="id">The correlation id or message id of the message being answered.</param>
    /// <param name="text">The reply.</param>
    public FleetSendOutcome Reply(TenantId tenant, FleetParty replier, string id, string text)
    {
        ArgumentNullException.ThrowIfNull(replier);
        var now = _clock();
        var result = _store.TryReply(tenant, replier.SessionId, replier.Name, replier.Machine, id ?? "", text ?? "", now,
            (history, original) => FleetMessagePolicy.Decide(new FleetMessageAttempt(
                SenderSessionId: replier.SessionId,
                SenderControllerSessionId: replier.ControllerSessionId,
                RecipientSessionId: original.OriginalSenderSessionId ?? "",
                RecipientControllerSessionId: null,
                Text: text ?? "",
                NowUtc: now,
                SentBySenderInWindow: history.SentBySenderInWindow,
                LastSentToRecipientUtc: history.LastSentToRecipientUtc,
                RecipientHasUnreadDuplicate: history.RecipientHasUnreadDuplicate,
                Kind: FleetMessageKinds.Reply,
                ReplyTo: original), _limits));

        var from = Short(replier.SessionId);
        var len = (text ?? "").Length;
        var original = result.Original;
        var response = new FleetMessageSendResponse
        {
            RecipientSessionId = original?.SenderSessionId ?? "",
            InReplyToMessageId = original?.MessageId,
            CorrelationId = original?.CorrelationId,
        };
        FleetMessageOutcome outcome;
        switch (result.Miss)
        {
            case FleetReplyMiss.NotFound:
                outcome = FleetMessageOutcome.RefusedUnknownMessage;
                response.Status = "refused";
                response.Error = $"No message has the id {id}. Use the correlation id or message id shown by " +
                                 "cc-devthrottle message inbox. Nothing was queued.";
                FileLog.Write($"[FleetMessageService] Reply REFUSED (unknown id): from={from} id={id} len={len}");
                return new FleetSendOutcome(response, outcome);
            case FleetReplyMiss.NoReplyWanted:
                outcome = FleetMessageOutcome.RefusedNoReplyWanted;
                response.Status = "refused";
                response.Error = $"Message {original!.MessageId} did not ask for a reply, so there is nothing to answer. " +
                                 $"Nothing was queued. {FleetMessagePolicy.PutItInYourReport}";
                FileLog.Write($"[FleetMessageService] Reply REFUSED (no reply wanted): from={from} original={original.MessageId} len={len}");
                return new FleetSendOutcome(response, outcome);
        }

        var verdict = result.Verdict;
        switch (verdict.Outcome)
        {
            case FleetMessageOutcome.Queued:
                response.Status = "queued";
                response.MessageId = result.Written!.MessageId;
                var late = original!.ReplyByUtc is { } by && now > by;
                FileLog.Write($"[FleetMessageService] Reply QUEUED: from={from} to={Short(original.SenderSessionId)} original={original.MessageId} correlation={original.CorrelationId} id={result.Written.MessageId} late={late} len={len}");
                break;
            case FleetMessageOutcome.DuplicateDropped:
                response.Status = "duplicate";
                response.Note = verdict.Reason;
                FileLog.Write($"[FleetMessageService] Reply DUPLICATE dropped: from={from} original={original!.MessageId} len={len}");
                break;
            default:
                // A refused reply never names the original's sender to a session the question was not sent to.
                response.RecipientSessionId = "";
                response.Status = "refused";
                response.Error = verdict.Reason;
                FileLog.Write($"[FleetMessageService] Reply REFUSED ({verdict.Outcome}): from={from} original={original!.MessageId} len={len}");
                break;
        }
        return new FleetSendOutcome(response, verdict.Outcome);
    }

    /// <summary>
    /// Mark every message whose reply deadline passed with no reply, and queue ONE no-reply notice to each sender -
    /// in one write, as stuck and its notice are (slice 3). Called from the doorbell's heartbeat. Returns the rows
    /// marked.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> MarkReplyOverdueAndNotify(TenantId tenant, Func<string, string?>? recipientName = null)
    {
        var now = _clock();
        var marked = _store.MarkReplyOverdueWithNotices(tenant, now,
            m => m.SenderSessionId is null
                ? null
                : new FleetMessageDraft(m.SenderSessionId, null, null, null, FleetMessageKinds.System,
                    NoReplyNoticeText(m, recipientName?.Invoke(m.RecipientSessionId), _limits.MaxTextLength),
                    CorrelationId: m.CorrelationId, InReplyToMessageId: m.MessageId),
            _limits);
        foreach (var (m, notice) in marked)
            FileLog.Write($"[FleetMessageService] NO REPLY by the deadline: id={m.MessageId} correlation={m.CorrelationId} " +
                          $"to={Short(m.RecipientSessionId)} from={Short(m.SenderSessionId)} notice={notice?.MessageId ?? "(none)"}");
        return marked.Select(x => x.Overdue).ToList();
    }

    /// <summary>The notice a sender receives when nobody replied to its message by the deadline.</summary>
    public static string NoReplyNoticeText(FleetMessageEntity message, string? recipientName) =>
        NoReplyNoticeText(message, recipientName, int.MaxValue);

    /// <summary>
    /// The no-reply notice, built to fit <paramref name="maxLength"/> whatever the recipient's roster name
    /// (inspection 6, ruling 1). Only the name is ever cut - to fit, ending in "..." - and it is left out
    /// altogether when fewer than four characters of it would fit. The message id, the correlation id and the
    /// advice are never cut; a cap too small even for those gives the text uncut, and the policy refuses it.
    /// </summary>
    public static string NoReplyNoticeText(FleetMessageEntity message, string? recipientName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bare = NoReplyNoticeTextFor(message, Short(message.RecipientSessionId));
        if (string.IsNullOrWhiteSpace(recipientName)) return bare;
        // The name adds itself plus " (" and ")" around the short id.
        var room = (long)maxLength - bare.Length - 3;
        string name;
        if (room >= recipientName.Length) name = recipientName;
        else if (room >= 4) name = recipientName[..(int)(room - 3)] + "...";
        else return bare;
        return NoReplyNoticeTextFor(message, $"{name} ({Short(message.RecipientSessionId)})");
    }

    private static string NoReplyNoticeTextFor(FleetMessageEntity message, string who) =>
        $"No reply to your message {message.MessageId} (correlation {message.CorrelationId}) from {who} by its " +
        "deadline. Carry on without the answer and say so in your report; do not send the question again. " +
        "If a reply comes later it still lands in your inbox.";

    /// <summary>
    /// Read one session's own inbox. Every unread message comes back in full and is marked read by this call.
    /// </summary>
    public FleetInboxResponse ReadInbox(TenantId tenant, string sessionId, bool includeRecent)
    {
        var read = _store.ReadInbox(tenant, sessionId, _clock(), includeRecent, _limits.RecentReadWindow, _limits.RecentReadCap);
        FileLog.Write($"[FleetMessageService] ReadInbox: sid={Short(sessionId)} unread={read.Unread.Count} recent={read.Recent.Count}/{read.RecentTotal} truncated={read.RecentTruncated} ids=[{string.Join(",", read.Unread.Select(m => m.MessageId))}]");
        return new FleetInboxResponse
        {
            SessionId = sessionId,
            UnreadCount = read.Unread.Count,
            Unread = read.Unread.Select(m => ToDto(m, read.Originals)).ToList(),
            Recent = read.Recent.Select(m => ToDto(m, read.Originals)).ToList(),
            RecentTotal = read.RecentTotal,
            Truncated = read.RecentTruncated,
        };
    }

    /// <summary>
    /// One inbox row as the reader sees it. THE GATEWAY SAYS WHAT A ROW IS (project rule 7): whether it wants a reply,
    /// whether it is a no-reply notice, and which question a reply or notice is about are decided here and put on
    /// the row, so the command line only lays them out.
    /// </summary>
    private static FleetInboxMessageDto ToDto(FleetMessageEntity m, IReadOnlyDictionary<string, FleetMessageEntity>? originals)
    {
        var isOriginal = m.InReplyToMessageId is null;
        var dto = new FleetInboxMessageDto
        {
            MessageId = m.MessageId,
            FromSessionId = m.SenderSessionId,
            FromName = m.SenderName,
            FromMachine = m.SenderMachine,
            Kind = m.Kind,
            Text = m.Text,
            SentAtUtc = m.CreatedAtUtc,
            ReadAtUtc = m.ReadAtUtc,
            CorrelationId = m.CorrelationId,
            ReplyWanted = isOriginal && m.CorrelationId is not null && m.SenderSessionId is not null,
            ReplyByUtc = isOriginal ? m.ReplyByUtc : null,
            Notice = m.Kind == FleetMessageKinds.System && !isOriginal ? FleetInboxNotices.NoReply : null,
        };
        if (dto.ReplyWanted)
            dto.ReplyHint = $"cc-devthrottle message reply {m.CorrelationId} \"<your answer>\"";
        if (!isOriginal)
        {
            var question = new FleetInboxQuestionDto { MessageId = m.InReplyToMessageId!, CorrelationId = m.CorrelationId };
            if (originals is not null && originals.TryGetValue(m.InReplyToMessageId!, out var o))
            {
                question.ToSessionId = o.RecipientSessionId;
                question.Text = o.Text;
                question.SentAtUtc = o.CreatedAtUtc;
                question.ReplyByUtc = o.ReplyByUtc;
                question.Late = m.Kind == FleetMessageKinds.Reply && o.ReplyByUtc is { } by && m.CreatedAtUtc > by;
            }
            dto.InReplyTo = question;
        }
        return dto;
    }

    private static string Short(string? id) => string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
