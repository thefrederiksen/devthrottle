// @vitest-environment jsdom

// THE PRINTED ADDRESS LANDS, SIGNED IN AND SIGNED OUT (dev reports mission, phase 3b, issue #3025), AND IT
// LANDS ON THE REPORT ITSELF (issue #3074).
//
// The Gateway's `/r/{report id}` is a public shell picker: it 302s anything that is not a phone to
// `/report/{report id}` and stops. Everything after that is this app's, and every leg below drives the REAL
// route table the app mounts (COCKPIT_ROUTES) rather than one written for the test - which is the whole
// point, because the defect phase 3b fixed was a route that did not exist, and a hand-written table would
// have been green throughout.
//
// WHAT CHANGED IN #3074: this address used to forward into the session's Reports tab, so the person it was
// sent to - who does not have that session - arrived inside somebody else's fleet, with the report squeezed
// between a navigation rail, a session list and a queue dock. It is the report now, on its own page, mounted
// OUTSIDE the AppShell so there is no rail to squeeze it. The forward is gone on purpose; the way into the
// session is a link on the report, in the Gateway's words, for the reader who has one.
//
//  1. SIGNED IN, mounting /report/{id} stays there and shows that report, with no app chrome around it.
//  2. SIGNED OUT, mounting it sends the browser to this shell's sign-in carrying next=/report/{id}.
//  3. A `next` of /report/{id} RESOLVES when the router is told to go there, which is exactly what
//     DeviceCallback does at the end of the sign-in round trip: `navigate(takeEnrollNext())`. A Gateway path
//     such as /r/{id} did not resolve, and that is why the signed-out case used to land nowhere.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router-dom";
import type { DevReportDetail } from "@devthrottle/client-core/devreports/devReportsClient";

const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";
const SESSION = "7d2f9c10-0000-4000-8000-000000000031";

let deviceKey: string | null = null;
vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => deviceKey !== null,
  getDeviceKey: () => deviceKey,
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));

const getDevReport = vi.fn<(reportId: string, signal?: AbortSignal) => Promise<DevReportDetail | null>>();
vi.mock("@devthrottle/client-core/devreports/devReportsClient", () => ({
  getDevReport: (reportId: string, signal?: AbortSignal) => getDevReport(reportId, signal),
  listDevReports: vi.fn(async () => []),
  getDevReportHtml: vi.fn(),
  sendDevReportItems: vi.fn(),
}));

// The report itself is client-core's and is tested there; here it only has to say which report it was handed.
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: ({ reportId }: { reportId: string }) => <div data-testid="fake-report-viewer">{reportId}</div>,
}));
vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({ DevReportList: () => <div /> }));
vi.mock("@devthrottle/client-core/devreports/DevReportConversation", () => ({ DevReportConversation: () => null }));

// The rest of the shell cannot run in jsdom (a terminal engine, live sockets, a microphone, a canvas) and is
// not the subject. The frame itself - the rail, the gate, the session page - is real.
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({ getSuggestionCount: vi.fn(async () => 0) }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/sessions/VerdictPanel", () => ({ VerdictPanel: () => null }));
vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("./network/CockpitStatusPill", () => ({ CockpitStatusPill: () => null }));
vi.mock("./panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./sessions/SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./sessions/SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./sessions/SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./sessions/ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./sessions/VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./sessions/SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./sessions/QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./sessions/ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));
vi.mock("./sessions/SessionsView", async () => {
  // The roster region fetches and streams; the detail routed underneath it is the REAL SessionDetail, so it
  // only has to supply the outlet and the one session on the roster.
  const { Outlet } = await import("react-router-dom");
  return {
    SessionsView: () => <Outlet context={{ sessions: [{ sessionId: SESSION }] }} />,
    SessionsEmpty: () => <div />,
  };
});

import { COCKPIT_ROUTES } from "./routes";

function detail(): DevReportDetail {
  return {
    report: {
      id: REPORT,
      sessionId: SESSION,
      key: "C:/work/report.html",
      title: "Queue audit",
      status: "waiting-on-you",
      version: 1,
      publishedAtUtc: "2026-09-17T10:00:00Z",
      updatedAtUtc: "2026-09-17T10:00:00Z",
      sessionEnded: false,
      openItems: 0,
    },
    items: [],
    replies: [],
  };
}

/** The app's OWN route table, mounted at one address. */
function mountAt(entry: string) {
  const router = createMemoryRouter(COCKPIT_ROUTES, { initialEntries: [entry] });
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(() => {
  cleanup();
  getDevReport.mockReset();
  deviceKey = null;
});

describe("the Cockpit report address", () => {
  it("shows that report, at that address, from an address carrying nothing but the report id", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    const router = mountAt(`/report/${REPORT}`);

    await waitFor(() => expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT));
    // It STAYS here. The old behaviour replaced this address with the session's Reports tab.
    expect(router.state.location.pathname + router.state.location.search).toBe(`/report/${REPORT}`);
  });

  it("puts nothing around the report - the page is outside the app shell", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    // THE INSTRUMENT FIRST. An ordinary page mounts inside the AppShell, so the Primary navigation IS
    // found - which is what makes its absence on the report page evidence rather than a query that matches
    // nothing anywhere.
    mountAt("/sessions");
    expect(screen.getByRole("navigation", { name: "Primary" })).toBeTruthy();
    cleanup();

    mountAt(`/report/${REPORT}`);

    await waitFor(() => expect(screen.getByTestId("report-page")).toBeTruthy());
    expect(screen.queryByRole("navigation", { name: "Primary" })).toBeNull();
  });

  it("sends a signed-out browser to this shell's sign-in carrying the report address", () => {
    deviceKey = null;

    const router = mountAt(`/report/${REPORT}`);

    expect(router.state.location.pathname + router.state.location.search).toBe(
      `/signin?next=${encodeURIComponent(`/report/${REPORT}`)}`,
    );
    // Nothing was read: the gate answered before the page mounted.
    expect(getDevReport).not.toHaveBeenCalled();
  });

  it("resolves a next of /report/{id} when the router is told to go there, as the callback does", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    // DeviceCallback ends the sign-in round trip with navigate(takeEnrollNext()) - a ROUTER navigation, not a
    // browser one. This is that call. A Gateway path (/r/{id}) has no route here and lands on Not found,
    // which is the defect phase 3b fixed; an in-shell /report/{id} resolves.
    const router = mountAt("/signin");
    await router.navigate(`/report/${REPORT}`);

    await waitFor(() => expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT));
    expect(router.state.location.pathname).toBe(`/report/${REPORT}`);
  });

  it("still has no route for the Gateway's own /r/{id}, which is why it must never appear in next", async () => {
    // The control for the test above, and a guard against someone "fixing" the signed-out path by adding a
    // /r/:id route to this shell: the printed address is the GATEWAY's and is resolved by the Gateway before
    // any gate. If this ever stops rendering Not found, the two designs are both half-present.
    deviceKey = "a-device-key";

    mountAt(`/r/${REPORT}`);

    await waitFor(() => expect(screen.getByText("Page not found")).toBeTruthy());
  });
});
