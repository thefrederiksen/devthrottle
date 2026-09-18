using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The Message Load mission, slice 2: the DOORBELL area of the Director's tunnel command surface - one verb,
/// <c>ring</c>. The Gateway asks; <see cref="FleetDoorbellRinger"/> decides against the live screen whether the
/// one doorbell line may be typed, and this answers <c>rung</c> or <c>deferred</c> with the reason.
///
/// A DEFERRAL IS A SUCCESSFUL ANSWER. "Not now, the owner has text in the composer" is the Director doing its
/// job, so it travels as a 200 with the reason in the body; only a malformed request, a missing session or a
/// submit that failed part-way is a failure.
/// </summary>
internal sealed class FleetDoorbellExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        FleetDoorbellVerbs.Ring,
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return command.Verb switch
        {
            FleetDoorbellVerbs.Ring => RingAsync(context, command),
            _ => Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                $"verb '{command.Verb}' is not handled by the fleet doorbell area")),
        };
    }

    internal static async Task<DirectorCommandResult> RingAsync(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<FleetRingRequest>(command.PayloadJson);
        if (request is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "ring payload is required");

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        try
        {
            var answer = await FleetDoorbellRinger.RingAsync(session, request.UnreadCount);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(answer));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetDoorbellExecutor] ring FAILED: session={guid}: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, $"the doorbell could not be typed: {ex.Message}");
        }
    }
}
