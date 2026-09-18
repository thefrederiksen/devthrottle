// @vitest-environment jsdom

// THE PRINTED ADDRESS LANDS ON THE PHONE, SIGNED IN AND SIGNED OUT (dev reports mission, phase 3b, #3025).
//
// The Gateway's `/r/{report id}` is a public shell picker now: it 302s a phone to `/mobile/report/{report
// id}` and stops. Everything after that is this app's, and every leg below drives the REAL route table the
// app mounts (MOBILE_ROUTES, at the REAL basename) rather than one written for the test - which is the whole
// point, because the defect this phase fixes was a route that did not exist, and a hand-written table would
// have been green throughout.
//
// The basename matters here more than anywhere: this router is rooted at /mobile, so a Gateway path in
// `next` would be asked for as /mobile/r/{id} at the end of the sign-in round trip and match nothing. The
// last test pins that, so nobody "fixes" the signed-out path by teaching this shell the Gateway's address.

import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter, matchRoutes } from "react-router-dom";
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

// The gated layout's app-level machinery cannot run in jsdom (a wake lock, a visual viewport, a heartbeat, a
// live recording session) and is not the subject. The gate, the layout and the report screen are real.
vi.mock("./hooks/useScreenWakeLock", () => ({ useScreenWakeLock: () => {} }));
vi.mock("./hooks/useVisibleViewportHeight", () => ({ useVisibleViewportHeight: () => {} }));
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/recorder/ingestUpload", () => ({ resumePendingRecordingUploads: vi.fn(async () => {}) }));
vi.mock("./components/ConnectionBanner", () => ({ ConnectionBanner: () => null }));
vi.mock("./components/VoiceModeBanner", () => ({ VoiceModeBanner: () => null }));
vi.mock("./components/RecordingBanner", () => ({ RecordingBanner: () => null }));

import { MOBILE_BASENAME, MOBILE_ROUTES } from "./routes";

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

/** The app's OWN route table at the app's OWN basename, mounted at one address. */
// NOTE: router.state.location.pathname carries the BASENAME, while useLocation() inside the app does not -
// which is why the addresses asserted below start with /mobile and the `next` the gate writes does not.
function mountAt(entry: string) {
  const router = createMemoryRouter(MOBILE_ROUTES, { basename: MOBILE_BASENAME, initialEntries: [entry] });
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(() => {
  cleanup();
  getDevReport.mockReset();
  deviceKey = null;
});

describe("the phone report landing", () => {
  it("lands in that report, from an address carrying nothing but the report id", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    const router = mountAt(`/mobile/report/${REPORT}`);

    await waitFor(() =>
      expect(router.state.location.pathname).toBe(`/mobile/session/${SESSION}/reports/${REPORT}`),
    );
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
    // The landing REPLACED itself, so Back from the report does not bounce through a forwarding screen.
    expect(router.state.historyAction).toBe("REPLACE");
  });

  it("sends a signed-out phone to this shell's sign-in carrying the report address", () => {
    deviceKey = null;

    const router = mountAt(`/mobile/report/${REPORT}`);

    expect(router.state.location.pathname + router.state.location.search).toBe(
      `/mobile/signin?next=${encodeURIComponent(`/report/${REPORT}`)}`,
    );
    // Nothing was read: the gate answered before the landing mounted.
    expect(getDevReport).not.toHaveBeenCalled();
  });

  it("resolves a next of /report/{id} when the router is told to go there, as the callback does", async () => {
    deviceKey = "a-device-key";
    getDevReport.mockResolvedValue(detail());

    // DeviceCallback ends the sign-in round trip with navigate(takeEnrollNext()) - a ROUTER navigation, which
    // this router resolves UNDER its /mobile basename. That is why `next` has to be an in-shell route.
    const router = mountAt("/mobile/signin");
    await router.navigate(`/report/${REPORT}`);

    await waitFor(() =>
      expect(router.state.location.pathname).toBe(`/mobile/session/${SESSION}/reports/${REPORT}`),
    );
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
  });

  it("still has no route for the Gateway's own /r/{id} under this basename", () => {
    // The control for the test above, and a guard against someone "fixing" the signed-out path by teaching
    // this shell the Gateway's address: under the /mobile basename a `next` of /r/{id} is asked for as
    // /mobile/r/{id}, and this table matches nothing at all for it. Asked of the table rather than rendered,
    // because rendering a no-route match runs the stale-shell recovery, which unregisters service workers
    // and reloads the page - real side effects that say nothing about routing.
    expect(matchRoutes(MOBILE_ROUTES, `/mobile/r/${REPORT}`, MOBILE_BASENAME)).toBeNull();
    // ...and the address it DOES match is the one the printed link is redirected to.
    const matched = matchRoutes(MOBILE_ROUTES, `/mobile/report/${REPORT}`, MOBILE_BASENAME);
    expect(matched?.[matched.length - 1].route.path).toBe("/report/:reportId");
  });
});
