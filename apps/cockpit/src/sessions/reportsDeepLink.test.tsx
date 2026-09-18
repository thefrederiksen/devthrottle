// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes, useLocation } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// A LINK LANDS INSIDE THE REPORT (dev reports mission, phase 3b, proof 4).
//
// `/session/{sid}?tab=reports&report={rid}` opens the Reports tab with THAT report already open - no list
// first - and closing the report takes `report` back out of the address. The tab and the open report used to
// be component state, which a link cannot reach; this drives the REAL SessionDetail and the REAL ReportsTab,
// so putting either back into state goes red here.

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

// The shared report views are client-core's and are tested there. Here they only have to say which of the
// two is on screen, and which report the viewer was handed.
vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({
  DevReportList: ({ sessionId, onOpen }: { sessionId: string; onOpen: (r: { id: string }) => void }) => (
    <button type="button" data-testid="fake-report-list" onClick={() => onOpen({ id: OTHER_REPORT })}>
      {sessionId}
    </button>
  ),
}));
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: ({ reportId }: { reportId: string }) => <div data-testid="fake-report-viewer">{reportId}</div>,
}));
vi.mock("@devthrottle/client-core/devreports/DevReportConversation", () => ({ DevReportConversation: () => null }));

// The page's other regions cannot run in jsdom (a terminal engine, a live socket, a microphone) and are not
// the subject.
vi.mock("@devthrottle/client-core/sessions/VerdictPanel", () => ({ VerdictPanel: () => null }));
vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000031";
const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";
const OTHER_REPORT = "b41e77a2-0000-4000-8000-0000000000bb";

function Address() {
  const location = useLocation();
  return <span data-testid="address">{location.pathname + location.search}</span>;
}

function Shell({ sessions }: { sessions: SessionDto[] }) {
  return (
    <>
      <Address />
      <Outlet context={{ sessions }} />
    </>
  );
}

function mountAt(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route element={<Shell sessions={[{ sessionId: SID } as unknown as SessionDto]} />}>
          <Route path="/session/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => cleanup());

describe("the Cockpit session address", () => {
  it("opens the Reports tab with that report already open, straight from the address", () => {
    mountAt(`/session/${SID}?tab=reports&report=${REPORT}`);

    expect(screen.getByRole("tab", { name: "Reports" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(REPORT);
    expect(screen.queryByTestId("fake-report-list")).toBeNull();
  });

  it("takes the report back out of the address when it is closed", () => {
    mountAt(`/session/${SID}?tab=reports&report=${REPORT}`);

    fireEvent.click(screen.getByTestId("reports-back"));

    expect(screen.getByTestId("address").textContent).toBe(`/session/${SID}?tab=reports`);
    expect(screen.getByTestId("fake-report-list")).toBeTruthy();
    expect(screen.queryByTestId("fake-report-viewer")).toBeNull();
  });

  it("writes an opened report into the address", () => {
    mountAt(`/session/${SID}?tab=reports`);

    fireEvent.click(screen.getByTestId("fake-report-list"));

    expect(screen.getByTestId("address").textContent).toBe(`/session/${SID}?tab=reports&report=${OTHER_REPORT}`);
    expect(screen.getByTestId("fake-report-viewer").textContent).toBe(OTHER_REPORT);
  });

  it("still selects the other tabs from the address, and Terminal when it says nothing", () => {
    mountAt(`/session/${SID}?tab=wingman`);
    expect(screen.getByRole("tab", { name: "Wingman" }).getAttribute("aria-selected")).toBe("true");
    cleanup();

    mountAt(`/session/${SID}`);
    expect(screen.getByRole("tab", { name: "Terminal" }).getAttribute("aria-selected")).toBe("true");
  });
});
