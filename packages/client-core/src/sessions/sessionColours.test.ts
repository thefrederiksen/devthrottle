import { describe, expect, it } from "vitest";
import { MalformedColourLegendError, parseSessionColourLegend } from "./sessionColours";

// The client checks the Gateway's legend field by field and refuses a broken one whole. A legend drawn with a gap
// would tell a person that the missing colour does not exist.

const good = {
  entries: [{ colour: "red", hex: "#EF4444", title: "Needs you", means: "Waiting for you.", asksForYou: "Yes" }],
  broken: { hex: "#FF00FF", title: "Magenta", means: "Not a state." },
  verdictNote: "Some colours need the verdicts switched on.",
};

describe("parseSessionColourLegend", () => {
  it("keeps a complete legend exactly as the Gateway sent it", () => {
    expect(parseSessionColourLegend(good)).toEqual(good);
  });

  it("refuses with its own error type, so the dialog can show what is missing", () => {
    expect(() => parseSessionColourLegend(null)).toThrow(MalformedColourLegendError);
  });

  it("refuses a body that is not a legend", () => {
    expect(() => parseSessionColourLegend(null)).toThrow("missing its body");
    expect(() => parseSessionColourLegend({ ...good, entries: [] })).toThrow("missing its list of colours");
    expect(() => parseSessionColourLegend({ ...good, entries: "red" })).toThrow("missing its list of colours");
  });

  it("refuses an entry with a blank sentence", () => {
    expect(() => parseSessionColourLegend({ ...good, entries: [{ ...good.entries[0], means: "  " }] })).toThrow(
      "missing what red means",
    );
  });

  it("refuses an entry whose swatch is not a colour", () => {
    expect(() => parseSessionColourLegend({ ...good, entries: [{ ...good.entries[0], hex: "red" }] })).toThrow(
      "missing a usable colour for red",
    );
  });

  it("refuses a legend without its magenta note or its verdict note", () => {
    expect(() => parseSessionColourLegend({ ...good, broken: undefined })).toThrow("missing the note about magenta");
    expect(() => parseSessionColourLegend({ ...good, verdictNote: "" })).toThrow("missing the note about the Wingman's verdicts");
  });
});
