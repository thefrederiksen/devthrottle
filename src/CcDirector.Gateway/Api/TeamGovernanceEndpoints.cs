using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Skills;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The team's Governance tab (Teams v1): the rules a team sets for how its members work, and the record of every change.
///
/// <list type="bullet">
/// <item><c>GET /teams/{teamId}/governance</c> - the tab, finished for display (rule 7), for the Owner, Managers and
/// Developers.</item>
/// <item><c>PUT /teams/{teamId}/governance</c> - change the rules, for the Owner and Managers. Only what the body names
/// changes, so one switch saves only itself:
/// <c>{"review": {"noSelfMerge": true}, "agents": {"codex": false},
/// "items": [{"kind": "Skill", "id": "...", "level": "Required"}], "limits": {"sessionsAtOnce": 8, "agentHoursPerWeek": null}}</c>.
/// A limit of null removes it; an item's level of None takes it off the list. Answers the tab as it now stands.</item>
/// </list>
///
/// WHO MAY DO WHAT is decided twice and the same way: <see cref="TeamEndpointGate"/> from <see cref="TeamEndpointRules"/>
/// before the route runs, and <see cref="TeamRegistry"/> from the role table again before anything is read or saved. The
/// team's own skills and workflows are read inside the team's tenant, entered ONLY when the gate allowed this request in
/// exactly this team (<see cref="TeamLibraryEndpoints.AdmitIntoTeam"/>).
///
/// Mapped only while Teams is released; a dark Gateway answers 404 on both, as on every team route.
/// </summary>
internal static class TeamGovernanceEndpoints
{
    /// <summary>The route.</summary>
    public const string GovernancePath = "/teams/{teamId}/governance";

    /// <summary>Maps both routes.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamRegistry teams, SkillStore skills, WorkflowStore workflows,
        HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        app.MapGet(GovernancePath, (HttpContext ctx, string teamId) =>
        {
            try
            {
                var (team, caller, denial) = TeamLibraryEndpoints.AdmitIntoTeam(ctx, boundary, tenants);
                if (denial is not null) return denial;
                return Read(teams, team!, caller!, ReadLibrary(boundary, team!, skills, workflows));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamGovernanceEndpoints] GET {GovernancePath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "DevThrottle could not read the team's rules just now because of a fault. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPut(GovernancePath, async (HttpContext ctx, string teamId) =>
        {
            try
            {
                var (team, caller, denial) = TeamLibraryEndpoints.AdmitIntoTeam(ctx, boundary, tenants);
                if (denial is not null) return denial;

                JsonDocument body;
                try
                {
                    body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    FileLog.Write($"[TeamGovernanceEndpoints] PUT {GovernancePath}: rejected, the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = BodyHelp });
                }

                using (body)
                {
                    var (request, invalid) = ParseChange(body.RootElement);
                    if (invalid is not null)
                    {
                        FileLog.Write($"[TeamGovernanceEndpoints] PUT {GovernancePath}: rejected, {invalid}");
                        return Results.BadRequest(new { error = invalid });
                    }
                    var library = ReadLibrary(boundary, team!, skills, workflows);
                    return Change(teams, team!, caller!, request!, library);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TeamGovernanceEndpoints] PUT {GovernancePath} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { error = "The team's rules could not be changed just now because of a fault in DevThrottle. Nothing was saved. Try again shortly." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        FileLog.Write($"[TeamGovernanceEndpoints] mapped GET {GovernancePath} and PUT {GovernancePath}");
    }

    /// <summary>What a body that cannot be read is told.</summary>
    internal const string BodyHelp =
        "The request body is not readable. Send a JSON object with any of \"review\", \"agents\", \"items\" and \"limits\".";

    /// <summary>The tab for the caller: 200, 404 for anyone not in the team, 403 with the role table's sentence.</summary>
    internal static IResult Read(TeamRegistry teams, string teamId, string callerSubject, IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        var result = teams.DescribeTeamGovernance(teamId, callerSubject, library);
        switch (result.Outcome)
        {
            case TeamGovernanceViewOutcome.NotFound:
                FileLog.Write($"[TeamGovernanceEndpoints] GET {GovernancePath}: no such team for this caller");
                return Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal });
            case TeamGovernanceViewOutcome.Forbidden:
                FileLog.Write($"[TeamGovernanceEndpoints] GET {GovernancePath}: refused for this role");
                return Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden);
        }
        return Results.Json(result.View);
    }

    /// <summary>One change as HTTP: the tab as it now stands, or 400, 403, 404 or 409 with the sentence to show.</summary>
    internal static IResult Change(TeamRegistry teams, string teamId, string callerSubject, TeamGovernanceChangeRequest request,
        IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        var result = teams.ChangeTeamGovernance(teamId, callerSubject, request, library);
        FileLog.Write($"[TeamGovernanceEndpoints] PUT {GovernancePath}: outcome={result.Outcome} changes={result.Changes}");
        return result.Outcome switch
        {
            TeamGovernanceChangeOutcome.Done => Read(teams, teamId, callerSubject, library),
            TeamGovernanceChangeOutcome.NotFound => Results.NotFound(new { error = TeamEndpoints.NoSuchTeamRefusal }),
            TeamGovernanceChangeOutcome.Forbidden => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status403Forbidden),
            TeamGovernanceChangeOutcome.Invalid => Results.BadRequest(new { error = result.Refusal }),
            TeamGovernanceChangeOutcome.Conflict => Results.Json(new { error = result.Refusal }, statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unknown governance change outcome {result.Outcome}."),
        };
    }

    /// <summary>The team's OWN skills and workflows - never the built-ins - read inside the team's tenant.</summary>
    private static IReadOnlyList<TeamGovernanceLibraryEntry> ReadLibrary(HostedTenantBoundary boundary, string teamId, SkillStore skills, WorkflowStore workflows)
    {
        using (boundary.EnterScope(new TenantId(teamId)))
        {
            return skills.ListPublished().Where(s => !s.IsBuiltIn)
                .Select(s => new TeamGovernanceLibraryEntry(TeamGovernanceCatalog.KindSkill, s.Id, s.Name))
                .Concat(workflows.ListPublished().Where(w => !w.IsBuiltIn)
                    .Select(w => new TeamGovernanceLibraryEntry(TeamGovernanceCatalog.KindWorkflow, w.Id, w.Name)))
                .ToList();
        }
    }

    /// <summary>
    /// The body as a change, or the sentence saying what is wrong with it. Pure, so every branch is tested. A key that is
    /// present with a value of the wrong type is refused, never skipped: a change that silently lost a part would read as
    /// saved.
    /// </summary>
    internal static (TeamGovernanceChangeRequest? Request, string? Invalid) ParseChange(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, BodyHelp);

        var review = new Dictionary<string, bool>(StringComparer.Ordinal);
        var agents = new Dictionary<string, bool>(StringComparer.Ordinal);
        var items = new List<TeamGovernanceItemChange>();
        var limits = new Dictionary<string, int?>(StringComparer.Ordinal);

        foreach (var section in root.EnumerateObject())
        {
            switch (section.Name)
            {
                case "review":
                case "agents":
                    if (section.Value.ValueKind != JsonValueKind.Object)
                        return (null, $"\"{section.Name}\" must be an object of rule names to true or false.");
                    foreach (var rule in section.Value.EnumerateObject())
                    {
                        if (rule.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return (null, $"\"{rule.Name}\" must be true or false.");
                        (section.Name == "review" ? review : agents)[rule.Name] = rule.Value.GetBoolean();
                    }
                    break;
                case "limits":
                    if (section.Value.ValueKind != JsonValueKind.Object)
                        return (null, "\"limits\" must be an object of limit names to a whole number, or null for no limit.");
                    foreach (var limit in section.Value.EnumerateObject())
                    {
                        if (limit.Value.ValueKind == JsonValueKind.Null)
                            limits[limit.Name] = null;
                        else if (limit.Value.ValueKind == JsonValueKind.Number && limit.Value.TryGetInt32(out var n))
                            limits[limit.Name] = n;
                        else
                            return (null, $"\"{limit.Name}\" must be a whole number, or null for no limit.");
                    }
                    break;
                case "items":
                    if (section.Value.ValueKind != JsonValueKind.Array)
                        return (null, "\"items\" must be a list of {\"kind\", \"id\", \"level\"}.");
                    foreach (var item in section.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !TryString(item, "kind", out var kind) || !TryString(item, "id", out var id) || !TryString(item, "level", out var level))
                            return (null, "Each item must name its \"kind\", \"id\" and \"level\".");
                        items.Add(new TeamGovernanceItemChange(kind, id, level));
                    }
                    break;
                default:
                    return (null, $"\"{section.Name}\" is not part of the team's rules. Send any of \"review\", \"agents\", \"items\" and \"limits\".");
            }
        }

        return (new TeamGovernanceChangeRequest(review, agents, items, limits), null);
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString()!.Trim();
        return value.Length > 0;
    }
}
