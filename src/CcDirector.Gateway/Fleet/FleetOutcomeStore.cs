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

    /// <summary>The record was already answered. The first answer stands and nothing was changed.</summary>
    AlreadyAnswered,
}

/// <summary>The result of <see cref="FleetOutcomeStore.Answer"/>: what happened, and the record as it now is
/// (null only when it was not found).</summary>
public sealed record FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus Status, FleetOutcomeDto? Outcome);

/// <summary>
/// The durable record of the news the Fleet Manager brought the owner - READY, FINDING, DECISION - over the
/// <c>fleet_outcomes</c> table (the Fleet Manager mission, step 3).
///
/// A RECORD STAYS OPEN UNTIL THE OWNER ANSWERS IT, across a Gateway restart and across a restart or a move of
/// the Fleet Manager itself. It belongs to the account: nothing here reads or filters by the session that
/// filed it, so a new Fleet Manager session sees and answers what the old one filed.
///
/// AN ANSWER IS FINAL. Answering an answered record is refused and changes nothing - the owner's first word
/// is never silently replaced by a second.
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

    /// <summary>The longest title accepted. A title is one line naming the news.</summary>
    public const int MaxTitleLength = 300;

    /// <summary>The longest free-text field accepted (an answer, a reason, a question, how it was tested).</summary>
    public const int MaxTextLength = 4000;

    /// <summary>The most options or links one record may carry.</summary>
    public const int MaxListItems = 20;

    /// <summary>The page when the caller names no count, and the most one read returns.</summary>
    public const int DefaultCount = 50;
    public const int MaxCount = 200;

    public static readonly IReadOnlyList<string> Kinds = new[] { KindReady, KindFinding, KindDecision };
    public static readonly IReadOnlyList<string> Statuses = new[] { StatusOpen, StatusAnswered, StatusAll };
    public static readonly IReadOnlyList<string> Risks = new[] { "low", "medium", "high" };
    public static readonly IReadOnlyList<string> Checks = new[] { "passed", "failed", "none" };

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

            var entity = new FleetOutcomeEntity
            {
                Kind = kind,
                FiledBy = filedBy,
                AboutSessionId = about,
                CreatedAtUtc = Utc(nowUtc),
                Title = title,
                DetailsJson = JsonSerializer.Serialize(details, DetailsJsonOptions),
                Status = StatusOpen,
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
    /// This account's records, newest first.
    /// </summary>
    /// <param name="status"><c>open</c>, <c>answered</c> or <c>all</c>.</param>
    /// <param name="kind">One kind, or null for every kind.</param>
    /// <param name="count">How many at most, 1 to <see cref="MaxCount"/>.</param>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values.</exception>
    public IReadOnlyList<FleetOutcomeDto> List(TenantId tenant, string status, string? kind, int count)
    {
        FileLog.Write($"[FleetOutcomeStore] List: tenant={tenant}, status={status}, kind={kind}, count={count}");
        var wantedStatus = RequireOneOf(status, Statuses, "status");
        var wantedKind = kind is null ? null : RequireOneOf(kind, Kinds, "kind");
        if (count < 1 || count > MaxCount)
            throw new ArgumentException($"count must be between 1 and {MaxCount}, got {count}");

        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetOutcomes.AsNoTracking();
        if (wantedStatus != StatusAll) query = query.Where(o => o.Status == wantedStatus);
        if (wantedKind is not null) query = query.Where(o => o.Kind == wantedKind);
        var rows = query.OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id).Take(count).ToList();

        FileLog.Write($"[FleetOutcomeStore] List: returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>
    /// Answer a record. An open record becomes answered, with the words exactly as given; an answered record
    /// is left exactly as it was and the result says so.
    ///
    /// On a decision the answer need not be one of the options - the owner may say something else - and
    /// <see cref="FleetOutcomeDto.AnswerMatchedOption"/> records whether it was one (compared after trimming,
    /// ignoring case).
    /// </summary>
    /// <param name="answeredBy">The calling session id, or <see cref="OwnerCaller"/>.</param>
    /// <exception cref="ArgumentException">The answer is blank or too long.</exception>
    public FleetOutcomeAnswerResult Answer(TenantId tenant, Guid id, string? answer, string answeredBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetOutcomeStore] Answer: tenant={tenant}, id={id}, answeredBy={answeredBy}");
        try
        {
            if (string.IsNullOrWhiteSpace(answer))
                throw new ArgumentException("answer is required: the owner's words, exactly as given");
            if (answer.Length > MaxTextLength)
                throw new ArgumentException($"answer is {answer.Length} characters; the most accepted is {MaxTextLength}");
            RequireCaller(answeredBy, "answeredBy");

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var row = ctx.FleetOutcomes.FirstOrDefault(o => o.Id == id);
                if (row is null)
                {
                    FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=not found");
                    return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.NotFound, null);
                }
                if (row.Status == StatusAnswered)
                {
                    FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=already answered at {row.AnsweredAtUtc:O}");
                    return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.AlreadyAnswered, ToDto(row));
                }

                row.Status = StatusAnswered;
                row.AnsweredAtUtc = Utc(nowUtc);
                row.AnswerText = answer;
                row.AnsweredBy = answeredBy;
                if (row.Kind == KindDecision)
                {
                    var decision = ReadDetails(row).Decision;
                    var spoken = answer.Trim();
                    row.AnswerMatchedOption = decision is not null
                        && decision.Options.Any(o => string.Equals(o.Trim(), spoken, StringComparison.OrdinalIgnoreCase));
                }
                ctx.SaveChanges();

                FileLog.Write($"[FleetOutcomeStore] Answer: id={id}, result=answered, matchedOption={row.AnswerMatchedOption}");
                return new FleetOutcomeAnswerResult(FleetOutcomeAnswerStatus.Answered, ToDto(row));
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetOutcomeStore] Answer FAILED: {ex.Message}");
            throw;
        }
    }

    // ---- validation --------------------------------------------------------------------------------------

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
            AnswerMatchedOption = row.AnswerMatchedOption,
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
