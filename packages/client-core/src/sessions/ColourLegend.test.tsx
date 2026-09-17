import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ColourLegendButton, ColourLegendPanel } from "./ColourLegend";
import { resetLegendForTests, SESSION_COLOURS_PATH } from "./sessionColours";

// The legend as a person meets it, against a fake Gateway: nothing is read until it is asked for, the Gateway's words
// and pixels are drawn verbatim, a failed or malformed read says so instead of drawing a partial legend, focus goes in
// and comes back, and only a press that starts outside the panel closes it.

const LEGEND = {
  entries: [
    { colour: "red", hex: "#EF4444", title: "Needs you", means: "Waiting for you.", asksForYou: "Yes" },
    { colour: "cyan", hex: "#06B6D4", title: "Done", means: "Finished, asks nothing.", asksForYou: "No" },
    { colour: "green", hex: "#22C55E", title: "Ready", means: "Brand new.", asksForYou: "No" },
  ],
  broken: { hex: "#FF00FF", title: "Magenta", means: "Not a state." },
  verdictNote: "Some colours need the verdicts switched on.",
};

let calls: string[] = [];

function fakeGateway(status = 200, body: unknown = LEGEND) {
  calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      calls.push(url);
      const text = JSON.stringify(body);
      return {
        ok: status >= 200 && status < 300,
        status,
        headers: new Headers({ "Content-Type": "application/json" }),
        json: async () => JSON.parse(text),
        text: async () => text,
        clone() {
          return this;
        },
      } as unknown as Response;
    }),
  );
}

beforeEach(() => {
  resetLegendForTests();
  fakeGateway();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

// jsdom reports an inline colour as rgb(), so compare in that form.
function rgb(hex: string): string {
  const n = hex.replace("#", "");
  return `rgb(${parseInt(n.slice(0, 2), 16)}, ${parseInt(n.slice(2, 4), 16)}, ${parseInt(n.slice(4, 6), 16)})`;
}

function openLegend(): HTMLElement {
  render(<ColourLegendButton />);
  fireEvent.click(screen.getByRole("button", { name: "What do the colours mean?" }));
  return screen.getByRole("dialog", { name: "What the colours mean" });
}

describe("ColourLegendButton", () => {
  it("shows no legend and asks the Gateway nothing until it is opened", () => {
    render(<ColourLegendButton />);

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(calls).toEqual([]);
  });

  it("reads the legend from the Gateway and draws its words and pixels verbatim", async () => {
    const dialog = openLegend();

    await within(dialog).findByText("Waiting for you.");
    expect(calls).toEqual([SESSION_COLOURS_PATH]);
    for (const entry of LEGEND.entries) {
      const row = dialog.querySelector(`[data-colour="${entry.colour}"]`) as HTMLElement | null;
      if (row === null) throw new Error(`the legend drew no row for ${entry.colour}`);
      expect(within(row).getByText(entry.title)).toBeTruthy();
      expect(within(row).getByText(entry.means)).toBeTruthy();
      expect(within(row).getByText(`Asks for you: ${entry.asksForYou}`)).toBeTruthy();
      expect((row.querySelector(".colour-legend-dot") as HTMLElement).style.backgroundColor).toBe(rgb(entry.hex));
    }
    expect(within(dialog).getByText(LEGEND.broken.means)).toBeTruthy();
    expect(within(dialog).getByText(LEGEND.verdictNote)).toBeTruthy();
  });

  it("says the read failed, and draws no colours, when the Gateway refuses", async () => {
    fakeGateway(500, { error: "boom" });
    const dialog = openLegend();

    expect(await within(dialog).findByRole("alert")).toBeTruthy();
    expect(dialog.querySelectorAll(".colour-legend-row")).toHaveLength(0);
  });

  it("refuses a malformed legend whole rather than drawing it with a gap", async () => {
    fakeGateway(200, { ...LEGEND, entries: [{ ...LEGEND.entries[0], hex: "" }, LEGEND.entries[1]] });
    const dialog = openLegend();

    expect((await within(dialog).findByRole("alert")).textContent).toContain("missing a usable colour for red");
    expect(dialog.querySelectorAll(".colour-legend-row")).toHaveLength(0);
  });

  it("moves focus to Close, keeps it inside on Tab, and returns it to the opener on close", async () => {
    render(<ColourLegendButton />);
    const opener = screen.getByRole("button", { name: "What do the colours mean?" });
    opener.focus();
    fireEvent.click(opener);
    const close = within(screen.getByRole("dialog")).getByRole("button", { name: "Close" });

    expect(document.activeElement).toBe(close);
    fireEvent.keyDown(window, { key: "Tab" });
    expect(document.activeElement).toBe(close);

    fireEvent.click(close);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(document.activeElement).toBe(opener);
  });

  it("closes on Escape", () => {
    openLegend();
    fireEvent.keyDown(window, { key: "Escape" });

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("does not close on a drag that starts inside the panel and ends on the backdrop", async () => {
    const dialog = openLegend();
    const backdrop = dialog.parentElement as HTMLElement;
    const sentence = await within(dialog).findByText("Waiting for you.");

    // What the browser does: the press lands on the sentence, and the click fires on the common ancestor.
    fireEvent.mouseDown(sentence);
    fireEvent.click(backdrop);

    expect(screen.queryByRole("dialog")).not.toBeNull();
  });

  it("closes on a press that starts and ends on the backdrop", () => {
    const backdrop = openLegend().parentElement as HTMLElement;
    fireEvent.mouseDown(backdrop);
    fireEvent.click(backdrop);

    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("ColourLegendPanel", () => {
  it("draws every colour the Gateway sends, on screen without being opened", async () => {
    render(<ColourLegendPanel />);
    const panel = screen.getByRole("complementary", { name: "What the colours mean" });

    await within(panel).findByText("Waiting for you.");
    for (const entry of LEGEND.entries) {
      const row = panel.querySelector(`[data-colour="${entry.colour}"]`) as HTMLElement | null;
      if (row === null) throw new Error(`the panel drew no row for ${entry.colour}`);
      expect(within(row).getByText(entry.title)).toBeTruthy();
      expect(within(row).getByText(entry.means)).toBeTruthy();
      expect((row.querySelector(".colour-legend-dot") as HTMLElement).style.backgroundColor).toBe(rgb(entry.hex));
    }
  });

  it("reads the legend once however many surfaces show it", async () => {
    render(
      <>
        <ColourLegendPanel />
        <ColourLegendPanel />
      </>,
    );
    await waitFor(() => expect(screen.getAllByText("Waiting for you.")).toHaveLength(2));
    expect(calls).toEqual([SESSION_COLOURS_PATH]);
  });

  it("says the read failed, and draws no colours, when the Gateway refuses", async () => {
    fakeGateway(500, { error: "boom" });
    render(<ColourLegendPanel />);
    const panel = screen.getByRole("complementary", { name: "What the colours mean" });

    expect(await within(panel).findByRole("alert")).toBeTruthy();
    expect(panel.querySelectorAll(".colour-legend-row")).toHaveLength(0);
  });
});
