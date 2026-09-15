import { describe, expect, it } from "vitest";
import { BROKEN_NOTE, COLOUR_LEGEND, SHARES_AN_ENTRY, swatchHex } from "./colourMeanings";
import { BROKEN_HEX, dotColor, paletteNames } from "./ordering";

// The legend is only worth anything while it explains the palette the roster actually paints. These hold the two to
// each other in BOTH directions, so a colour added to COLORS without words - the way cyan arrived - fails here
// instead of reaching a screen as a dot nobody can look up.

describe("the colour legend", () => {
  it("explains every colour the palette can paint", () => {
    const explained = new Set(COLOUR_LEGEND.map((e) => e.colour));
    const unexplained = paletteNames().filter((name) => !explained.has(name) && !(name in SHARES_AN_ENTRY));

    expect(unexplained).toEqual([]);
  });

  it("explains no colour the palette does not have", () => {
    const palette = new Set(paletteNames());

    expect(COLOUR_LEGEND.map((e) => e.colour).filter((c) => !palette.has(c))).toEqual([]);
  });

  it("gives each colour exactly one entry", () => {
    const colours = COLOUR_LEGEND.map((e) => e.colour);

    expect(new Set(colours).size).toBe(colours.length);
  });

  it("paints each swatch with the palette's own hex", () => {
    for (const entry of COLOUR_LEGEND) expect(swatchHex(entry)).toBe(dotColor(entry.colour));
  });

  it("points a shared name at an entry that exists and paints the same pixel", () => {
    for (const [name, entryColour] of Object.entries(SHARES_AN_ENTRY)) {
      expect(COLOUR_LEGEND.some((e) => e.colour === entryColour)).toBe(true);
      expect(dotColor(name)).toBe(dotColor(entryColour));
    }
  });

  it("never shows two entries in the same pixel", () => {
    // Issue #2892 was two meanings in one green. The legend must not repeat it with two entries in one swatch.
    const hexes = COLOUR_LEGEND.map((e) => swatchHex(e).toUpperCase());

    expect(new Set(hexes).size).toBe(hexes.length);
  });

  it("says only red asks for you outright", () => {
    expect(COLOUR_LEGEND.filter((e) => e.asksForYou === "Yes").map((e) => e.colour)).toEqual(["red"]);
  });

  it("describes the magenta sentinel as the sentinel the dots actually paint, and as no real colour", () => {
    expect(BROKEN_NOTE.hex).toBe(BROKEN_HEX);
    expect(paletteNames().map((n) => dotColor(n).toUpperCase())).not.toContain(BROKEN_NOTE.hex.toUpperCase());
  });

  it("has words for every entry", () => {
    for (const entry of COLOUR_LEGEND) {
      expect(entry.title.trim()).not.toBe("");
      expect(entry.means.trim()).not.toBe("");
      expect(entry.asksForYou.trim()).not.toBe("");
    }
  });
});
