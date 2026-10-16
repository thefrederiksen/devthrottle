using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>The verbs only an API-mode session has (mission "API mode for agent wrappers", issue #4070).</summary>
internal enum ApiModeVerb
{
    /// <summary>The user takes the wheel: the Director stops the protocol process and opens the agent's real terminal
    /// on the same conversation.</summary>
    TakeWheel,

    /// <summary>The user hands it back: the Director closes the terminal and resumes the conversation over the protocol.</summary>
    HandBackWheel,

    /// <summary>Stop the running turn: the Director asks the agent, in its own protocol, to end the turn (for Pi, its
    /// <c>abort</c> command), and the agent confirms it.</summary>
    StopTurn,
}

/// <summary>
/// What the Gateway knows about a session's connection and its wheel, read from the session the owning Director pushed.
///
/// THE ONE PLACE THIS IS READ. How the Gateway learns the mode and the holder was not settled when the verb was built
/// (issue #4070, phase 2): it starts against <see cref="SessionDto.ConnectionMode"/> and
/// <see cref="SessionDto.WheelHolder"/>, and if the source moves, this reader is the only thing that changes.
/// </summary>
/// <param name="IsApiMode">True when the Director drives the session over the agent's protocol.</param>
/// <param name="Holder">Who holds the conversation, one of <see cref="WheelHolders"/>; empty for a terminal session.</param>
/// <param name="Problem">Set when the pushed session could not be read: a value the Gateway does not know, or an
/// API-mode session that names no holder. Never guessed past.</param>
internal readonly record struct SessionWheelState(bool IsApiMode, string Holder, string? Problem)
{
    /// <summary>
    /// Read one pushed session. A Director that predates API mode pushes neither field, and the contract's defaults read
    /// that row as <see cref="ConnectionModes.Terminal"/>, which every session it runs is. An explicit null or empty
    /// mode, which the defaults cannot catch, is read the same way.
    /// </summary>
    public static SessionWheelState Read(SessionDto session)
    {
        var mode = session.ConnectionMode;
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, ConnectionModes.Terminal, StringComparison.OrdinalIgnoreCase))
            return new SessionWheelState(false, "", null);

        if (!string.Equals(mode, ConnectionModes.Api, StringComparison.OrdinalIgnoreCase))
            return new SessionWheelState(false, "",
                $"The machine running this session reported a connection mode the Gateway does not know (\"{mode}\"), "
                + "so it cannot tell whether there is a wheel to take. Update the Gateway or that Director so they agree.");

        var holder = session.WheelHolder;
        foreach (var known in new[] { WheelHolders.None, WheelHolders.Protocol, WheelHolders.User })
            if (string.Equals(holder, known, StringComparison.OrdinalIgnoreCase))
                return new SessionWheelState(true, known, null);

        return new SessionWheelState(true, "",
            string.IsNullOrEmpty(holder)
                ? "The machine running this session reported it in API mode but did not say who holds the wheel, so "
                  + "nothing was sent. Try again in a few seconds; if it persists, that Director needs updating."
                : $"The machine running this session reported a wheel holder the Gateway does not know (\"{holder}\"), "
                  + "so nothing was sent. Update the Gateway or that Director so they agree.");
    }
}

internal static partial class GatewayEndpoints
{
    /// <summary>The command the owning Director receives to take the wheel (agreed with the Director side, phase 4).</summary>
    internal const string WheelTakeCommand = "wheel-take";

    /// <summary>The command the owning Director receives to hand the wheel back.</summary>
    internal const string WheelHandBackCommand = "wheel-hand-back";

    /// <summary>The command the owning Director receives to stop the running turn of an API-mode session. It maps to the
    /// agent's own stop (for Pi, <c>abort</c>). The name is being confirmed with the Director side.</summary>
    internal const string TurnStopCommand = "turn-stop";

    /// <summary>The refusal codes these verbs answer with. A client shows the <c>error</c> sentence verbatim; the code
    /// is for a client or a test that needs to tell the refusals apart without reading the words.</summary>
    internal static class WheelRefusalCodes
    {
        public const string NotApiMode = "not_api_mode";
        public const string AlreadyHeld = "wheel_already_held";
        public const string NotHeld = "wheel_not_held";
        public const string UnreadableState = "wheel_state_unreadable";
        public const string NotYourSession = "not_your_session";
    }

    internal const string WheelNotApiModeReason =
        "This session is in terminal mode, so there is no wheel to take or hand back: you already type into its "
        + "terminal directly. Take the wheel is only for a session started in API mode.";

    internal const string StopNotApiModeReason =
        "This session is in terminal mode, so Stop over the agent's protocol does not apply. To stop its turn, interrupt "
        + "it instead (cc-devthrottle session interrupt, or Ctrl+C in its terminal).";

    internal const string WheelAlreadyHeldReason =
        "You already have the wheel of this session: its terminal is open on the conversation. Hand it back first "
        + "if you want the session to continue in API mode.";

    internal const string WheelNotHeldReason =
        "You do not have the wheel of this session, so there is nothing to hand back: it is still running in API mode. "
        + "Take the wheel first if you want to type into its terminal.";

    /// <summary>
    /// <c>POST /sessions/{sid}/wheel/take</c>, <c>POST /sessions/{sid}/wheel/hand-back</c> and
    /// <c>POST /sessions/{sid}/turn/stop</c>, the handler. Held here rather than inline so every exit can be driven by a
    /// unit test with no booted host.
    ///
    /// It locates the session exactly as interrupt does (scoped to the caller's account). A SESSION KEY may act only on a
    /// session it controls (the owner's ruling on review finding 1: the prompt route's owned-session rule,
    /// <see cref="OwnedSessionInput.OwnsDirectly"/>); a raised session is the owner's own grant and is not held to it,
    /// exactly as on the prompt route. It then RULES on the Gateway what it can see from the session the Director
    /// pushed - not API mode, take while already held, hand back while the protocol holds it - and sends the rest to
    /// the owning Director. A refusal the Director itself returns (for example a hand back while the terminal is
    /// mid-turn) is passed through in the Director's own words with its own status, and a Director that is not
    /// connected is answered the way interrupt answers it.
    ///
    /// The Gateway's ruling reads a pushed state, which can be a push behind the Director. In one direction that is
    /// safe on its own: a request the stale state lets through is refused by the Director, which holds the ownership
    /// lock, and that refusal comes back through here verbatim. In the other direction it would not be: a hand back
    /// sent the moment a take was answered would read the old holder and be refused with a sentence that is untrue.
    /// That is closed by a CONTRACT ON THE DIRECTOR (review finding 3, carried to phase 4): before it returns its reply
    /// to <c>wheel-take</c> or <c>wheel-hand-back</c>, the Director has finished sending <c>PushDelta</c> with the
    /// session's new holder on the same hub connection (awaited, not the fire-and-forget <c>NotifyDelta</c>). The reply
    /// is a client result on that connection and the hub runs one call per connection at a time, so the Gateway has
    /// applied the new holder before the caller hears the reply. The Gateway's ruling exists so the common refusals are
    /// answered at once and in one set of words for every client (CLAUDE.md rule 7).
    /// </summary>
    internal static async Task<IResult> RunApiModeVerbAsync(
        HttpContext ctx,
        string sid,
        ApiModeVerb verb,
        Tenancy.HostedTenantBoundary? tenantBoundary,
        DirectorRegistry registry,
        Streaming.PushedSessionStore? pushedSessions,
        TimeSpan streamStale,
        SessionOwnerCache? owners,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        var command = verb switch
        {
            ApiModeVerb.TakeWheel => WheelTakeCommand,
            ApiModeVerb.HandBackWheel => WheelHandBackCommand,
            ApiModeVerb.StopTurn => TurnStopCommand,
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown API-mode verb"),
        };
        var caller = AuthMiddleware.CallingSession(ctx);
        var raised = AuthMiddleware.RaisedGrantOf(ctx) is not null;
        FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} command={command} "
            + $"caller={(caller is null ? "(the owner's device)" : caller.SessionId.ToString())} raised={raised}");

        var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStale, owners);
        if (session is null || director is null)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED: session not located");
            return SessionUnavailable(ctx, tenantBoundary, pushedSessions, sid);
        }

        // A SESSION KEY ACTS ONLY ON A SESSION IT CONTROLS. Checked before anything about the session's state is said,
        // so a caller that does not control the session learns nothing about its wheel. The guard let the path through
        // for every session key because it cannot read an id; here the target is in hand.
        if (caller is not null && !raised && !OwnedSessionInput.OwnsDirectly(caller.SessionId.ToString(), session))
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED: caller={caller.SessionId} "
                + $"does not control it (owner={session.ControllerSessionId ?? "(the owner)"})");
            return WheelRefusal(WheelRefusalCodes.NotYourSession, AgentInputRefusal.ApiModeNotYourSession, StatusCodes.Status403Forbidden);
        }

        var state = SessionWheelState.Read(session);
        // Stop needs only the mode: a holder the Gateway cannot read must never keep a user from stopping a turn.
        var problem = verb == ApiModeVerb.StopTurn && state.IsApiMode ? null : state.Problem;
        if (problem is not null)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync FAILED: sid={sid} verb={verb} mode={session.ConnectionMode ?? "(none)"} "
                + $"holder={session.WheelHolder ?? "(none)"}: {problem}");
            return WheelRefusal(WheelRefusalCodes.UnreadableState, problem, StatusCodes.Status502BadGateway);
        }

        if (!state.IsApiMode)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED: terminal mode (mode={session.ConnectionMode ?? "(none)"})");
            return WheelRefusal(WheelRefusalCodes.NotApiMode,
                verb == ApiModeVerb.StopTurn ? StopNotApiModeReason : WheelNotApiModeReason, StatusCodes.Status409Conflict);
        }

        if (verb == ApiModeVerb.TakeWheel && state.Holder == WheelHolders.User)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED: the user already holds the wheel");
            return WheelRefusal(WheelRefusalCodes.AlreadyHeld, WheelAlreadyHeldReason, StatusCodes.Status409Conflict);
        }

        // Hand back is refused only while the PROTOCOL holds the conversation: that is the one holder that says the
        // session is running in API mode, which is what the refusal's sentence tells the user. A holder of none (no
        // process holds it - for example the terminal the user took ended on its own) goes to the Director, exactly as
        // a take on none does: what none means after the user held the wheel is the Director's to rule, and hand back
        // may be the very verb that resumes the session (review finding 2).
        if (verb == ApiModeVerb.HandBackWheel && state.Holder == WheelHolders.Protocol)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED: the protocol holds the wheel, not the user");
            return WheelRefusal(WheelRefusalCodes.NotHeld, WheelNotHeldReason, StatusCodes.Status409Conflict);
        }

        var result = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, command, sid, null,
            CancellationToken.None, machineName: director.MachineName);
        if (result is null)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} NOT DELIVERED: director={director.DirectorId} is not connected");
            return TunnelFailure(null);
        }

        if (!result.Ok)
        {
            FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} REFUSED by director={director.DirectorId}: "
                + DirectorCommandRouter.DescribeFailure(result));
            return MapDirectorFailure(result);
        }

        FileLog.Write($"[GatewayEndpoints] RunApiModeVerbAsync: sid={sid} verb={verb} accepted by director={director.DirectorId}");
        return Results.Json(new { accepted = true });
    }

    private static IResult WheelRefusal(string code, string reason, int status)
        => Results.Json(new { error = reason, code }, statusCode: status);
}
