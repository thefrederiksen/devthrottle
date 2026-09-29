// @vitest-environment jsdom
// The "not sent, shown back" strip, checked against the REAL stylesheet.
//
// The defect this pins, from a real phone on 2026-09-28: a dictation older than 5 minutes came back as the
// red dropped strip, quoting every word. The phone's Voice, Chat and Terminal screens are fixed-height and
// the strip sits ABOVE each screen's own scroll area, so the quote of a long dictation grew past the bottom
// of the phone. Send anyway and Dismiss were pushed off the screen, and nothing on the page could scroll to
// them - the screen was frozen until the app was left.
//
// vitest stubs CSS imports, so the stylesheet is read off disk and injected here, the same way
// auth/accountsLayout.test.tsx does it. jsdom does not lay out, so this pins the DECLARATIONS that keep the
// quote bounded and scrollable, not the pixels.
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { DictationStatusStrip } from "./DictationStatusStrip";
import { allDictationStatuses, clearDictationStatus, publishDictationStatus } from "./status";

const HERE = dirname(fileURLToPath(import.meta.url));
const SESSION_ID = "session-one";

beforeEach(() => {
  document.head.querySelectorAll("style").forEach((s) => s.remove());
  const css = readFileSync(join(HERE, "dictationStrip.css"), "utf8");
  expect(css.length).toBeGreaterThan(0);
  const style = document.createElement("style");
  style.textContent = css;
  document.head.appendChild(style);
});

afterEach(() => {
  cleanup();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
});

describe("the dropped strip, against the real stylesheet", () => {
  it("caps the quoted words and lets them scroll, so a long dictation cannot push the buttons off the screen", () => {
    const longWords = "I think we need to add something I was saying in the beginning of this. ".repeat(40).trim();
    publishDictationStatus({
      sessionId: SESSION_ID,
      uploadId: "up-long",
      phase: "dropped",
      retryable: false,
      recoverableText: longWords,
      offerSendAnyway: true,
      error: "This recording is more than 5 minutes old, so it was not sent automatically. Here is what you said - send it?",
    });
    render(<DictationStatusStrip sessionId={SESSION_ID} />);

    const strip = screen.getByRole("alert");
    const quote = strip.querySelector(".dictate-strip-quote") as HTMLElement;
    expect(quote.textContent).toBe(longWords);

    const cs = getComputedStyle(quote);
    expect(cs.maxHeight).not.toBe("");
    expect(cs.maxHeight).not.toBe("none");
    expect(cs.overflowY).toBe("auto");

    // The buttons are still there, outside the capped quote.
    expect(within(strip).getByRole("button", { name: "Send anyway" })).toBeTruthy();
    expect(within(strip).getByRole("button", { name: "Dismiss" })).toBeTruthy();
    expect(quote.contains(within(strip).getByRole("button", { name: "Dismiss" }))).toBe(false);
  });
});
