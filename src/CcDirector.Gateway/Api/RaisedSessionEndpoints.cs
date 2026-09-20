using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// RAISE AND LOWER A SESSION (the Fleet Manager Improvement mission, phase 1):
///
///   POST /sessions/{sid}/raise   the owner lets this session act with his permissions inside his account
///   POST /sessions/{sid}/lower   the owner takes that back
///
/// Both answer <see cref="SessionRaiseResponse"/>: the session's raise state after the change, in the finished words
/// its roster row now carries. A refusal answers <c>{ code, error }</c>.
///
/// THE OWNER'S OWN DEVICE, AND NOTHING ELSE. <see cref="SessionKeyGuard"/> lists neither route, so every session key -
/// a raised one included - is refused before this runs: a raised session never raises another, and never itself, and
/// it cannot lower one either. Behind the guard, <see cref="FleetManagerOwnerDevice"/> refuses a Director's key and
/// the shared machine token, which the guard never sees.
///
/// THE ACCOUNT IS THE CALLER'S. The session is looked for inside the account the device key belongs to, so a session
/// of another account answers exactly as an unknown one does.
///
/// BOTH ARE RECORDED, with the device that did it, before the list changes - so there is never a raised session with
/// no record of who raised it.
/// </summary>
internal static class RaisedSessionEndpoints
{
    public const string RaiseRoute = "/sessions/{sid}/raise";
    public const string LowerRoute = "/sessions/{sid}/lower";

    private const string SessionKeySentence =
        "Only the owner, on their own signed-in phone or browser, can raise or lower a session. A raised session never "
        + "raises another session, and never itself.";

    /// <param name="findSession">The last row any Director of this account reported for one session, live or not; null
    /// when the account has no such session.</param>
    public static void Map(IEndpointRouteBuilder app, Func<HttpContext, TenantId?> resolveTenant,
        RaisedSessionStore raised, RaisedSessionRecord record, Func<TenantId, string, SessionDto?> findSession,
        Func<DateTime> nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolveTenant);
        ArgumentNullException.ThrowIfNull(raised);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(findSession);
        ArgumentNullException.ThrowIfNull(nowUtc);

        app.MapPost(RaiseRoute, (HttpContext ctx, string sid) => Change(ctx, sid, raise: true, resolveTenant, raised, record, findSession, nowUtc));
        app.MapPost(LowerRoute, (HttpContext ctx, string sid) => Change(ctx, sid, raise: false, resolveTenant, raised, record, findSession, nowUtc));
        FileLog.Write($"[RaisedSessionEndpoints] mapped {RaiseRoute} and {LowerRoute}");
    }

    internal static IResult Change(HttpContext ctx, string sid, bool raise, Func<HttpContext, TenantId?> resolveTenant,
        RaisedSessionStore raised, RaisedSessionRecord record, Func<TenantId, string, SessionDto?> findSession,
        Func<DateTime> nowUtc)
    {
        var what = raise ? "raise" : "lower";
        FileLog.Write($"[RaisedSessionEndpoints] POST {what}: sid={sid}");
        try
        {
            if (resolveTenant(ctx) is not { } tenant)
                return Refuse(StatusCodes.Status403Forbidden, "no_account", "no account is bound to this request");

            var device = FleetManagerOwnerDevice.Require(ctx, $"{what} a session", SessionKeySentence,
                nameof(RaisedSessionEndpoints), out var refused);
            if (device is null) return refused!;

            if (!Guid.TryParse(sid, out var parsed))
                return Refuse(StatusCodes.Status400BadRequest, "invalid_session_id", $"'{sid}' is not a session id");
            var sessionId = parsed.ToString("D");

            var session = findSession(tenant, sessionId);
            if (session is null)
                return Refuse(StatusCodes.Status404NotFound, "session_not_found",
                    $"No session {sessionId} is known in this account, so nothing was changed.");

            var actor = SessionStopFold.ActorFor(null, device.DeviceType, device.DeviceId, credentialAuthenticated: false);
            if (raise)
            {
                if (FleetManagerSessions.IsGone(session))
                    return Refuse(StatusCodes.Status409Conflict, "session_ended",
                        $"Session {sessionId} has ended, so it was not raised. A raised entry ends with its session.");
                record.Raised(tenant, sessionId, actor, "the owner raised it from their own device");
                raised.Raise(tenant, sessionId, actor, nowUtc());
            }
            else
            {
                record.Lowered(tenant, sessionId, actor, "the owner lowered it from their own device");
                raised.Lower(tenant, sessionId);
            }

            var state = RaisedSessionRosterFold.For(session, raised.IsRaised(tenant, sessionId));
            FileLog.Write($"[RaisedSessionEndpoints] POST {what}: sid={sessionId}, by={actor}, raised={state.Raised}");
            return Results.Json(new SessionRaiseResponse { SessionId = sessionId, Raise = state });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RaisedSessionEndpoints] POST {what} FAILED: {ex.Message}");
            throw;
        }
    }

    private static IResult Refuse(int status, string code, string sentence)
        => Results.Json(new { code, error = sentence }, statusCode: status);
}
