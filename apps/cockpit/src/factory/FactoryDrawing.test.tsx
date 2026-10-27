// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { FactoryDrawingView } from "@devthrottle/client-core/factory/factoriesScreenClient";

// What a factory does, drawn (owner, 18 Oct 2026). Held down: the drawing is the Gateway's - lanes, shapes, arrows,
// legend, notes and the status line are rendered exactly as sent; a click on a step opens what it does and the
// command it runs; a seat's page shows only that seat's lane; and a refusal shows the Gateway's sentence.

const screenClient = vi.hoisted(() => ({ getFactoryDrawing: vi.fn() }));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

import { FactoryDrawingPanel } from "./FactoryDrawing";

const DRAWING: FactoryDrawingView = {
  factoryId: "machine-care",
  title: "Machine Care",
  width: 900,
  height: 300,
  status: {
    text: "The Cleaner's run of today 00:00 did not start.",
    sub: "The Director on SOREN_NORTH is version 2.20.1. Update this Director and try again.",
    tone: "red",
  },
  lanes: [
    { seatId: "cleaner", title: "Cleaner", sub: "SOREN_NORTH", href: "/factories/machine-care/seats/cleaner", note: null, x: 20, y: 20, width: 860, height: 120 },
    { seatId: "boss", title: "Boss", sub: "SOREN_NORTH", href: "/factories/machine-care/seats/boss", note: "Boss lists no steps yet.", x: 20, y: 152, width: 860, height: 120 },
  ],
  nodes: [
    { id: "cleaner/start", seatId: "cleaner", kind: "start-clock", title: "Daily 00:00", sub: "did not start, today 00:00", tone: "red", repeats: false, dashed: false, panel: null, x: 162, y: 52, width: 44, height: 44 },
    {
      id: "cleaner/1", seatId: "cleaner", kind: "task", title: "Worktree sweep", sub: "ReposFred and D:/wt", tone: "neutral", repeats: true, dashed: false,
      panel: { heading: "Worktree sweep (step 1 of 2, Cleaner)", lines: ["Repeats until it says DONE, at most 12 times."], runs: "powershell -Command worktree_sweep.ps1 -Apply" },
      x: 246, y: 46, width: 150, height: 56,
    },
    { id: "cleaner/2", seatId: "cleaner", kind: "decision", title: "Anything wrong?", sub: null, tone: "neutral", repeats: false, dashed: false, panel: { heading: "Anything wrong? (step 2 of 2, Cleaner)", lines: ["Decides: a failure"], runs: null }, x: 436, y: 42, width: 124, height: 64 },
    { id: "cleaner/end", seatId: "cleaner", kind: "end-done", title: "Done", sub: null, tone: "grey", repeats: false, dashed: false, panel: null, x: 600, y: 52, width: 44, height: 44 },
    { id: "boss/start", seatId: "boss", kind: "start-talk", title: "When you talk to it", sub: null, tone: "neutral", repeats: false, dashed: false, panel: null, x: 162, y: 184, width: 44, height: 44 },
  ],
  arrows: [
    { from: "cleaner/start", to: "cleaner/1", path: "M 206 74 C 206 74 238 74 238 74", head: "246,74 238,70.5 238,77.5", label: null, labelX: 0, labelY: 0, tone: "red", dashed: true },
    { from: "cleaner/2", to: "cleaner/end", path: "M 560 74 C 560 74 592 74 592 74", head: "600,74 592,70.5 592,77.5", label: "no", labelX: 580, labelY: 66, tone: "neutral", dashed: false },
    { from: "boss/start", to: "boss/end", path: "M 206 206 C 206 206 238 206 238 206", head: "246,206 238,202.5 238,209.5", label: null, labelX: 0, labelY: 0, tone: "neutral", dashed: false },
  ],
  legend: [
    { kind: "decision", tone: "neutral", text: "a decision" },
    { kind: "tone", tone: "red", text: "failed or did not start" },
  ],
  notes: ["Steps are not tracked one by one yet: the colours show how the newest run started and how it ended."],
};

function renderPanel(props: { seat?: string; showStatus?: boolean } = {}) {
  return render(
    <MemoryRouter>
      <FactoryDrawingPanel factory="machine-care" {...props} />
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  screenClient.getFactoryDrawing.mockReset();
});

describe("What a factory does, drawn", () => {
  it("draws every lane, shape and arrow the Gateway sent, with its legend and notes", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel();

    await screen.findByTestId("fd-drawing");
    expect(screenClient.getFactoryDrawing).toHaveBeenCalledWith("machine-care", expect.anything());
    expect(screen.getByTestId("fd-lane-cleaner")).toBeTruthy();
    expect(screen.getByTestId("fd-lane-boss")).toBeTruthy();
    expect(screen.getByText("Worktree sweep")).toBeTruthy();
    expect(screen.getByText("did not start, today 00:00")).toBeTruthy();
    expect(screen.getByTestId("fd-node-cleaner/start").getAttribute("class")).toContain("fd-tone-red");
    expect(screen.getByTestId("fd-node-cleaner/1").querySelector(".fd-repeat")).not.toBeNull();
    expect(screen.getByTestId("fd-arrow-cleaner/start-cleaner/1").getAttribute("class")).toContain("ff-line-dashed");
    expect(screen.getByText("no")).toBeTruthy();
    expect(within(screen.getByTestId("fd-legend")).getByText("a decision")).toBeTruthy();
    expect(screen.getByText(DRAWING.notes[0])).toBeTruthy();
    // The status line is the Floor's factory drawing, not every drawing (a seat's page draws one too).
    expect(screen.queryByTestId("fd-status")).toBeNull();
  });

  it("shows the newest run's status line, with its reason, when asked", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel({ showStatus: true });

    const status = await screen.findByTestId("fd-status");
    expect(status.getAttribute("class")).toContain("fd-tone-red");
    expect(within(status).getByText(DRAWING.status!.text)).toBeTruthy();
    expect(within(status).getByText(DRAWING.status!.sub!)).toBeTruthy();
  });

  it("opens a step on a click: what it does and the command it runs; a second click closes it", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: "Worktree sweep (step 1 of 2, Cleaner)" }));

    const step = screen.getByTestId("fd-step");
    expect(within(step).getByText("Worktree sweep (step 1 of 2, Cleaner)")).toBeTruthy();
    expect(within(step).getByText("Repeats until it says DONE, at most 12 times.")).toBeTruthy();
    expect(within(step).getByText("powershell -Command worktree_sweep.ps1 -Apply")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Worktree sweep (step 1 of 2, Cleaner)" }));
    expect(screen.queryByTestId("fd-step")).toBeNull();
  });

  it("a shape with nothing more to say is not a button", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel();

    await screen.findByTestId("fd-drawing");
    expect(screen.getByTestId("fd-node-cleaner/end").getAttribute("role")).toBeNull();
  });

  it("on a seat's page draws only that seat's lane", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel({ seat: "cleaner", showStatus: true });

    await screen.findByTestId("fd-drawing");
    expect(screen.getByTestId("fd-lane-cleaner")).toBeTruthy();
    expect(screen.queryByTestId("fd-lane-boss")).toBeNull();
    expect(screen.queryByTestId("fd-node-boss/start")).toBeNull();
    expect(screen.queryByTestId("fd-arrow-boss/start-boss/end")).toBeNull();
    expect(screen.queryByTestId("fd-status")).toBeNull();
    // Another seat's note is not on this seat's page.
    expect(screen.queryByText("Boss lists no steps yet.")).toBeNull();
  });

  it("is a group, never an image, so its steps reach assistive technology as buttons", async () => {
    screenClient.getFactoryDrawing.mockResolvedValue(DRAWING);
    renderPanel();

    const drawing = await screen.findByTestId("fd-drawing");
    expect(drawing.getAttribute("role")).toBe("group");
    expect(screen.getByTestId("fd-lane-note-boss").textContent).toBe("Boss lists no steps yet.");
  });

  it("a refusal shows the Gateway's sentence", async () => {
    screenClient.getFactoryDrawing.mockRejectedValue(new Error("There is no registered factory 'nope'."));
    renderPanel();

    expect(await screen.findByText(/There is no registered factory 'nope'\./)).toBeTruthy();
  });
});
