// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";

// The three cards of the Fleet Manager page (step 6). Each renders the record's fields exactly as the Gateway sent
// them, and a button is ONE Gateway call that answers the record; the Gateway passes the words to the Fleet Manager.

vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({ reportClientError: vi.fn() }));

import { OutcomeCard } from "./OutcomeCard";
import { ANSWERED_CARD, DECISION_CARD, FINDING_CARD, READY_CARD } from "./fixtures";
import type { CardAnswerDeps } from "@devthrottle/client-core/fleetmanager/answerCard";

function deps(overrides: Partial<CardAnswerDeps> = {}) {
  return {
    answer: vi.fn(async (_id: string, _words: string) => undefined),
    ...overrides,
  };
}

describe("OutcomeCard", () => {
  beforeEach(() => cleanup());

  it("draws a Ready card from the record's fields verbatim", () => {
    render(<OutcomeCard card={READY_CARD} onAnswered={() => undefined} deps={deps()} />);

    for (const text of [
      "Ready for you (fake)",
      "Fix the flaky list test",
      "Risk low (fake)",
      "passed",
      "3 of 3 live",
      "a second reviewer, 1 finding fixed",
      "The list now waits for the redraw itself. Nothing a user sees changes.",
    ])
      expect(screen.getByText(text)).toBeTruthy();
    const link = screen.getByRole("link", { name: "Open pull request #2934" });
    expect(link.getAttribute("href")).toBe("/example/acme/widgets/pull/2934");
    expect(screen.getByRole("button", { name: "Merge" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Send it back..." })).toBeTruthy();
  });

  it("draws a Finding card: the answer, then the reason, then the links", () => {
    render(<OutcomeCard card={FINDING_CARD} onAnswered={() => undefined} deps={deps()} />);

    const card = screen.getByTestId(`fmp-card-${FINDING_CARD.id}`);
    const text = card.textContent ?? "";
    const answerAt = text.indexOf("Both reviews say the same thing from different sides.");
    const reasonAt = text.indexOf("Their tools run agents we cannot see, and they stop short of merged.");
    const linkAt = text.indexOf("Open tool-a-review.md");
    expect(answerAt).toBeGreaterThan(-1);
    expect(reasonAt).toBeGreaterThan(answerAt);
    expect(linkAt).toBeGreaterThan(reasonAt);
    expect(screen.getByText("Finding (fake)")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Open tool-b-review.md" }).getAttribute("href")).toBe(
      "/example/reports/tool-b-review.md",
    );
  });

  it("draws a Decision card: the options, the recommended one marked, why, and one button per option", () => {
    render(<OutcomeCard card={DECISION_CARD} onAnswered={() => undefined} deps={deps()} />);

    expect(screen.getByText("Decision - only you can make this (fake)")).toBeTruthy();
    expect(screen.getByText("Recommended (fake)")).toBeTruthy();
    const recommended = screen.getByText("Recommended (fake)").parentElement!;
    expect(recommended.className).toContain("fmp-opt-rec");
    expect(recommended.textContent).toContain("Replace it for ordinary changes.");
    expect(screen.getByText("Why: running both doubled the time on the last four changes and found nothing extra.")).toBeTruthy();
    const buttons = screen.getAllByRole("button").map((b) => b.textContent);
    expect(buttons).toEqual(["Replace it for ordinary changes. Keep the reviewer only for missions.", "Always run both."]);
  });

  it("a button makes one call - the answer, with the button's words - and the page re-reads", async () => {
    let release: () => void = () => undefined;
    const d = deps({ answer: vi.fn(() => new Promise<undefined>((r) => (release = () => r(undefined)))) });
    const onAnswered = vi.fn();
    render(<OutcomeCard card={DECISION_CARD} onAnswered={onAnswered} deps={d} />);

    fireEvent.click(screen.getByRole("button", { name: "Always run both." }));

    // While it is recorded, the button shows the Gateway's busy label.
    expect(screen.getByRole("button", { name: "Recording... (fake)" })).toBeTruthy();
    release();
    await waitFor(() => expect(onAnswered).toHaveBeenCalled());
    expect(d.answer).toHaveBeenCalledTimes(1);
    expect(d.answer).toHaveBeenCalledWith(DECISION_CARD.id, "Always run both.");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("Send it back asks for words, and sends the Gateway's prefix followed by them exactly as typed", async () => {
    const d = deps();
    render(<OutcomeCard card={READY_CARD} onAnswered={() => undefined} deps={d} />);

    fireEvent.click(screen.getByRole("button", { name: "Send it back..." }));
    expect(d.answer).not.toHaveBeenCalled();
    fireEvent.change(screen.getByPlaceholderText("What should change? (fake)"), { target: { value: "  use the real clock  " } });
    fireEvent.click(screen.getByRole("button", { name: "Send it back" }));

    const words = "Send it back: Fix the flaky list test.   use the real clock  ";
    await waitFor(() => expect(d.answer).toHaveBeenCalledWith(READY_CARD.id, words));
    expect(d.answer).toHaveBeenCalledTimes(1);
  });

  it("Send it back's box is put away with the Gateway's cancel label, and nothing is recorded", () => {
    const d = deps();
    render(<OutcomeCard card={READY_CARD} onAnswered={() => undefined} deps={d} />);

    fireEvent.click(screen.getByRole("button", { name: "Send it back..." }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel (fake)" }));

    expect(screen.queryByPlaceholderText("What should change? (fake)")).toBeNull();
    expect(d.answer).not.toHaveBeenCalled();
  });

  it("an answered card shows the answer, how far it has got, and offers no buttons", () => {
    render(<OutcomeCard card={ANSWERED_CARD} onAnswered={() => undefined} deps={deps()} />);

    expect(screen.getByText("Answered 10:40 (fake)")).toBeTruthy();
    expect(screen.getByText("Always run both, for now.")).toBeTruthy();
    expect(screen.getByText("Waiting to reach the Fleet Manager (fake).")).toBeTruthy();
    expect(screen.queryAllByRole("button")).toHaveLength(0);
  });

  it("a refused answer is shown with the Gateway's words and the buttons stay", async () => {
    const d = deps({ answer: vi.fn(async () => Promise.reject(new Error("outcome was already answered (fake Gateway)"))) });
    const onAnswered = vi.fn();
    render(<OutcomeCard card={DECISION_CARD} onAnswered={onAnswered} deps={d} />);

    fireEvent.click(screen.getByRole("button", { name: "Always run both." }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("Your answer was not recorded (fake): outcome was already answered (fake Gateway)");
    expect(onAnswered).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Always run both." })).toBeTruthy();
  });
});
