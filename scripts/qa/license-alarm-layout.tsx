import React from "react";
import { createRoot } from "react-dom/client";
import { createMemoryRouter, RouterProvider, type RouteObject } from "react-router-dom";
import { COCKPIT_ROUTES } from "../../apps/cockpit/src/routes";
import { AppShell } from "../../apps/cockpit/src/AppShell";
import "../../apps/cockpit/src/styles.css";

// The real routes, frame, banner and pages. Only network answers are replaced. Sessions has enough rows to scroll;
// other pages exercise their empty or failed-read layout without reaching a live account or making a write.
const sessions = Array.from({ length: 100 }, (_, i) => ({
  sessionId: `test-session-${i}`, name: `Layout session ${i}`, number: String(i + 1).padStart(3, "0"),
  createdAt: "2026-10-19T08:00:00Z", sortOrder: i, directorId: "test-director", machineName: "Test computer",
  repoPath: "D:/test/repository", effectiveColor: "green", effectiveColorHex: "#22C55E",
  stateLabel: "Working", triageBucket: "active", agent: "Test", agentType: "Test",
}));
const alarms = {
  asOfUtc: "2026-10-19T08:00:00Z", count: 4, summary: "4 license problems", linkText: "Open Licenses",
  alarms: Array.from({ length: 4 }, (_, i) => ({ alarmId: `test-alarm-${i}`, kind: "report-refused",
    text: `Problem ${i}. This deliberately long detail sentence must appear only on the Licenses page.`,
    machineName: "Test computer", accountEmail: null, openedAtUtc: "2026-10-19T08:00:00Z" })),
};
window.fetch = async (input) => {
  const url = typeof input === "string" ? input : input instanceof Request ? input.url : input.toString();
  const path = new URL(url, "http://test.invalid").pathname;
  let answer: unknown;
  if (path.endsWith("/licenses/alarms")) answer = alarms;
  else if (path === "/sessions") answer = { sessions, directors: [] };
  else if (path === "/directors") answer = [];
  else if (path === "/teams") answer = { teams: [], start: { where: "own-account" } };
  else if (path.includes("demo-mode")) answer = { enabled: false };
  else if (path.includes("factory-agents/switch")) answer = { enabled: true };
  else return new Response(JSON.stringify({ error: "Test read unavailable", message: "Test read unavailable" }), { status: 503, headers: { "Content-Type": "application/json" } });
  return new Response(JSON.stringify(answer), { headers: { "Content-Type": "application/json" } });
};

function findShell(routes: RouteObject[]): RouteObject {
  for (const route of routes) {
    if (React.isValidElement(route.element) && route.element.type === AppShell) return route;
    if (route.children) {
      const found = findShellOrNull(route.children);
      if (found) return found;
    }
  }
  throw new Error("The production route table has no AppShell");
}
function findShellOrNull(routes: RouteObject[]): RouteObject | null {
  for (const route of routes) {
    if (React.isValidElement(route.element) && route.element.type === AppShell) return route;
    const nested = route.children ? findShellOrNull(route.children) : null;
    if (nested) return nested;
  }
  return null;
}
const shell = findShell(COCKPIT_ROUTES);
function paths(routes: RouteObject[], parent = ""): string[] {
  return routes.flatMap((route) => {
    const path = route.path === undefined ? parent : route.path.startsWith("/") ? route.path : `${parent}/${route.path}`;
    return [...(route.path !== undefined ? [path.replace(/:[^/]+/g, "test").replace("*", "unknown")] : []),
      ...(route.children ? paths(route.children, path) : [])];
  });
}
const routePaths = [...new Set(["/sessions", ...paths(shell.children ?? [])])];
const host = document.getElementById("root");
if (!host) throw new Error("No root");
const root = createRoot(host);
let router: ReturnType<typeof createMemoryRouter> | null = null;

Object.assign(window, {
  licenseLayoutPaths: routePaths,
  openLicenseLayout(path: string) {
    router?.dispose();
    router = createMemoryRouter([shell], { initialEntries: [path] });
    root.render(<RouterProvider key={path} router={router} />);
  },
  measureLicenseLayout() {
    const banner = document.querySelector<HTMLElement>(".license-alarm-banner");
    const page = document.querySelector<HTMLElement>(".routed-page");
    if (!banner || !page || !page.firstElementChild) throw new Error("The banner or routed page has not rendered");
    const b = banner.getBoundingClientRect();
    const p = page.getBoundingClientRect();
    const content = page.firstElementChild.getBoundingClientRect();
    const roster = document.querySelector<HTMLElement>(".roster-rail");
    const screen = document.querySelector<HTMLElement>(".sessions-screen");
    return {
      banner: { top: b.top, bottom: b.bottom, left: b.left, right: b.right, height: b.height, text: banner.textContent },
      linkRight: banner.querySelector('a')?.getBoundingClientRect().right ?? null,
      page: { top: p.top, bottom: p.bottom, height: p.height, contentTop: content.top },
      screenBottom: screen?.getBoundingClientRect().bottom ?? null,
      rows: document.querySelectorAll(".roster-row").length,
      scrolls: roster ? roster.scrollHeight > roster.clientHeight : null,
      viewportHeight: innerHeight,
    };
  },
});
(window as unknown as { openLicenseLayout(path: string): void }).openLicenseLayout("/sessions");
