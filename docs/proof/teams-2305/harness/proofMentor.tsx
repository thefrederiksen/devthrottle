// THE PROOF HARNESS for the Mentor page (devthrottle_internal#2305). Not part of the Cockpit: it is copied next to the
// Cockpit's source only while the screenshots are taken (see take-screenshots.py), then removed.
//
// It renders the REAL Cockpit shell and the REAL Mentor page with the real styles, and answers the page's Gateway reads
// with the contract's example data (docs/proof/teams-2305/contract.md in the Gateway worktree): a test team of two -
// priya@example.com, a Manager who ran no sessions that week and so has no block, and rob@example.com, a Developer who
// has one. `?as=manager` answers as the Gateway answers a Manager (scope "everyone"); `?as=developer` as it answers
// Rob (scope "own", his block only, and who else reads it). Nothing here is the running Gateway.
import React from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import { AppShell } from "./AppShell";
import { MentorView } from "./mentor/MentorView";
import "./styles.css";
import "./components/components.css";

const as = new URLSearchParams(window.location.search).get("as") === "developer" ? "developer" : "manager";
const TEAM_ID = "6f0c2d1e-0000-4000-8000-000000002305";

const ROB_BLOCK = {
  personSubject: "a1b2c3d4",
  personEmail: "rob@example.com",
  role: "Developer",
  tone: "hard",
  toneLabel: "a hard week",
  workedOn: "The new signup page and two bug fixes in the installer.",
  howItWent: null,
  wentBadlyAndWhy:
    "On Tuesday the same task was restarted four times. The first instruction didn't say which file to change, so the agent guessed differently each time.",
  quotes: [{ promptId: "p_3f9a", at: "2026-09-29T09:14:03Z", text: "fix the signup thing so it doesnt break on mobile" }],
  oneThingToTry: "Name the file and the result you expect in the first line, before asking for the change.",
  writtenAtUtc: "2026-10-05T00:20:11Z",
};

const answers: Record<string, unknown> = {
  "/teams": {
    count: 1,
    teams: [{ id: TEAM_ID, name: "Teams test", role: as === "developer" ? "Developer" : "Manager", memberCount: 2, people: "2 people" }],
  },
  [`/teams/${TEAM_ID}/mentor`]: {
    teamId: TEAM_ID,
    week: "2026-W40",
    weekStart: "2026-09-28",
    weekEnd: "2026-10-04",
    timeZone: "Europe/Copenhagen",
    scope: as === "developer" ? "own" : "everyone",
    written: true,
    blocks: [ROB_BLOCK],
    readers: [{ email: "priya@example.com", role: "Manager" }],
  },
};

window.fetch = async (input: RequestInfo | URL) => {
  const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url, window.location.origin);
  const body = answers[url.pathname];
  if (body === undefined) {
    return new Response(JSON.stringify({ error: "Not part of this proof." }), { status: 404, headers: { "Content-Type": "application/json" } });
  }
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
};

window.localStorage.setItem("devthrottle.currentTeam", TEAM_ID);

const router = createMemoryRouter(
  [{ element: <AppShell />, children: [{ path: "/mentor", element: <MentorView /> }] }],
  { initialEntries: ["/mentor"] },
);

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>,
);
