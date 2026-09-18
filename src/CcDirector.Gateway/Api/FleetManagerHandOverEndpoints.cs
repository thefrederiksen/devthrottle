using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Hand over (the Fleet Manager mission, step 8):
///
///   POST /gateway/fleet-manager/hand-over   { "session": "&lt;full id&gt;", "to": "fleet-manager" | "owner" }
///
/// 200 answers <see cref="FleetHandOverResultDto"/>. A refusal answers { "error": "&lt;sentence&gt;" } with 400 (a bad
/// request), 403 (a caller that may not hand this over), 404 (no such running session in this account), 409 (the session
/// or the account does not allow it) or 502 (the Director did not make the change).
///
/// WHO MAY CALL IT. Two callers, and nobody else:
///  - THE OWNER, from their own signed-in phone or browser (<see cref="FleetManagerOwnerDevice"/>, shared with the
///    walkthrough). A Director's own key and the shared machine token are refused with <c>code: "owner_only"</c>.
///  - THE ACCOUNT'S FLEET MANAGER, with its own session key (the Architect's ruling on step 8). It may take to itself a
///    session of its own account that answers to the owner, and hand a session it owns back to the owner - and nothing
///    more: it never takes a session another running session owns, and its key only ever reaches its own account's
///    roster. It does this only when the owner has asked; the Fleet Manager skill says so. Every other session key is
///    refused by <see cref="FleetManagerHandOverService"/> with <c>code: "not_fleet_manager"</c> and the reason, after
///    <see cref="SessionKeyGuard"/> has let the one POST through.
/// </summary>
internal static class FleetManagerHandOverEndpoints
{
    public const string HandOverRoute = FleetManagerEndpoints.Prefix + "/hand-over";

    private static readonly JsonSerializerOptions BodyJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant, FleetManagerHandOverService service)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(service);
        app.MapPost(HandOverRoute, (Func<HttpContext, Task<IResult>>)(ctx => HandOverAsync(ctx, resolveTenant, service)));
        FileLog.Write($"[FleetManagerHandOverEndpoints] mapped {HandOverRoute}");
    }

    internal static async Task<IResult> HandOverAsync(HttpContext ctx, Func<HttpContext, TenantId?> resolveTenant,
        FleetManagerHandOverService service)
    {
        FileLog.Write("[FleetManagerHandOverEndpoints] POST hand-over");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no account is bound to this request");
            string actor;
            string? callingSessionId = null;
            if (AuthMiddleware.CallingSession(ctx) is { } session)
            {
                // A session key names its own account; the account this request resolved to must be that one.
                if (session.Tenant != tenant)
                {
                    FileLog.Write($"[FleetManagerHandOverEndpoints] REFUSED: session {session.SessionId} resolved to another account");
                    return Refuse(StatusCodes.Status403Forbidden, "a session key may only hand over sessions of its own account");
                }
                callingSessionId = session.SessionId.ToString();
                actor = SessionStopFold.ActorFor(callingSessionId, null, null, credentialAuthenticated: true);
            }
            else
            {
                var device = FleetManagerOwnerDevice.Require(ctx, "hand a session over",
                    "Only the owner or the account's Fleet Manager can hand a session over.",
                    nameof(FleetManagerHandOverEndpoints), out var refused);
                if (device is null) return refused!;
                actor = SessionStopFold.ActorFor(null, device.DeviceType, device.DeviceId, credentialAuthenticated: false);
            }

            FleetHandOverRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<FleetHandOverRequest>(ctx.Request.Body, BodyJsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Refuse(StatusCodes.Status400BadRequest, $"The body is not valid JSON: {ex.Message}");
            }

            var result = await service.HandOverAsync(tenant, body, actor, ctx.RequestAborted, callingSessionId);
            FileLog.Write($"[FleetManagerHandOverEndpoints] POST hand-over: status={result.Status}, caller={actor}");
            if (result.Answer is not null)
                return Results.Json(result.Answer, statusCode: result.Status);
            return result.Code is not null
                ? Results.Json(new { code = result.Code, error = result.Error }, statusCode: result.Status)
                : Refuse(result.Status, result.Error ?? "The hand over was refused.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FleetManagerHandOverEndpoints] POST hand-over FAILED: {ex.Message}");
            throw;
        }
    }

    private static IResult Refuse(int status, string sentence)
        => Results.Json(new { error = sentence }, statusCode: status);
}

/// <summary>The production wiring of hand over: the pushed roster, the Director tunnel, the audit trail and the Fleet
/// Manager's events.</summary>
internal sealed class GatewayFleetManagerHandOverEnvironment : IFleetManagerHandOverEnvironment
{
    public required Streaming.PushedSessionStore Pushed { get; init; }
    public required TimeSpan StaleAfter { get; init; }
    public required DirectorRegistry Directors { get; init; }
    public required Streaming.FleetManagerHomeCapabilityRegistry Capabilities { get; init; }
    public required DirectorCommandRouter.SendDirectorCommandAsync SendCommand { get; init; }
    public required Func<TenantId, string?> Mark { get; init; }
    public required Governance.GovernanceAuditLog AuditLog { get; init; }
    public required Func<FleetManagerEventService?> Events { get; init; }
    public required Func<TenantId, IDisposable> EnterTenantScope { get; init; }

    public string? MarkedFleetManager(TenantId tenant) => Mark(tenant);

    public Func<TenantId, string?>? Successor { get; init; }

    public string? WaitingFleetManager(TenantId tenant) => Successor?.Invoke(tenant);

    public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant)
    {
        var roster = Pushed.SnapshotFresh(tenant, StaleAfter);
        FleetRoleResolver.Stamp(roster.Select(r => r.Session).Where(s => s is not null).ToList(), Mark(tenant));
        return roster;
    }

    public bool ChangesOwnerIfExpected(TenantId tenant, string directorId) => Capabilities.ChangesOwnerIfExpected(tenant, directorId);

    public async Task<(SessionDto? Session, string? Error, bool OwnerMoved)> SetControllerAsync(TenantId tenant, string directorId,
        string sessionId, string? expectedControllerSessionId, string? controllerSessionId, CancellationToken ct)
    {
        using var scope = EnterTenantScope(tenant);
        var director = Directors.Get(tenant, directorId);
        var request = new SetControllerRequest
        {
            ControllerSessionId = controllerSessionId,
            ExpectedControllerSessionId = string.IsNullOrWhiteSpace(expectedControllerSessionId)
                ? SetControllerRequest.NoOwner
                : expectedControllerSessionId,
        };
        var result = await DirectorCommandRouter.TrySendAsync(SendCommand, directorId, "set-controller", sessionId,
            request, ct, machineName: director?.MachineName);
        if (result is null) return (null, "its Director is not connected", false);
        if (!result.Ok)
            return (null, result.Error ?? result.Status.ToString(), result.Status == DirectorCommandStatus.Conflict);
        var row = DirectorCommandRouter.ReadBody<SessionDto>(result);
        return row is null ? (null, "its Director answered without the session", false) : (row, null, false);
    }

    public void Audit(TenantId tenant, string sessionId, string actor, string detail)
    {
        using var scope = EnterTenantScope(tenant);
        AuditLog.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = sessionId,
            Category = GovernanceAuditCategory.Intervention,
            EventType = GovernanceAuditEventType.HandedOver,
            Actor = actor,
            Detail = detail.Length > Governance.GovernanceAuditLog.MaxDetailChars
                ? detail[..Governance.GovernanceAuditLog.MaxDetailChars]
                : detail,
        });
    }

    public void OwnerChanged(TenantId tenant, string directorId, SessionDto row)
    {
        var events = Events();
        if (events is null)
        {
            FileLog.Write($"[GatewayFleetManagerHandOverEnvironment] owner of {row.SessionId} changed before the Fleet Manager's " +
                          "events started; the reconcile will follow the new owner when it runs");
            return;
        }
        events.OnOwnerChanged(tenant, directorId, row);
    }
}
