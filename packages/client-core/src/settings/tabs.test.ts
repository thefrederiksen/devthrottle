import { describe, expect, it } from "vitest";
import { visibleTabs, tabFromParam } from "./tabs";

describe("visibleTabs", () => {
  it("shows the four shared tabs on the phone, notifications first", () => {
    expect(visibleTabs("mobile").map((t) => t.id)).toEqual([
      "notifications",
      "language",
      "transcription",
      "fleetmanager",
    ]);
  });

  it("shows the same four on the Cockpit, in the same order, plus its own Injected text", () => {
    expect(visibleTabs("cockpit").map((t) => t.id)).toEqual([
      "notifications",
      "language",
      "transcription",
      "fleetmanager",
      "injectedtext",
    ]);
  });

  // Issue #1010: Language takes the slot AI held. Asserted on BOTH surfaces and as a relation to the AI
  // row, because "the Language tab shipped" and "it shipped on the phone too" are different claims - and
  // the phone is where the last attempt's failures were noticed.
  it("offers Language where AI used to be, on both surfaces", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const ids = visibleTabs(surface).map((t) => t.id);
      expect(ids).toContain("language");
      expect(ids).not.toContain("ai");
      expect(ids.indexOf("language")).toBe(1);
    }
  });

  // Hidden on BOTH surfaces, not on one - a tab dropped from the desktop strip and left on the phone
  // would be precisely the drift this shared list exists to prevent.
  it("keeps the AI tab out of the strip on both surfaces", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(visibleTabs(surface).map((t) => t.id)).not.toContain("ai");
    }
  });

  // The parity law: the desktop may go DEEPER, never sideways. Every tab the phone shows must also be on
  // the Cockpit, in the same relative order - a phone-only tab, or a reshuffle, would be exactly the
  // drift this shared list exists to prevent. Written as a relation between the two lists rather than as
  // a second hardcoded list, so it keeps holding as tabs are added.
  it("gives the phone a subset of the Cockpit's tabs, in the same order", () => {
    const cockpit = visibleTabs("cockpit").map((t) => t.id);
    const mobile = visibleTabs("mobile").map((t) => t.id);
    expect(cockpit.filter((id) => mobile.includes(id))).toEqual(mobile);
  });

  it("labels a tab identically on both surfaces", () => {
    const cockpit = new Map(visibleTabs("cockpit").map((t) => [t.id, t.label]));
    for (const t of visibleTabs("mobile")) expect(cockpit.get(t.id)).toBe(t.label);
  });

  // The Fleet Manager mission, step 5: the Fleet Manager tab takes the Assistant's place on both surfaces, and the
  // Assistant is hidden - not deleted - until step 9 removes the Assistant from the product.
  it("offers Fleet Manager where Assistant was, on both surfaces, and hides Assistant", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const tabs = visibleTabs(surface);
      const ids = tabs.map((t) => t.id);
      expect(ids.indexOf("fleetmanager")).toBe(3);
      expect(tabs[3].label).toBe("Fleet Manager");
      expect(ids).not.toContain("assistant");
    }
  });

  it("sends an old link to the Assistant tab to the default, and a Fleet Manager link to its tab", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam("assistant", surface)).toBe("notifications");
      expect(tabFromParam("fleetmanager", surface)).toBe("fleetmanager");
    }
  });

  it("keeps Injected text off the phone - it is Cockpit only (issue #550)", () => {
    expect(visibleTabs("mobile").map((t) => t.id)).not.toContain("injectedtext");
  });

  // Car Mode was removed from the product (#1028). The tab it used to own is now the Assistant tab - the
  // one setting it held that was never Car Mode's alone - so "carmode" is a retired id like the others: an
  // old link lands on the default rather than on a tab that no longer exists.
  it("no longer offers a Car Mode tab on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(visibleTabs(surface).map((t) => t.id as string)).not.toContain("carmode");
      expect(tabFromParam("carmode", surface)).toBe("notifications");
    }
  });

  it("never includes a machine or Privacy tab", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const ids = visibleTabs(surface).map((t) => t.id as string);
      expect(ids).not.toContain("machine");
      expect(ids).not.toContain("privacy");
    }
  });
});

describe("tabFromParam", () => {
  it("resolves each of a surface's own tab ids to itself", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      for (const t of visibleTabs(surface)) expect(tabFromParam(t.id, surface)).toBe(t.id);
    }
  });

  it("falls back to notifications for missing or unknown ids", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam(null, surface)).toBe("notifications");
      expect(tabFromParam("nonsense", surface)).toBe("notifications");
    }
  });

  // A hidden tab is not a back door either: no escape hatch was wanted, so ?tab=ai gets the same
  // treatment as any other id that is not one of this surface's tabs. Asserted rather than left implied,
  // because "hidden from the strip but still reachable by its link" is the other thing this could
  // plausibly have meant, and it is not what was decided.
  it("does not resolve the hidden AI tab from a link either, on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam("ai", surface)).toBe("notifications");
    }
  });

  // A deep link is not permission to render something. A phone opening a Cockpit link must land on a real
  // tab, not select one its own strip does not list and its own panel cannot draw.
  it("refuses a Cockpit-only tab on the phone and falls back to the default", () => {
    expect(tabFromParam("injectedtext", "mobile")).toBe("notifications");
    expect(tabFromParam("injectedtext", "cockpit")).toBe("injectedtext");
  });

  it("no longer resolves the retired machine/telemetry/privacy ids to a tab", () => {
    expect(tabFromParam("machine", "cockpit")).toBe("notifications");
    expect(tabFromParam("telemetry", "cockpit")).toBe("notifications");
    expect(tabFromParam("privacy", "cockpit")).toBe("notifications");
  });
});
