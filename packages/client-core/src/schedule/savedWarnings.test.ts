import { describe, it, expect } from "vitest";
import { savedWarnings } from "./savedWarnings";

describe("savedWarnings", () => {
  it("is null when the Gateway gave neither warning", () => {
    expect(savedWarnings({ loadWarning: null, shiftWarning: null })).toBeNull();
    expect(savedWarnings({})).toBeNull();
  });

  it("gives each warning verbatim, the load warning first", () => {
    expect(savedWarnings({ loadWarning: "Busy at 06:00.", shiftWarning: "Ends after its shift." })).toBe(
      "Busy at 06:00. Ends after its shift.",
    );
    expect(savedWarnings({ shiftWarning: "Ends after its shift." })).toBe("Ends after its shift.");
  });
});
