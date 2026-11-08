// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { ClampText } from "./ClampText";

// jsdom has no layout, so the cut is faked by giving the clamped box a scroll height taller than its own height.
function fakeHeights(scrollHeight: number, clientHeight: number) {
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(scrollHeight);
  vi.spyOn(HTMLElement.prototype, "clientHeight", "get").mockReturnValue(clientHeight);
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("ClampText", () => {
  it("ClampText_TextFits_ShowsNoMoreLink", () => {
    fakeHeights(30, 30);
    render(<ClampText>Running</ClampText>);
    expect(screen.getByText("Running")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "More" })).toBeNull();
  });

  it("ClampText_TextCutOff_ShowsMoreWhichUnclampsAndTogglesToLess", () => {
    fakeHeights(120, 36);
    const { container } = render(<ClampText lines={2}>A long status the Gateway sent, every word of it.</ClampText>);
    const box = container.querySelector(".clamp-text") as HTMLElement;
    expect(box.className).toContain("clamped");
    expect(box.style.webkitLineClamp).toBe("2");

    fireEvent.click(screen.getByRole("button", { name: "More" }));
    expect(box.className).not.toContain("clamped");
    expect(screen.getByRole("button", { name: "Less" }).getAttribute("aria-expanded")).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "Less" }));
    expect(box.className).toContain("clamped");
  });

  it("ClampText_MoreClicked_DoesNotReachTheRowBeneath", () => {
    fakeHeights(120, 36);
    const onRow = vi.fn();
    render(
      <div onClick={onRow}>
        <ClampText>Long words</ClampText>
      </div>,
    );
    fireEvent.click(screen.getByRole("button", { name: "More" }));
    expect(onRow).not.toHaveBeenCalled();
  });
});
