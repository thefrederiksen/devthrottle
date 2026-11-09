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

        var onIt = directorsInTeam
            .Where(d => string.Equals(d.MachineName?.Trim(), machine.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(d => d.DirectorId)
            .ToList();
        if (onIt.Count > 0 && onIt.All(caller.OwnsDirector))
            return null;

        FileLog.Write($"[TeamMachineGuard] {what}: REFUSED in tenant {caller.Tenant.ToLogString()} - " +
                      (onIt.Count == 0 ? "no Director of the team is known on the named computer" : "a Director on the named computer is not the caller's own"));
        return Refusal;
    }
}
