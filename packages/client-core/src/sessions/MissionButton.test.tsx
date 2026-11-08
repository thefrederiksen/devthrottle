// @vitest-environment jsdom
// The shared Mission button (the Mission Button on the Web mission). It draws the Gateway's MissionButtonDisplay
// verbatim: each of the three colour keys gets its own look and each of the three icon keys its own icon, an unknown key
// of either kind gets a visible problem note and a central report instead of a guess, a null verdict draws nothing, a
// document address opens in a new tab, and the touch bubble is kept inside the screen.
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

vi.mock("../errors/reportClientError", () => ({
  reportShownError: vi.fn((_surface: string, _action: string, message: string) => message),
}));

import {
  LONG_PRESS_MS,
  MissionButton,
  bubbleShift,
  missionButtonColourClass,
  missionButtonKnowsIcon,
  missionButtonProblem,
} from "./MissionButton";
import { reportShownError } from "../errors/reportClientError";
import type { MissionButtonDisplay } from "../api/client";

const WITH_DOCUMENT: MissionButtonDisplay = {
  verdict: "mission-with-document",
  label: "Mission",
  colorKey: "mission",
  iconKey: "document",
  tooltip: "Mission: Mission Button on the Web\nGoal: The Cockpit and the mobile app show each session's Mission\nClick to open the mission document.",
  documentAddress: "/r/a24e87a1-fd9e-4f8b-87e6-04013cf1e75f",
};
const WITHOUT_DOCUMENT: MissionButtonDisplay = {
  verdict: "mission-without-document",
  label: "Mission",
  colorKey: "mission-outline",
  iconKey: "none",
  tooltip: "Mission: Quiet Mission\nThis Mission has no document yet.",
  documentAddress: null,
};
const NO_MISSION: MissionButtonDisplay = {
  verdict: "no-mission",
  label: "Mission",
  colorKey: "warning",
  iconKey: "warning",
  tooltip: "This session has no Mission. Attach it to one with: cc-devthrottle mission attach <session> <mission>",
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
    const link = screen.getByRole("link", { name: "Mission" });
    expect(link.className).toContain("mb-mission");
    expect(link.className).not.toContain("mb-mission-outline");
    expect(link.getAttribute("title")).toBe(WITH_DOCUMENT.tooltip);
  });

  it("MissionButton_MissionOutlineKey_DrawsOutlineLook", () => {
    render(<MissionButton display={WITHOUT_DOCUMENT} />);
    const button = screen.getByRole("button", { name: "Mission" });
    expect(button.className).toContain("mb-mission-outline");
    expect(button.getAttribute("title")).toBe(WITHOUT_DOCUMENT.tooltip);
  });

  it("MissionButton_WarningKey_DrawsYellowOutline", () => {
    render(<MissionButton display={NO_MISSION} />);
    const button = screen.getByRole("button", { name: "Mission" });
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

describe("MissionButton icon keys", () => {
  it("MissionButton_DocumentIconKey_DrawsTheDocumentIconBeforeTheWord", () => {
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission" });
    const icons = link.querySelectorAll("svg");
    expect(icons.length).toBe(1);
    expect(icons[0].getAttribute("data-icon")).toBe("document");
    expect(icons[0].getAttribute("aria-hidden")).toBe("true");
    expect(link.firstElementChild).toBe(icons[0]);
    expect(link.textContent).toBe("Mission");
  });

  it("MissionButton_NoneIconKey_DrawsTheWordAlone", () => {
    render(<MissionButton display={WITHOUT_DOCUMENT} />);
    const button = screen.getByRole("button", { name: "Mission" });
    expect(button.querySelectorAll("svg").length).toBe(0);
    expect(button.textContent).toBe("Mission");
  });

  it("MissionButton_WarningIconKey_DrawsTheExclamationCircleBeforeTheWord", () => {
    render(<MissionButton display={NO_MISSION} />);
    const button = screen.getByRole("button", { name: "Mission" });
    const icons = button.querySelectorAll("svg");
    expect(icons.length).toBe(1);
    expect(icons[0].getAttribute("data-icon")).toBe("warning");
    expect(button.firstElementChild).toBe(icons[0]);
    // A circle, never a closed triangle: the license warning is a yellow triangle and the two must not look alike.
    expect(icons[0].querySelector('circle[r="6.5"]')).not.toBeNull();
    const closedPaths = Array.from(icons[0].querySelectorAll("path")).filter((p) => /z/i.test(p.getAttribute("d") ?? ""));
    expect(closedPaths).toHaveLength(0);
  });

  it("MissionButton_IconKeyIsNotTiedToTheColour_EachKeyIsDrawnAsSent", () => {
    // The icon is the Gateway's choice, not inferred from the colour: a filled button told "warning" draws the exclamation circle.
    render(<MissionButton display={{ ...WITH_DOCUMENT, iconKey: "warning" }} />);
    const link = screen.getByRole("link", { name: "Mission" });
    expect(link.className).toContain("mb-mission");
    expect(link.querySelector("svg")?.getAttribute("data-icon")).toBe("warning");
  });

  it("missionButtonKnowsIcon_EachKnownKeyAndNoOther", () => {
    expect(missionButtonKnowsIcon("document")).toBe(true);
    expect(missionButtonKnowsIcon("none")).toBe(true);
    expect(missionButtonKnowsIcon("warning")).toBe(true);
    expect(missionButtonKnowsIcon("Document")).toBe(false);
    expect(missionButtonKnowsIcon("toString")).toBe(false);
    expect(missionButtonKnowsIcon("")).toBe(false);
    expect(missionButtonKnowsIcon(undefined)).toBe(false);
  });

  it("MissionButton_UnknownIconKey_ShowsProblemNoteAndReportsIt", () => {
    render(<MissionButton display={{ ...WITH_DOCUMENT, iconKey: "star" }} sessionId="s7" />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
    const note = screen.getByRole("alert");
    expect(note.textContent).toContain('icon this app does not know ("star")');
    expect(reportShownError).toHaveBeenCalledWith(
      "mission button",
      "draw the Mission button",
      expect.stringContaining('"star"'),
      { sessionId: "s7", background: true },
    );
  });

  it("MissionButton_NoIconKeyAtAll_IsAProblemNotAGuessedIcon", () => {
    // A Gateway older than the icon key sends none.
    const { iconKey: _dropped, ...older } = WITH_DOCUMENT;
    render(<MissionButton display={older as MissionButtonDisplay} />);
    expect(screen.queryByRole("link")).toBeNull();
    expect(screen.getByRole("alert").textContent).toContain('("undefined")');
  });
});

describe("MissionButton bubble stays inside the screen", () => {
  it("bubbleShift_Fits_IsZero", () => {
    expect(bubbleShift(20, 300, 360)).toBe(0);
  });

  it("bubbleShift_PastTheLeftEdge_SlidesRightToTheGap", () => {
    expect(bubbleShift(-24, 296, 360)).toBe(32);
  });

  it("bubbleShift_PastTheRightEdge_SlidesLeftToTheGap", () => {
    expect(bubbleShift(100, 380, 360)).toBe(-28);
  });

  it("bubbleShift_WiderThanTheScreen_PinsItsStartToTheLeftGap", () => {
    expect(bubbleShift(8, 420, 360)).toBe(0);
    expect(bubbleShift(40, 452, 360)).toBe(-32);
  });

  it("MissionButton_LongPressBubblePastTheLeftEdge_IsSlidBackOnScreen", () => {
    vi.useFakeTimers();
    Object.defineProperty(document.documentElement, "clientWidth", { value: 360, configurable: true });
    const rect = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return (this.className === "mb-tip" ? { left: -24, right: 296 } : { left: 0, right: 0 }) as DOMRect;
    });
    try {
      render(<MissionButton display={WITH_DOCUMENT} />);
      const link = screen.getByRole("link", { name: "Mission" });
      touch(link, "pointerdown");
      act(() => {
        vi.advanceTimersByTime(LONG_PRESS_MS);
      });
      expect(screen.getByRole("tooltip").style.transform).toBe("translateX(32px)");
    } finally {
      rect.mockRestore();
    }
  });

  it("MissionButton_TooltipChangesWhileBubbleOpen_IsMeasuredAgain", () => {
    vi.useFakeTimers();
    Object.defineProperty(document.documentElement, "clientWidth", { value: 360, configurable: true });
    let tipLeft = 40;
    const rect = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return (this.className === "mb-tip" ? { left: tipLeft, right: 296 } : { left: 0, right: 0 }) as DOMRect;
    });
    try {
      const { rerender } = render(<MissionButton display={WITH_DOCUMENT} />);
      touch(screen.getByRole("link", { name: "Mission" }), "pointerdown");
      act(() => {
        vi.advanceTimersByTime(LONG_PRESS_MS);
      });
      expect(screen.getByRole("tooltip").style.transform).toBe("");
      tipLeft = -24;
      rerender(<MissionButton display={{ ...WITH_DOCUMENT, tooltip: `${WITH_DOCUMENT.tooltip} A much longer name.` }} />);
      expect(screen.getByRole("tooltip").style.transform).toBe("translateX(32px)");
    } finally {
      rect.mockRestore();
    }
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
    // A browser resolves a backslash after the first slash as a second slash: this leaves the Gateway too.
    expect(missionButtonProblem({ ...WITH_DOCUMENT, documentAddress: "/\\evil.example/r/1" })).not.toBeNull();
    expect(missionButtonProblem(WITH_DOCUMENT)).toBeNull();
    expect(missionButtonProblem(null)).toBeNull();
  });
});

describe("MissionButton link and tooltip", () => {
  it("MissionButton_DocumentAddress_OpensItInANewTab", () => {
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission" });
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
    fireEvent.click(screen.getByRole("button", { name: "Mission" }));
    expect(screen.getByRole("tooltip").textContent).toBe(NO_MISSION.tooltip);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("MissionButton_LongPressOnLink_ShowsTooltipAndDoesNotOpen", () => {
    vi.useFakeTimers();
    render(<MissionButton display={WITH_DOCUMENT} />);
    const link = screen.getByRole("link", { name: "Mission" });
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
    const link = screen.getByRole("link", { name: "Mission" });
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
