// @vitest-environment jsdom
// The shared Mission button (the Mission Button on the Web mission). It draws the Gateway's MissionButtonDisplay
// verbatim: each of the three colour keys gets its own look, an unknown key gets a visible problem note and a central
// report instead of a guessed colour, a null verdict draws nothing, and a document address opens in a new tab.
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

vi.mock("../errors/reportClientError", () => ({
  reportShownError: vi.fn((_surface: string, _action: string, message: string) => message),
}));

import { LONG_PRESS_MS, MissionButton, missionButtonColourClass, missionButtonProblem } from "./MissionButton";
import { reportShownError } from "../errors/reportClientError";
import type { MissionButtonDisplay } from "../api/client";

const WITH_DOCUMENT: MissionButtonDisplay = {
  verdict: "mission-with-document",
  label: "Mission Button on the Web",
  colorKey: "mission",
  tooltip: "Mission Button on the Web\nThe Cockpit and the mobile app show each session's Mission",
  documentAddress: "/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f",
};
const WITHOUT_DOCUMENT: MissionButtonDisplay = {
  verdict: "mission-without-document",
  label: "Quiet Mission",
  colorKey: "mission-outline",
  tooltip: "Quiet Mission\nNo mission document yet",
  documentAddress: null,
};
const NO_MISSION: MissionButtonDisplay = {
  verdict: "no-mission",
  label: "No mission",
  colorKey: "warning",
  tooltip: "This session is attached to no Mission.",
  documentAddress: null,
};

// jsdom builds pointer events without a pointerType, so a finger is described by hand.
function touch(el: HTMLElement, type: "pointerdown" | "pointerup"): void {
  const e = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(e, "pointerType", { value: "touch" });
  act(() => {
    el.dispatchEvent(e);
  });
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.mocked(reportShownError).mockClear();
});

describe("MissionButton colour keys", () => {
  it("MissionButton_MissionKey_DrawsFilledIndigoLinkWithLabelAndTooltip", () => {
    render(<MissionButton display={WITH_DOCUMENT} sessionId="s1" />);
    const link = screen.getByRole("link", { name: "Mission Button on the Web" });
    expect(link.className).toContain("mb-mission");
    expect(link.className).not.toContain("mb-mission-outline");
    expect(link.getAttribute("title")).toBe(WITH_DOCUMENT.tooltip);
  });

  it("MissionButton_MissionOutlineKey_DrawsOutlineLook", () => {
    render(<MissionButton display={WITHOUT_DOCUMENT} />);
    const button = screen.getByRole("button", { name: "Quiet Mission" });
    expect(button.className).toContain("mb-mission-outline");
    expect(button.getAttribute("title")).toBe(WITHOUT_DOCUMENT.tooltip);
  });

  it("MissionButton_WarningKey_DrawsYellowNoMission", () => {
    render(<MissionButton display={NO_MISSION} />);
    const button = screen.getByRole("button", { name: "No mission" });
    expect(button.className).toContain("mb-warning");
    expect(button.getAttribute("title")).toBe(NO_MISSION.tooltip);
  });

  it("missionButtonColourClass_EachKnownKey_MapsToItsOwnClass", () => {
    expect(missionButtonColourClass("mission")).toBe("mb-mission");
    expect(missionButtonColourClass("mission-outline")).toBe("mb-mission-outline");
    expect(missionButtonColourClass("warning")).toBe("mb-warning");
    expect(missionButtonColourClass("toString")).toBeNull();
    expect(missionButtonColourClass(undefined)).toBeNull();
  });

  it("MissionButton_LabelIsNotReadToDecide_SameKeyDifferentVerdictDrawsSameLook", () => {
    // The verdict word is the Gateway's record, never this client's input: only the colour key picks the look.
    render(<MissionButton display={{ ...NO_MISSION, verdict: "something-new", label: "Anything" }} />);
    expect(screen.getByRole("button", { name: "Anything" }).className).toContain("mb-warning");
  });
});

describe("MissionButton absences and problems", () => {
  it("MissionButton_NullVerdict_RendersNothing", () => {
    const { container } = render(<MissionButton display={null} />);
    expect(container.innerHTML).toBe("");
  });

  it("MissionButton_UndefinedVerdict_RendersNothing", () => {
    const { container } = render(<MissionButton display={undefined} />);
    expect(container.innerHTML).toBe("");
  });

  it("MissionButton_UnknownColourKey_ShowsProblemNoteAndReportsIt", () => {
    render(<MissionButton display={{ ...WITH_DOCUMENT, colorKey: "teal" }} sessionId="s9" />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
    const note = screen.getByRole("alert");
    expect(note.textContent).toContain('"teal"');
    expect(note.className).toBe("mb-problem");
    expect(reportShownError).toHaveBeenCalledWith(
      "mission button",
      "draw the Mission button",
      expect.stringContaining('"teal"'),
      { sessionId: "s9", background: true },
    );
  });

  it("MissionButton_AddressNotAGatewayPath_ShowsProblemNoteNotALink", () => {
    render(<MissionButton display={{ ...WITH_DOCUMENT, documentAddress: "https://elsewhere.example/r/1" }} />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByRole("alert").textContent).toContain("starting with one slash");
  });

  it("missionButtonProblem_ProtocolRelativeAddress_IsAProblem", () => {
    expect(missionButtonProblem({ ...WITH_DOCUMENT, documentAddress: "//evil.example/r/1" })).not.toBeNull();
    expect(missionButtonProblem(WITH_DOCUMENT)).toBeNull();
    expect(missionButtonProblem(null)).toBeNull();
  });
});

describe("MissionButton link and tooltip", () => {
  it("MissionButton_DocumentAddress_OpensItInANewTab", () => {
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission Button on the Web" });
    expect(link.getAttribute("href")).toBe("/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(link.getAttribute("rel")).toBe("noopener noreferrer");
  });

  it("MissionButton_NoDocumentAddress_IsNotALink", () => {
    render(<MissionButton display={NO_MISSION} />);
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("MissionButton_TapWithNoDocument_ShowsTheTooltipWords", () => {
    render(<MissionButton display={NO_MISSION} />);
    expect(screen.queryByRole("tooltip")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "No mission" }));
    expect(screen.getByRole("tooltip").textContent).toBe(NO_MISSION.tooltip);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("MissionButton_LongPressOnLink_ShowsTooltipAndDoesNotOpen", () => {
    vi.useFakeTimers();
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission Button on the Web" });
    touch(link, "pointerdown");
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS);
    });
    expect(screen.getByRole("tooltip").textContent).toBe(WITH_DOCUMENT.tooltip);
    touch(link, "pointerup");
    const click = new MouseEvent("click", { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
  });

  it("MissionButton_ShortTapOnLink_OpensNormally", () => {
    vi.useFakeTimers();
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission Button on the Web" });
    touch(link, "pointerdown");
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS - 100);
    });
    touch(link, "pointerup");
    const click = new MouseEvent("click", { bubbles: true, cancelable: true });
    link.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(false);
    expect(screen.queryByRole("tooltip")).toBeNull();
  });
});
