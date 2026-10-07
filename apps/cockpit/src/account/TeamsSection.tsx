import { useRef, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { createTeam, type TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// THE TEAMS SECTION OF THE ACCOUNT PAGE (Teams v1, devthrottle_internal#2098): the one place a person creates a team.
// It lists the teams the person is in, each with THEIR role as the Gateway labels it - the same GET /teams answer the
// team switcher reads, from the shared current team - and a form that creates a new one (POST /teams).
//
// It is drawn ONLY when the Gateway has answered with a list of teams (`ready`). A Gateway with Teams dark, or a
// self-hosted one, answers `not-offered`, and the section is not there at all - so a person on such a Gateway sees no
// change. The rail is untouched: the switcher still shows nothing until the person is in a team.
//
// On Create the shared list is read again, so the switcher appears at once; the new team is put on screen and its Team
// page opened, where the Owner starts the plan and invites. A refusal shows the Gateway's own sentence.

export function TeamsSection() {
  const { status, teams, refresh, choose } = useCurrentTeam();
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [creating, setCreating] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  // Held in a ref as well as state: two clicks inside one tick both see `creating` false, but not this.
  const inFlight = useRef(false);

  if (status !== "ready") return null;

  const create = async (e: FormEvent) => {
    e.preventDefault();
    if (inFlight.current) return;
    inFlight.current = true;
    setCreating(true);
    setRefusal(null);
    let team: TeamSummary;
    try {
      team = await createTeam(name);
    } catch (err) {
      setRefusal(gatewayErrorMessage(err, "create the team"));
      inFlight.current = false;
      setCreating(false);
      return;
    }
    try {
      await refresh();
    } catch (err) {
      // The team exists; only the list could not be read again. Say so, rather than suggest the create failed - a
      // second Create would make a second team.
      setRefusal(`${team.name} was created, but your teams could not be read again: ${gatewayErrorMessage(err, "read your teams")} Reload the page to open it.`);
      setName("");
      inFlight.current = false;
      setCreating(false);
      return;
    }
    choose(team.id);
    navigate(`/team/${encodeURIComponent(team.id)}/members`);
  };

  return (
    <section className="acct-teams" aria-label="Your teams" data-testid="account-teams">
      <h2>Teams</h2>
      {teams.length === 0 ? (
        <p className="acct-teams-none">You are not in a team.</p>
      ) : (
        <ul className="acct-teams-list">
          {teams.map((team) => (
            <li key={team.id} className="acct-team-row" data-testid="account-team-row">
              <span className="acct-team-name">{team.name}</span>
              <span className="acct-team-role">{`${team.role} - ${team.people}`}</span>
            </li>
          ))}
        </ul>
      )}

      <form className="acct-card acct-team-create" onSubmit={(e) => void create(e)}>
        <h3>Create a team</h3>
        <p className="acct-note">A team has one Owner, who pays for it. You become the Owner of the team you create.</p>
        <div className="acct-team-create-row">
          <label className="acct-team-create-label">
            <span>Team name</span>
            <input
              className="acct-team-name-input"
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              disabled={creating}
            />
          </label>
          <button className="acct-btn primary" type="submit" disabled={creating}>
            {creating ? "Creating..." : "Create"}
          </button>
        </div>
        {refusal !== null && (
          <div className="acct-team-create-error" role="alert">
            {refusal}
          </div>
        )}
      </form>
    </section>
  );
}
