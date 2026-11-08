import React from "react";
import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider, type RouteObject } from "react-router-dom";
import { COCKPIT_ROUTES } from "../../../apps/cockpit/src/routes";
import { AppShell } from "../../../apps/cockpit/src/AppShell";
// The same stylesheets, in the same order, as the Cockpit's own entry point (apps/cockpit/src/main.tsx).
import "../../../apps/cockpit/src/styles.css";
import "../../../apps/cockpit/src/components/components.css";
import "../../../apps/cockpit/src/fleetmanager/fleetmanager.css";
import "../../../apps/cockpit/src/fleet/fleet.css";
import "../../../apps/cockpit/src/fleet/fleetmap.css";
import "../../../apps/cockpit/src/licenses/licenses.css";
import "../../../apps/cockpit/src/boxes/boxes.css";
import "../../../apps/cockpit/src/history/history.css";
import "../../../apps/cockpit/src/missions/missions.css";
import "../../../apps/cockpit/src/schedule/schedule.css";
import "../../../apps/cockpit/src/workflows/workflows.css";
import "../../../apps/cockpit/src/dictionary/dictionary.css";
import "../../../apps/cockpit/src/transcripts/transcripts.css";
import "../../../apps/cockpit/src/throttle/throttle.css";
import "../../../apps/cockpit/src/transcription/transcriptionhealth.css";
import "../../../apps/cockpit/src/account/account.css";
import "../../../apps/cockpit/src/about/about.css";
import "../../../apps/cockpit/src/settings/settings.css";
import "../../../apps/cockpit/src/settings/injectedtext.css";
import { installFakeGateway, sessionId } from "./fake-gateway";

// The Cockpit: its real route table and frame over the fake Gateway, opened on the page named in the address's hash
// (#session, #session-terminal, #schedule, #directors, #voice).
installFakeGateway();

function findShell(routes: RouteObject[]): RouteObject | null {
  for (const route of routes) {
    if (React.isValidElement(route.element) && route.element.type === AppShell) return route;
    const nested = route.children ? findShell(route.children) : null;
    if (nested) return nested;
  }
  return null;
}

const pages: Record<string, string> = {
  session: `/session/${sessionId}`,
  "session-terminal": `/session/${sessionId}?tab=terminal`,
  schedule: "/schedule",
  directors: "/directors",
  voice: "/transcripts",
};
const page = pages[window.location.hash.slice(1)];
if (!page) throw new Error(`Unknown page ${window.location.hash}`);
const shell = findShell(COCKPIT_ROUTES);
if (!shell) throw new Error("The production route table has no AppShell");
const router = createMemoryRouter([shell], { initialEntries: [page] });
const host = document.getElementById("root");
if (!host) throw new Error("No #root element");
createRoot(host).render(<RouterProvider router={router} />);
