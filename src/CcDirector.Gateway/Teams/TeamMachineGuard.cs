using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// WORK INSIDE A TEAM RUNS ONLY ON THE CALLER'S OWN COMPUTERS (Team implementation 2.1, review note N1). A schedule
/// (<c>/cron/jobs</c>), a trigger, a factory talk and the schedule editor's folder choices name a COMPUTER in their body
/// or query, which the team gate cannot read. The gate lets the Owner through on the factory row; this is the one place
/// that then asks whether the computer named is the caller's own - the role table's principle that no role, the Owner
/// included, may join or watch someone else's session, and that a member's Director is that member's own.
///
/// The rule: in a team's account, a named computer is allowed only when at least one Director of the team is known on
/// it and EVERY Director of the team known on it is the caller's own (<see cref="TeamCaller.OwnsDirector"/>, the one
/// ownership rule). A computer with no Director of the team, or with another member's, is refused - with one sentence
/// for both, so the refusal never says whose computers exist. A personal account (no <see cref="TeamCaller"/>) is
/// answered exactly as before, and a request that names no computer is not asked here.
///
/// WHERE IT IS ASKED (review of the second commit, F1 and F2). Every session a person or a factory starts on a Director
/// goes through one door, <c>GatewayEndpoints.StartSessionOnDirectorAsync</c>, which asks <see cref="Refuse"/> for that
/// Director's computer - the talk, the Factory Manager's talk, "handle the issue", a Boss's seat start and anything added
/// later. A definition's publish and restore ask it for every computer the definition names, before the check asks
/// those computers anything and before the apply writes a schedule or a trigger. A stored schedule firing asks
/// <see cref="RefuseRun"/>. The write routes of a schedule, a trigger and the folder choices ask <see cref="Refuse"/> as
/// well, so the refusal comes at the write rather than at the first run.
/// </summary>
public static class TeamMachineGuard
{
    /// <summary>The one sentence every refusal carries. It names nothing about the team's computers.</summary>
    public const string Refusal =
        "Inside a team you may run work only on your own computers, and DevThrottle cannot show that the computer " +
        "named here is one of yours. Choose a computer your own Director runs on. Nothing was done.";

    /// <summary>
    /// Why <paramref name="caller"/> may not name <paramref name="machine"/>, or null when they may.
    /// </summary>
    /// <param name="caller">The caller inside a team's tenant, or null for a personal account (never refused here).</param>
    /// <param name="machine">The computer the request names, or null/blank when it names none.</param>
    /// <param name="directorsInTeam">Every Director of the team's tenant, by id and computer name.</param>
    /// <param name="what">What the request is, for the log line only.</param>
    public static string? Refuse(TeamCaller? caller, string? machine,
        IEnumerable<(string DirectorId, string? MachineName)> directorsInTeam, string what)
    {
        ArgumentNullException.ThrowIfNull(directorsInTeam);
        if (caller is null || string.IsNullOrWhiteSpace(machine))
            return null;

        var onIt = OnMachine(directorsInTeam, machine);
        if (onIt.Count > 0 && onIt.All(caller.OwnsDirector))
            return null;

        FileLog.Write($"[TeamMachineGuard] {what}: REFUSED in tenant {caller.Tenant.ToLogString()} - " +
                      (onIt.Count == 0 ? "no Director of the team is known on the named computer" : "a Director on the named computer is not the caller's own"));
        return Refusal;
    }

    /// <summary>
    /// Why a team's STORED work - a schedule firing, a run its check asked for - may not start on
    /// <paramref name="machine"/>, or null when it may. There is no caller at fire time, so the rule is the role table's:
    /// at least one Director of the team is known on the computer, and every one of them belongs to a member whose role
    /// may run the team's factories (<see cref="TeamAction.OpenRunAndTalkToTeamFactories"/> - the Owner alone in step
    /// one). This holds a schedule written before the write-time check existed, or by a path that check did not see, off
    /// another member's computer.
    /// </summary>
    /// <param name="machine">The computer the run would start on; null or blank is not asked here.</param>
    /// <param name="directorsInTeam">Every Director of the team's tenant, by id and computer name.</param>
    /// <param name="personOfDirector">The person a Director of the team belongs to (TeamCallerOwnership.OwnerOf), or null.</param>
    /// <param name="roleOf">A person's role in the team, or null when they are not a member.</param>
    /// <param name="what">What is starting, for the log line only.</param>
    public static string? RefuseRun(string? machine, IEnumerable<(string DirectorId, string? MachineName)> directorsInTeam,
        Func<string, string?> personOfDirector, Func<string, TeamRole?> roleOf, string what)
    {
        ArgumentNullException.ThrowIfNull(directorsInTeam);
        ArgumentNullException.ThrowIfNull(personOfDirector);
        ArgumentNullException.ThrowIfNull(roleOf);
        if (string.IsNullOrWhiteSpace(machine))
            return null;

        var onIt = OnMachine(directorsInTeam, machine);
        bool MayRun(string directorId) =>
            personOfDirector(directorId) is { } person && roleOf(person) is { } role
            && TeamPermissions.Allows(role, TeamAction.OpenRunAndTalkToTeamFactories);
        if (onIt.Count > 0 && onIt.All(MayRun))
            return null;

        FileLog.Write($"[TeamMachineGuard] {what}: REFUSED - " +
                      (onIt.Count == 0 ? "no Director of the team is known on the computer" : "a Director on the computer belongs to a member who may not run the team's factories"));
        return Refusal;
    }

    private static List<string> OnMachine(IEnumerable<(string DirectorId, string? MachineName)> directors, string machine) =>
        directors
            .Where(d => string.Equals(d.MachineName?.Trim(), machine.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(d => d.DirectorId)
            .ToList();
}
