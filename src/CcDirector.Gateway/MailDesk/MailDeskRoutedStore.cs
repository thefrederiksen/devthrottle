using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// EVERY WRITE TO AN EMAIL ROUTED TO A SEAT (Mail Desk mission, phase 4 contract, section 3.1): one row per live
/// <c>route</c> or <c>mail-desk-agent</c> decision in <c>mail_desk_routed</c>, and nothing else writes that table.
///
/// The life of a row:
///  - created QUEUED (state <c>open</c>, no session) for its decision's seat, or the channel's Mail Desk seat;
///  - a start begun names it (<see cref="BeginStart"/>), and the start's answer either gives it the session
///    (<see cref="Take"/>), counts a failed start (<see cref="RecordStartFailure"/>), or leaves the name for the
///    router to settle (a start whose outcome was not known, <see cref="Adopt"/>);
///  - a session that ended without saying done or returning it queues it once more (<see cref="Reroute"/>);
///  - a seat that cannot take it sends it to the channel's Mail Desk seat (<see cref="Redirect"/>);
///  - the session says <see cref="Done"/>, or <see cref="Return"/>s it to the Mail Desk;
///  - and whatever cannot be dealt with goes to the owner (<see cref="HandToOwner"/>: state <c>returned</c>, with the
///    reason in <see cref="MailDeskRoutedEntity.OwnerReason"/>).
///
/// The decision's outcome is written in the SAME save as the row that caused it, so the two cannot disagree: a session
/// given the email makes it <c>seat-started</c>, and an email that never reached a seat and went to the owner makes it
/// <c>failed</c>. Every write records <see cref="MailDeskRoutedEntity.ChangedUtc"/>, and every change of state is
/// logged with ids and counts only - never a sender, a subject or a note.
/// </summary>
public sealed class MailDeskRoutedStore
{
    /// <summary>How many routed emails a read holds when the reader does not say.</summary>
    public const int DefaultList = 50;

    /// <summary>The most routed emails one read may hold.</summary>
    public const int MaxList = 500;

    /// <summary>The longest a start's name is kept - its column's bound.</summary>
    public const int MaxStartNameLength = 256;

    /// <summary>The longest a note or an owner reason is kept. A longer one is cut: it is a seat's words, and the row
    /// must still be written.</summary>
    public const int MaxNoteLength = 4000;

    /// <summary>How many times an email is given to a new session after one ended without saying done or returning
    /// it. The next time, it goes to the owner.</summary>
    public const int MaxReroutes = 1;

    /// <summary>The failed start that hands an email to the owner.</summary>
    public const int StartFailuresBeforeOwner = 3;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskRoutedStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- create ----------

    /// <summary>
    /// CREATE (section 3.2, step 1): one queued row for every decision of the account recorded in <c>live</c> mode
    /// that is a <c>route</c> (to its factory and seat) or <c>mail-desk-agent</c> (to its channel's own factory and
    /// seat), whose outcome is not set and which has no routed row. An observe decision never gets one. Oldest email
    /// first. A row another writer created first is left as it is. Returns the rows this call created.
    /// </summary>
    public IReadOnlyList<MailDeskRoutedEntity> CreateMissing(TenantId tenant, DateTime nowUtc)
    {
        var created = new List<MailDeskRoutedEntity>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var waiting = ctx.MailDeskDecisions.AsNoTracking()
                .Where(d => d.Mode == MailDeskWords.Modes.Live
                            && (d.Decision == MailDeskWords.Decisions.Route || d.Decision == MailDeskWords.Decisions.MailDeskAgent)
                            && d.Outcome == null
                            && !ctx.MailDeskRouted.Any(r => r.DecisionId == d.Id))
                .OrderBy(d => d.ReceivedUtc).ThenBy(d => d.Id)
                .ToList();
            if (waiting.Count == 0) return created;

            var channels = ctx.MailDeskChannels.AsNoTracking().ToDictionary(c => c.Id);
            foreach (var decision in waiting)
            {
                if (!channels.TryGetValue(decision.ChannelId, out var channel))
                    throw new InvalidOperationException($"Mail Desk decision {decision.Id} names channel {decision.ChannelId}, which is not a channel of this account.");
                var (factory, seat) = decision.Decision == MailDeskWords.Decisions.Route
                    ? (decision.RouteFactory!, decision.RouteSeat!)
                    : (channel.Factory, channel.Seat);
                var row = new MailDeskRoutedEntity
                {
                    TenantId = tenant.Value,
                    DecisionId = decision.Id,
                    ChannelId = decision.ChannelId,
                    Factory = factory,
                    Seat = seat,
                    State = MailDeskWords.RoutedStates.Open,
                    CreatedUtc = nowUtc,
                    ChangedUtc = nowUtc,
                };
                ctx.MailDeskRouted.Add(row);
                try
                {
                    ctx.SaveChanges();
                    created.Add(row);
                    FileLog.Write($"[MailDeskRoutedStore] CreateMissing: {row.Id} for decision {decision.Id} ({decision.Decision}) queued for {factory}/{seat}");
                }
                catch (DbUpdateException ex)
                {
                    ctx.ChangeTracker.Clear();
                    // Another replica routed the same decision first: the unique index refused this one.
                    if (ctx.MailDeskRouted.AsNoTracking().Any(r => r.DecisionId == decision.Id)) continue;
                    FileLog.Write($"[MailDeskRoutedStore] CreateMissing FAILED for decision {decision.Id} and no other writer holds it: {ex.Message}");
                    throw;
                }
            }
        }
        FileLog.Write($"[MailDeskRoutedStore] CreateMissing: account={tenant.ToLogString()}, created={created.Count}");
        return created;
    }

    // ---------- reads ----------

    /// <summary>One routed email, or null when the account has no such row.</summary>
    public MailDeskRoutedEntity? Get(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskRouted.AsNoTracking().FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>The routed row of one decision, or null when it was never routed.</summary>
    public MailDeskRoutedEntity? ForDecision(TenantId tenant, Guid decisionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskRouted.AsNoTracking().FirstOrDefault(r => r.DecisionId == decisionId);
        }
    }

    /// <summary>Every <c>open</c> row of the account - queued or with a session - oldest first.</summary>
    public IReadOnlyList<MailDeskRoutedEntity> Open(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskRouted.AsNoTracking()
                .Where(r => r.State == MailDeskWords.RoutedStates.Open)
                .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id)
                .ToList();
        }
    }

    /// <summary>
    /// Every row of one seat that a start was ever made for, newest start first, whatever its state - what decides
    /// whether the newest session the Mail Desk started for the seat may still be alive (section 1, "A busy seat").
    /// </summary>
    public IReadOnlyList<MailDeskRoutedEntity> StartsOf(TenantId tenant, string factory, string seat)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskRouted.AsNoTracking()
                .Where(r => r.Factory == factory && r.Seat == seat && r.StartedUtc != null)
                .OrderByDescending(r => r.StartedUtc).ThenBy(r => r.Id)
                .ToList();
        }
    }

    /// <summary>
    /// The routed emails newest first, every filter applied (contract section 4.1, <c>GET /routed</c>): one state,
    /// queued only (open with no session), one factory, one seat. The total is over everything that matches, not only
    /// the page.
    /// </summary>
    public MailDeskRoutedList List(TenantId tenant, MailDeskRoutedFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.N is < 1 or > MaxList)
            throw new ArgumentOutOfRangeException(nameof(filter), filter.N, $"a routed read holds 1 to {MaxList} emails");
        if (filter.State is not null && !MailDeskWords.IsOneOf(filter.State, MailDeskWords.RoutedStates.All))
            throw new ArgumentException($"'{filter.State}' is not a routed state; it is one of {MailDeskWords.InWords(MailDeskWords.RoutedStates.All)}.", nameof(filter));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.MailDeskRouted.AsNoTracking();
            if (filter.State is not null) rows = rows.Where(r => r.State == filter.State);
            if (filter.Queued) rows = rows.Where(r => r.State == MailDeskWords.RoutedStates.Open && r.SessionId == null);
            if (filter.Factory is not null) rows = rows.Where(r => r.Factory == filter.Factory);
            if (filter.Seat is not null) rows = rows.Where(r => r.Seat == filter.Seat);
            var total = rows.Count();
            var page = rows.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id).Take(filter.N).ToList();
            return new MailDeskRoutedList(page, total);
        }
    }

    // ---------- the start ----------

    /// <summary>
    /// A START IS BEGUN for these queued rows: each takes the start's exact name and time BEFORE the start is sent, so a
    /// start whose outcome is not known - or one a stopped Gateway left behind - is found by that name afterwards and
    /// never started twice (section 3.4). Only rows still open, queued and with no start are named; the rows named are
    /// returned.
    /// </summary>
    public IReadOnlyList<MailDeskRoutedEntity> BeginStart(TenantId tenant, IReadOnlyCollection<Guid> ids, string startName,
        DateTime startedUtc, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(startName);
        if (startName.Length > MaxStartNameLength)
            throw new ArgumentException($"A start's name takes at most {MaxStartNameLength} characters; this one is {startName.Length}.", nameof(startName));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = Load(ctx, ids).Where(IsQueuedWithNoStart).ToList();
            foreach (var row in rows)
            {
                row.StartName = startName;
                row.StartedUtc = startedUtc;
                row.ChangedUtc = nowUtc;
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] BeginStart: {rows.Count} of {ids.Count} rows named for a start at {startedUtc:O}: {Ids(rows)}");
            return rows;
        }
    }

    /// <summary>
    /// THE START GAVE A SESSION: every open row still waiting on the start named <paramref name="startName"/> takes the
    /// session, and each decision's outcome becomes <c>seat-started</c> with "session &lt;id&gt; on &lt;machine&gt;
    /// (&lt;factory&gt;/&lt;seat&gt;)". The same write adopts a session found by name after a start whose outcome was
    /// not known (section 3.2, step 2). Returns the rows given the session.
    /// </summary>
    public IReadOnlyList<MailDeskRoutedEntity> Take(TenantId tenant, string startName, string sessionId, string machine, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.MailDeskRouted
                .Where(r => r.State == MailDeskWords.RoutedStates.Open && r.SessionId == null && r.StartName == startName)
                .ToList();
            foreach (var row in rows)
            {
                row.SessionId = sessionId;
                row.ChangedUtc = nowUtc;
                if (ctx.MailDeskDecisions.FirstOrDefault(d => d.Id == row.DecisionId) is { } decision)
                {
                    decision.Outcome = MailDeskWords.Outcomes.SeatStarted;
                    decision.OutcomeDetail = Cut($"session {sessionId} on {machine} ({row.Factory}/{row.Seat})", MailDeskDecisionStore.MaxReasonLength);
                }
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] Take: session {sessionId} on {machine} took {rows.Count} rows: {Ids(rows)}");
            return rows;
        }
    }

    /// <summary>The same write as <see cref="Take"/>, named for what the router does with it: a session found by the
    /// exact name of a start whose outcome was not known is adopted (section 3.2, step 2).</summary>
    public IReadOnlyList<MailDeskRoutedEntity> Adopt(TenantId tenant, string startName, string sessionId, string machine, DateTime nowUtc)
        => Take(tenant, startName, sessionId, machine, nowUtc);

    /// <summary>
    /// A START FOR THIS ROW FAILED OUTRIGHT (section 3.4): its failures are counted and its start name cleared, so it
    /// is queued again. The third failure hands it to the owner with "its session could not be started 3 times:
    /// &lt;error&gt;", and the decision's outcome becomes <c>failed</c> with that reason. A row no longer open is left
    /// as it is. Returns the row as it now stands, or null when there is no such row.
    /// </summary>
    public MailDeskRoutedEntity? RecordStartFailure(TenantId tenant, Guid id, string error, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (row is null || row.State != MailDeskWords.RoutedStates.Open || row.SessionId is not null) return row;
            row.StartFailures++;
            row.StartName = null;
            row.StartedUtc = null;
            row.ChangedUtc = nowUtc;
            if (row.StartFailures >= StartFailuresBeforeOwner)
            {
                ToOwner(ctx, row, MailDeskRoutedReasons.StartFailedTimes(row.StartFailures, error), failDecision: true, nowUtc);
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] RecordStartFailure: {row.Id}, failures={row.StartFailures}, state={row.State}");
            return row;
        }
    }

    // ---------- the router's other writes ----------

    /// <summary>
    /// AN ORPHAN (section 3.2, step 3): the session this open row is with ended without saying done or returning it.
    /// The first time it is queued again for the same seat (<see cref="MailDeskRoutedEntity.RerouteCount"/> 1); the
    /// second time it is handed to the owner, "its session ended twice without saying done or returning it". A row
    /// that is no longer open, or is not with a session, is left as it is. Returns the row as it now stands.
    /// </summary>
    public MailDeskRoutedEntity? Reroute(TenantId tenant, Guid id, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (row is null || row.State != MailDeskWords.RoutedStates.Open || row.SessionId is null) return row;
            var ended = row.SessionId;
            if (row.RerouteCount >= MaxReroutes)
            {
                ToOwner(ctx, row, MailDeskRoutedReasons.SessionEndedTwice, failDecision: false, nowUtc);
            }
            else
            {
                row.RerouteCount++;
                Queue(row, nowUtc);
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] Reroute: {row.Id}, its session {ended} ended without done or return; reroutes={row.RerouteCount}, state={row.State}");
            return row;
        }
    }

    /// <summary>
    /// THE SEAT CANNOT TAKE IT (section 3.4): its factory has no published definition, the version cannot be read, or
    /// it holds no such seat. A row not at its channel's Mail Desk seat is sent there, queued, with the reason as its
    /// return note - not counted as a return. A row already at the Mail Desk seat is handed to the owner with the
    /// reason, and the decision's outcome becomes <c>failed</c>. Returns the row as it now stands.
    /// </summary>
    public MailDeskRoutedEntity? Redirect(TenantId tenant, Guid id, string reason, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (row is null || row.State != MailDeskWords.RoutedStates.Open || row.SessionId is not null) return row;
            var channel = ChannelOf(ctx, row);
            var from = $"{row.Factory}/{row.Seat}";
            if (IsMailDeskSeat(row, channel))
            {
                ToOwner(ctx, row, Cut(reason, MaxNoteLength), failDecision: true, nowUtc);
            }
            else
            {
                row.Factory = channel.Factory;
                row.Seat = channel.Seat;
                row.ReturnNote = Cut(reason, MaxNoteLength);
                Queue(row, nowUtc);
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] Redirect: {row.Id} could not go to {from}; now state={row.State} at {row.Factory}/{row.Seat}");
            return row;
        }
    }

    /// <summary>
    /// HAND IT TO THE OWNER (contract section 1, "With the owner"): state <c>returned</c> with the reason. When
    /// <paramref name="failDecision"/>, the email never reached a seat, and the decision's outcome becomes
    /// <c>failed</c> with the reason. A row no longer open is left as it is. Returns the row as it now stands.
    /// </summary>
    public MailDeskRoutedEntity? HandToOwner(TenantId tenant, Guid id, string reason, bool failDecision, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (row is null || row.State != MailDeskWords.RoutedStates.Open) return row;
            ToOwner(ctx, row, Cut(reason, MaxNoteLength), failDecision, nowUtc);
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] HandToOwner: {row.Id}");
            return row;
        }
    }

    // ---------- the session's two answers ----------

    /// <summary>
    /// <c>maildesk done</c> (section 4.1): only the session the row is with now may say it. Refused, with nothing
    /// written: no such row; a caller that is not that session (a person, another session, a session it was with
    /// before a re-route, a machine - all pass their session id or null); a row not open. Otherwise the row is
    /// <c>done</c> with the note, if any.
    /// </summary>
    public MailDeskRoutedAnswer Done(TenantId tenant, Guid id, string? callerSessionId, string? note, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (Refuse(row, callerSessionId) is { } refused) return Log("Done", id, refused);
            row!.State = MailDeskWords.RoutedStates.Done;
            row.DoneNote = string.IsNullOrWhiteSpace(note) ? null : Cut(note.Trim(), MaxNoteLength);
            row.ChangedUtc = nowUtc;
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] Done: {row.Id} by session {callerSessionId}");
            return MailDeskRoutedAnswer.Written(row);
        }
    }

    /// <summary>
    /// <c>maildesk return</c> (section 4.1): only the session the row is with now may return it, and only with a note.
    /// The first return from a seat that is not the channel's Mail Desk seat queues it for the Mail Desk seat with the
    /// note (<see cref="MailDeskRoutedEntity.ReturnedCount"/> 1). A second return, or a return by the Mail Desk agent
    /// itself, hands it to the owner: "returned twice: &lt;note&gt;" or "the Mail Desk agent returned it: &lt;note&gt;".
    /// Every refusal writes nothing.
    /// </summary>
    public MailDeskRoutedAnswer Return(TenantId tenant, Guid id, string? callerSessionId, string? note, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(note))
            return Log("Return", id, MailDeskRoutedAnswer.Refused(MailDeskRoutedRefusal.NoteRequired, "A return says what you know about the email: give a note."));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id);
            if (Refuse(row, callerSessionId) is { } refused) return Log("Return", id, refused);
            var channel = ChannelOf(ctx, row!);
            var said = Cut(note.Trim(), MaxNoteLength);
            row!.ReturnedCount++;
            row.ReturnNote = said;
            if (IsMailDeskSeat(row, channel))
            {
                ToOwner(ctx, row, Cut(MailDeskRoutedReasons.MailDeskAgentReturned(said), MaxNoteLength), failDecision: false, nowUtc);
            }
            else if (row.ReturnedCount > 1)
            {
                ToOwner(ctx, row, Cut(MailDeskRoutedReasons.ReturnedTwice(said), MaxNoteLength), failDecision: false, nowUtc);
            }
            else
            {
                row.Factory = channel.Factory;
                row.Seat = channel.Seat;
                Queue(row, nowUtc);
            }
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskRoutedStore] Return: {row.Id} by session {callerSessionId}; returned={row.ReturnedCount}, state={row.State} at {row.Factory}/{row.Seat}");
            return MailDeskRoutedAnswer.Written(row);
        }
    }

    // ---------- helpers ----------

    private static MailDeskRoutedAnswer? Refuse(MailDeskRoutedEntity? row, string? callerSessionId)
    {
        if (row is null)
            return MailDeskRoutedAnswer.Refused(MailDeskRoutedRefusal.NoSuchRoutedEmail, "There is no routed email with that id in this account.");
        if (string.IsNullOrEmpty(callerSessionId) || !string.Equals(row.SessionId, callerSessionId, StringComparison.OrdinalIgnoreCase))
            return MailDeskRoutedAnswer.Refused(MailDeskRoutedRefusal.NotYourEmail, MailDeskRoutedReasons.WhoHolds(row));
        if (row.State != MailDeskWords.RoutedStates.Open)
            return MailDeskRoutedAnswer.Refused(MailDeskRoutedRefusal.NotOpen, MailDeskRoutedReasons.NotOpen(row));
        return null;
    }

    private static MailDeskRoutedAnswer Log(string what, Guid id, MailDeskRoutedAnswer answer)
    {
        FileLog.Write($"[MailDeskRoutedStore] {what} REFUSED: {id}, {answer.Refusal}");
        return answer;
    }

    private static void ToOwner(GatewayDbContext ctx, MailDeskRoutedEntity row, string reason, bool failDecision, DateTime nowUtc)
    {
        row.State = MailDeskWords.RoutedStates.Returned;
        row.OwnerReason = reason;
        row.StartName = null;
        row.ChangedUtc = nowUtc;
        if (failDecision && ctx.MailDeskDecisions.FirstOrDefault(d => d.Id == row.DecisionId) is { } decision)
        {
            decision.Outcome = MailDeskWords.Outcomes.Failed;
            decision.OutcomeDetail = Cut(reason, MailDeskDecisionStore.MaxReasonLength);
        }
        FileLog.Write($"[MailDeskRoutedStore] ToOwner: {row.Id} handed to the owner (decision {row.DecisionId}, failed decision={failDecision})");
    }

    /// <summary>Back to waiting for a session of its seat: no session, no start.</summary>
    private static void Queue(MailDeskRoutedEntity row, DateTime nowUtc)
    {
        row.SessionId = null;
        row.StartName = null;
        row.StartedUtc = null;
        row.ChangedUtc = nowUtc;
    }

    private static bool IsQueuedWithNoStart(MailDeskRoutedEntity r)
        => r.State == MailDeskWords.RoutedStates.Open && r.SessionId is null && r.StartName is null;

    private static bool IsMailDeskSeat(MailDeskRoutedEntity row, MailDeskChannelEntity channel)
        => string.Equals(row.Factory, channel.Factory, StringComparison.Ordinal) && string.Equals(row.Seat, channel.Seat, StringComparison.Ordinal);

    private static MailDeskChannelEntity ChannelOf(GatewayDbContext ctx, MailDeskRoutedEntity row)
        => ctx.MailDeskChannels.AsNoTracking().FirstOrDefault(c => c.Id == row.ChannelId)
           ?? throw new InvalidOperationException($"Routed email {row.Id} names channel {row.ChannelId}, which is not a channel of this account.");

    private static List<MailDeskRoutedEntity> Load(GatewayDbContext ctx, IReadOnlyCollection<Guid> ids)
    {
        // One read per id: a list Contains is translated differently by the two providers on this runtime.
        var rows = new List<MailDeskRoutedEntity>(ids.Count);
        foreach (var id in ids.Distinct())
            if (ctx.MailDeskRouted.FirstOrDefault(r => r.Id == id) is { } row) rows.Add(row);
        return rows;
    }

    private static string Ids(IEnumerable<MailDeskRoutedEntity> rows) => string.Join(",", rows.Select(r => r.Id.ToString("D")));

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>The reasons and sentences of routed emails, held once.</summary>
public static class MailDeskRoutedReasons
{
    /// <summary>Why an orphan went to the owner the second time.</summary>
    public const string SessionEndedTwice = "its session ended twice without saying done or returning it";

    /// <summary>Why an email whose session could not be started went to the owner.</summary>
    public static string StartFailedTimes(int times, string error) => $"its session could not be started {times} times: {error}";

    /// <summary>Why an email returned a second time went to the owner.</summary>
    public static string ReturnedTwice(string note) => $"returned twice: {note}";

    /// <summary>Why an email the Mail Desk agent returned went to the owner.</summary>
    public static string MailDeskAgentReturned(string note) => $"the Mail Desk agent returned it: {note}";

    /// <summary>The sentence of a refusal to a caller the email is not with: who holds it now.</summary>
    public static string WhoHolds(MailDeskRoutedEntity row) => row.State switch
    {
        MailDeskWords.RoutedStates.Done => $"This email was done by session {row.SessionId}; only the session it is with may answer for it.",
        MailDeskWords.RoutedStates.Returned => "This email is with the owner; only the session it is with may answer for it.",
        _ => row.SessionId is null
            ? $"This email is queued for {row.Factory}/{row.Seat} and is with no session yet; only the session it is with may answer for it."
            : $"This email is with session {row.SessionId}; only that session may answer for it.",
    };

    /// <summary>The sentence of a refusal for a row that is not open.</summary>
    public static string NotOpen(MailDeskRoutedEntity row) => row.State == MailDeskWords.RoutedStates.Done
        ? "This email is already done."
        : $"This email is with the owner: {row.OwnerReason}";
}

/// <summary>Why <see cref="MailDeskRoutedStore.Done"/> or <see cref="MailDeskRoutedStore.Return"/> wrote nothing.</summary>
public enum MailDeskRoutedRefusal
{
    None,
    /// <summary>No such row in the account (404, <c>no_such_routed_email</c>).</summary>
    NoSuchRoutedEmail,
    /// <summary>The caller is not the session the row is with now (403, <c>not_your_email</c>).</summary>
    NotYourEmail,
    /// <summary>The row is done or with the owner (409, <c>not_open</c>).</summary>
    NotOpen,
    /// <summary>A return without a note (400, <c>note_required</c>).</summary>
    NoteRequired,
}

/// <summary>What a session's answer came to: the row as written, or the refusal and its sentence.</summary>
public sealed record MailDeskRoutedAnswer(MailDeskRoutedRefusal Refusal, MailDeskRoutedEntity? Row, string? Detail)
{
    public bool Succeeded => Refusal == MailDeskRoutedRefusal.None;
    public static MailDeskRoutedAnswer Written(MailDeskRoutedEntity row) => new(MailDeskRoutedRefusal.None, row, null);
    public static MailDeskRoutedAnswer Refused(MailDeskRoutedRefusal refusal, string detail) => new(refusal, null, detail);
}

/// <summary>The filters of a routed read (section 4.1, <c>GET /routed</c>). Null is "any".</summary>
public sealed record MailDeskRoutedFilter(
    string? State = null,
    bool Queued = false,
    string? Factory = null,
    string? Seat = null,
    int N = MailDeskRoutedStore.DefaultList);

/// <summary>One page of routed emails, newest first, with the total of everything matching.</summary>
public sealed record MailDeskRoutedList(IReadOnlyList<MailDeskRoutedEntity> Routed, int Total);
