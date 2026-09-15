import { afterEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { ColourLegendButton } from "./ColourLegend";
import { BROKEN_NOTE, COLOUR_LEGEND, VERDICT_NOTE, swatchHex } from "./colourMeanings";

// The legend as a person meets it: a button, a dialog with every colour in its real pixel, and three ways out.

afterEach(cleanup);

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
  it("shows no legend until it is asked for", () => {
    render(<ColourLegendButton />);

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("lists every colour with its name, its meaning and its own swatch", () => {
    const dialog = openLegend();

    for (const entry of COLOUR_LEGEND) {
      const row = dialog.querySelector(`[data-colour="${entry.colour}"]`) as HTMLElement | null;
      if (row === null) throw new Error(`the legend has no row for ${entry.colour}`);
      expect(within(row).getByText(entry.title)).toBeTruthy();
      expect(within(row).getByText(entry.means)).toBeTruthy();
      expect(within(row).getByText(`Asks for you: ${entry.asksForYou}`)).toBeTruthy();
      const dot = row.querySelector(".colour-legend-dot") as HTMLElement;
      expect(dot.style.backgroundColor).toBe(rgb(swatchHex(entry)));
    }
  });

  it("tells a finished session and a brand-new one apart", () => {
    const dialog = openLegend();
    const done = dialog.querySelector('[data-colour="cyan"] .colour-legend-dot') as HTMLElement;
    const ready = dialog.querySelector('[data-colour="green"] .colour-legend-dot') as HTMLElement;

    expect(done.style.backgroundColor).not.toBe(ready.style.backgroundColor);
  });

  it("explains the magenta sentinel and when the Wingman's colours appear", () => {
    const dialog = openLegend();

    expect(within(dialog).getByText(BROKEN_NOTE.means)).toBeTruthy();
    expect(within(dialog).getByText(VERDICT_NOTE)).toBeTruthy();
  });

  it("closes on the Close button", () => {
    const dialog = openLegend();
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes on Escape", () => {
    openLegend();
    fireEvent.keyDown(window, { key: "Escape" });

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes on a tap outside the panel, and stays open on a tap inside it", () => {
    const dialog = openLegend();
    fireEvent.click(within(dialog).getByText(COLOUR_LEGEND[0].means));
    expect(screen.queryByRole("dialog")).not.toBeNull();

    fireEvent.click(dialog.parentElement as HTMLElement);
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
