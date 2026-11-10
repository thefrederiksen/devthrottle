import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { ContributorWelcomeView } from "@devthrottle/client-core/teams/ContributorWelcome";

// THE CONTRIBUTOR'S WELCOME in the Cockpit (Team implementation piece 4.2). The page itself is shared (client-core); this
// only names the team on screen. The route around it (TeamPageRoute) has already shown the Gateway's page list for this
// team names the welcome, so a team is on screen here.
export function ContributorWelcomePage() {
  const { current } = useCurrentTeam();
  if (current === null) throw new Error("The welcome was mounted with no team on screen - its route did not check the page list.");
  return <ContributorWelcomeView key={current.id} teamId={current.id} />;
}
