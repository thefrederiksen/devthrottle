import React from "react";
import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider, type RouteObject } from "react-router-dom";
import { COCKPIT_ROUTES } from "../../../apps/cockpit/src/routes";
import { AppShell } from "../../../apps/cockpit/src/AppShell";
import "../../../apps/cockpit/src/styles.css";
import { installFakeGateway, rootElement, stuckId } from "./fake-gateway";

// The Cockpit: its real route table and frame, with the alerts tray opened by its own deep link (?alert={id}).
installFakeGateway();

function findShell(routes: RouteObject[]): RouteObject | null {
  for (const route of routes) {
    if (React.isValidElement(route.element) && route.element.type === AppShell) return route;
    const nested = route.children ? findShell(route.children) : null;
    if (nested) return nested;
  }
  return null;
}

const shell = findShell(COCKPIT_ROUTES);
if (!shell) throw new Error("The production route table has no AppShell");
const router = createMemoryRouter([shell], { initialEntries: [`/sessions?alert=${encodeURIComponent(stuckId)}`] });
createRoot(rootElement()).render(<RouterProvider router={router} />);
