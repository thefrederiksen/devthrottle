// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";

// A LINK LANDS INSIDE THE REPORT ON THE PHONE (dev reports mission, phase 3b, proof 5).
//
// `/session/{sid}/reports/{rid}` opens THAT report from cold - the reader never passes through the list and
// nothing on the way chose the report. This mounts DEV_REPORT_ROUTES, the very array the phone app's route
// table spreads in, so dropping or renaming the route in the app goes red here.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => String(e),
  listSessions: () => Promise.resolve([]),
  GatewayError: class GatewayError extends Error {},
}));

// The shared report views are client-core's and are tested there. Here they only say which one is on screen.
vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({
  DevReportList: () => <div data-testid="fake-report-list" />,
}));
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: ({ reportId }: { reportId: string }) => <div data-testid="fake-report-viewer">{reportId}</div>,
}));
vi.mock("@devthrottle/client-core/devreports/DevReportConversation", () => ({ DevReportConversation: () => null }));
vi.mock("../components/useSessionManage", () => ({ useSessionManage: () => ({}) }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));

import { DEV_REPORT_ROUTES } from "./reportRoutes";

const SID = "7d2f9c10-0000-4000-8000-000000000031";
const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";

afterEach(() => cleanup());

function mountAt(entry: string) {
  return render(<RouterProvider router={createMemoryRouter(DEV_REPORT_ROUTES, { initialEntries: [entry] })} />);
}

describe("the phone's report address", () => {
  it("opens that report from a cold navigation, with no list on the way", () => {
    mountAt(`/session/${SID}/reports/${REPORT}`);

    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
    expect(screen.queryByTestId("fake-report-list")).toBeNull();
  });

  it("still shows the session's list at the address without a report", () => {
    mountAt(`/session/${SID}/reports`);

    expect(screen.getByTestId("fake-report-list")).toBeTruthy();
    expect(screen.queryByTestId("fake-report-viewer")).toBeNull();
  });
});
