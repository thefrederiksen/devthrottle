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

    /// <summary>
    /// NOTHING IS ORDERED HERE, and <c>ring</c> was until the review of #4816 - for the keystroke reason,
    /// since it writes a doorbell line into the session.
    ///
    /// It comes off for the reason that review established: ring WAITS, and a chained wait sits in front of
    /// everything else that session is sent, including the Escape somebody pressed to stop what is running.
    /// It protects itself where it can: <c>FleetDoorbellRinger.Ringing</c> refuses a second ring to the same
    /// session (answering <c>working</c> rather than queueing), and <c>SubmitDoorbellLineAsync</c> stands
    /// down with <c>NotTyped</c> while input is already in flight.
    ///
    /// WHAT IT DOES NOT PROTECT, said plainly, because an earlier version of this comment claimed a guarded
    /// send that only the API-mode ring actually uses: the doorbell neither marks input in flight nor holds
    /// the one-send-at-a-time gate while IT types, so an ordinary send starting inside its line-echo-Enter
    /// window welds onto it. That window is tens to hundreds of milliseconds, it is pre-existing - on the
    /// delivery loop the ring released it at the first wait between screen frames - and a chain here would
    /// not have closed it either, because the sends that collide with it are not chained.
    /// </summary>
    public IReadOnlyCollection<string> VerbsThatMustArriveInOrder { get; } = Array.Empty<string>();

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
