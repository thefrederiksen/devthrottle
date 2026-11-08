// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE COCKPIT SESSION HEADER DRAWS THE SESSION'S MISSION (the Mission Button on the Web mission). The button itself is
// tested in client-core (MissionButton.test.tsx); this pins the Cockpit's half: the session view hands the shared
// component the SELECTED row's `missionButton`, verbatim, in the session header beside the session's actions, and
// draws nothing when the row carries no verdict.

vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/api/client")>()),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(() => new Promise<Response>(() => {})),
}));
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => <span data-testid="session-menu" /> }));
vi.mock("./LinkRequests", () => ({ LinkRequestChip: () => null }));
vi.mock("./SecretTransferChip", () => ({ SecretTransferChip: () => null }));
vi.mock("./ChatTab", () => ({
  ChatTab: ({ toolbarEnd }: { toolbarEnd?: import("react").ReactNode }) => <div data-testid="chat-filter-row">{toolbarEnd}</div>,
}));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./ReportsTab", () => ({ ReportsTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));
vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("./wingmanNowActions", () => ({ wingmanNowActions: () => ({}), sendingButtonFor: () => "send" }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000002";
const OTHER = "7d2f9c10-0000-4000-8000-000000000003";

function row(sessionId: string, missionButton: SessionDto["missionButton"]): SessionDto {
  return { sessionId, name: sessionId, missionButton } as SessionDto;
}

function renderDetail(sessions: SessionDto[], tab = "terminal") {
  function Shell() {
    return <Outlet context={{ sessions, directors: [], refreshNow: vi.fn() }} />;
  }
  return render(
    <MemoryRouter initialEntries={[`/sessions/${SID}?tab=${tab}`]}>
      <Routes>
        <Route element={<Shell />}>
          <Route path="/sessions/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => cleanup());

describe("the Cockpit session header", () => {
  it("SessionDetail_SelectedRowWithMission_DrawsIndigoLinkInTheHeaderBeforeTheMenu", () => {
    renderDetail([
      row(OTHER, { verdict: "no-mission", label: "Mission", colorKey: "warning", iconKey: "warning", tooltip: "none", documentAddress: null }),
      row(SID, {
        verdict: "mission-with-document",
        label: "Mission",
        colorKey: "mission",
        iconKey: "document",
        tooltip: "Mission Button on the Web\nShow each session's Mission",
        documentAddress: "/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f",
      }),
    ]);
    const link = screen.getByRole("link", { name: "Mission" });
    expect(link.className).toContain("mb-mission");
    expect(link.getAttribute("href")).toBe("/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f");
    expect(link.getAttribute("target")).toBe("_blank");
    // On the row UNDER the tab strip (Web UI Polish, fix 6), so the tabs have the top row to themselves; the
    // session menu stays at the top right of the strip.
    expect(link.closest(".session-tabs")).toBeNull();
    expect(link.closest(".session-subbar")).not.toBeNull();
    const menu = screen.getByTestId("session-menu");
    expect(menu.closest(".session-tabs")).not.toBeNull();
    // The other row's yellow verdict is not drawn on this session's screen.
    expect(screen.queryByRole("button", { name: "Mission" })).toBeNull();
  });

  it("SessionDetail_ChatTab_PutsMissionOnTheChatFilterRowNotARowOfItsOwn", () => {
    const { container } = renderDetail(
      [row(SID, { verdict: "no-mission", label: "Mission", colorKey: "warning", iconKey: "warning", tooltip: "none", documentAddress: null })],
      "chat",
    );
    const button = screen.getByRole("button", { name: "Mission" });
    expect(button.closest("[data-testid='chat-filter-row']")).not.toBeNull();
    expect(container.querySelector(".session-subbar")).toBeNull();
    expect(button.closest(".session-tabs")).toBeNull();
  });

  it("SessionDetail_SelectedRowWithNoMission_DrawsYellowNoMission", () => {
    renderDetail([row(SID, { verdict: "no-mission", label: "Mission", colorKey: "warning", iconKey: "warning", tooltip: "none", documentAddress: null })]);
    expect(screen.getByRole("button", { name: "Mission" }).className).toContain("mb-warning");
  });

  it("SessionDetail_SelectedRowWithoutVerdict_DrawsNoMissionButton", () => {
    const { container } = renderDetail([row(SID, null)]);
    expect(container.querySelector(".mb-root")).toBeNull();
  });
});
