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
    public FleetSendOutcome Send(
        TenantId tenant,
        FleetParty? sender,
        FleetParty recipient,
        string text,
        string kind,
        FleetMessageExemption exemption = FleetMessageExemption.None)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (sender is null && exemption != FleetMessageExemption.System)
            throw new ArgumentException("Only a system notice has no sender.", nameof(sender));

        var now = _clock();
        var draft = new FleetMessageDraft(recipient.SessionId, sender?.SessionId, sender?.Name, sender?.Machine, kind, text ?? "");
        var (verdict, written) = _store.TryEnqueue(tenant, draft, now, _limits.SenderWindow, history =>
            FleetMessagePolicy.Decide(new FleetMessageAttempt(
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
                Kind: kind), _limits));

        var response = new FleetMessageSendResponse { RecipientSessionId = recipient.SessionId };
        var from = sender is null ? "gateway" : Short(sender.SessionId);
        var len = (text ?? "").Length;
        switch (verdict.Outcome)
        {
            case FleetMessageOutcome.Queued:
                response.Status = "queued";
                response.MessageId = written!.MessageId;
                FileLog.Write($"[FleetMessageService] Send QUEUED: from={from} to={Short(recipient.SessionId)} kind={kind} exemption={exemption} id={written.MessageId} len={len}");
                break;
            case FleetMessageOutcome.DuplicateDropped:
                response.Status = "duplicate";
                response.Note = verdict.Reason;
                FileLog.Write($"[FleetMessageService] Send DUPLICATE dropped: from={from} to={Short(recipient.SessionId)} kind={kind} len={len}");
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
    /// Read one session's own inbox. Every unread message comes back in full and is marked read by this call.
    /// </summary>
    public FleetInboxResponse ReadInbox(TenantId tenant, string sessionId, bool includeRecent)
    {
        var read = _store.ReadInbox(tenant, sessionId, _clock(), includeRecent, _limits.RecentReadWindow);
        FileLog.Write($"[FleetMessageService] ReadInbox: sid={Short(sessionId)} unread={read.Unread.Count} recent={read.Recent.Count} ids=[{string.Join(",", read.Unread.Select(m => m.MessageId))}]");
        return new FleetInboxResponse
        {
            SessionId = sessionId,
            UnreadCount = read.Unread.Count,
            Unread = read.Unread.Select(ToDto).ToList(),
            Recent = read.Recent.Select(ToDto).ToList(),
        };
    }

    private static FleetInboxMessageDto ToDto(FleetMessageEntity m) => new()
    {
        MessageId = m.MessageId,
        FromSessionId = m.SenderSessionId,
        FromName = m.SenderName,
        FromMachine = m.SenderMachine,
        Kind = m.Kind,
        Text = m.Text,
        SentAtUtc = m.CreatedAtUtc,
        ReadAtUtc = m.ReadAtUtc,
    };

    private static string Short(string? id) => string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
