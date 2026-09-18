import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { dotTitle, MalformedColourLegendError, parseSessionColourLegend, type SessionColourLegend } from "./sessionColours";

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

describe("dotTitle - the hover over a session's dot", () => {
  const legend: SessionColourLegend = {
    entries: [
      { colour: "red", hex: "#EF4444", title: "Needs you", means: "Waiting for you.", asksForYou: "Yes" },
      { colour: "purple", hex: "#A855F7", title: "Carrying on", means: "Will continue on its own.", asksForYou: "No" },
      { colour: "grey", hex: "#6B7280", title: "Snoozed or exited", means: "Resting.", asksForYou: "No" },
    ],
    broken: { hex: "#FF00FF", title: "Magenta", means: "Not a state." },
    verdictNote: "note",
  };
  const stamped = (effectiveColor: string, stateLabel: string, lastStatusReason = "needs you"): SessionDto =>
    ({ sessionId: "s", effectiveColor, stateLabel, lastStatusReason }) as SessionDto;

  it("names the colour and says the Gateway's label - never the Director's pre-Wingman reason", () => {
    // The owner's case, 16 September: session 144 painted purple hovered "needs you".
    const title = dotTitle(stamped("purple", "Monitor fix round 2 progress"), legend);
    expect(title).toBe("Carrying on: Monitor fix round 2 progress");
    expect(title.toLowerCase()).not.toContain("needs you");
  });

  it("does not repeat a label that is the colour's own name", () => {
    expect(dotTitle(stamped("red", "Needs you"), legend)).toBe("Needs you");
  });

  it("explains an unknown state under the grey entry, as the Gateway's legend does", () => {
    expect(dotTitle(stamped("unknown", "Unknown"), legend)).toBe("Snoozed or exited: Unknown");
  });

  it("says the stamped label alone until the legend has loaded", () => {
    expect(dotTitle(stamped("purple", "Monitor fix round 2 progress"), null)).toBe("Monitor fix round 2 progress");
  });
});
