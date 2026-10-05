import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { RequestsView } from "../../team/RequestsView";
import "./collaborator.css";

// REQUESTS (screen S9): the slot devthrottle_internal#2306 made, filled by devthrottle_internal#2308 - the request form
// and what happened to each request the person sent, in the team on screen. The navigation and the route guard are
// #2306's and do not change for it: TeamPageRoute draws this only inside a team whose page verdict lists it.
export function RequestsPage() {
  const { current } = useCurrentTeam();
  if (current === null) {
    throw new Error("The Requests page was drawn with no team on screen. TeamPageRoute must guard it.");
  }
  return (
    <section className="pane team-page" data-testid="team-page-requests">
      <RequestsView teamId={current.id} />
    </section>
  );
}
