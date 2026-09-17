using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The OWNER area of the Director's tunnel command surface (the Fleet Manager mission, step 8) - one verb,
/// <c>set-controller</c>, through which the Gateway hands an existing session to another owning session, or back to
/// the user.
///
/// The owner used to be set only at birth (<c>--controlled-by</c> on a spawn). It lives here, on the Director's
/// <see cref="Core.Sessions.Session"/>, and the Gateway reads it off every pushed row, so a change is made here and
/// pushed straight back up. The Gateway makes every decision first - the account, the caller, the Fleet Manager, the
/// current owner - and this Director stores the answer. A Director says on connecting that it has this verb
/// (<see cref="DirectorStreamHello.ChangesOwner"/>), and the Gateway refuses a hand over to one that did not.
/// </summary>
internal sealed class SessionOwnerExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "set-controller",
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(command.Verb switch
        {
            "set-controller" => SetController(context, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the session owner area"),
        });
    }

    /// <summary>
    /// The <c>set-controller</c> verb: the session named by the command is owned by the payload's session from now on,
    /// or by the user when that is null or blank. Answers the session as it now is, through the same
    /// <see cref="ControlEndpoints.Map"/> the stream snapshot uses. An id that is not a session id is refused, never
    /// dropped - dropping it would hand the session to the user when the Gateway asked for an owner.
    /// </summary>
    internal static DirectorCommandResult SetController(SessionCommandContext context, DirectorCommand command)
    {
        FileLog.Write($"[SessionOwnerExecutor] set-controller: session={command.SessionId}");
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<SetControllerRequest>(command.PayloadJson);
        if (request is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "an owner payload is required");

        Guid? controller = null;
        if (!string.IsNullOrWhiteSpace(request.ControllerSessionId))
        {
            if (!Guid.TryParse(request.ControllerSessionId, out var parsed))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"controllerSessionId '{request.ControllerSessionId}' is not a session id");
            if (parsed == guid)
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                    $"session {guid} cannot own itself");
            controller = parsed;
        }

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var changed = session.SetController(controller);
        FileLog.Write($"[SessionOwnerExecutor] set-controller: session={guid}, owner={controller?.ToString() ?? "(the user)"}, changed={changed}");
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ControlEndpoints.Map(session, context.DirectorId)));
    }
}
