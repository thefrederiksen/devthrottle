using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Inbox;

/// <summary>Who sent a message into Your inbox, as the Gateway's own records name them at the moment of sending. The
/// routes fill this from the sending session's key and its recorded history - never from the request body.</summary>
/// <param name="FromKind">One of <see cref="YourInboxWords.Senders"/>.</param>
/// <param name="SessionId">The session that wrote it, or null when no session did.</param>
/// <param name="SessionNumber">Its three-digit number, when known.</param>
/// <param name="SessionName">Its name, when known.</param>
/// <param name="Factory">The factory it came from, or null.</param>
/// <param name="Seat">The factory seat that wrote it, or null.</param>
public sealed record YourInboxSender(string FromKind, string? SessionId, int? SessionNumber, string? SessionName, string? Factory, string? Seat);

/// <summary>One message to write into Your inbox.</summary>
/// <param name="ReportId">The dev report it is about, already proved to be one of the account's, or null.</param>
/// <param name="ReportTitle">That report's title, or null.</param>
/// <param name="FactoryMessageId">The Factory inbox message it answers, or null.</param>
public sealed record YourInboxSendInput(
    string? Subject,
    string? Body,
    YourInboxSender Sender,
    DateTime NowUtc,
    Guid? ReportId = null,
    string? ReportTitle = null,
    Guid? FactoryMessageId = null);

/// <summary>A request the store refuses, with the code and the sentence the route answers with.</summary>
public sealed class YourInboxRefusedException : Exception
{
    public YourInboxRefusedException(string code, string message, int status = 400) : base(message)
    {
        Code = code;
        Status = status;
    }

    /// <summary>The machine-readable reason.</summary>
    public string Code { get; }

    /// <summary>The HTTP status the route answers with.</summary>
    public int Status { get; }
}

/// <summary>
/// THE YOUR INBOX STORE (Factory Inbox mission, phase 3): the messages to the owner - from any session, and from a
/// factory's Boss or morning summary - and the replies he sends from them.
///
/// This store decides nothing about WHO may call it; that is the routes' ruling (<c>Api.YourInboxEndpoints</c>). It
/// decides what a call may do to the rows: a message is checked and written once, its state is one of unread, read and
/// archived, and a reply is kept beside it with where it went.
///
/// NOTHING IS DELETED BY THE SYSTEM. There is no delete here, and nothing in the Gateway sweeps this table.
/// </summary>
public sealed class YourInboxStore
{
    /// <summary>The longest subject, in characters (UTF-16 code units).</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>The longest message body, in characters.</summary>
    public const int MaxBodyLength = 20000;

    /// <summary>The longest body the table holds: a session's body, or a Boss's decision - its why and the owner's whole
    /// message quoted beneath it, with the few fixed words between them.</summary>
    public const int MaxStoredBodyLength = Factory.Inbox.FactoryInboxStore.MaxTextLength + Factory.Inbox.FactoryInboxStore.MaxWhyLength + 400;

    /// <summary>The seat a Boss's decision is shown from.</summary>
    public const string BossSeat = "boss";

    /// <summary>The seat a factory's 08:00 morning summary is shown from ("Booking - Morning summary").</summary>
    public const string MorningSeat = "morning summary";

    /// <summary>The longest reply, in characters. Well under the fleet message limit, so the reply and the line that
    /// says what it answers always fit in one fleet message.</summary>
    public const int MaxReplyLength = 8000;

    /// <summary>The most messages a list read returns when the reader does not say.</summary>
    public const int DefaultList = 100;

    /// <summary>The most messages one list read may return.</summary>
    public const int MaxList = 500;

    private readonly GatewayDatabase _db;

    public YourInboxStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------------------------------------------------------------- writing a message

    /// <summary>
    /// Write one message into the owner's inbox, unread. A bad subject or body is refused with a sentence
    /// (<see cref="YourInboxRefusedException"/>): empty, too long, or a subject that is more than one line.
    /// </summary>
    public YourInboxMessageEntity Send(TenantId tenant, YourInboxSendInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Sender);
        FileLog.Write($"[YourInboxStore] Send: tenant={tenant.ToLogString()} from={input.Sender.FromKind} session={input.Sender.SessionId ?? "(none)"} factory={input.Sender.Factory ?? "(none)"}");
        if (input.Sender.FromKind is not (YourInboxWords.Senders.Session or YourInboxWords.Senders.Factory))
            throw new ArgumentException($"'{input.Sender.FromKind}' is not a Your inbox sender.", nameof(input));
        if (input.Sender.FromKind == YourInboxWords.Senders.Session && string.IsNullOrWhiteSpace(input.Sender.SessionId))
            throw new ArgumentException("A message from a session names that session.", nameof(input));
        if (input.Sender.FromKind == YourInboxWords.Senders.Factory && string.IsNullOrWhiteSpace(input.Sender.Factory))
            throw new ArgumentException("A message from a factory names that factory.", nameof(input));

        var subject = (input.Subject ?? "").Trim();
        if (subject.Length == 0)
            throw new YourInboxRefusedException("subject_empty", "The subject is empty. Say in one line what the message is about.");
        if (subject.Contains('\n') || subject.Contains('\r'))
            throw new YourInboxRefusedException("subject_not_one_line", "The subject is more than one line. Put the detail in the body.");
        if (subject.Length > MaxSubjectLength)
            throw new YourInboxRefusedException("subject_too_long", $"The subject is {subject.Length} characters; the limit is {MaxSubjectLength}. Put the detail in the body.");
        var body = input.Body ?? "";
        if (string.IsNullOrWhiteSpace(body))
            throw new YourInboxRefusedException("body_empty", "The body is empty. Write what the owner needs to know.");
        if (body.Length > MaxBodyLength)
            throw new YourInboxRefusedException("body_too_long", $"The body is {body.Length} characters; the limit is {MaxBodyLength}.");

        var row = new YourInboxMessageEntity
        {
            TenantId = tenant.Value,
            Subject = subject,
            Body = body,
            FromKind = input.Sender.FromKind,
            SessionId = input.Sender.SessionId,
            SessionNumber = input.Sender.SessionNumber,
            SessionName = input.Sender.SessionName,
            Factory = input.Sender.Factory,
            Seat = input.Sender.Seat,
            ReportId = input.ReportId,
            ReportTitle = input.ReportTitle,
            FactoryMessageId = input.FactoryMessageId,
            State = YourInboxWords.States.Unread,
            SentAtUtc = input.NowUtc,
        };
        using (var ctx = _db.CreateContext(tenant))
        {
            ctx.YourInboxMessages.Add(row);
            ctx.SaveChanges();
        }
        FileLog.Write($"[YourInboxStore] Send: wrote {row.Id}");
        return row;
    }

    // ---------------------------------------------------------------- a Boss's decision

    /// <summary>
    /// THE MESSAGE A BOSS'S DECISION WRITES (mission section 7): Declined, Cannot be done and Pushed back each tell the
    /// owner, with exactly one message, and Delegated, To do and Noted tell him nothing. This only BUILDS the row; the
    /// Factory inbox store adds it inside the same transaction as the decision (<c>FactoryInboxStore.Decide</c>), so a
    /// decision and its message are written together or not at all, and a second decide of the same message - refused as
    /// already decided - can never write a second one. The subject says what was decided about which message; the body
    /// is the Boss's why, in its words, and the owner's own message beneath it.
    /// </summary>
    public static YourInboxMessageEntity ForBossDecision(TenantId tenant, string factory, string bossSessionId, Guid factoryMessageId,
        string ownerText, DateTime ownerSentAtUtc, string kind, string why, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bossSessionId);
        ArgumentNullException.ThrowIfNull(ownerText);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (!Factory.Inbox.FactoryInboxWords.Decisions.TellsTheOwner(kind))
            throw new ArgumentException($"A decision of '{kind}' does not tell the owner, so it writes nothing into Your inbox.", nameof(kind));
        var label = Factory.Inbox.FactoryInboxWords.DecisionLabel(kind);

        var firstLine = ownerText.Trim().Split('\n')[0].Trim();
        var subject = $"{label}: {firstLine}";
        if (subject.Length > MaxSubjectLength) subject = subject[..(MaxSubjectLength - 3)].TrimEnd() + "...";
        var body = $"{label}. The Boss's reason:\n{why.Trim()}\n\nYour message (sent {ownerSentAtUtc:yyyy-MM-dd HH:mm} UTC):\n{ownerText}";
        if (body.Length > MaxStoredBodyLength)
            throw new InvalidOperationException(
                $"A Boss's decision made a Your inbox body of {body.Length} characters, over the stored limit of {MaxStoredBodyLength}; the why and message limits should make that impossible.");
        FileLog.Write($"[YourInboxStore] ForBossDecision: factory={factory} message={factoryMessageId} kind={kind}");
        return new YourInboxMessageEntity
        {
            TenantId = tenant.Value,
            Subject = subject,
            Body = body,
            FromKind = YourInboxWords.Senders.Factory,
            SessionId = bossSessionId,
            Factory = factory,
            Seat = BossSeat,
            FactoryMessageId = factoryMessageId,
            State = YourInboxWords.States.Unread,
            SentAtUtc = nowUtc,
        };
    }

    // ---------------------------------------------------------------- a factory's morning summary

    /// <summary>
    /// THE MESSAGE A FACTORY'S 08:00 MORNING RUN WRITES (Factory Inbox mission, phase 5 "Email off"): it replaces the
    /// factory email, and no email is sent. Sent from the factory, seat <see cref="MorningSeat"/>, at the slot's 08:00
    /// boundary - the time it describes, and the key <see cref="MorningOf"/> finds it by, so a slot tried again never
    /// writes a second one. A body longer than <see cref="MaxBodyLength"/> is cut at a line end and says so.
    /// </summary>
    public static YourInboxMessageEntity ForMorningSummary(TenantId tenant, string factory, string subject, string body, DateTime boundaryUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (subject.Length > MaxSubjectLength) subject = subject[..(MaxSubjectLength - 3)].TrimEnd() + "...";
        if (body.Length > MaxBodyLength)
        {
            const string Cut = "\n\nThis summary was longer than one message holds; the rest is on the factory's page.";
            var keep = body[..(MaxBodyLength - Cut.Length)];
            var lineEnd = keep.LastIndexOf('\n');
            body = (lineEnd > 0 ? keep[..lineEnd] : keep).TrimEnd() + Cut;
        }
        FileLog.Write($"[YourInboxStore] ForMorningSummary: factory={factory} boundary={boundaryUtc:o} bodyLength={body.Length}");
        return new YourInboxMessageEntity
        {
            TenantId = tenant.Value,
            Subject = subject,
            Body = body,
            FromKind = YourInboxWords.Senders.Factory,
            Factory = factory,
            Seat = MorningSeat,
            State = YourInboxWords.States.Unread,
            SentAtUtc = DateTime.SpecifyKind(boundaryUtc, DateTimeKind.Utc),
        };
    }

    /// <summary>Write a message built by <see cref="ForMorningSummary"/>, <see cref="ForNoFactoryProblems"/> or
    /// <see cref="ForScheduleFailure"/>, unread.</summary>
    public YourInboxMessageEntity Add(TenantId tenant, YourInboxMessageEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TenantId != tenant.Value)
            throw new ArgumentException($"The message belongs to another account than {tenant.ToLogString()}.", nameof(row));
        using (var ctx = _db.CreateContext(tenant))
        {
            ctx.YourInboxMessages.Add(row);
            ctx.SaveChanges();
        }
        FileLog.Write($"[YourInboxStore] Add: wrote {row.Id} factory={row.Factory ?? "(none)"} seat={row.Seat ?? "(none)"}");
        return row;
    }

    /// <summary>
    /// THE 08:00 MESSAGE ABOUT PROBLEMS NO FACTORY'S SUMMARY CARRIES (the Delivery Lead's ruling, Factory Inbox mission,
    /// phase 5): a problem whose schedule is in no factory - or in one that writes no morning summary, an archived one -
    /// was told to the owner by the factory email, and email off must not make it silent. From the Gateway, seat
    /// <see cref="YourInboxWords.MorningReportSeat"/>, at the slot's boundary, so <see cref="NoFactoryMorningOf"/> finds it.
    /// </summary>
    public static YourInboxMessageEntity ForNoFactoryProblems(TenantId tenant, string subject, string body, DateTime boundaryUtc)
    {
        var row = ForMorningSummary(tenant, "-", subject, body, boundaryUtc);
        row.FromKind = YourInboxWords.Senders.Gateway;
        row.Factory = null;
        row.Seat = YourInboxWords.MorningReportSeat;
        return row;
    }

    /// <summary>
    /// THE NOTICE THAT A SCHEDULE FAILED (issue #4436; Factory Inbox mission, "factories stop emailing"): it replaced the
    /// "Schedule failed" email, and no email is sent. When the schedule belongs to <paramref name="factory"/> it is sent
    /// from that factory, seat <see cref="YourInboxWords.ScheduleFailureSeat"/>, so a reply is left in that factory's
    /// Factory inbox, as a morning summary's is; when it belongs to no factory (null) it is sent from the Gateway, and
    /// nobody reads a reply to it. A subject or body over the limits is cut and says so.
    /// </summary>
    public static YourInboxMessageEntity ForScheduleFailure(TenantId tenant, string? factory, string subject, string body, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        subject = subject.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (subject.Length > MaxSubjectLength) subject = subject[..(MaxSubjectLength - 3)].TrimEnd() + "...";
        if (body.Length > MaxBodyLength)
        {
            const string Cut = "\n\nThis notice was longer than one message holds; every run is on the Schedule page.";
            body = body[..(MaxBodyLength - Cut.Length)].TrimEnd() + Cut;
        }
        var fromFactory = !string.IsNullOrWhiteSpace(factory);
        FileLog.Write($"[YourInboxStore] ForScheduleFailure: factory={(fromFactory ? factory : "(none)")} bodyLength={body.Length}");
        return new YourInboxMessageEntity
        {
            TenantId = tenant.Value,
            Subject = subject,
            Body = body,
            FromKind = fromFactory ? YourInboxWords.Senders.Factory : YourInboxWords.Senders.Gateway,
            Factory = fromFactory ? factory!.Trim() : null,
            Seat = YourInboxWords.ScheduleFailureSeat,
            State = YourInboxWords.States.Unread,
            SentAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
        };
    }

    /// <summary>The 08:00 message about problems in no factory already written for the boundary, or null.</summary>
    public YourInboxMessageEntity? NoFactoryMorningOf(TenantId tenant, DateTime boundaryUtc)
    {
        var at = DateTime.SpecifyKind(boundaryUtc, DateTimeKind.Utc);
        using var ctx = _db.CreateContext(tenant);
        return ctx.YourInboxMessages.AsNoTracking()
            .FirstOrDefault(m => m.FromKind == YourInboxWords.Senders.Gateway && m.Seat == YourInboxWords.MorningReportSeat && m.SentAtUtc == at);
    }

    /// <summary>The morning summary a factory was already sent for the 08:00 boundary, or null.</summary>
    public YourInboxMessageEntity? MorningOf(TenantId tenant, string factory, DateTime boundaryUtc)
    {
        var at = DateTime.SpecifyKind(boundaryUtc, DateTimeKind.Utc);
        using var ctx = _db.CreateContext(tenant);
        return ctx.YourInboxMessages.AsNoTracking()
            .FirstOrDefault(m => m.FromKind == YourInboxWords.Senders.Factory && m.Factory == factory
                                 && m.Seat == MorningSeat && m.SentAtUtc == at);
    }

    // ---------------------------------------------------------------- reads

    /// <summary>One message of the account, or null.</summary>
    public YourInboxMessageEntity? Get(TenantId tenant, Guid id)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.YourInboxMessages.AsNoTracking().FirstOrDefault(m => m.Id == id);
    }

    /// <summary>
    /// One view of the inbox, newest first, at most <paramref name="limit"/>; and how many the view holds in all. The
    /// inbox view is every message not archived; the archived view is the archived ones.
    /// </summary>
    public (IReadOnlyList<YourInboxMessageEntity> Messages, int Total) List(TenantId tenant, string view, int limit)
    {
        if (!YourInboxWords.Views.All.Contains(view))
            throw new ArgumentOutOfRangeException(nameof(view), view, "not a Your inbox view");
        using var ctx = _db.CreateContext(tenant);
        var query = view switch
        {
            YourInboxWords.Views.Unread => ctx.YourInboxMessages.AsNoTracking().Where(m => m.State == YourInboxWords.States.Unread),
            YourInboxWords.Views.Archived => ctx.YourInboxMessages.AsNoTracking().Where(m => m.State == YourInboxWords.States.Archived),
            _ => ctx.YourInboxMessages.AsNoTracking().Where(m => m.State != YourInboxWords.States.Archived),
        };
        var total = query.Count();
        // Ordered and cut in the database: the inbox is never read whole.
        var rows = query.OrderByDescending(m => m.SentAtUtc).ThenByDescending(m => m.Id).Take(limit).ToList();
        FileLog.Write($"[YourInboxStore] List: view={view} total={total} returned={rows.Count}");
        return (rows, total);
    }

    /// <summary>How many messages each view holds, for the counts on the screen's filters, in the order of
    /// <see cref="YourInboxWords.Views.All"/>.</summary>
    public IReadOnlyList<(string View, int Count)> ViewCounts(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var byState = ctx.YourInboxMessages.AsNoTracking().GroupBy(m => m.State)
            .Select(g => new { State = g.Key, Count = g.Count() }).ToList()
            .ToDictionary(x => x.State, x => x.Count);
        int Of(string state) => byState.TryGetValue(state, out var n) ? n : 0;
        var unread = Of(YourInboxWords.States.Unread);
        var read = Of(YourInboxWords.States.Read);
        var archived = Of(YourInboxWords.States.Archived);
        return YourInboxWords.Views.All.Select(v => (v, v switch
        {
            YourInboxWords.Views.Unread => unread,
            YourInboxWords.Views.Archived => archived,
            _ => unread + read,
        })).ToList();
    }

    /// <summary>How many messages are unread - the number on the Inbox entry.</summary>
    public int UnreadCount(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.YourInboxMessages.AsNoTracking().Count(m => m.State == YourInboxWords.States.Unread);
    }

    /// <summary>A message's replies, oldest first.</summary>
    public IReadOnlyList<YourInboxReplyEntity> Replies(TenantId tenant, Guid messageId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.YourInboxReplies.AsNoTracking().Where(r => r.MessageId == messageId)
            .OrderBy(r => r.SentAtUtc).ThenBy(r => r.Id).ToList();
    }

    // ---------------------------------------------------------------- the owner's changes

    /// <summary>
    /// Set a message's state: unread, read or archived. Every move between the three is allowed; setting the state it
    /// already has changes nothing and is not an error. Reading stamps the read time; archiving stamps the archive time,
    /// and moving it back to the inbox clears it. Null when the account holds no such message.
    /// </summary>
    public YourInboxMessageEntity? SetState(TenantId tenant, Guid id, string state, DateTime nowUtc)
    {
        if (!YourInboxWords.States.All.Contains(state))
            throw new YourInboxRefusedException("bad_state",
                $"The state must be one of {string.Join(", ", YourInboxWords.States.All)}, not '{state}'.");
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.YourInboxMessages.FirstOrDefault(m => m.Id == id);
        if (row is null) return null;
        if (row.State == state)
        {
            FileLog.Write($"[YourInboxStore] SetState: {id} is already {state}");
            return row;
        }
        var was = row.State;
        row.State = state;
        if (state == YourInboxWords.States.Read) row.ReadAtUtc = nowUtc;
        row.ArchivedAtUtc = state == YourInboxWords.States.Archived ? nowUtc : null;
        ctx.SaveChanges();
        FileLog.Write($"[YourInboxStore] SetState: {id} {was} -> {state}");
        return row;
    }

    /// <summary>
    /// Keep a reply the owner sent, once it has gone where it was routed; and mark the message read, because answering it
    /// is reading it. An archived message stays archived.
    /// </summary>
    public YourInboxReplyEntity AddReply(TenantId tenant, YourInboxMessageEntity message, string text, string routedTo,
        string? sessionId, string? factory, string? fleetMessageId, Guid? factoryMessageId, string sentBy, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (routedTo is not (YourInboxWords.ReplyTargets.Session or YourInboxWords.ReplyTargets.Factory))
            throw new ArgumentException($"'{routedTo}' is not a Your inbox reply target.", nameof(routedTo));
        var reply = new YourInboxReplyEntity
        {
            TenantId = tenant.Value,
            MessageId = message.Id,
            Text = text,
            RoutedTo = routedTo,
            SessionId = sessionId,
            Factory = factory,
            FleetMessageId = fleetMessageId,
            FactoryMessageId = factoryMessageId,
            SentBy = sentBy,
            SentAtUtc = nowUtc,
        };
        using var ctx = _db.CreateContext(tenant);
        ctx.YourInboxReplies.Add(reply);
        var row = ctx.YourInboxMessages.First(m => m.Id == message.Id);
        if (row.State == YourInboxWords.States.Unread)
        {
            row.State = YourInboxWords.States.Read;
            row.ReadAtUtc = nowUtc;
        }
        ctx.SaveChanges();
        FileLog.Write($"[YourInboxStore] AddReply: {reply.Id} on {message.Id} routed to {routedTo}");
        return reply;
    }

    /// <summary>Check a reply's text before it is routed anywhere. Throws <see cref="YourInboxRefusedException"/>.</summary>
    public static string CheckReply(string? text)
    {
        var value = text ?? "";
        if (string.IsNullOrWhiteSpace(value))
            throw new YourInboxRefusedException("reply_empty", "The reply is empty. Write what you want to say.");
        if (value.Length > MaxReplyLength)
            throw new YourInboxRefusedException("reply_too_long", $"The reply is {value.Length} characters; the limit is {MaxReplyLength}.");
        return value;
    }
}
