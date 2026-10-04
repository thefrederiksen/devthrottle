import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import "./teams.css";

// THE TEAM SWITCHER (devthrottle_internal#2312, screens S11 and D4-D5): the one control at the top of the rail that
// puts a team on screen. It lists the person's own account first - the fleet they had before Teams - and then each
// of their teams with THEIR role in it, as the Gateway labels it.
//
// It writes the shared current team (client-core's CurrentTeam); it does not hold one of its own, so the Fleet Map,
// the Team page and the Skills page all follow it.
//
// A person with no team, or on a Gateway that has not turned Teams on, sees NOTHING here - not an empty control,
// not a "no teams" line. Teams must change nothing for someone who never joins one.

/** The value the select uses for the person's own account (a team id is a GUID, so it can never be this). */
const OWN_ACCOUNT = "own-account";

export function TeamSwitcher() {
  const { status, teams, current, error, choose } = useCurrentTeam();

  if (status === "error") {
    // Teams IS on for this Gateway but the list could not be read. Said, quietly, rather than hidden: a person in a
    // team who sees no switcher would otherwise take it that they had been removed.
    return (
      <div className="team-switcher team-switcher-error" data-testid="team-switcher-error" title={error ?? undefined}>
        Your teams could not be read just now.
      </div>
    );
  }
  if (status !== "ready" || teams.length === 0) return null;

  return (
    <label className="team-switcher" data-testid="team-switcher">
      <span className="team-switcher-label">Team</span>
      <select
        className="team-switcher-select"
        value={current?.id ?? OWN_ACCOUNT}
        onChange={(e) => choose(e.target.value === OWN_ACCOUNT ? null : e.target.value)}
      >
        <option value={OWN_ACCOUNT}>Your own account</option>
        {teams.map((team) => (
          <option key={team.id} value={team.id}>
            {`${team.name} - ${team.role}`}
          </option>
        ))}
      </select>
    </label>
  );
}
