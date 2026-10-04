import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
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

export function TeamSwitcher({
  onSwitched,
}: {
  /** Called after the person picks a team (or their own account, as null), with the team that was on screen before.
   *  The shell uses it to open the new team where it starts (devthrottle_internal#2306). */
  onSwitched?: (now: TeamSummary | null, before: TeamSummary | null) => void;
} = {}) {
  const { status, teams, current, resolving, error, choose } = useCurrentTeam();

  if (status === "error") {
    // The list could not be read, and is being asked again. Said, quietly, ONLY to a person this browser knows is on
    // a team (review finding F1): they would otherwise take a missing switcher to mean they had been removed. Someone
    // who has never picked a team sees nothing - they may have no team at all, and must see no change.
    if (!resolving) return null;
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
        title={current === null ? "Your own account" : `${current.name} - ${current.role}`}
        value={current?.id ?? OWN_ACCOUNT}
        onChange={(e) => {
          const id = e.target.value === OWN_ACCOUNT ? null : e.target.value;
          choose(id);
          onSwitched?.(id === null ? null : teams.find((t) => t.id === id) ?? null, current);
        }}
      >
        <option value={OWN_ACCOUNT}>Your own account</option>
        {teams.map((team) => (
          <option key={team.id} value={team.id}>
            {`${team.name} - ${team.role}`}
          </option>
        ))}
      </select>
      {/* The role, always readable: a long team name can push it out of the closed control (S11: "Owner - 5 people"). */}
      {current !== null && (
        <span className="team-switcher-role" data-testid="team-switcher-role">
          {`${current.role} - ${current.people}`}
        </span>
      )}
    </label>
  );
}
