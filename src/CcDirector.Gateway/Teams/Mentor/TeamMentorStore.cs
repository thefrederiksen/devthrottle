using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>One prompt the Mentor quotes: a reference to the prompt record, when it was sent, and its text exactly as
/// the prompt log holds it.</summary>
public sealed record MentorQuote(
    [property: JsonPropertyName("promptId")] string PromptId,
    [property: JsonPropertyName("at")] DateTime AtUtc,
    [property: JsonPropertyName("text")] string Text);

/// <summary>One stored block of the Mentor's weekly page, as written.</summary>
public sealed record MentorBlock(
    string Week,
    string PersonSubject,
    string Tone,
    string WorkedOn,
    string? HowItWent,
    string? WentBadlyAndWhy,
    IReadOnlyList<MentorQuote> Quotes,
    string OneThingToTry,
    DateTime WrittenAtUtc,
    string Model);

/// <summary>The outcome words stored in <c>team_mentor_outcomes</c>.</summary>
public static class MentorOutcomes
{
    /// <summary>A block was written.</summary>
    public const string Written = "written";

    /// <summary>The person ran no sessions in the team that week: no block, no model call.</summary>
    public const string NoSessions = "no-sessions";

    /// <summary>The person ran sessions but sent no prompts the Gateway holds for that week: nothing for the Mentor to
    /// read, so no block and no model call.</summary>
    public const string NoPrompts = "no-prompts";

    /// <summary>The model answered, and the answer was not accepted. No block.</summary>
    public const string Refused = "refused";

    /// <summary>The model could not be reached or failed, and was still failing once the following week had closed.
    /// No block.</summary>
    public const string ModelFailed = "model-failed";

    /// <summary>The person's role may not read their own Mentor page (a Collaborator), so no block is written about
    /// them: the person always reads the same words their Manager does. No model call.</summary>
    public const string MayNotRead = "may-not-read";
}

/// <summary>One stored outcome row.</summary>
public sealed record MentorOutcome(string Week, string PersonSubject, string Outcome, string? Reason, DateTime AtUtc);

/// <summary>
/// THE MENTOR'S STORAGE (devthrottle_internal#2305): the blocks, what happened for each member, and the per-week
/// "already ran" marker. Explicit tenant (the team's) on every call, through <see cref="GatewayDatabase.CreateContext(TenantId)"/>,
/// under one write lock - the pattern of the other tenant-scoped stores. The subject and the text are personally
/// identifying and never logged.
/// </summary>
public sealed class TeamMentorStore
{
    private static readonly JsonSerializerOptions QuoteJson = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public TeamMentorStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Whether the Mentor run for <paramref name="week"/> has completed in this team.</summary>
    public bool HasRun(TenantId team, MentorWeek week)
    {
        var key = week.ToString();
        using var ctx = _db.CreateContext(team);
        return ctx.TeamMentorRuns.AsNoTracking().Any(r => r.Week == key);
    }

    /// <summary>Mark the team's week as run. Written once; a second call for the same week throws, because two runs
    /// of one week is the defect the marker exists to prevent.</summary>
    public void RecordRun(TenantId team, MentorWeek week, string timeZone, int blocksWritten, DateTime ranAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(team);
            ctx.TeamMentorRuns.Add(new TeamMentorRunEntity
            {
                TenantId = team.Value,
                Week = week.ToString(),
                TimeZone = timeZone,
                BlocksWritten = blocksWritten,
                RanAtUtc = ranAtUtc,
            });
            ctx.SaveChanges();
        }
        FileLog.Write($"[TeamMentorStore] RecordRun: team {team.ToLogString()} week={week} blocks={blocksWritten}");
    }

    /// <summary>The outcome already recorded for one member and week, or null.</summary>
    public MentorOutcome? OutcomeOf(TenantId team, MentorWeek week, string personSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        var key = week.ToString();
        using var ctx = _db.CreateContext(team);
        var row = ctx.TeamMentorOutcomes.AsNoTracking()
            .FirstOrDefault(o => o.Week == key && o.PersonSubject == personSubject);
        return row is null ? null : ToOutcome(row);
    }

    /// <summary>Every outcome recorded for a week in this team.</summary>
    public IReadOnlyList<MentorOutcome> Outcomes(TenantId team, MentorWeek week)
    {
        var key = week.ToString();
        using var ctx = _db.CreateContext(team);
        return ctx.TeamMentorOutcomes.AsNoTracking()
            .Where(o => o.Week == key)
            .ToList()
            .Select(ToOutcome)
            .ToList();
    }

    /// <summary>Record an outcome that wrote no block: no sessions, no prompts, a refused answer, a failed model.</summary>
    public void RecordOutcome(TenantId team, MentorWeek week, string personSubject, string outcome, string? reason, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personSubject);
        if (outcome == MentorOutcomes.Written)
            throw new ArgumentException("A written block is recorded with SaveBlock, which stores the block and its outcome together.", nameof(outcome));
        if (outcome is not (MentorOutcomes.NoSessions or MentorOutcomes.NoPrompts or MentorOutcomes.Refused or MentorOutcomes.ModelFailed or MentorOutcomes.MayNotRead))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a Mentor outcome.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext(team);
            ctx.TeamMentorOutcomes.Add(new TeamMentorOutcomeEntity
            {
                TenantId = team.Value,
                Week = week.ToString(),
                PersonSubject = personSubject,
                Outcome = outcome,
                Reason = reason,
                AtUtc = atUtc,
            });
            ctx.SaveChanges();
        }
        FileLog.Write($"[TeamMentorStore] RecordOutcome: team {team.ToLogString()} week={week} outcome={outcome}");
    }

    /// <summary>Store a block and its <c>written</c> outcome in one write.</summary>
    public void SaveBlock(TenantId team, MentorBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(team);
            ctx.TeamMentorBlocks.Add(new TeamMentorBlockEntity
            {
                TenantId = team.Value,
                Week = block.Week,
                PersonSubject = block.PersonSubject,
                Tone = block.Tone,
                WorkedOn = block.WorkedOn,
                HowItWent = block.HowItWent,
                WentBadlyAndWhy = block.WentBadlyAndWhy,
                OneThingToTry = block.OneThingToTry,
                QuotesJson = JsonSerializer.Serialize(block.Quotes, QuoteJson),
                WrittenAtUtc = block.WrittenAtUtc,
                Model = block.Model,
            });
            ctx.TeamMentorOutcomes.Add(new TeamMentorOutcomeEntity
            {
                TenantId = team.Value,
                Week = block.Week,
                PersonSubject = block.PersonSubject,
                Outcome = MentorOutcomes.Written,
                Reason = null,
                AtUtc = block.WrittenAtUtc,
            });
            ctx.SaveChanges();
        }
        FileLog.Write($"[TeamMentorStore] SaveBlock: team {team.ToLogString()} week={block.Week} quotes={block.Quotes.Count}");
    }

    /// <summary>Every block of a week in this team, in a fixed order (by the person's subject); the page orders them for
    /// reading.</summary>
    public IReadOnlyList<MentorBlock> Blocks(TenantId team, MentorWeek week)
    {
        var key = week.ToString();
        using var ctx = _db.CreateContext(team);
        return ctx.TeamMentorBlocks.AsNoTracking()
            .Where(b => b.Week == key)
            .ToList()
            .Select(ToBlock)
            .OrderBy(b => b.PersonSubject, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether any block, of any week, is stored about this person under this tenant. A personal page uses it to
    /// say whether an empty week is the first one or just a quiet one.</summary>
    public bool HasAnyBlockFor(TenantId tenant, string personSubject)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.TeamMentorBlocks.AsNoTracking().Any(b => b.PersonSubject == personSubject);
    }

    private static MentorBlock ToBlock(TeamMentorBlockEntity e) => new(
        e.Week, e.PersonSubject, e.Tone, e.WorkedOn, e.HowItWent, e.WentBadlyAndWhy,
        JsonSerializer.Deserialize<List<MentorQuote>>(e.QuotesJson, QuoteJson)
            ?? throw new InvalidOperationException("A stored Mentor block's quotes are not a JSON array."),
        e.OneThingToTry, e.WrittenAtUtc, e.Model);

    private static MentorOutcome ToOutcome(TeamMentorOutcomeEntity e) => new(e.Week, e.PersonSubject, e.Outcome, e.Reason, e.AtUtc);
}
