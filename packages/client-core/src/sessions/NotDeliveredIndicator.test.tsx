// @vitest-environment jsdom
// The small red "Not delivered" chip (owner ruling 2026-10-07). A failed delivery used to put a full-width banner
// above the composer AND a big box with the words under it; together they took the screen over. These pin the
// replacement: collapsed it is one chip and nothing else, a click opens everything the two blocks carried, a
// session screen and its composer strip show ONE chip between them, and the quoted words stay capped.
//
// vitest stubs CSS imports, so the stylesheet is read off disk and injected, the same way the auth layout tests
// do it. jsdom does not lay out, so the stylesheet checks pin DECLARATIONS, not pixels.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

vi.mock("../dictation/backgroundSend", () => ({
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
}));

import { NotDeliveredIndicator } from "./NotDeliveredIndicator";
import { DictationStatusStrip } from "../dictation/DictationStatusStrip";
import { allDictationStatuses, clearDictationStatus, publishDictationStatus } from "../dictation/status";
import { dismissDictationStatus, sendDroppedDictationAnyway } from "../dictation/backgroundSend";

const HERE = dirname(fileURLToPath(import.meta.url));
const SID = "session-one";
const NOTICE = "Your last prompt was not delivered - the agent never received it. The composer still holds text.";
const LABEL = "This recording is more than 5 minutes old, so it was not sent automatically. Here is what you said - send it?";

function publishDropped(words: string, offerSendAnyway = true): void {
  publishDictationStatus({
    sessionId: SID,
    uploadId: "up-1",
    phase: "dropped",
    retryable: false,
    recoverableText: words,
    offerSendAnyway,
    error: LABEL,
  });
}

function chips(): HTMLElement[] {
  return screen.queryAllByRole("button", { name: /Not delivered/ });
}

function open(): HTMLElement {
  fireEvent.click(screen.getByRole("button", { name: /Not delivered/ }));
  return screen.getByRole("dialog", { name: "Not delivered" });
}

beforeEach(() => {
  document.head.querySelectorAll("style").forEach((s) => s.remove());
  const css = readFileSync(join(HERE, "notDeliveredIndicator.css"), "utf8");
  expect(css.length).toBeGreaterThan(0);
  const style = document.createElement("style");
  style.textContent = css;
  document.head.appendChild(style);
  vi.mocked(dismissDictationStatus).mockClear();
  vi.mocked(sendDroppedDictationAnyway).mockClear();
});

afterEach(() => {
  cleanup();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
});

describe("the Not delivered chip", () => {
  it("renders nothing when no prompt was lost", () => {
    const { container } = render(<NotDeliveredIndicator sessionId={SID} notice={null} claim />);
    expect(container.textContent).toBe("");
  });

  it("collapsed, is one small chip - not the Gateway's sentence, not the words, not the buttons", () => {
    publishDropped("go");
    render(<NotDeliveredIndicator sessionId={SID} notice={NOTICE} history="1 composer retry" claim />);
    expect(chips()).toHaveLength(1);
    expect(screen.queryByText(NOTICE)).toBeNull();
    expect(screen.queryByText("go")).toBeNull();
    expect(screen.queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("a click opens the Gateway's sentence verbatim, the history, the words, and Send anyway / Dismiss", async () => {
    publishDropped("go");
    render(<NotDeliveredIndicator sessionId={SID} notice={NOTICE} history="1 composer retry" claim />);
    const panel = open();
    expect(within(panel).getByText(NOTICE)).toBeTruthy();
    expect(within(panel).getByText("1 composer retry")).toBeTruthy();
    expect(within(panel).getByText(LABEL)).toBeTruthy();
    expect(within(panel).getByText("go")).toBeTruthy();
    fireEvent.click(within(panel).getByRole("button", { name: "Send anyway" }));
    await waitFor(() => expect(sendDroppedDictationAnyway).toHaveBeenCalledWith("up-1"));
  });

  it("shows only the notice when the Gateway's sentence is all there is", () => {
    render(<NotDeliveredIndicator sessionId={SID} notice={NOTICE} claim />);
    const panel = open();
    expect(within(panel).getByText(NOTICE)).toBeTruthy();
    expect(within(panel).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(panel).queryByRole("button", { name: "Dismiss" })).toBeNull();
  });

  it("offers no second send when the Gateway did not offer one", () => {
    publishDropped("the words nobody confirmed", false);
    render(<NotDeliveredIndicator sessionId={SID} claim />);
    const panel = open();
    expect(within(panel).queryByRole("button", { name: "Send anyway" })).toBeNull();
    fireEvent.click(within(panel).getByRole("button", { name: "Dismiss" }));
    expect(dismissDictationStatus).toHaveBeenCalledWith("up-1");
  });

  it("closes on Escape and on the close button", () => {
    render(<NotDeliveredIndicator sessionId={SID} notice={NOTICE} claim />);
    open();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    const panel = open();
    fireEvent.click(within(panel).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("shows ONE chip when the session screen and its composer strip are both mounted", () => {
    publishDropped("go");
    render(
      <>
        <NotDeliveredIndicator sessionId={SID} notice={NOTICE} claim />
        <DictationStatusStrip sessionId={SID} />
      </>,
    );
    expect(chips()).toHaveLength(1);
    // The one chip carries the strip's words too.
    expect(within(open()).getByText("go")).toBeTruthy();
  });

  it("the strip on its own (no session screen around it) still shows the chip, so no surface loses the words", () => {
    publishDropped("go");
    render(<DictationStatusStrip sessionId={SID} />);
    expect(chips()).toHaveLength(1);
    expect(screen.queryByText("go")).toBeNull();
  });

  it("caps the quoted words and lets them scroll, so a long prompt cannot push the buttons away", () => {
    const longWords = "I think we need to add something I was saying in the beginning of this. ".repeat(40).trim();
    publishDropped(longWords);
    render(<NotDeliveredIndicator sessionId={SID} claim />);
    const panel = open();
    const quote = panel.querySelector(".nd-quote") as HTMLElement;
    expect(quote.textContent).toBe(longWords);
    const cs = getComputedStyle(quote);
    expect(cs.maxHeight).not.toBe("");
    expect(cs.maxHeight).not.toBe("none");
    expect(cs.overflowY).toBe("auto");
    expect(quote.contains(within(panel).getByRole("button", { name: "Dismiss" }))).toBe(false);
  });

  it("opens over the page, never inside the layout", () => {
    render(<NotDeliveredIndicator sessionId={SID} notice={NOTICE} claim />);
    const panel = open();
    expect(getComputedStyle(panel).position).toBe("absolute");
  });
});
