using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #3559: the Fleet Manager's LESSONS area of the Director's tunnel command surface - one verb,
/// <c>set-fleet-manager-lessons</c>, through which the Gateway gives this Director the confirmed lessons its marked
/// Fleet Manager session must keep across a compaction or a clear.
///
/// Like <c>set-resolved-role</c> this delivers a FACT. The Director stores the finished block verbatim on the session,
/// and the preamble it maintains for the session-start hook puts it first (<c>SessionPreambleFile</c>). The Director
/// never reads, filters or rewrites a lesson: the Gateway decided which lessons are confirmed and wrote the block.
/// </summary>
internal sealed class FleetManagerLessonsExecutor : ISessionCommandArea
{
    public const string Verb = "set-fleet-manager-lessons";

    public IReadOnlyCollection<string> Verbs { get; } = new[] { Verb };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(command.Verb switch
        {
            Verb => SetLessons(context, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the fleet manager lessons area"),
        });
    }

    /// <summary>Store the Gateway's lessons block on one session. A null or empty block clears it.</summary>
    internal static DirectorCommandResult SetLessons(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<SetFleetManagerLessonsRequest>(command.PayloadJson);
        if (request is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "lessons payload is required");

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.SetFleetManagerLessons(request.Lessons);
        FileLog.Write($"[FleetManagerLessonsExecutor] {Verb}: session={guid}, length={request.Lessons?.Length ?? 0}");
        return DirectorCommandResult.Success();
    }
}
