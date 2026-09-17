using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>What happened when an answer was offered for a record.</summary>
public enum FleetOutcomeAnswerStatus
{
    /// <summary>The record was open and is now answered.</summary>
    Answered,

    /// <summary>This account holds no record with that id.</summary>
    NotFound,

    /// <summary>The record was already answered - before this call, or by another caller that won the race while
    /// this call was running. The first answer stands and nothing was changed.</summary>
    AlreadyAnswered,
}

/// <summary>The result of <see cref="FleetOutcomeStore.Answer"/>: what happened, and the record as it now is
/// (null only when it was not found).</summary>
public sealed record FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus Status, FleetOutcomeDto? Outcome);

/// <summary>What happened when advice or an owner's note was offered for a record (step 7).</summary>
public enum FleetOutcomeUpdateStatus
{
    /// <summary>The record was open and now carries the new value.</summary>
    Updated,

    /// <summary>This account holds no record with that id.</summary>
    NotFound,

    /// <summary>The record is answered, so it is not changed: an answered record is the owner's settled word.</summary>
    AlreadyAnswered,
}

/// <summary>The result of <see cref="FleetOutcomeStore.SetAdvice"/> and <see cref="FleetOutcomeStore.NoteOwnerAction"/>:
/// what happened, and the record as it now is (null only when it was not found).</summary>
public sealed record FleetOutcomeUpdateResult(FleetOutcomeUpdateStatus Status, FleetOutcomeDto? Outcome);

/// <summary>One page of <see cref="FleetOutcomeStore.ListPage"/>: the records, and the opaque cursor that continues
/// after the last of them - null when no record remains.</summary>
public sealed record FleetOutcomePage(IReadOnlyList<FleetOutcomeDto> Outcomes, string? NextCursor);

/// <summary>
/// The durable record of the news the Fleet Manager brought the owner - READY, FINDING, DECISION - over the
/// <c>fleet_outcomes</c> table (the Fleet Manager mission, step 3).
///
/// A RECORD STAYS OPEN UNTIL THE OWNER ANSWERS IT, across a Gateway restart and across a restart or a move of
/// the Fleet Manager itself. It belongs to the account: nothing here reads or filters by the session that
/// filed it, so a new Fleet Manager session sees and answers what the old one filed.
///
/// AN ANSWER IS FINAL, ACROSS EVERY GATEWAY INSTANCE. The answer is written by ONE conditional update - only
/// where the record is still open - and the affected-row count decides who won. Two callers on two instances
/// over the same database cannot both succeed: exactly one update touches the row, and the other is told the
/// record was already answered. No in-process lock is relied on for this.
///
/// NO FALLBACKS. Every field is checked when the record is filed, and a missing or wrong one is refused with
/// a message that names the field and the values it accepts. A record the Cockpit could not draw is never
/// stored.
///
/// TENANT-PARTITIONED BY CONSTRUCTION, the <see cref="Wingman.TurnVerdictStore"/> shape: every method takes
/// the tenant explicitly and opens its context with <see cref="GatewayDatabase.CreateContext(TenantId)"/>, so
/// another account's record is never found at all.
/// </summary>
public sealed class FleetOutcomeStore
{
    public const string KindReady = "ready";
    public const string KindFinding = "finding";
    public const string KindDecision = "decision";

    public const string StatusOpen = "open";
    public const string StatusAnswered = "answered";

    /// <summary>The filter word that asks for both statuses.</summary>
    public const string StatusAll = "all";

    /// <summary>The caller recorded when a person's device, not a session, filed or answered.</summary>
    public const string OwnerCaller = "owner";

    /// <summary>Who gave an answer: the owner, through their own signed-in device.</summary>
    public const string RoleOwner = "owner";

    /// <summary>Who gave an answer: the account's Fleet Manager session, relaying the owner's word.</summary>
    public const string RoleFleetManager = "fleet-manager";

    /// <summary>The longest title accepted. A title is one line naming the news.</summary>
    public const int MaxTitleLength = 300;

    /// <summary>The longest free-text field accepted (an answer, a reason, a question, how it was tested).</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The longest line of Fleet Manager advice accepted (step 7). Advice is ONE line beside the Wingman's
    /// reading, never a paragraph.</summary>
    public const int MaxAdviceLength = 300;

    /// <summary>The most options or links one record may carry.</summary>
    public const int MaxListItems = 20;

    /// <summary>The page when the caller names no count, and the most one read returns.</summary>
    public const int DefaultCount = 50;
    public const int MaxCount = 200;

    public static readonly IReadOnlyList<string> Kinds = new[] { KindReady, KindFinding, KindDecision };
    public static readonly IReadOnlyList<string> Statuses = new[] { StatusOpen, StatusAnswered, StatusAll };
    public static readonly IReadOnlyList<string> Risks = new[] { "low", "medium", "high" };
    public static readonly IReadOnlyList<string> Checks = new[] { "passed", "failed", "none" };
    public static readonly IReadOnlyList<string> AnswerRoles = new[] { RoleOwner, RoleFleetManager };

    private static readonly JsonSerializerOptions DetailsJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetOutcomeStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// File one record and return it as stored.
    /// </summary>
    /// <param name="filedBy">The calling session id, or <see cref="OwnerCaller"/>.</param>
    /// <exception cref="ArgumentException">A field is missing or wrong; the message says which and what is
    /// accepted.</exception>
    public FleetOutcomeDto File(TenantId tenant, FleetOutcomeFileRequest request, string filedBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetOutcomeStore] File: tenant={tenant}, kind={request?.Kind}, filedBy={filedBy}");
        try
        {
            if (request is null)
                throw new ArgumentException("a body is required: { kind, title, ... }");
            RequireCaller(filedBy, "filedBy");

            var kind = RequireOneOf(request.Kind, Kinds, "kind");
            var title = RequireLine(request.Title, "title", MaxTitleLength);
            var about = OptionalSessionId(request.SessionId);
            var details = ValidateDetails(kind, request);
            string? advice = null;
            string? pick = null;
            if (request.Advice is not null || request.FleetManagerPick is not null)
            {
                if (request.Advice is null)
                    throw new ArgumentException(
                        "fleetManagerPick is set together with advice; give the one line of advice that goes with the pick");
                advice = RequireAdvice(request.Advice);
                pick = OptionalPick(request.FleetManagerPick);
            }

            var entity = new FleetOutcomeEntity
            {
                Kind = kind,
                FiledBy = filedBy,
                AboutSessionId = about,
                CreatedAtUtc = Utc(nowUtc),
                Title = title,
                DetailsJson = JsonSerializer.Serialize(details, DetailsJsonOptions),
                Status = StatusOpen,
                Advice = advice,
                FleetManagerPick = pick,
                AdviceSetAtUtc = advice is null ? null : Utc(nowUtc),
            };

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetOutcomes.Add(entity);
                ctx.SaveChanges();
            }

            FileLog.Write($"[FleetOutcomeStore] File: stored id={entity.Id}, kind={kind}, tenant={tenant}");
            return ToDto(entity);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetOutcomeStore] File FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>One record of this account, or null when the account holds none with that id.</summary>
    public FleetOutcomeDto? Get(TenantId tenant, Guid id)
    {
        FileLog.Write($"[FleetOutcomeStore] Get: tenant={tenant}, id={id}");
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FleetOutcomes.AsNoTracking().FirstOrDefault(o => o.Id == id);
        FileLog.Write($"[FleetOutcomeStore] Get: id={id}, found={row is not null}");
        return row is null ? null : ToDto(row);
    }

    /// <summary>
    /// The first page of this account's records, newest first - <see cref="ListPage"/> with no cursor.
    /// </summary>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values.</exception>
    public IReadOnlyList<FleetOutcomeDto> List(TenantId tenant, string status, string? kind, int count)
        => ListPage(tenant, status, kind, count, cursor: null).Outcomes;

    /// <summary>
    /// One page of this account's records, newest first, and the cursor that continues after it.
    ///
    /// EVERY RECORD IS REACHABLE. The order is <c>(CreatedAtUtc, Id)</c> descending - unique, because the id is -
    /// and a cursor names the last record a page returned, so the next page starts strictly after it. There is
    /// no offset: a record answered between pages (which leaves an <c>open</c> list) moves no other record, so
    /// nothing after the cursor is skipped and nothing before it comes round again. A record filed after the
    /// first page is newer than every cursor and so is not in later pages; listing again from the start shows it.
    /// </summary>
    /// <param name="status"><c>open</c>, <c>answered</c> or <c>all</c>.</param>
    /// <param name="kind">One kind, or null for every kind.</param>
    /// <param name="count">How many at most, 1 to <see cref="MaxCount"/>.</param>
    /// <param name="cursor">The <see cref="FleetOutcomePage.NextCursor"/> of the page before, or null for the first.</param>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values, or the cursor is not one
    /// this Gateway issued.</exception>
    public FleetOutcomePage ListPage(TenantId tenant, string status, string? kind, int count, string? cursor)
    {
        FileLog.Write($"[FleetOutcomeStore] ListPage: tenant={tenant}, status={status}, kind={kind}, count={count}, "
                      + $"cursor={(cursor is null ? "none" : "given")}");
        var wantedStatus = RequireOneOf(status, Statuses, "status");
        var wantedKind = kind is null ? null : RequireOneOf(kind, Kinds, "kind");
        if (count < 1 || count > MaxCount)
            throw new ArgumentException($"count must be between 1 and {MaxCount}, got {count}");
        var after = cursor is null ? ((DateTime CreatedAtUtc, Guid Id)?)null : DecodeCursor(cursor);

        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetOutcomes.AsNoTracking();
        if (wantedStatus != StatusAll) query = query.Where(o => o.Status == wantedStatus);
        if (wantedKind is not null) query = query.Where(o => o.Kind == wantedKind);
        if (after is { } a)
        {
            var at = a.CreatedAtUtc;
            var id = a.Id;
            query = query.Where(o => o.CreatedAtUtc < at || (o.CreatedAtUtc == at && o.Id.CompareTo(id) < 0));
        }
        // One more than asked, so the page knows whether any record remains after it.
        var rows = query.OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id).Take(count + 1).ToList();
        var more = rows.Count > count;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = more ? EncodeCursor(rows[^1]) : null;

        FileLog.Write($"[FleetOutcomeStore] ListPage: returned={rows.Count}, hasMore={more}");
        return new FleetOutcomePage(rows.Select(ToDto).ToList(), next);
    }

    private const string CursorVersion = "v1";

    private static string EncodeCursor(FleetOutcomeEntity last)
    {
        var text = $"{CursorVersion}:{Utc(last.CreatedAtUtc).Ticks}:{last.Id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (DateTime CreatedAtUtc, Guid Id) DecodeCursor(string cursor)
    {
        var refusal = $"cursor '{cursor}' is not one this Gateway issued; list again without a cursor to start from the newest";
        var b64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            throw new ArgumentException(refusal);
        }
        var parts = text.Split(':');
        if (parts.Length != 3 || parts[0] != CursorVersion
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[2], "N", out var id))
            throw new ArgumentException(refusal);
        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }

    /// <summary>How many records of this account match the filter, counted by the database - never from a
    /// capped list, so a reader of one page can say how many there are in all.</summary>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values.</exception>
    public int Count(TenantId tenant, string status, string? kind)
    {
        FileLog.Write($"[FleetOutcomeStore] Count: tenant={tenant}, status={status}, kind={kind}");
        var wantedStatus = RequireOneOf(status, Statuses, "status");
        var wantedKind = kind is null ? null : RequireOneOf(kind, Kinds, "kind");

        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetOutcomes.AsNoTracking();
        if (wantedStatus != StatusAll) query = query.Where(o => o.Status == wantedStatus);
        if (wantedKind is not null) query = query.Where(o => o.Kind == wantedKind);
        var total = query.Count();
        FileLog.Write($"[FleetOutcomeStore] Count: total={total}");
        return total;
    }

    /// <summary>
    /// EVERY open record of this account, newest first, with NO cap - what the digest serves, so a Fleet Manager
    /// reset or moved rebuilds the whole of the outstanding work and never a newest slice of it. An open record
    /// stays open only until the owner answers it, so this list is the owner's outstanding work, not history.
    /// </summary>
    public IReadOnlyList<FleetOutcomeDto> ListOpen(TenantId tenant)
    {
        FileLog.Write($"[FleetOutcomeStore] ListOpen: tenant={tenant}");
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetOutcomes.AsNoTracking()
            .Where(o => o.Status == StatusOpen)
            .OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id)
            .ToList();
        FileLog.Write($"[FleetOutcomeStore] ListOpen: returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>
    /// This account's records ANSWERED at or after <paramref name="sinceUtc"/>, newest answer first, at most
    /// <paramref name="max"/> - what the digest carries so the Fleet Manager learns what the owner decided (step 7),
    /// including answers given in the walkthrough, which are not typed to it.
    /// </summary>
    public IReadOnlyList<FleetOutcomeDto> ListAnsweredSince(TenantId tenant, DateTime sinceUtc, int max)
    {
        FileLog.Write($"[FleetOutcomeStore] ListAnsweredSince: tenant={tenant}, since={sinceUtc:O}, max={max}");
        if (max < 1 || max > MaxCount)
            throw new ArgumentException($"max must be between 1 and {MaxCount}, got {max}");
        var since = Utc(sinceUtc);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetOutcomes.AsNoTracking()
            .Where(o => o.Status == StatusAnswered && o.AnsweredAtUtc >= since)
            .OrderByDescending(o => o.AnsweredAtUtc).ThenByDescending(o => o.Id)
            .Take(max)
            .ToList();
        FileLog.Write($"[FleetOutcomeStore] ListAnsweredSince: returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>This account's OPEN records by kind, counted by the database.</summary>
    public FleetOutcomeCounts CountOpen(TenantId tenant)
    {
        FileLog.Write($"[FleetOutcomeStore] CountOpen: tenant={tenant}");
        using var ctx = _db.CreateContext(tenant);
        var byKind = ctx.FleetOutcomes.AsNoTracking()
            .Where(o => o.Status == StatusOpen)
            .GroupBy(o => o.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToList();
        int Of(string kind) => byKind.Where(k => k.Kind == kind).Sum(k => k.Count);
        var counts = new FleetOutcomeCounts
        {
            Ready = Of(KindReady),
            Finding = Of(KindFinding),
            Decision = Of(KindDecision),
            Total = byKind.Sum(k => k.Count),
        };
        FileLog.Write($"[FleetOutcomeStore] CountOpen: ready={counts.Ready}, finding={counts.Finding}, "
                      + $"decision={counts.Decision}, total={counts.Total}");
        return counts;
    }

    /// <summary>
    /// Answer a record. An open record becomes answered, with the words exactly as given; an answered record
    /// is left exactly as it was and the result says so.
    ///
    /// FINAL UNDER CONCURRENT CALLERS. The write is one conditional update, <c>WHERE id = @id AND status =
    /// 'open'</c>, and its affected-row count is the verdict: one row means this call won; none means another
    /// caller answered first (on this instance or another), and the record is returned as it now stands with
    /// <see cref="FleetOutcomeAnswerStatus.AlreadyAnswered"/>.
    ///
    /// On a decision the answer need not be one of the options - the owner may say something else - and
    /// <see cref="FleetOutcomeDto.AnswerMatchedOption"/> records whether it was one (compared after trimming,
    /// ignoring case).
    /// </summary>
    /// <param name="answeredBy">The calling session id, or <see cref="OwnerCaller"/>.</param>
    /// <param name="answeredByRole"><see cref="RoleOwner"/> or <see cref="RoleFleetManager"/>.</param>
    /// <exception cref="ArgumentException">The answer is blank or too long, or the role is not one of the two.</exception>
    public FleetOutcomeAnswerResult Answer(TenantId tenant, Guid id, string? answer, string answeredBy,
        string answeredByRole, DateTime nowUtc)
    {
        FileLog.Write($"[FleetOutcomeStore] Answer: tenant={tenant}, id={id}, answeredBy={answeredBy}, role={answeredByRole}");
        try
        {
            if (string.IsNullOrWhiteSpace(answer))
                throw new ArgumentException("answer is required: the owner's words, exactly as given");
            if (answer.Length > MaxTextLength)
                throw new ArgumentException($"answer is {answer.Length} characters; the most accepted is {MaxTextLength}");
            RequireCaller(answeredBy, "answeredBy");
            var role = RequireOneOf(answeredByRole, AnswerRoles, "answeredByRole");

            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FleetOutcomes.AsNoTracking().FirstOrDefault(o => o.Id == id);
            if (row is null)
            {
                FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=not found");
                return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.NotFound, null);
            }
            if (row.Status != StatusOpen)
            {
                FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=already answered at {row.AnsweredAtUtc:O}");
                return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.AlreadyAnswered, ToDto(row));
            }

            bool? matched = null;
            if (row.Kind == KindDecision)
            {
                var decision = ReadDetails(row).Decision;
                var spoken = answer.Trim();
                matched = decision is not null
                    && decision.Options.Any(o => string.Equals(o.Trim(), spoken, StringComparison.OrdinalIgnoreCase));
            }

            var answeredAt = Utc(nowUtc);
            var affected = ctx.FleetOutcomes
                .Where(o => o.Id == id && o.Status == StatusOpen)
                .ExecuteUpdate(setters => setters
                    .SetProperty(o => o.Status, StatusAnswered)
                    .SetProperty(o => o.AnsweredAtUtc, answeredAt)
                    .SetProperty(o => o.AnswerText, answer)
                    .SetProperty(o => o.AnsweredBy, answeredBy)
                    .SetProperty(o => o.AnsweredByRole, role)
                    .SetProperty(o => o.AnswerMatchedOption, matched));

            var now = ctx.FleetOutcomes.AsNoTracking().First(o => o.Id == id);
            if (affected == 0)
            {
                FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=lost the race; answered at {now.AnsweredAtUtc:O} by {now.AnsweredBy}");
                return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.AlreadyAnswered, ToDto(now));
            }

            FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=answered, matchedOption={matched}");
            return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.Answered, ToDto(now));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetOutcomeStore] Answer FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Replace the Fleet Manager's advice and pick on an OPEN record (step 7). An answered record is left exactly as it
    /// was: the owner has settled it, and advice written afterwards would describe a question nobody is asking.
    ///
    /// One conditional update, <c>WHERE id = @id AND status = 'open'</c>, so a record answered while this call runs is
    /// not changed either. A null pick clears the pick.
    /// </summary>
    /// <exception cref="ArgumentException">The advice is missing, not one line, or too long; or the pick is not one line
    /// or too long.</exception>
    public FleetOutcomeUpdateResult SetAdvice(TenantId tenant, Guid id, string? advice, string? pick, DateTime nowUtc)
    {
        FileLog.Write($"[FleetOutcomeStore] SetAdvice: tenant={tenant}, id={id}, hasPick={pick is not null}");
        try
        {
            var line = RequireAdvice(advice);
            var picked = OptionalPick(pick);
            var at = Utc(nowUtc);
            return UpdateOpen(tenant, id, "SetAdvice", q => q.ExecuteUpdate(setters => setters
                .SetProperty(o => o.Advice, line)
                .SetProperty(o => o.FleetManagerPick, picked)
                .SetProperty(o => o.AdviceSetAtUtc, at)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetOutcomeStore] SetAdvice FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Record what the owner did about an OPEN record without answering it (step 7: a snooze from the walkthrough), in
    /// the Gateway's own words, so the Fleet Manager's digest carries it. The record stays open. An answered record is
    /// left as it was.
    /// </summary>
    /// <exception cref="ArgumentException">The note is blank, not one line, or too long.</exception>
    public FleetOutcomeUpdateResult NoteOwnerAction(TenantId tenant, Guid id, string note, DateTime nowUtc)
    {
        FileLog.Write($"[FleetOutcomeStore] NoteOwnerAction: tenant={tenant}, id={id}");
        try
        {
            var line = RequireLine(note, "note", MaxTitleLength);
            var at = Utc(nowUtc);
            return UpdateOpen(tenant, id, "NoteOwnerAction", q => q.ExecuteUpdate(setters => setters
                .SetProperty(o => o.OwnerNote, line)
                .SetProperty(o => o.OwnerNoteAtUtc, at)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetOutcomeStore] NoteOwnerAction FAILED: {ex.Message}");
            throw;
        }
    }

    private FleetOutcomeUpdateResult UpdateOpen(TenantId tenant, Guid id, string what,
        Func<IQueryable<FleetOutcomeEntity>, int> update)
    {
        using var ctx = _db.CreateContext(tenant);
        var affected = update(ctx.FleetOutcomes.Where(o => o.Id == id && o.Status == StatusOpen));
        var now = ctx.FleetOutcomes.AsNoTracking().FirstOrDefault(o => o.Id == id);
        if (now is null)
        {
            FileLog.Write($"[FleetOutcomeStore] {what}: id={id}, result=not found");
            return new FleetOutcomeUpdateResult(FleetOutcomeUpdateStatus.NotFound, null);
        }
        if (affected == 0)
        {
            FileLog.Write($"[FleetOutcomeStore] {what}: id={id}, result=already answered at {now.AnsweredAtUtc:O}");
            return new FleetOutcomeUpdateResult(FleetOutcomeUpdateStatus.AlreadyAnswered, ToDto(now));
        }
        FileLog.Write($"[FleetOutcomeStore] {what}: id={id}, result=updated");
        return new FleetOutcomeUpdateResult(FleetOutcomeUpdateStatus.Updated, ToDto(now));
    }

    // ---- validation --------------------------------------------------------------------------------------

    /// <summary>
    /// The one-line rule for advice (step 7). The value is checked for a line break BEFORE it is trimmed, so a
    /// trailing newline is refused rather than quietly removed: the Fleet Manager is told its advice is one line,
    /// and a value that is not is its mistake to see.
    /// </summary>
    internal static string RequireAdvice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "advice is required: one line for the owner, using what you know and the Wingman does not");
        if (value.IndexOfAny(LineBreaks) >= 0)
            throw new ArgumentException(
                "advice must be one line: it is shown as a single line beside the Wingman's reading, so remove the line break");
        var v = value.Trim();
        if (v.Length > MaxAdviceLength)
            throw new ArgumentException(
                $"advice is {v.Length} characters; one line of advice is at most {MaxAdviceLength}, so shorten it");
        return v;
    }

    private static readonly char[] LineBreaks = { '\n', '\r', '\u0085', '\u2028', '\u2029' };

    private static string? OptionalPick(string? value)
    {
        if (value is null) return null;
        if (value.IndexOfAny(LineBreaks) >= 0)
            throw new ArgumentException("fleetManagerPick must be one line: the key of one of the Wingman's options");
        return RequireLine(value, "fleetManagerPick", MaxTitleLength);
    }

    /// <summary>The kind-specific block, checked. The two blocks that do not match the kind must be absent: a
    /// body that says "ready" and carries decision options is a caller that is confused about what it filed,
    /// and storing half of it would hide that.</summary>
    private static FleetOutcomeFileRequest ValidateDetails(string kind, FleetOutcomeFileRequest request)
    {
        var others = new List<string>();
        if (kind != KindReady && request.Ready is not null) others.Add(KindReady);
        if (kind != KindFinding && request.Finding is not null) others.Add(KindFinding);
        if (kind != KindDecision && request.Decision is not null) others.Add(KindDecision);
        if (others.Count > 0)
            throw new ArgumentException(
                $"a {kind} record carries only the '{kind}' block; remove the {string.Join(" and ", others.Select(o => $"'{o}'"))} block");

        switch (kind)
        {
            case KindReady:
            {
                var r = request.Ready
                    ?? throw new ArgumentException(
                        "a ready record needs a 'ready' block: { pullRequest, risk, checks, tested, reviewedBy, change }");
                return new FleetOutcomeFileRequest
                {
                    Ready = new FleetReadyDetails
                    {
                        PullRequest = RequireLink(r.PullRequest, "ready.pullRequest"),
                        Risk = RequireOneOf(r.Risk, Risks, "ready.risk"),
                        Checks = RequireOneOf(r.Checks, Checks, "ready.checks"),
                        Tested = RequireText(r.Tested, "ready.tested"),
                        ReviewedBy = RequireLine(r.ReviewedBy, "ready.reviewedBy", MaxTitleLength),
                        Change = RequireText(r.Change, "ready.change"),
                    },
                };
            }
            case KindFinding:
            {
                var f = request.Finding
                    ?? throw new ArgumentException("a finding record needs a 'finding' block: { answer, reason?, links? }");
                var links = f.Links ?? new List<string>();
                if (links.Count > MaxListItems)
                    throw new ArgumentException($"finding.links has {links.Count} entries; the most accepted is {MaxListItems}");
                return new FleetOutcomeFileRequest
                {
                    Finding = new FleetFindingDetails
                    {
                        Answer = RequireText(f.Answer, "finding.answer"),
                        Reason = OptionalText(f.Reason, "finding.reason"),
                        Links = links.Select((l, i) => RequireLink(l, $"finding.links[{i}]")).ToList(),
                    },
                };
            }
            case KindDecision:
            {
                var d = request.Decision
                    ?? throw new ArgumentException(
                        "a decision record needs a 'decision' block: { question, options (two or more), recommended?, why? }");
                var options = (d.Options ?? new List<string>())
                    .Select((o, i) => RequireLine(o, $"decision.options[{i}]", MaxTitleLength))
                    .ToList();
                if (options.Count < 2)
                    throw new ArgumentException($"decision.options needs at least two options, got {options.Count}");
                if (options.Count > MaxListItems)
                    throw new ArgumentException($"decision.options has {options.Count} entries; the most accepted is {MaxListItems}");
                var duplicate = options.GroupBy(o => o.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (duplicate is not null)
                    throw new ArgumentException($"decision.options lists '{duplicate.Key}' more than once; each option must differ");

                string? recommended = null;
                if (d.Recommended is not null)
                {
                    recommended = options.FirstOrDefault(o => string.Equals(o, d.Recommended, StringComparison.Ordinal))
                        ?? throw new ArgumentException(
                            $"decision.recommended '{d.Recommended}' is not one of the options; use one of: {string.Join(", ", options.Select(o => $"'{o}'"))}");
                }

                return new FleetOutcomeFileRequest
                {
                    Decision = new FleetDecisionDetails
                    {
                        Question = RequireText(d.Question, "decision.question"),
                        Options = options,
                        Recommended = recommended,
                        Why = OptionalText(d.Why, "decision.why"),
                    },
                };
            }
            default:
                throw new InvalidOperationException($"kind '{kind}' passed validation but has no details rule");
        }
    }

    /// <summary>The value, lower-cased, when it is one of <paramref name="valid"/>; otherwise a refusal that names
    /// every valid value.</summary>
    internal static string RequireOneOf(string? value, IReadOnlyList<string> valid, string field)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0)
            throw new ArgumentException($"{field} is required; use one of: {string.Join(", ", valid)}");
        if (!valid.Contains(v, StringComparer.Ordinal))
            throw new ArgumentException($"{field} '{value}' is not valid; use one of: {string.Join(", ", valid)}");
        return v;
    }

    private static string RequireLine(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0)
            throw new ArgumentException($"{field} is required");
        if (v.Contains('\n') || v.Contains('\r'))
            throw new ArgumentException($"{field} must be one line");
        if (v.Length > max)
            throw new ArgumentException($"{field} is {v.Length} characters; the most accepted is {max}");
        return v;
    }

    private static string RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{field} is required");
        var v = value.Trim();
        if (v.Length > MaxTextLength)
            throw new ArgumentException($"{field} is {v.Length} characters; the most accepted is {MaxTextLength}");
        return v;
    }

    private static string? OptionalText(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? null : RequireText(value, field);

    private static string RequireLink(string? value, string field)
    {
        var v = RequireLine(value, field, MaxTextLength);
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"{field} '{v}' is not a full link; give the whole address, starting https://");
        return v;
    }

    private static string? OptionalSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Guid.TryParse(value.Trim(), out var sid))
            throw new ArgumentException($"sessionId '{value}' is not a session id; give the full id");
        return sid.ToString();
    }

    private static void RequireCaller(string? caller, string name)
    {
        if (string.IsNullOrWhiteSpace(caller))
            throw new ArgumentException($"{name} is required: a session id or '{OwnerCaller}'");
    }

    // ---- mapping -----------------------------------------------------------------------------------------

    private static FleetOutcomeFileRequest ReadDetails(FleetOutcomeEntity row)
        => JsonSerializer.Deserialize<FleetOutcomeFileRequest>(row.DetailsJson, DetailsJsonOptions)
           ?? throw new InvalidOperationException($"fleet outcome {row.Id} has an empty details document");

    internal static FleetOutcomeDto ToDto(FleetOutcomeEntity row)
    {
        var details = ReadDetails(row);
        return new FleetOutcomeDto
        {
            Id = row.Id.ToString(),
            Kind = row.Kind,
            Title = row.Title,
            Status = row.Status,
            FiledBy = row.FiledBy,
            SessionId = row.AboutSessionId,
            CreatedAtUtc = row.CreatedAtUtc,
            Ready = details.Ready,
            Finding = details.Finding,
            Decision = details.Decision,
            AnsweredAtUtc = row.AnsweredAtUtc,
            Answer = row.AnswerText,
            AnsweredBy = row.AnsweredBy,
            AnsweredByRole = row.AnsweredByRole,
            AnswerMatchedOption = row.AnswerMatchedOption,
            Advice = row.Advice,
            FleetManagerPick = row.FleetManagerPick,
            AdviceSetAtUtc = row.AdviceSetAtUtc,
            OwnerNote = row.OwnerNote,
            OwnerNoteAtUtc = row.OwnerNoteAtUtc,
        };
    }

    private static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
