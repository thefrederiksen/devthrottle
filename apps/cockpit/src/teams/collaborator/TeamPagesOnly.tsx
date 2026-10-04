import { Navigate, Outlet, useLocation } from "react-router-dom";
import type { TeamPagesApp } from "@devthrottle/client-core/teams/teamsClient";
import "./collaborator.css";

// THE MAIN PANE OF A COCKPIT THAT IS ONLY A FEW PAGES (devthrottle_internal#2306) - a Collaborator's, in a team where
// they are one. The Gateway's verdict says which pages this person may open, where they land, and what every other
// address says; this component only applies it:
//
//   - an address of one of the pages, or anything under it, renders the routed page;
//   - the bare root opens on the landing page;
//   - ANY other address, typed or bookmarked, shows the Gateway's one plain sentence, and nothing else - so no page
//     the person may not open is ever mounted, and none of its data is asked for. The server refuses that data
//     anyway (TeamEndpointGate); this is so the screen never pretends otherwise.
export function TeamPagesOnly({ app }: { app: TeamPagesApp }) {
  const { pathname } = useLocation();
  if (pathname === "/") return <Navigate to={app.landing} replace />;
  if (app.pages.some((p) => pathname === p.path || pathname.startsWith(`${p.path}/`))) return <Outlet />;
  return <TeamPageNotAvailable sentence={app.elsewhere} />;
}

/** The one plain page at every address a role does not open. The sentence is the Gateway's. */
export function TeamPageNotAvailable({ sentence }: { sentence: string }) {
  return (
    <section className="pane team-page" data-testid="team-page-not-available">
      <h1 className="pane-title team-not-available-title">{sentence}</h1>
    </section>
  );
}
