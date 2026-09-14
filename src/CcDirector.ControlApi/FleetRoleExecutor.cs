using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Defect 5: the ROLE area of the Director's tunnel command surface - one verb, <c>set-resolved-role</c>,
/// through which the Gateway tells this Director what one of its sessions' role IS.
///
/// This verb is unlike every other in the command surface: the others are the Gateway asking the Director
/// to DO something, and this one is the Gateway telling the Director a FACT. The Director stores it and
/// decides nothing - "is this session's controller still alive?" is unanswerable from one Director, which
/// is precisely why the answer has to be delivered. The stored value is read back out by
/// <c>ControlEndpoints.Map</c> onto <c>SessionDto.SessionRole</c>, which is what finally lets the desktop
/// rail fold the same role the phone and the Cockpit fold.
///
/// It is its own area because a verb belongs to exactly one area and an area is the unit that avoids the
/// merge chokepoint - adding this verb touched no other area's file.
/// (docs/new_architecture/sessions.html, defect 5.)
/// </summary>
internal sealed class FleetRoleExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "set-resolved-role",
    };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(command.Verb switch
        {
            "set-resolved-role" => SetResolvedRole(context, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the fleet role area"),
        });
    }

    /// <summary>
    /// The <c>set-resolved-role</c> verb: store the Gateway's resolved role for one session.
    ///
    /// A blank role CLEARS the stamp (back to "no answer") rather than being rejected - that is how the
    /// Gateway retracts a role it can no longer resolve, and it must be expressible. The role is NOT
    /// validated against <see cref="SessionRoles"/> here: the Gateway is the authority on what a role is,
    /// and a Director second-guessing it would be the Director deciding.
    /// </summary>
    internal static DirectorCommandResult SetResolvedRole(SessionCommandContext context, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<SetResolvedRoleRequest>(command.PayloadJson);
        if (request is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "role payload is required");

        var session = context.SessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.SetGatewayResolvedRole(request.Role, request.HasLiveSupervisor);
        FileLog.Write($"[FleetRoleExecutor] set-resolved-role: session={guid}, role={request.Role}, " +
                      $"liveSupervisor={request.HasLiveSupervisor}");
        return DirectorCommandResult.Success();
    }
}
