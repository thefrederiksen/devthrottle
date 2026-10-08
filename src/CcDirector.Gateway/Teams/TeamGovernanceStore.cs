using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>One on/off rule of the Governance tab: its id on the wire, its words, and where it lives on the row.</summary>
public sealed record TeamGovernanceSwitch(string Id, string Label, string? Detail,
    Func<TeamGovernanceEntity, bool> Read, Action<TeamGovernanceEntity, bool> Write);

/// <summary>One limit of the Governance tab: its id on the wire, its words, the range it accepts, and where it lives.</summary>
public sealed record TeamGovernanceLimit(string Id, string Label, string? Detail, int Min, int Max,
    Func<int, string> Display, Func<TeamGovernanceEntity, int?> Read, Action<TeamGovernanceEntity, int?> Write);

/// <summary>A skill or workflow in the team's own library, as the Governance tab may name it.</summary>
public sealed record TeamGovernanceLibraryEntry(string Kind, string Id, string Name);

/// <summary>One change to a skill or workflow's place in the governance: Required, Suggested, or None (off the list).</summary>
public sealed record TeamGovernanceItemChange(string Kind, string Id, string Level);

/// <summary>
/// A change to a team's governance, as asked: only what is present changes. Ids are the catalog's
/// (<see cref="TeamGovernanceCatalog"/>); a limit of null removes it.
/// </summary>
public sealed record TeamGovernanceChangeRequest(
    IReadOnlyDictionary<string, bool> Review,
    IReadOnlyDictionary<string, bool> Agents,
    IReadOnlyList<TeamGovernanceItemChange> Items,
    IReadOnlyDictionary<string, int?> Limits)
{
    /// <summary>A request that changes nothing.</summary>
    public static TeamGovernanceChangeRequest Empty { get; } = new(
        new Dictionary<string, bool>(), new Dictionary<string, bool>(), Array.Empty<TeamGovernanceItemChange>(), new Dictionary<string, int?>());
}

/// <summary>What a change to a team's governance came to.</summary>
public enum TeamGovernanceChangeOutcome
{
    /// <summary>Saved (or nothing differed, so nothing needed saving).</summary>
    Done,

    /// <summary>No such team for this caller.</summary>
    NotFound,

    /// <summary>The caller's role may not change the rules.</summary>
    Forbidden,

    /// <summary>The request names something the rules do not have, or a value they do not accept.</summary>
    Invalid,

    /// <summary>Another change was saved at the same moment; nothing was saved.</summary>
    Conflict,
}

/// <summary>The answer to a change: the outcome, and the sentence a person reads when it is not <see cref="TeamGovernanceChangeOutcome.Done"/>.</summary>
public sealed record TeamGovernanceChangeResult(TeamGovernanceChangeOutcome Outcome, string? Refusal = null, int Changes = 0);

/// <summary>
/// THE RULES THE GOVERNANCE TAB HOLDS, ONCE (Teams v1, the team's Governance tab). The tab's sections, their rows, their
/// words and their wire ids live here and nowhere else: the Gateway builds the page from this list (rule 7) and checks
/// every change against it. Adding a rule is one entry here, one column on <see cref="TeamGovernanceEntity"/> and a
/// migration.
/// </summary>
public static class TeamGovernanceCatalog
{
    /// <summary>The kinds an item in the team's library has.</summary>
    public const string KindSkill = "Skill";

    /// <summary>The kinds an item in the team's library has.</summary>
    public const string KindWorkflow = "Workflow";

    /// <summary>An item every member's Directors are to carry.</summary>
    public const string LevelRequired = "Required";

    /// <summary>An item the team suggests.</summary>
    public const string LevelSuggested = "Suggested";

    /// <summary>Off the list - the item is neither required nor suggested.</summary>
    public const string LevelNone = "None";

    /// <summary>Section 1, Review.</summary>
    public static readonly IReadOnlyList<TeamGovernanceSwitch> Review = new[]
    {
        new TeamGovernanceSwitch("agentReviewsPullRequests", "An agent reviews every pull request before a person merges it", null,
            g => g.AgentReviewsPullRequests, (g, on) => g.AgentReviewsPullRequests = on),
        new TeamGovernanceSwitch("noSelfMerge", "Nobody merges their own agent's work", "A second member approves.",
            g => g.NoSelfMerge, (g, on) => g.NoSelfMerge = on),
        new TeamGovernanceSwitch("workStartsAsAssignedIssue", "Every piece of work starts as an assigned issue", null,
            g => g.WorkStartsAsAssignedIssue, (g, on) => g.WorkStartsAsAssignedIssue = on),
    };

    /// <summary>Section 3, Agents members may run.</summary>
    public static readonly IReadOnlyList<TeamGovernanceSwitch> Agents = new[]
    {
        new TeamGovernanceSwitch("claudeCode", "Claude Code", null, g => g.AllowClaudeCode, (g, on) => g.AllowClaudeCode = on),
        new TeamGovernanceSwitch("codex", "Codex", null, g => g.AllowCodex, (g, on) => g.AllowCodex = on),
        new TeamGovernanceSwitch("otherAgents", "Any other agent", null, g => g.AllowOtherAgents, (g, on) => g.AllowOtherAgents = on),
    };

    /// <summary>Section 5, Limits.</summary>
    public static readonly IReadOnlyList<TeamGovernanceLimit> Limits = new[]
    {
        new TeamGovernanceLimit("agentHoursPerWeek", "Agent hours per member per week", "Warn the member and their Manager at 80%",
            1, 168, h => h.ToString(CultureInfo.InvariantCulture) + " h", g => g.AgentHoursPerWeek, (g, v) => g.AgentHoursPerWeek = v),
        new TeamGovernanceLimit("sessionsAtOnce", "Sessions running at once per member", null,
            1, 100, n => n.ToString(CultureInfo.InvariantCulture), g => g.SessionsAtOnce, (g, v) => g.SessionsAtOnce = v),
        new TeamGovernanceLimit("keepMentorPagesMonths", "Keep Mentor pages", null,
            1, 120, m => m == 1 ? "1 month" : m.ToString(CultureInfo.InvariantCulture) + " months", g => g.KeepMentorPagesMonths, (g, v) => g.KeepMentorPagesMonths = v),
    };

    /// <summary>What a limit that is not set shows.</summary>
    public const string NoLimit = "No limit";

    /// <summary>A limit's value as the page shows it.</summary>
    public static string LimitDisplay(TeamGovernanceLimit limit, int? value) => value is { } v ? limit.Display(v) : NoLimit;

    /// <summary>
    /// The rules a team has before anyone changes them: no review rule switched on, every agent allowed - which is what
    /// the product does today - and no limit. Not a row in the database; a team gets its row on its first change.
    /// </summary>
    public static TeamGovernanceEntity Defaults(string teamId) => new()
    {
        TeamId = teamId,
        AgentReviewsPullRequests = false,
        NoSelfMerge = false,
        WorkStartsAsAssignedIssue = false,
        AllowClaudeCode = true,
        AllowCodex = true,
        AllowOtherAgents = true,
        AgentHoursPerWeek = null,
        SessionsAtOnce = null,
        KeepMentorPagesMonths = null,
    };
}

/// <summary>A team's governance as stored: the rules (the defaults when never changed), the named items, and the record of
/// changes, newest first.</summary>
public sealed record TeamGovernanceSnapshot(TeamGovernanceEntity Rules, IReadOnlyList<TeamGovernanceItemEntity> Items,
    IReadOnlyList<TeamGovernanceChangeEntity> Changes);

/// <summary>
/// THE ONE WRITER OF A TEAM'S GOVERNANCE (Teams v1, the team's Governance tab). It keeps the rules, the required and
/// suggested skills and workflows, and the record of changes - and writes a line of that record for EVERY change, in the
/// same database write as the change, so a rule can never change without its line.
///
/// WHO MAY CALL WHAT IS NOT DECIDED HERE. The role table is asked by <see cref="TeamRegistry"/> before
/// <see cref="Apply"/> is called; this store only checks a change against <see cref="TeamGovernanceCatalog"/> and the
/// team's library as the caller passes it in.
///
/// Writes are serialized under one lock inside this process. Across processes the rules row carries a concurrency token
/// (<see cref="TeamGovernanceEntity.Version"/>): a second Gateway process saving a stale copy is refused and nothing is
/// saved. A team id is logged only in its hashed tenant form.
/// </summary>
public sealed class TeamGovernanceStore
{
    /// <summary>The most lines of the record the page reads.</summary>
    public const int ChangesShown = 100;

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;
    private readonly object _writeLock = new();

    /// <summary>TEST SEAM: called just before a change is saved, so a test can change the rules underneath it the way a
    /// second Gateway process would. Null in production.</summary>
    internal Action<string>? BeforeSaveForTests { get; set; }

    /// <param name="db">The Gateway database. The governance tables are global, so they are read through the UNSCOPED context.</param>
    /// <param name="utcNow">The clock; the system clock when omitted.</param>
    public TeamGovernanceStore(GatewayDatabase db, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The team's governance: its rules (the defaults when never changed), its named items and its newest changes.</summary>
    public TeamGovernanceSnapshot Read(string teamId)
    {
        var id = RequireTeam(teamId);
        using var ctx = _db.CreateUnscopedContext();
        var rules = ctx.TeamGovernance.AsNoTracking().FirstOrDefault(g => g.TeamId == id) ?? TeamGovernanceCatalog.Defaults(id);
        var items = ctx.TeamGovernanceItems.AsNoTracking().Where(i => i.TeamId == id).ToList()
            .OrderBy(i => i.Level == TeamGovernanceCatalog.LevelRequired ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ToList();
        var changes = ctx.TeamGovernanceChanges.AsNoTracking().Where(c => c.TeamId == id).ToList()
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(ChangesShown)
            .ToList();
        return new TeamGovernanceSnapshot(rules, items, changes);
    }

    /// <summary>
    /// Apply <paramref name="request"/> for the member <paramref name="changedBy"/> names, checking it first against the
    /// catalog and against <paramref name="library"/> - the team's own skills and workflows. Everything that differs is
    /// saved with one line of the record each, in ONE database write; a request that differs in nothing writes nothing.
    /// An invalid request saves nothing at all, not even its valid parts.
    /// </summary>
    public TeamGovernanceChangeResult Apply(string teamId, string changedBy, TeamGovernanceChangeRequest request,
        IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        var id = RequireTeam(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(library);
        FileLog.Write($"[TeamGovernanceStore] Apply: team {LogTeam(id)} review={request.Review.Count} agents={request.Agents.Count} items={request.Items.Count} limits={request.Limits.Count}");

        var invalid = Validate(request, library);
        if (invalid is not null)
        {
            FileLog.Write($"[TeamGovernanceStore] Apply: team {LogTeam(id)} REFUSED - {invalid}");
            return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.Invalid, invalid);
        }

        lock (_writeLock)
        {
            using var ctx = _db.CreateUnscopedContext();
            if (!ctx.Teams.AsNoTracking().Any(t => t.Id == id))
                return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.NotFound, TeamRefusals.NoSuchTeam);

            var now = _utcNow();
            var rules = ctx.TeamGovernance.FirstOrDefault(g => g.TeamId == id);
            var isNew = rules is null;
            rules ??= TeamGovernanceCatalog.Defaults(id);
            var lines = new List<string>();

            void Switch(IReadOnlyList<TeamGovernanceSwitch> section, IReadOnlyDictionary<string, bool> asked)
            {
                foreach (var rule in section)
                {
                    if (!asked.TryGetValue(rule.Id, out var on) || rule.Read(rules) == on) continue;
                    rule.Write(rules, on);
                    lines.Add($"{(on ? "switched on" : "switched off")} \"{rule.Label}\"");
                }
            }
            Switch(TeamGovernanceCatalog.Review, request.Review);
            Switch(TeamGovernanceCatalog.Agents, request.Agents);

            foreach (var limit in TeamGovernanceCatalog.Limits)
            {
                if (!request.Limits.TryGetValue(limit.Id, out var value) || limit.Read(rules) == value) continue;
                limit.Write(rules, value);
                lines.Add(value is { } v
                    ? $"set \"{limit.Label}\" to {limit.Display(v)}"
                    : $"removed the limit on \"{limit.Label}\"");
            }

            var itemsChanged = false;
            foreach (var change in request.Items)
            {
                var row = ctx.TeamGovernanceItems.FirstOrDefault(i => i.TeamId == id && i.Kind == change.Kind && i.ItemId == change.Id);
                if (change.Level == TeamGovernanceCatalog.LevelNone)
                {
                    if (row is null) continue;
                    ctx.TeamGovernanceItems.Remove(row);
                    lines.Add($"took \"{row.Name}\" off the required and suggested skills and workflows");
                    itemsChanged = true;
                    continue;
                }

                var name = library.First(e => e.Kind == change.Kind && e.Id == change.Id).Name;
                if (row is not null && row.Level == change.Level) continue;
                if (row is null)
                {
                    ctx.TeamGovernanceItems.Add(new TeamGovernanceItemEntity
                    {
                        TeamId = id, Kind = change.Kind, ItemId = change.Id, Name = name, Level = change.Level, UpdatedAtUtc = now,
                    });
                }
                else
                {
                    row.Level = change.Level;
                    row.Name = name;
                    row.UpdatedAtUtc = now;
                }
                lines.Add($"made \"{name}\" {change.Level.ToLowerInvariant()}");
                itemsChanged = true;
            }

            if (lines.Count == 0)
            {
                FileLog.Write($"[TeamGovernanceStore] Apply: team {LogTeam(id)} nothing differed, nothing saved");
                return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.Done);
            }

            // The rules row is written on EVERY change, items included, so its version token guards the whole change.
            rules.UpdatedAtUtc = now;
            rules.Version += 1;
            if (isNew)
            {
                rules.CreatedAtUtc = now;
                ctx.TeamGovernance.Add(rules);
            }

            // One tick apart, so the record keeps the order the lines were made in when it is read newest first.
            for (var i = 0; i < lines.Count; i++)
            {
                ctx.TeamGovernanceChanges.Add(new TeamGovernanceChangeEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TeamId = id,
                    ChangedBy = changedBy,
                    What = lines[i],
                    CreatedAtUtc = now.AddTicks(i),
                });
            }

            BeforeSaveForTests?.Invoke(id);
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[TeamGovernanceStore] Apply: team {LogTeam(id)} REFUSED - another change was saved at the same moment ({ex.GetType().Name})");
                return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.Conflict, ChangedAtTheSameMoment);
            }

            FileLog.Write($"[TeamGovernanceStore] Apply: team {LogTeam(id)} saved {lines.Count} change(s){(itemsChanged ? ", items included" : "")}, version {rules.Version}");
            return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.Done, Changes: lines.Count);
        }
    }

    /// <summary>The sentence for a change that lost a race with another.</summary>
    public const string ChangedAtTheSameMoment =
        "Someone else changed the team's rules at the same moment, so this change was not saved. Reload the page and try again.";

    /// <summary>Why a request cannot be applied, or null when it can.</summary>
    private static string? Validate(TeamGovernanceChangeRequest request, IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        foreach (var key in request.Review.Keys)
            if (TeamGovernanceCatalog.Review.All(r => r.Id != key))
                return $"There is no review rule called \"{key}\".";
        foreach (var key in request.Agents.Keys)
            if (TeamGovernanceCatalog.Agents.All(a => a.Id != key))
                return $"There is no agent rule called \"{key}\".";
        foreach (var (key, value) in request.Limits)
        {
            var limit = TeamGovernanceCatalog.Limits.FirstOrDefault(l => l.Id == key);
            if (limit is null)
                return $"There is no limit called \"{key}\".";
            if (value is { } v && (v < limit.Min || v > limit.Max))
                return $"\"{limit.Label}\" must be a whole number from {limit.Min} to {limit.Max}, or no limit.";
        }

        var seen = new HashSet<(string, string)>();
        foreach (var item in request.Items)
        {
            if (item.Kind != TeamGovernanceCatalog.KindSkill && item.Kind != TeamGovernanceCatalog.KindWorkflow)
                return $"\"{item.Kind}\" is not a kind of item. Use {TeamGovernanceCatalog.KindSkill} or {TeamGovernanceCatalog.KindWorkflow}.";
            if (item.Level != TeamGovernanceCatalog.LevelRequired && item.Level != TeamGovernanceCatalog.LevelSuggested && item.Level != TeamGovernanceCatalog.LevelNone)
                return $"\"{item.Level}\" is not a level. Use {TeamGovernanceCatalog.LevelRequired}, {TeamGovernanceCatalog.LevelSuggested} or {TeamGovernanceCatalog.LevelNone}.";
            if (!seen.Add((item.Kind, item.Id)))
                return "The same skill or workflow is named twice in one change.";
            if (item.Level != TeamGovernanceCatalog.LevelNone && !library.Any(e => e.Kind == item.Kind && e.Id == item.Id))
                return "That skill or workflow is not in the team's library. Only the team's own skills and workflows can be required or suggested.";
        }
        return null;
    }

    private static string RequireTeam(string teamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        return teamId.Trim();
    }

    private static string LogTeam(string teamId) => new TenantId(teamId).ToLogString();
}
