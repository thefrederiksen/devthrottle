// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import type { SessionManage } from "./useSessionManage";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE PHONE'S SESSION APP BAR DRAWS THE SESSION'S MISSION (the Mission Button on the Web mission). The button itself
// is tested in client-core (MissionButton.test.tsx); this pins the phone's half: the app bar hands the shared
// component the LIVE row's `missionButton`, verbatim, and draws nothing when the row carries no verdict.

const SID = "9c41e7a2-0000-4000-8000-000000000000";

vi.mock("react-router-dom", () => ({
  useNavigate: () => vi.fn(),
  useParams: () => ({ sessionId: "9c41e7a2-0000-4000-8000-000000000000" }),
  useLocation: () => ({ pathname: "/session/9c41e7a2-0000-4000-8000-000000000000", search: "", hash: "", state: null, key: "default" }),
}));

import { SessionAppBar } from "./SessionAppBar";

function manage(session: SessionDto | null): SessionManage {
  return {
    session,
    onHold: false,
    held: false,
    deferred: false,
    snoozed: false,
    holdCountdown: null,
    sessionProblem: null,
    deliveryNotice: null,
    deliveryHistory: null,
    busy: false,
    error: null,
    setError: vi.fn(),
    toggleHold: () => Promise.resolve(true),
    holdFor: () => Promise.resolve(true),
    stopSession: () => Promise.resolve(),
    rename: () => Promise.resolve(true),
    refreshNow: () => {},
  } as unknown as SessionManage;
}

function row(missionButton: SessionDto["missionButton"]): SessionDto {
  return { sessionId: SID, name: "Developer", missionButton } as SessionDto;
}

afterEach(() => cleanup());

describe("the phone's session app bar", () => {
  it("SessionAppBar_RowWithMissionDocument_DrawsIndigoLinkToTheDocument", () => {
    render(
      <SessionAppBar
        title="Developer"
        manage={manage(
          row({
            verdict: "mission-with-document",
            label: "Mission",
            colorKey: "mission",
            iconKey: "document",
            tooltip: "Mission Button on the Web\nShow each session's Mission",
            documentAddress: "/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f",
          }),
        )}
      />,
    );
    const link = screen.getByRole("link", { name: "Mission" });
    expect(link.className).toContain("mb-mission");
    expect(link.getAttribute("href")).toBe("/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(link.closest("header")?.className).toContain("session-app-bar");
  });

  it("SessionAppBar_RowWithNoMission_DrawsYellowNoMission", () => {
    render(
      <SessionAppBar
        title="Developer"
        manage={manage(row({ verdict: "no-mission", label: "Mission", colorKey: "warning", iconKey: "warning", tooltip: "Attached to no Mission.", documentAddress: null }))}
      />,
    );
    expect(screen.getByRole("button", { name: "Mission" }).className).toContain("mb-warning");
  });

  it("SessionAppBar_RowWithoutVerdict_DrawsNoMissionButton", () => {
    const { container } = render(<SessionAppBar title="Developer" manage={manage(row(null))} />);
    expect(container.querySelector(".mb-root")).toBeNull();
  });

  it("SessionAppBar_NoLiveRow_DrawsNoMissionButton", () => {
    const { container } = render(<SessionAppBar title="Developer" manage={manage(null)} />);
    expect(container.querySelector(".mb-root")).toBeNull();
  });
});
