// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { FleetManagerWalkthrough } from "@devthrottle/client-core/fleetmanager/walkthroughClient";
import type { WalkthroughActionDeps } from "@devthrottle/client-core/fleetmanager/walkthroughActions";
import { cardItem, doneItem, LAYOUTS, menuItem, walkthrough } from "./walkthroughFixtures";

// "Take me through them" (step 7): every sentence, mark and permission is the Gateway's and is rendered as sent; the
// view only keeps its place in the round.

const api = vi.hoisted(() => ({
  get: vi.fn<(round?: readonly string[] | null) => Promise<FleetManagerWalkthrough>>(),
  close: vi.fn<(id: string) => Promise<{ headline: string; recordError: string | null }>>(),
  lines: vi.fn(async () => "Which layout should I keep?\n> 1. A - card grid\n  2. B - single column\n"),
  refreshPage: vi.fn(),
}));

vi.mock("@devthrottle/client-core/fleetmanager/walkthroughClient", () => ({
  getWalkthrough: api.get,
  closeWalkthroughSession: api.close,
  readSessionLines: api.lines,
  // Reached only through the answer and snooze buttons, which these tests hand fakes.
  recordWalkthroughAnswer: vi.fn(),
  recordWalkthroughSnooze: vi.fn(),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  reportClientError: vi.fn(),
  describeAndReport: (_s: string, _a: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
}));
vi.mock("./pageStore", () => ({ fleetManagerPageStore: { refreshNow: api.refreshPage } }));
// The card is step 6's and has its own tests; here it only has to be mounted, and to say when it was answered.
vi.mock("./OutcomeCard", () => ({
  OutcomeCard: (p: { card: { id: string; title: string }; onAnswered: () => void }) => (
    <div data-testid="outcome-card" data-card={p.card.id}>
      {p.card.title}
      <button type="button" onClick={p.onAnswered}>
        answer the card (fake)
      </button>
    </div>
  ),
}));

import { WalkthroughView } from "./WalkthroughView";

function makeDeps(overrides: Partial<WalkthroughActionDeps> = {}): WalkthroughActionDeps {
  return {
    answer: vi.fn(async () => ({ reason: "Sent to the session (fake)." })),
    recordAnswer: vi.fn(async () => undefined),
    snooze: vi.fn(async () => undefined),
    recordSnooze: vi.fn(async () => undefined),
    ...overrides,
  };
}

function renderView(deps: WalkthroughActionDeps = makeDeps()) {
  render(
    <MemoryRouter initialEntries={["/fleet-manager/walkthrough"]}>
      <Routes>
        <Route path="/fleet-manager/walkthrough" element={<WalkthroughView deps={deps} />} />
        <Route path="/fleet-manager" element={<div>the conversation page</div>} />
        <Route path="/session/:sid" element={<div>a session page</div>} />
      </Routes>
    </MemoryRouter>,
  );
  return deps;
}

describe("WalkthroughView", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    api.get.mockResolvedValue(walkthrough());
  });

  it("opens on the first open item and renders the reading, the advice and the round as sent", async () => {
    renderView();

    const item = await screen.findByTestId("fmw-item-rec-2");
    expect(within(item).getByText("2 of 3")).toBeTruthy();
    expect(within(item).getByText("Pick a fire-safety layout")).toBeTruthy();
    expect(within(item).getByText("waiting 42m")).toBeTruthy();
    const reading = screen.getByTestId("fmw-reading");
    expect(within(reading).getByText("What it needs from you - read by the Wingman")).toBeTruthy();
    expect(within(reading).getByText("Which fire-safety layout to keep")).toBeTruthy();
    // The session's own words, character for character - the double space included.
    expect(screen.getByTestId("fmw-evidence").textContent).toBe("Which layout should I keep?  The other one will be deleted.");
    expect(within(reading).getByText("Risk: irreversible")).toBeTruthy();
    expect(within(screen.getByTestId("fmw-advice")).getByText(
      "You picked the long column for the last two client pages, and the client reads on a phone. I'd pick B.",
    )).toBeTruthy();
    expect(screen.getByText("This round - 3")).toBeTruthy();
    expect(screen.getByTestId("fmw-step-rec-1").textContent).toContain("You said \"Yes.\" - 08:43");
    expect(screen.getByTestId("fmw-step-rec-2").getAttribute("aria-current")).toBe("step");
    expect(screen.getByTestId("fmw-not-in-round").textContent).toBe(
      "Not in this round: 2 you snoozed and 4 of the Fleet Manager's sessions that are working.",
    );
    // The first read starts the round; nothing is sent back until there is one.
    expect(api.get).toHaveBeenCalledWith(null, undefined);
  });

  it("marks both picks with the Gateway's words, on the options it named", async () => {
    renderView();

    const a = await screen.findByTestId("fmw-option-0");
    const b = screen.getByTestId("fmw-option-1");
    expect(a.textContent).toBe("A - card grid (its pick)");
    expect(b.textContent).toBe("B - single column (Fleet Manager's pick)");
    expect(a.className).toContain("fmw-session-pick");
    expect(b.className).toContain("ui-btn-primary");
    expect(a.className).not.toContain("ui-btn-primary");
  });

  it("shows the session's last lines from the buffer route", async () => {
    renderView();

    const text = await screen.findByTestId("fmw-screen-text");
    await waitFor(() => expect(text.textContent).toBe("Which layout should I keep?\n> 1. A - card grid\n  2. B - single column"));
    expect(api.lines).toHaveBeenCalledWith(LAYOUTS, 14, expect.anything());
  });

  it("answers the session through the option's index, then moves to the next open item with the same round", async () => {
    const deps = renderView();
    await screen.findByTestId("fmw-item-rec-2"); // the first read is done
    const settledRound = walkthrough([doneItem(), { ...menuItem(), done: true, stepLine: "You said \"B - single column\" - 14:31" }, cardItem()]);
    api.get.mockResolvedValue(settledRound);

    fireEvent.click(await screen.findByTestId("fmw-option-1"));

    await screen.findByTestId("fmw-item-rec-3");
    expect(deps.answer).toHaveBeenCalledWith(LAYOUTS, "verdict-layouts", [1]);
    expect(deps.recordAnswer).toHaveBeenCalledWith("rec-2", "verdict-layouts", [1]);
    expect(screen.getByText("Sent to the session (fake).")).toBeTruthy();
    expect(api.get).toHaveBeenLastCalledWith(["rec-1", "rec-2", "rec-3"], undefined);
    expect(api.refreshPage).toHaveBeenCalled();
    expect(screen.getByTestId("fmw-step-rec-2").textContent).toContain("You said \"B - single column\" - 14:31");
  });

  it("shows a refused answer verbatim, records nothing and stays on the item", async () => {
    const deps = renderView(makeDeps({
      answer: vi.fn(async () => Promise.reject(new Error("The session's screen changed since the Wingman read it, so nothing was sent."))),
    }));

    fireEvent.click(await screen.findByTestId("fmw-option-0"));

    expect((await screen.findByTestId("fmw-refusal")).textContent).toBe(
      "The session's screen changed since the Wingman read it, so nothing was sent.",
    );
    expect(deps.recordAnswer).not.toHaveBeenCalled();
    expect(screen.getByTestId("fmw-item-rec-2")).toBeTruthy();
  });

  it("says so when the session took the answer but the record was not updated", async () => {
    renderView(makeDeps({ recordAnswer: vi.fn(async () => Promise.reject(new Error("outcome rec-2 was already answered"))) }));

    fireEvent.click(await screen.findByTestId("fmw-option-1"));

    expect((await screen.findByRole("alert")).textContent).toBe(
      "The session took your answer, but the Fleet Manager's record was not updated: outcome rec-2 was already answered",
    );
  });

  it("closes only through the confirmation window, then moves on", async () => {
    renderView();
    api.close.mockResolvedValue({ headline: "Stopped the session (fake).", recordError: null });

    fireEvent.click(await screen.findByText("Close the session..."));
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByText("Close Harbour Bistro - Worker - fire-safety mockups?")).toBeTruthy();
    expect(within(dialog).getByText("Its branch main is fully in origin/main (fake). Closing stops it; it cannot be undone.")).toBeTruthy();
    expect(api.close).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByText("Close it"));

    await waitFor(() => expect(api.close).toHaveBeenCalledWith("rec-2"));
    expect(await screen.findByText("Stopped the session (fake).")).toBeTruthy();
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());
  });

  it("cancelling the confirmation closes nothing", async () => {
    renderView();

    fireEvent.click(await screen.findByText("Close the session..."));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByText("Cancel"));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(api.close).not.toHaveBeenCalled();
  });

  it("a close the Gateway refuses stays in the window with its sentence", async () => {
    renderView();
    api.close.mockRejectedValue(new Error("Close is not offered: this session has 1 uncommitted file."));

    fireEvent.click(await screen.findByText("Close the session..."));
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByText("Close it"));

    const dialog = await screen.findByRole("alertdialog");
    expect(await within(dialog).findByText("Close is not offered: this session has 1 uncommitted file.")).toBeTruthy();
  });

  it("offers no close when the Gateway refused it, and says why", async () => {
    api.get.mockResolvedValue(walkthrough([cardItem()]));
    renderView();

    const item = await screen.findByTestId("fmw-item-rec-3");
    expect(within(item).queryByText("Close the session...")).toBeNull();
    expect(screen.getByTestId("fmw-close-refused").textContent).toBe("Close is not offered: this session has 3 uncommitted files.");
    expect(within(item).queryByText("Snooze 1 hour")).toBeNull();
    expect(within(item).getByText("Cannot be snoozed now (fake).")).toBeTruthy();
  });

  it("answers an item with no current reading through its card, told to the Fleet Manager", async () => {
    api.get.mockResolvedValue(walkthrough([cardItem()]));
    renderView();

    const card = await screen.findByTestId("outcome-card");
    // The card is the page's own: one Gateway call, which passes the answer to the Fleet Manager.
    expect(card.getAttribute("data-card")).toBe("rec-3");
    expect(screen.getByText("The Wingman has no reading of this session's current stop (fake).")).toBeTruthy();
    expect(screen.getByText("The Fleet Manager wrote no advice for this one.")).toBeTruthy();
    expect(screen.getByText("The session's computer is not reporting (fake).")).toBeTruthy();
    expect(screen.queryByTestId("fmw-answer")).toBeNull();
  });

  it("skip moves to the next item and changes nothing", async () => {
    const deps = renderView();

    fireEvent.click((await screen.findAllByText("Skip for now"))[0]);

    expect(await screen.findByTestId("fmw-item-rec-3")).toBeTruthy();
    expect(deps.answer).not.toHaveBeenCalled();
    expect(deps.snooze).not.toHaveBeenCalled();
    expect(api.close).not.toHaveBeenCalled();
    expect(api.get).toHaveBeenCalledTimes(1);
  });

  it("skipping the last item ends the round with the Gateway's words, and can go through the skipped ones again", async () => {
    renderView();

    fireEvent.click((await screen.findAllByText("Skip for now"))[0]);
    fireEvent.click((await screen.findAllByText("Skip for now"))[0]);

    const end = await screen.findByTestId("fmw-end");
    expect(within(end).getByText("End of the round")).toBeTruthy();
    expect(within(end).getByText("2 items in this round are still waiting (fake).")).toBeTruthy();

    fireEvent.click(within(end).getByText("Go through the skipped ones again"));
    expect(await screen.findByTestId("fmw-item-rec-2")).toBeTruthy();
  });

  it("snoozes for the Gateway's length, then moves on", async () => {
    const deps = renderView();

    fireEvent.click(await screen.findByText("Snooze 1 hour"));

    await screen.findByTestId("fmw-item-rec-3");
    expect(deps.snooze).toHaveBeenCalledWith(LAYOUTS, 60);
    expect(deps.recordSnooze).toHaveBeenCalledWith("rec-2");
  });

  it("opens the session's page", async () => {
    renderView();

    fireEvent.click(await screen.findByText("Open the session"));

    expect(await screen.findByText("a session page")).toBeTruthy();
  });

  it("goes back to the conversation", async () => {
    renderView();

    fireEvent.click(await screen.findByTestId("fmw-back"));

    expect(await screen.findByText("the conversation page")).toBeTruthy();
  });

  it("a round with everything settled shows its end", async () => {
    api.get.mockResolvedValue(walkthrough([doneItem()]));
    renderView();

    const end = await screen.findByTestId("fmw-end");
    expect(within(end).getByText("That is the round.")).toBeTruthy();
    expect(within(end).queryByText("Go through the skipped ones again")).toBeNull();
  });

  it("shows the Gateway's words when the walkthrough cannot be read", async () => {
    api.get.mockRejectedValue(new Error("The walkthrough is the owner's."));
    renderView();

    expect((await screen.findByRole("alert")).textContent).toBe("The walkthrough is the owner's.");
  });
});
