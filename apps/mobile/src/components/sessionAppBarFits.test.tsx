// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionManage } from "./useSessionManage";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE PHONE SESSION HEADER FITS (Web UI Polish, 7 November 2026). Row one carries Back, the Mission button, the license
// triangle, the bell and the menu. With "<- Sessions" in a style of its own, a 360px phone squeezed the bell to 17px; and
// "Voice mode" wrapped, making the tab row two lines tall. Measured in a real browser for the pull request (390 and 360
// wide: every control inside the bar, the bell 44px, the tabs one line). These pin the two words that made the room:
// the same Back every other phone screen has, and a one-word Voice tab. Width itself needs a browser; jsdom has none.

vi.mock("react-router-dom", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("react-router-dom");
  return { ...actual, useNavigate: () => vi.fn() };
});

import { SessionAppBar } from "./SessionAppBar";
import { ViewTabs } from "./ViewTabs";

function manage(): SessionManage {
  return {
    session: { sessionId: "s1", name: "Developer", missionButton: null } as SessionDto,
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

afterEach(() => cleanup());

describe("the phone session header fits", () => {
  it("SessionAppBar_Back_IsTheSameBackEveryPhoneScreenHas", () => {
    render(
      <MemoryRouter>
        <SessionAppBar title="Developer" manage={manage()} />
      </MemoryRouter>,
    );

    const back = screen.getByTestId("session-back");
    expect(back.textContent).toBe("Back");
    expect(back.className).toBe("back-link");
    // Its accessible name still says where it goes.
    expect(screen.getByRole("button", { name: "Back to sessions" })).toBe(back);
  });

  it("ViewTabs_VoiceTab_IsOneWord", () => {
    render(
      <MemoryRouter>
        <ViewTabs sessionId="s1" active="chat" />
      </MemoryRouter>,
    );

    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(["Chat", "Terminal", "Voice", "Reports"]);
  });
});
