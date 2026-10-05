import type { ReactNode } from "react";
import { useCurrentTeam } from "@devthrottle/client-core/teams/CurrentTeam";
import { LoadingState } from "../../components";
import { NotFound } from "../../panes/NotFound";

// ONE TEAM PAGE'S ROUTE (devthrottle_internal#2306). /questions, /requests and /reports exist only inside a team whose
// Gateway verdict lists the page for this person; everywhere else - the person's own account, a person with no team,
// a Gateway with Teams dark - the address is the ordinary "Page not found", exactly as before Teams.
//
// The list is the Gateway's (`current.app.pages`, rule 7): this route never decides from the role who may open it.
//
// WHILE THE LIST OF TEAMS IS STILL BEING READ, THE ANSWER HERE IS NOT YET KNOWN (round 3 review, R1). A browser that
// remembers no team draws the own account at once, so a Collaborator opening a mailed link to one of these pages in a
// fresh browser would otherwise see "Page not found" until the Gateway's start arrives. Only these three addresses
// wait, so nobody else is touched.
export function TeamPageRoute({ pageId, children }: { pageId: string; children: ReactNode }) {
  const { current, resolving, status } = useCurrentTeam();
  if (resolving || status === "loading") return <LoadingState message="Loading your team..." />;
  if (current === null || !current.app.pages.some((p) => p.id === pageId)) return <NotFound />;
  return <>{children}</>;
}
