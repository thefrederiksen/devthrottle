// @vitest-environment jsdom

// THE PRINTED ADDRESS LANDS, SIGNED IN AND SIGNED OUT (dev reports mission, phase 3b, issue #3025).
//
// The Gateway's `/r/{report id}` is a public shell picker now: it 302s anything that is not a phone to
// `/report/{report id}` and stops. Everything after that is this app's, and all three legs below drive the
// REAL route table the app mounts (COCKPIT_ROUTES) rather than one written for the test - which is the whole
// point, because the defect this phase fixes was a route that did not exist, and a hand-written table would
// have been green throughout.
//
//  1. SIGNED IN, mounting /report/{id} reads the report, learns its session, and replaces itself with that
//     session's Reports tab with the report open.
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

describe("the Cockpit report landing", () => {
  it("lands in that report, from an address carrying nothing but the report id", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    const router = mountAt(`/report/${REPORT}`);

    await waitFor(() =>
      expect(router.state.location.pathname + router.state.location.search).toBe(
        `/session/${SESSION}?tab=reports&report=${REPORT}`,
      ),
    );
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
    // The landing REPLACED itself: the browser's Back leaves the app rather than bouncing through a screen
    // whose only job is to forward again.
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("sends a signed-out browser to this shell's sign-in carrying the report address", () => {
    deviceKey = null;

    const router = mountAt(`/report/${REPORT}`);

    expect(router.state.location.pathname + router.state.location.search).toBe(
      `/signin?next=${encodeURIComponent(`/report/${REPORT}`)}`,
    );
    // Nothing was read: the gate answered before the landing mounted.
    expect(getDevReport).not.toHaveBeenCalled();
  });

  it("resolves a next of /report/{id} when the router is told to go there, as the callback does", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    // DeviceCallback ends the sign-in round trip with navigate(takeEnrollNext()) - a ROUTER navigation, not a
    // browser one. This is that call. A Gateway path (/r/{id}) has no route here and lands on Not found,
    // which is the defect this phase fixes; an in-shell /report/{id} resolves.
    const router = mountAt("/signin");
    await router.navigate(`/report/${REPORT}`);

    await waitFor(() =>
      expect(router.state.location.pathname + router.state.location.search).toBe(
        `/session/${SESSION}?tab=reports&report=${REPORT}`,
      ),
    );
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
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
