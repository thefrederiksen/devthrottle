using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator routes that fill ONE named team so it can be shown as it is, and empty it again (owner, 8 Oct 2026;
/// <see cref="TeamShowcase"/>):
///
///   POST /gateway/admin/team-showcase         { team, tag, actor, reason, content }  -> { outcome, team, tag, written }
///   POST /gateway/admin/team-showcase/remove  { team, tag, actor, reason }           -> { outcome, team, tag, removed }
///
/// AUTHORIZATION IS THE SAME ADMIN SERVICE TOKEN THE TRIAL, TURN-LOG, ACCOUNT LOOKUP AND FACTORY SWITCH SURFACES USE,
/// called rather than copied (<see cref="AdminTrialEndpoint.ServiceTokenDenial"/>). The gate runs before the body is read.
/// A self-hosted Gateway refuses after the gate: it has no teams.
///
/// EVERY CALL NAMES A PERSON AND A REASON, both required, and both are written to the Gateway log with the counts.
/// The script that calls these routes is <c>scripts/showcase/team-showcase.py</c>.
/// </summary>
internal static class AdminTeamShowcaseEndpoint
{
    /// <summary>The write route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/team-showcase";

    /// <summary>The removal route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string RemovePath = "/gateway/admin/team-showcase/remove";

    internal const string OutcomeWritten = "written";
    internal const string OutcomeRemoved = "removed";
    internal const string OutcomeUnknown = "unknown";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the caller sends. <see cref="Content"/> is required on the write and ignored on the removal.</summary>
    internal sealed record ShowcaseRequest(
        [property: JsonPropertyName("team")] string? Team,
        [property: JsonPropertyName("tag")] string? Tag,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("content")] TeamShowcaseContent? Content);

    public static void Map(IEndpointRouteBuilder app, bool hosted, TeamShowcase showcase)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(showcase);

        app.MapPost(Path, (Delegate)((HttpContext ctx) => Run(ctx, showcase, remove: false, hosted)));
        app.MapPost(RemovePath, (Delegate)((HttpContext ctx) => Run(ctx, showcase, remove: true, hosted)));
        FileLog.Write($"[AdminTeamShowcaseEndpoint] mapped {Path} and {RemovePath} (service-token authorized)");
    }

    private static async Task<IResult> Run(HttpContext ctx, TeamShowcase showcase, bool remove, bool hosted)
    {
        try
        {
            // THE GATE COMES FIRST, BEFORE THE BODY IS READ: until it runs the request is an anonymous one off the internet.
            if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

            ShowcaseRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ShowcaseRequest>(Json, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminTeamShowcaseEndpoint] rejected: the request body is not readable JSON ({ex.GetType().Name})");
                return Results.BadRequest(new { error = "the request body is not readable JSON" });
            }

            return Handle(ctx, body, showcase, remove, hosted);
        }
        catch (Exception ex)
        {
            // UNKNOWN, not a refusal: we do not know how much landed, and an administrator told "refused" would run it again.
            FileLog.Write($"[AdminTeamShowcaseEndpoint] {(remove ? RemovePath : Path)} FAILED ({ex.GetType().Name}): {ex.Message} - answering UNKNOWN");
            return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Internal so every refusal is tested without a host.</summary>
    internal static IResult Handle(HttpContext ctx, ShowcaseRequest? body, TeamShowcase showcase, bool remove, bool hosted)
    {
        if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        if (!hosted)
        {
            FileLog.Write("[AdminTeamShowcaseEndpoint] DENIED: this is a self-hosted Gateway - it has no teams");
            return Results.Json(new { error = "this Gateway is self-hosted, so it has no teams to fill" },
                statusCode: StatusCodes.Status409Conflict);
        }

        if (body is null) return Results.BadRequest(new { error = "a request body is required" });
        if (string.IsNullOrWhiteSpace(body.Team)) return Results.BadRequest(new { error = "a team id is required" });
        if (string.IsNullOrWhiteSpace(body.Actor))
            return Results.BadRequest(new { error = "an actor is required: every change records who made it" });
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "a reason is required: it is where the change is explained" });
        if (!remove && body.Content is null)
            return Results.BadRequest(new { error = "content is required: the members, Mentor blocks, reports and requests to write" });

        var team = body.Team.Trim();
        var result = remove ? showcase.Remove(team, body.Tag ?? "") : showcase.Write(team, body.Tag ?? "", body.Content!);
        if (result.Refusal is not null)
        {
            FileLog.Write($"[AdminTeamShowcaseEndpoint] {(remove ? "remove" : "write")} REFUSED: {result.Refusal}");
            return Results.BadRequest(new { error = result.Refusal });
        }

        FileLog.Write($"[AdminTeamShowcaseEndpoint] {(remove ? "remove" : "write")} by actor=\"{body.Actor}\" reason=\"{body.Reason}\" tag={body.Tag}");
        return remove
            ? Results.Json(new { outcome = OutcomeRemoved, team, tag = body.Tag, removed = result.Counts })
            : Results.Json(new { outcome = OutcomeWritten, team, tag = body.Tag, written = result.Counts });
    }
}
