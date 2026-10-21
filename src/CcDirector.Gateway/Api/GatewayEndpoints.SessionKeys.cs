using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

internal static partial class GatewayEndpoints
{
    /// <summary>The command the owning Director receives to press one Enter in a session, nothing typed before it.</summary>
    internal const string EnterCommand = "enter";

    /// <summary>The command the owning Director receives to press Escape in a session.</summary>
    internal const string EscapeCommand = "escape";

    /// <summary>
    /// <c>POST /sessions/{sid}/enter</c> and <c>POST /sessions/{sid}/escape</c>, the handler: one key pressed in a session.
    ///
    /// WHO MAY PRESS THEM (the owner's ruling, 21 October 2026): "the fleet manager can do it for any session, and other
    /// agents can press escape and [enter] in the session they started, or the sessions they own." The owner's own
    /// screens - a device key - press them in any session of the account, as the phone always has. A raised session (the
    /// Fleet Manager) is the owner's own grant and is not held to ownership. Every other session key presses them only in
    /// a session it owns directly (<see cref="OwnedSessionInput.OwnsDirectly"/>), the same rule as the prompt route. The
    /// guard let the path through for every session key because it cannot read an id; here the target is in hand.
    ///
    /// Every press is written to the log with the session that pressed it. A refusal the Director returns - for Enter,
    /// Claude Code's usage-limit dialog, where Enter would confirm a paid option - comes back in the Director's words.
    /// </summary>
    internal static async Task<IResult> PressSessionKeyAsync(
        HttpContext ctx,
        string sid,
        string command,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        DirectorRegistry registry,
        Streaming.PushedSessionStore? pushedSessions,
        TimeSpan streamStale,
        SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        if (command is not (EnterCommand or EscapeCommand))
            throw new ArgumentOutOfRangeException(nameof(command), command, "only enter and escape are single keys");

        var caller = AuthMiddleware.CallingSession(ctx);
        var raised = AuthMiddleware.RaisedGrantOf(ctx) is not null;
        var who = caller is null ? "(the owner's device)" : caller.SessionId.ToString();
        FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} caller={who} raised={raised}");

        var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStale, owners);
        if (session is null || director is null)
        {
            FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} REFUSED: session not located");
            return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
        }

        if (caller is not null && !raised && !OwnedSessionInput.OwnsDirectly(caller.SessionId.ToString(), session))
        {
            FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} REFUSED: caller={caller.SessionId} "
                + $"does not own it (owner={session.ControllerSessionId ?? "(the owner)"})");
            return Results.Json(new { error = AgentInputRefusal.KeysNotYourSession, code = WheelRefusalCodes.NotYourSession },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
        var result = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, command, sid, null,
            CancellationToken.None, machineName: director.MachineName);
        if (result is null)
        {
            FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} NOT DELIVERED: director={director.DirectorId} is not connected");
            return TunnelFailure(null);
        }

        if (!result.Ok)
        {
            FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} caller={who} REFUSED by director={director.DirectorId}: "
                + DirectorCommandRouter.DescribeFailure(result));
            // Escape keeps the answer it has always given (a Director's refusal as a 502 carrying its words); Enter is new
            // and answers a refusal as the caller's situation - 409 on the usage-limit dialog.
            return command == EscapeCommand ? TunnelFailure(result) : MapDirectorFailure(result);
        }

        FileLog.Write($"[GatewayEndpoints] PressSessionKeyAsync: sid={sid} key={command} pressed by caller={who} raised={raised} on director={director.DirectorId}");
        return Results.Json(new { accepted = true });
    }
}
