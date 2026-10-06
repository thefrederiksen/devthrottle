using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Skills;

/// <summary>Thrown when a placement push is malformed. The endpoint maps it to a 400 with the message, so
/// a Director learns what it sent wrong instead of having its report silently dropped.</summary>
public sealed class SkillPlacementValidationException : Exception
{
    public SkillPlacementValidationException(string message) : base(message) { }
}

/// <summary>
/// The Gateway's skill-placement store over <c>skill_placement_state</c>: the LATEST outcome each Director
/// reports for each agent family, which is the one feed telling this Gateway whether the skills it serves
/// can actually be READ on the machines it serves them to.
///
/// WHY IT EXISTS. Serving a skill and an agent being able to read it are two different facts, and only the
/// machine can observe the second. When they came apart on a real machine - a retired installer's leftovers
/// occupying every built-in name, so nothing was placed for Claude Code - the Gateway was serving correctly
/// and had no idea anything was wrong. Publishing a fix fleet-wide while blind to whether it lands is the
/// failure a central library is supposed to remove, not create.
///
/// OVERWRITE, NOT APPEND: one row per (tenant, director, agent kind). The question is whether placement
/// works NOW.
///
/// EVERY WRITE IS SCOPED BY THE PUSHER'S OWN TENANT, taken from the authenticated device key and never from
/// the payload, so a body cannot claim to be another account's machine. The read is scoped the same way.
///
/// THE VERDICT IS FOLDED HERE. <see cref="ReadAll"/> returns finished status strings and finished
/// sentences, because deciding what a row MEANS is the Gateway's job and rendering it is the client's.
/// </summary>
public sealed class SkillPlacementStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The ceiling on one push. There are eight agent families; anything beyond this is a
    /// producer bug and is rejected loudly rather than truncated.</summary>
    public const int MaxReportsPerPush = 32;

    /// <summary>Identity fields - ids and names, bounded because they are names, not prose.</summary>
    public const int MaxIdChars = 512;

    /// <summary>The ceiling on one serialized problem list.</summary>
    public const int MaxProblemsJsonChars = 100_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SkillPlacementStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Store a Director's batch, replacing the row for each (tenant, director, agent kind) it names.
    /// Validated as a WHOLE first: one malformed report rejects the entire push, because a half-landed
    /// batch would show one agent's new answer beside another's old one and call it a single moment.
    /// </summary>
    /// <exception cref="SkillPlacementValidationException">The push or a report in it is malformed.</exception>
    public int StoreBatch(TenantId tenant, string directorId, string machineName,
        IReadOnlyList<SkillPlacementReportDto> reports, DateTime receivedAtUtc)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (reports is null)
            throw new SkillPlacementValidationException("A reports list is required.");

        var director = Required(directorId, "directorId");
        if (reports.Count > MaxReportsPerPush)
            throw new SkillPlacementValidationException(
                $"A push carries at most {MaxReportsPerPush} reports (got {reports.Count}).");
        if (reports.Count == 0)
            return 0;

        var received = DateTime.SpecifyKind(receivedAtUtc.ToUniversalTime(), DateTimeKind.Utc);
        var machine = string.IsNullOrWhiteSpace(machineName) ? "" : machineName.Trim();
        if (machine.Length > MaxIdChars)
            throw new SkillPlacementValidationException($"The machineName is too long (limit {MaxIdChars} characters).");

        var rows = new List<SkillPlacementStateEntity>(reports.Count);
        foreach (var report in reports)
        {
            if (report is null)
                throw new SkillPlacementValidationException("The reports list contains a null entry.");
            rows.Add(BuildRow(tenant, director, machine, report, received));
        }

        // A duplicate agent kind INSIDE one push is a producer bug, not something to silently collapse:
        // two rows with the same key would make the stored answer depend on iteration order.
        var duplicate = rows.GroupBy(r => r.AgentKind, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new SkillPlacementValidationException(
                $"The push names the same agent kind more than once ('{duplicate.Key}').");

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);

            var kinds = rows.Select(r => r.AgentKind).ToList();
            // Reads THROUGH the global tenant query filter, so this only ever finds this tenant's own rows.
            var existing = ctx.SkillPlacementState
                .Where(e => e.DirectorId == director && kinds.Contains(e.AgentKind))
                .ToDictionary(e => e.AgentKind, StringComparer.Ordinal);

            foreach (var row in rows)
            {
                if (existing.TryGetValue(row.AgentKind, out var current))
                {
                    current.MachineName = row.MachineName;
                    current.Held = row.Held;
                    current.Reachable = row.Reachable;
                    current.StoreMissing = row.StoreMissing;
                    current.ProblemsJson = row.ProblemsJson;
                    current.ObservedAtUtc = row.ObservedAtUtc;
                    current.ReceivedAtUtc = row.ReceivedAtUtc;
                }
                else
                {
                    ctx.SkillPlacementState.Add(row);
                }
            }

            ctx.SaveChanges();
        }

        FileLog.Write($"[SkillPlacementStore] StoreBatch: tenant={tenant.ToLogString()} director={director} " +
                      $"stored {rows.Count} report(s)");
        return rows.Count;
    }

    /// <summary>
    /// Every placement row this tenant holds, worst first, with the verdict already decided. Sorting broken
    /// rows to the top is part of the ruling: the one row that needs attention must not be the twentieth
    /// thing on the page.
    /// </summary>
    public SkillPlacementListResponse ReadAll(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        List<SkillPlacementStateEntity> stored;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            stored = ctx.SkillPlacementState.ToList();
        }

        var rows = stored.Select(ToRow).ToList();
        var ordered = rows
            .OrderBy(r => r.Status == "broken" ? 0 : r.Status == "stale" ? 1 : 2)
            .ThenBy(r => r.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.AgentKind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SkillPlacementListResponse
        {
            Rows = ordered,
            AnyBroken = ordered.Any(r => r.Status == "broken"),
        };
    }

    /// <summary>
    /// FOLD THE VERDICT ONCE. Status and message are decided here and rendered verbatim by every client.
    /// A client that decides for itself what "2 of 5" means will, the first time it meets a row it did not
    /// expect, show something plausible instead of something true.
    /// </summary>
    private static SkillPlacementRowDto ToRow(SkillPlacementStateEntity e)
    {
        var problems = Deserialize(e.ProblemsJson);
        var row = new SkillPlacementRowDto
        {
            DirectorId = e.DirectorId,
            MachineName = e.MachineName,
            AgentKind = e.AgentKind,
            Held = e.Held,
            Reachable = e.Reachable,
            StoreMissing = e.StoreMissing,
            Problems = problems,
            ObservedAtUtc = e.ObservedAtUtc,
            ReceivedAtUtc = e.ReceivedAtUtc,
        };

        if (e.StoreMissing)
        {
            row.Status = "stale";
            row.Message = $"{Where(e)} has not reached this Gateway since it started, so it is running on " +
                          "whatever skills it already had.";
            return row;
        }
        if (e.Held == 0)
        {
            row.Status = "ok";
            row.Message = $"{Where(e)} has no skills to place.";
            return row;
        }
        if (e.Reachable >= e.Held && problems.Count == 0)
        {
            row.Status = "ok";
            row.Message = $"All {e.Held} skill(s) are readable by {e.AgentKind} on {e.MachineName}.";
            return row;
        }

        row.Status = "broken";
        var shadowed = problems.Where(p => p.Fault == "Shadowed").Select(p => p.SkillId).ToList();
        var heldElsewhere = problems.Where(p => p.Fault == "HeldByAnotherSource").Select(p => p.SkillId).ToList();
        var detail = shadowed.Count > 0
            ? $"blocked by a directory DevThrottle did not write ({string.Join(", ", shadowed)}) in " +
              $"{problems.First(p => p.Fault == "Shadowed").Target}"
            : heldElsewhere.Count > 0
                ? $"kept by another Director's library under the same name ({string.Join(", ", heldElsewhere)}) in " +
                  $"{problems.First(p => p.Fault == "HeldByAnotherSource").Target}"
                : problems.Any(p => p.Fault == "SourceUnknown")
                    ? "nothing was placed because this Gateway does not say which account its skills belong to - update the Gateway"
                    : problems.Any(p => p.Fault == "SourceMismatch")
                        ? "nothing was placed because the skills were fetched for a different account than the Director is set up for"
                        : problems.Any(p => p.Fault == "FolderBusy")
                            ? "nothing was placed because another Director was placing skills in the same folder at that moment"
                            : problems.Any(p => p.Fault == "FolderLinkUnresolved")
                                ? $"nothing was placed because {problems.First(p => p.Fault == "FolderLinkUnresolved").Target} " +
                                  "is a link whose target cannot be found - repair or remove the link"
                                : "could not be linked";
        row.Message = $"Only {e.Reachable} of {e.Held} skill(s) reached {e.AgentKind} on {e.MachineName} - {detail}.";
        return row;
    }

    private static string Where(SkillPlacementStateEntity e) => $"{e.AgentKind} on {e.MachineName}";

    private static List<SkillPlacementProblemDto> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<SkillPlacementProblemDto>();
        return JsonSerializer.Deserialize<List<SkillPlacementProblemDto>>(json, Json)
               ?? new List<SkillPlacementProblemDto>();
    }

    private static SkillPlacementStateEntity BuildRow(
        TenantId tenant, string director, string machine, SkillPlacementReportDto report, DateTime received)
    {
        var kind = Required(report.AgentKind, "agentKind");
        if (report.Held < 0 || report.Reachable < 0)
            throw new SkillPlacementValidationException("held and reachable cannot be negative.");
        if (report.Reachable > report.Held)
            throw new SkillPlacementValidationException(
                $"reachable ({report.Reachable}) cannot exceed held ({report.Held}) for '{kind}'.");

        var problemsJson = JsonSerializer.Serialize(report.Problems ?? new(), Json);
        if (problemsJson.Length > MaxProblemsJsonChars)
            throw new SkillPlacementValidationException(
                $"The problem list for '{kind}' is too long (limit {MaxProblemsJsonChars} characters).");

        return new SkillPlacementStateEntity
        {
            TenantId = tenant.Value,
            DirectorId = director,
            AgentKind = kind,
            MachineName = machine,
            Held = report.Held,
            Reachable = report.Reachable,
            StoreMissing = report.StoreMissing,
            ProblemsJson = problemsJson,
            ObservedAtUtc = DateTime.SpecifyKind(report.ObservedAtUtc.ToUniversalTime(), DateTimeKind.Utc),
            ReceivedAtUtc = received,
        };
    }

    private static string Required(string? value, string field)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw new SkillPlacementValidationException($"{field} is required.");
        if (trimmed.Length > MaxIdChars)
            throw new SkillPlacementValidationException($"{field} is too long (limit {MaxIdChars} characters).");
        return trimmed;
    }
}
