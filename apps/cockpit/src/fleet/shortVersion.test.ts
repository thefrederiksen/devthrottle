import { describe, expect, it } from "vitest";
import { shortVersion } from "./machinesFormat";

// The Directors table shows a version short, with the whole build string on hover (Web UI Polish, fix 8).
describe("shortVersion", () => {
  it("shortVersion_BuildMetadata_DropsEverythingFromThePlus", () => {
    expect(shortVersion("2.37.0+9ac50e5674b937a70a342cd81a6db9a42eb1513d")).toBe("2.37.0");
  });

  it("shortVersion_PlainRelease_IsDrawnWhole", () => {
    expect(shortVersion("2.35.1")).toBe("2.35.1");
  });

  it("shortVersion_PreReleaseWithMetadata_KeepsThePreRelease", () => {
    expect(shortVersion("2.38.0-rc.1+abc123")).toBe("2.38.0-rc.1");
  });

  it("shortVersion_LeadingPlusOrPlaceholder_IsDrawnWhole", () => {
    expect(shortVersion("-")).toBe("-");
    expect(shortVersion("+abc")).toBe("+abc");
  });
});
