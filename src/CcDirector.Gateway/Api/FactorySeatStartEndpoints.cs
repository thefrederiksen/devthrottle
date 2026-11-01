using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A BOSS STARTS A SEAT OF ITS OWN FACTORY: <c>cc-devthrottle factory seat start</c>.
///
///   POST /gateway/factory/seats/{seat}/start   body FactorySeatStartRequest -> 201 FactoryTalkStartedDto | 400 | 403 | 404 | 409 | 502
///
/// The rule is <see cref="FactorySeatDelegation"/>. The factory is the calling session's own, read from its history row;
/// the body carries a name and a job and nothing else, so a Boss has nothing to set but those. The create is built by
/// <see cref="FactorySeatStart.Build"/>, as a schedule's is, and goes to a Director on the seat's own computer, as a talk's
/// does; never one on another computer. The Boss owns the session it started, and the seat comes back to it.
///
/// Mapped into <see cref="FactoryAgentsGate"/>'s group, so it answers 404 while the factory agents switch is off.
/// </summary>
internal static class FactorySeatStartEndpoints
{
    public const string Route = "/gateway/factory/seats/{seat}/start";

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    /// <param name="sessionFactoryOf">A session's recorded factory, seat and kit, from its history row.</param>
    /// <param name="definitionOf">One factory's published definition, or null when it has none.</param>
    /// <param name="startOnDirector">Start a session on one Director, through the door the talk uses.</param>
    /// <param name="record">Write the factory activity row for one delegation.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        Func<string, SessionFactoryLookup> sessionFactoryOf, FactoryRegistryStore registry,
        Func<TenantId, string, PublishedFactoryDefinition?> definitionOf,
        Func<TenantId, IEnumerable<DirectorDto>> listDirectors,
        Func<HttpContext, string, NewSessionRequest, Task<DirectorSpawnOutcome>> startOnDirector,
        Action<TenantId, AppendFactoryActivityRequest, string> record)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(sessionFactoryOf);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(definitionOf);
        ArgumentNullException.ThrowIfNull(listDirectors);
        ArgumentNullException.ThrowIfNull(startOnDirector);
        ArgumentNullException.ThrowIfNull(record);

        app.MapPost(Route, async (HttpContext ctx, string seat) =>
        {
            FileLog.Write($"[FactorySeatStartEndpoints] POST start: seat={seat}");
            if (AuthMiddleware.CallingSession(ctx) is not { } session)
                return Refused(StatusCodes.Status403Forbidden, "a seat is started here by a factory's Boss session, with its own session key");
            if (resolveTenant(ctx) is not { } tenant)
                return Refused(StatusCodes.Status403Forbidden, "no account is bound to this request");

            var (body, badBody) = await ReadBodyAsync(ctx);
            if (badBody is not null) return badBody;

            var callerId = session.SessionId.ToString();
            var caller = sessionFactoryOf(callerId);
            if (!caller.IsKnown)
                return Results.Json(new { error = SpawnFactory.NotYetKnown, detail = SpawnFactory.NotYetKnownDetail },
                    statusCode: StatusCodes.Status409Conflict);

            PublishedFactoryDefinition? published;
            try
            {
                published = caller.IsInAFactory ? definitionOf(tenant, caller.Factory!) : null;
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] REFUSED: the definition of {caller.Factory} could not be read: {ex.Message}");
                return Refused(StatusCodes.Status409Conflict, $"No session was started: {ex.Message}.");
            }

            if (FactorySeatDelegation.RefuseCaller(caller, published) is { } notABoss)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] REFUSED session {callerId} (seat {caller.Seat ?? "none"}, kit {caller.Kit ?? "none"}): {notABoss}");
                return Refused(StatusCodes.Status403Forbidden, notABoss);
            }

            var wanted = (seat ?? "").Trim();
            if (FactorySeatDelegation.RefuseTarget(published!, wanted) is { } notThisSeat)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] REFUSED seat '{wanted}' for session {callerId}: {notThisSeat}");
                return Refused(StatusCodes.Status409Conflict, notThisSeat);
            }

            if (registry.Find(tenant, caller.Factory!) is not { } registered)
                return Refused(StatusCodes.Status404NotFound, $"'{caller.Factory}' is not a registered factory of this account, so no session was started.");
            if (registered.ArchivedAtUtc is not null)
                return Refused(StatusCodes.Status409Conflict, $"'{registered.Factory}' is archived, so no session was started. The owner restores it from the Factories screen.");
            var seated = registered.Seats.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.Ordinal));
            if (seated is null)
                return Refused(StatusCodes.Status409Conflict, $"The seat '{wanted}' is published but not registered on {registered.Title}, so no session was started. Publish the definition again.");

            var target = published!.Seat(wanted)!;
            var name = FactorySeatDelegation.SessionName(registered, seated, body!.Name!);
            var built = FactorySeatStart.Build(published, target, name,
                FactorySeatDelegation.FirstPrompt(registered, seated, published.BriefOf(target), body.Prompt!));
            built.Request.ControllerSessionId = callerId;
            built.Request.ParentSessionId = callerId;

            // Never a Director on another computer: the seat's repository, secrets and tools are on its own.
            var director = listDirectors(tenant).FirstOrDefault(d =>
                d.StoppedAtUtc is null && string.Equals(d.MachineName, built.Machine, StringComparison.OrdinalIgnoreCase));
            if (director is null)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] REFUSED: no Director running on {built.Machine} for {registered.Factory}/{wanted}");
                return Refused(StatusCodes.Status409Conflict,
                    $"No Director is running on {built.Machine}, the computer the seat {wanted} runs on, so no session was started.");
            }

            SpawnFactory.MarkGatewaySeatStart(ctx);
            var outcome = await startOnDirector(ctx, director.DirectorId, built.Request);
            if (outcome.Session is not { } started)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] the create on {director.DirectorId} was refused for {registered.Factory}/{wanted}");
                return outcome.Refusal!;
            }

            // The session exists, so a failed row must not read as a failed start: that would be retried into a second
            // session. The history row holds the seat, the kit and the Boss as parent and owner either way.
            try
            {
                record(tenant, FactorySeatDelegation.ActivityRow(registered.Factory, caller.Seat!, callerId, wanted,
                    published.Version, started.SessionId, name), "session:" + callerId);
            }
            catch (FactoryActivityValidationException ex)
            {
                FileLog.Write($"[FactorySeatStartEndpoints] the activity row for {started.SessionId} FAILED: {ex.Message}");
            }

            FileLog.Write($"[FactorySeatStartEndpoints] session {callerId} started {registered.Factory}/{wanted} v{published.Version} as {started.SessionId} on {built.Machine}");
            return Results.Json(new FactoryTalkStartedDto
            {
                SessionId = started.SessionId,
                SessionName = name,
                Href = "/session/" + Uri.EscapeDataString(started.SessionId),
                Factory = registered.Factory,
                Seat = wanted,
                Computer = built.Machine,
                DirectorId = director.DirectorId,
                BriefVersion = published.Version,
                BriefVersionKind = FactoryBriefVersionKinds.Definition,
            }, statusCode: StatusCodes.Status201Created);
        });

        FileLog.Write($"[FactorySeatStartEndpoints] mapped {Route}");
    }

    private static async Task<(FactorySeatStartRequest? Body, IResult? Refusal)> ReadBodyAsync(HttpContext ctx)
    {
        FactorySeatStartRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<FactorySeatStartRequest>(ctx.Request.Body, BodyJson, ctx.RequestAborted);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[FactorySeatStartEndpoints] REFUSED: bad JSON: {ex.Message}");
            return (null, Refused(StatusCodes.Status400BadRequest, "the body is not JSON with a name and a prompt"));
        }
        if (string.IsNullOrWhiteSpace(body?.Name) || string.IsNullOrWhiteSpace(body.Prompt))
            return (null, Refused(StatusCodes.Status400BadRequest, "a seat start needs a name and a prompt that holds the whole job"));
        return (body, null);
    }

    private static IResult Refused(int status, string error) => Results.Json(new { error }, statusCode: status);
}
