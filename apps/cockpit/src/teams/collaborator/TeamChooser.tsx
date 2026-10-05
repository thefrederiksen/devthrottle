import { activeAccount } from "@devthrottle/client-core/auth/accountStore";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import { Button, PageHeader } from "../../components";
import "./collaborator.css";

// CHOOSING A TEAM (screen S11, devthrottle_internal#2306, review finding F1): what a fresh browser shows when the
// Gateway says so - the person has several teams and no computer on their own account. One account, each team with
// the person's role in it, and their own account. Picking one is remembered, so this is asked once per browser.
export function TeamChooser({ teams, onOpen }: { teams: TeamSummary[]; onOpen: (teamId: string | null) => void }) {
  const account = activeAccount();
  const who = account === null ? null : account.email ?? account.label;
  return (
    <section className="pane team-page team-chooser" data-testid="team-chooser">
      <PageHeader title="Choose a team" subtitle={who === null ? undefined : `You are signed in as ${who}`} />
      <ul className="team-chooser-list">
        {teams.map((team) => (
          <li key={team.id} className="team-chooser-row">
            <div className="team-chooser-text">
              <div className="team-chooser-name">{team.name}</div>
              <div className="team-chooser-role">{`${team.role} - ${team.people}`}</div>
            </div>
            <Button variant="primary" onClick={() => onOpen(team.id)} aria-label={`Open ${team.name}`}>
              Open
            </Button>
          </li>
        ))}
        <li className="team-chooser-row">
          <div className="team-chooser-text">
            <div className="team-chooser-name">Your own account</div>
            <div className="team-chooser-role">Your own computers and sessions</div>
          </div>
          <Button onClick={() => onOpen(null)} aria-label="Open your own account">
            Open
          </Button>
        </li>
      </ul>
    </section>
  );
}
