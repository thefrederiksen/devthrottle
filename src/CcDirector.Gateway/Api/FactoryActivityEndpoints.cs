using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The factory activity record (Website Business Factory, product track). Append-only: a row can be recorded
/// and read, never edited or removed, so there is a POST and a GET and deliberately no PUT, PATCH or DELETE.
///
///   POST  /gateway/factory/activity   body AppendFactoryActivityRequest -> 201 FactoryActivityDto | 400
///   GET   /gateway/factory/activity   ?factory=&amp;agent=&amp;outcome=&amp;from=&amp;to=&amp;order=newest|oldest&amp;offset=&amp;limit=
///                                     -> FactoryActivityPage | 400
///
/// MAPPED ONLY WHILE THE SWITCH IS ON (<c>factoryAgents.enabled</c> in the Gateway's config.json, default
/// off). While it is off these paths are not mapped at all and answer 404 - see GatewayHost.
///
/// A session key may call both (SessionKeyGuard). The Gateway stamps the calling session as the actor, and as
/// the row's session, when the caller did not name them.
/// </summary>
internal static class FactoryActivityEndpoints
{
    public const string Route = "/gateway/factory/activity";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, FactoryActivityRecord record)
    {
        app.MapPost(Route, async (HttpContext ctx) =>
        {
            AppendFactoryActivityRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AppendFactoryActivityRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[FactoryActivityEndpoints] POST bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "a factory activity body is required" });

            var session = AuthMiddleware.CallingSession(ctx);
            if (session is not null && string.IsNullOrWhiteSpace(req.SessionId))
                req.SessionId = session.SessionId.ToString();
            var callingActor = session is not null
                ? "session:" + session.SessionId
                : AuthMiddleware.RegisteringCredential(ctx);

            return Guard(() => Results.Json(record.Append(req, callingActor), statusCode: StatusCodes.Status201Created));
        });

        app.MapGet(Route,
            (string? factory, string? agent, string? outcome, DateTime? from, DateTime? to,
             string? order, int? offset, int? limit) =>
                Guard(() =>
                {
                    var o = (order ?? "newest").Trim().ToLowerInvariant();
                    if (o != "newest" && o != "oldest")
                        throw new FactoryActivityValidationException($"order must be 'newest' or 'oldest', not '{order}'.");
                    return Results.Json(record.Query(factory, agent, outcome, from, to,
                        oldestFirst: o == "oldest", offset: offset ?? 0, limit: limit));
                }));

        FileLog.Write($"[FactoryActivityEndpoints] mapped {Route}");
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (FactoryActivityValidationException ex)
        {
            FileLog.Write($"[FactoryActivityEndpoints] refused: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
