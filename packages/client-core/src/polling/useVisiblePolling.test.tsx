// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render } from "@testing-library/react";
import { useVisiblePolling } from "./useVisiblePolling";

// A PAGE OPENED IN A BACKGROUND TAB GETS ITS FIRST ANSWER WHEN IT ASKS FOR ONE (the Licenses redesign, 8 November 2026).
// Before, a page that mounted hidden made no read at all until it was shown, so it sat on "Loading..." for as long as it
// stayed behind another tab. What these pin: with firstReadWhenHidden the first read is made at mount whatever the
// visibility; the REPEATING poll still waits for the tab to be visible; showing the tab reads again at once and resumes;
// and without it a hidden page makes no read at all, as before.

let visibility: DocumentVisibilityState;

function setVisibility(state: DocumentVisibilityState): void {
  visibility = state;
  document.dispatchEvent(new Event("visibilitychange"));
}

function Poller({ refresh, firstReadWhenHidden = true }: { refresh: (signal: AbortSignal) => void; firstReadWhenHidden?: boolean }) {
  useVisiblePolling(refresh, 1000, true, { firstReadWhenHidden });
  return null;
}

beforeEach(() => {
  vi.useFakeTimers();
  visibility = "visible";
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useVisiblePolling", () => {
  it("useVisiblePolling_HiddenAtMount_MakesTheFirstReadAndNoRepeatsUntilShown", () => {
    visibility = "hidden";
    const refresh = vi.fn();

    render(<Poller refresh={refresh} />);
    expect(refresh).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(5000);
    expect(refresh).toHaveBeenCalledTimes(1);

    setVisibility("visible");
    expect(refresh).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(1000);
    expect(refresh).toHaveBeenCalledTimes(3);
  });

  it("useVisiblePolling_HiddenAtMount_ShowingTheTabAbortsTheFirstReadAndReadsAgain", () => {
    visibility = "hidden";
    const signals: AbortSignal[] = [];
    render(<Poller refresh={(signal) => void signals.push(signal)} />);

    setVisibility("visible");

    expect(signals).toHaveLength(2);
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);
  });

  it("useVisiblePolling_HiddenAtMount_WithoutTheOption_MakesNoReadUntilShown", () => {
    visibility = "hidden";
    const refresh = vi.fn();

    render(<Poller refresh={refresh} firstReadWhenHidden={false} />);
    vi.advanceTimersByTime(5000);
    expect(refresh).not.toHaveBeenCalled();

    setVisibility("visible");
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("useVisiblePolling_VisibleAtMount_ReadsAndRepeats_AndGoesQuietWhenHidden", () => {
    const refresh = vi.fn();
    render(<Poller refresh={refresh} />);
    expect(refresh).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000);
    expect(refresh).toHaveBeenCalledTimes(2);

    setVisibility("hidden");
    vi.advanceTimersByTime(5000);
    expect(refresh).toHaveBeenCalledTimes(2);
  });
});
