using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The factory registry and the goal numbers (Factories screen mission, phase A).
///
///   PUT  /gateway/factory/registry                       body RegisterFactoryRequest -> 200 RegisteredFactoryDto | 400
///   GET  /gateway/factory/registry                       -> FactoryRegistryListDto
///   POST /gateway/factory/goal-numbers                   body PostGoalNumberRequest -> 201 GoalNumberDto | 400 | 403 | 409
///   GET  /gateway/factory/goal-numbers?factory=&amp;count=  -> GoalNumbersDto | 400
///
/// These are the DATA routes the <c>cc-devthrottle factory register</c> and <c>factory goal-number</c> commands call,
/// so they sit beside the other routes a factory's own session calls (<c>/gateway/factory/activity</c>,
/// <c>/gateway/factory/map</c>) and a session key may call all four (SessionKeyGuard). The owner's finished views
/// of the same data live under <c>/gateway/factory-agents</c> and refuse a session key. Behind the factory agents
/// switch per account, like every factory route.
///
/// WHO POSTED A GOAL NUMBER is the seat, and the Gateway settles it where it can: a call from a session a factory
/// agent started (its "started" row in the activity record) is posted by that agent, and naming another seat is
/// refused, as is posting for another factory. Anything else must name the seat.
/// </summary>
internal static class FactoryRegistryEndpoints
{
    public const string RegistryRoute = "/gateway/factory/registry";
    public const string GoalNumbersRoute = "/gateway/factory/goal-numbers";

    /// <summary>How many goal numbers a read returns when the caller names no count.</summary>
    public const int DefaultGoalNumbers = 20;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="sessionStart">For an account and a session id: the activity record's "started" row of that
    /// session, or null when no factory agent started it.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FactoryRegistryStore store,
        Func<TenantId, string, FactoryActivityDto?> sessionStart, Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessionStart);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapPut(RegistryRoute, async (HttpContext ctx) =>
        {
            var (req, bad) = await ReadBody<RegisterFactoryRequest>(ctx, "PUT registry");
            if (bad is not null) return bad;
            return Guard(ctx, resolveTenant, "PUT registry", tenant =>
            {
                var registered = store.Register(tenant, req, Caller(ctx), nowUtc());
                return Results.Json(registered);
            });
        });

        app.MapGet(RegistryRoute, (HttpContext ctx) =>
            Guard(ctx, resolveTenant, "GET registry", tenant =>
            {
                var all = store.List(tenant);
                return Results.Json(new FactoryRegistryListDto { Count = all.Count, Factories = all.ToList() });
            }));

        app.MapPost(GoalNumbersRoute, async (HttpContext ctx) =>
        {
            var (req, bad) = await ReadBody<PostGoalNumberRequest>(ctx, "POST goal-numbers");
            if (bad is not null) return bad;
            return Guard(ctx, resolveTenant, "POST goal-numbers", tenant =>
            {
                var session = AuthMiddleware.CallingSession(ctx);
                var sessionId = session?.SessionId.ToString();
                var postedBy = string.IsNullOrWhiteSpace(req!.PostedBy) ? null : req.PostedBy.Trim();

                if (sessionId is not null && sessionStart(tenant, sessionId) is { } started)
                {
                    // A FACTORY AGENT'S OWN SESSION posts as that agent, for its own factory. Naming another seat
                    // or another factory is refused rather than quietly corrected: the caller meant it, and
                    // answering with a different seat on the owner's screen would be a claim nobody made.
                    if (!SameId(started.Factory, req.Factory))
                        return Refused(StatusCodes.Status403Forbidden,
                            $"This session is the factory agent '{started.FactoryAgent}' of '{started.Factory}', so it may not post a goal number for '{req.Factory}'.");
                    if (postedBy is not null && !SameId(postedBy, started.FactoryAgent))
                        return Refused(StatusCodes.Status400BadRequest,
                            $"This session is the factory agent '{started.FactoryAgent}', so it posts as that seat, not as '{postedBy}'. Leave --by out.");
                    postedBy = started.FactoryAgent;
                }

                var posted = store.PostGoalNumber(tenant, req, postedBy, sessionId, nowUtc());
                return Results.Json(posted, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapGet(GoalNumbersRoute, (HttpContext ctx, string? factory, int? count) =>
            Guard(ctx, resolveTenant, $"GET goal-numbers {factory}", tenant =>
                Results.Json(store.GoalNumbers(tenant, factory, count ?? DefaultGoalNumbers))));

        FileLog.Write($"[FactoryRegistryEndpoints] mapped {RegistryRoute} and {GoalNumbersRoute}");
    }

    private static IResult Guard(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant, string what, Func<TenantId, IResult> handle)
    {
        FileLog.Write($"[FactoryRegistryEndpoints] {what}");
        if (resolveTenant(ctx) is not { } tenant)
            return Refused(StatusCodes.Status403Forbidden, "no account is bound to this request");
        try
        {
            return handle(tenant);
        }
        catch (FactoryViewValidationException ex)
        {
            FileLog.Write($"[FactoryRegistryEndpoints] {what} REFUSED: {ex.Message}");
            return Refused(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (FactoryNotRegisteredException ex)
        {
            // 409, not 404: a 404 from a factory route means the switch is off, and the command says so.
            FileLog.Write($"[FactoryRegistryEndpoints] {what} REFUSED: {ex.Message}");
            return Refused(StatusCodes.Status409Conflict, ex.Message);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryRegistryEndpoints] {what} FAILED: {ex.Message}");
            throw;
        }
    }

    // Who made the call, as the registry records it: a session, or the owner and the credential it came in on.
    private static string Caller(HttpContext ctx)
    {
        var session = AuthMiddleware.CallingSession(ctx);
        return session is not null
            ? "session " + session.SessionId
            : "the owner (" + (AuthMiddleware.RegisteringCredential(ctx) ?? AuthMiddleware.IdentityKind(ctx)) + ")";
    }

    private static async Task<(T? Body, IResult? Error)> ReadBody<T>(HttpContext ctx, string what) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            return body is null ? (null, Refused(StatusCodes.Status400BadRequest, "a body is required")) : (body, null);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[FactoryRegistryEndpoints] {what} bad JSON: {ex.Message}");
            return (null, Refused(StatusCodes.Status400BadRequest, "the body could not be read as JSON: " + ex.Message));
        }
    }

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);

    private static bool SameId(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
