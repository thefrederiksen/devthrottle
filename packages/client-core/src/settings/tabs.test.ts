import { describe, expect, it } from "vitest";
import { visibleTabs, tabFromParam } from "./tabs";

/** Where a link that names no tab of the surface lands: its first tab. */
const DEFAULT = { cockpit: "account", mobile: "notifications" } as const;

describe("visibleTabs", () => {
  it("shows the four shared tabs on the phone, notifications first", () => {
    expect(visibleTabs("mobile").map((t) => t.id)).toEqual([
      "notifications",
      "language",
      "transcription",
      "fleetmanager",
    ]);
  });

  // Owner, 8 Oct 2026: one Settings page, its tabs down the left in groups - You, Voice, Fleet - and the pages that
  // were rows of the Cockpit's menu are tabs of it now. The phone's four are among them, in the same order.
  it("shows the Cockpit's tabs grouped You, Voice, Fleet, with the four shared ones among them", () => {
    expect(visibleTabs("cockpit").map((t) => [t.group, t.id])).toEqual([
      ["you", "account"],
      ["you", "usage"],
      ["you", "notifications"],
      ["you", "language"],
      ["voice", "transcription"],
      ["voice", "dictionary"],
      ["voice", "injectedtext"],
      ["fleet", "fleetmanager"],
      ["fleet", "devices"],
    ]);
  });

  it("names the tabs the way the owner's mockup does", () => {
    expect(visibleTabs("cockpit", { team: true, teamPlan: true }).map((t) => t.label)).toEqual([
      "Account",
      "Plan and usage",
      "Notifications",
      "Language",
      "Transcription",
      "Dictionary",
      "Injected text",
      "Fleet Manager",
      "Devices and phone",
      "Members",
      "Team plan",
      "Governance",
    ]);
  });

  // The team's tabs are there only while a team is on screen, and Team plan only when the Gateway's Team page answer
  // carries a bill - its verdict that this role sees the plan. With Personal on screen the whole group is absent.
  it("offers the team's tabs only with a team on screen, and Team plan only when the Gateway shows the plan", () => {
    const ids = (team: boolean, teamPlan: boolean) => visibleTabs("cockpit", { team, teamPlan }).map((t) => t.id);
    expect(ids(false, false)).not.toContain("members");
    expect(ids(false, true)).not.toContain("teamplan");
    expect(ids(false, true)).not.toContain("governance");
    expect(ids(true, false).slice(-2)).toEqual(["members", "governance"]);
    expect(ids(true, true).slice(-3)).toEqual(["members", "teamplan", "governance"]);
    expect(visibleTabs("cockpit", { team: true, teamPlan: true }).filter((t) => t.group === "team").map((t) => t.id)).toEqual([
      "members",
      "teamplan",
      "governance",
    ]);
  });

  // The new tabs are the documented Cockpit-only exception until the phone's own layout pass (the 8 October report's
  // "Next" row) - the phone's Settings is unchanged by this work.
  it("keeps the phone's Settings exactly as it was, team or no team", () => {
    expect(visibleTabs("mobile", { team: true, teamPlan: true }).map((t) => t.id)).toEqual([
      "notifications",
      "language",
      "transcription",
      "fleetmanager",
    ]);
  });

  // Issue #1010: Language takes the slot AI held - straight after Notifications. Asserted on BOTH surfaces and as a
  // relation to its neighbour, because "the Language tab shipped" and "it shipped on the phone too" are different
  // claims - and the phone is where the last attempt's failures were noticed.
  it("offers Language where AI used to be, on both surfaces", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const ids = visibleTabs(surface).map((t) => t.id);
      expect(ids).toContain("language");
      expect(ids).not.toContain("ai");
      expect(ids.indexOf("language")).toBe(ids.indexOf("notifications") + 1);
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

  // The Fleet Manager mission: the Fleet Manager tab takes the Assistant's place on both surfaces (step 5), and
  // the Assistant tab is gone from the product (step 9).
  it("offers Fleet Manager where Assistant was, on both surfaces, and no Assistant", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const tabs = visibleTabs(surface);
      const ids = tabs.map((t) => t.id);
      expect(tabs[ids.indexOf("fleetmanager")].label).toBe("Fleet Manager");
      expect(ids).not.toContain("assistant");
    }
  });

  it("sends an old link to the Assistant tab to the default, and a Fleet Manager link to its tab", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam("assistant", surface)).toBe(DEFAULT[surface]);
      expect(tabFromParam("fleetmanager", surface)).toBe("fleetmanager");
    }
  });

  it("keeps Injected text off the phone - it is Cockpit only (issue #550)", () => {
    expect(visibleTabs("mobile").map((t) => t.id)).not.toContain("injectedtext");
  });

  // Car Mode was removed from the product (#1028), so "carmode" is a retired id like the others: an old link
  // lands on the default rather than on a tab that no longer exists.
  it("no longer offers a Car Mode tab on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(visibleTabs(surface).map((t) => t.id as string)).not.toContain("carmode");
      expect(tabFromParam("carmode", surface)).toBe(DEFAULT[surface]);
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

  // Each surface's first tab: Account on the Cockpit (Settings opens on you, as Mockup C draws it), Notifications on the
  // phone, as before.
  it("falls back to the surface's first tab for missing or unknown ids", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam(null, surface)).toBe(DEFAULT[surface]);
      expect(tabFromParam("nonsense", surface)).toBe(DEFAULT[surface]);
    }
  });

  // A link to a team tab with Personal on screen lands on the default: there is no team for it to show.
  it("refuses a team tab with no team on screen, and resolves it with one", () => {
    expect(tabFromParam("members", "cockpit")).toBe("account");
    expect(tabFromParam("members", "cockpit", { team: true, teamPlan: false })).toBe("members");
    expect(tabFromParam("teamplan", "cockpit", { team: true, teamPlan: false })).toBe("account");
    expect(tabFromParam("teamplan", "cockpit", { team: true, teamPlan: true })).toBe("teamplan");
    expect(tabFromParam("members", "mobile", { team: true, teamPlan: true })).toBe("notifications");
  });

  it("resolves each page that became a Cockpit tab, and none of them on the phone", () => {
    for (const id of ["account", "usage", "dictionary", "devices"]) {
      expect(tabFromParam(id, "cockpit")).toBe(id);
      expect(tabFromParam(id, "mobile")).toBe("notifications");
    }
  });

  // A hidden tab is not a back door either: no escape hatch was wanted, so ?tab=ai gets the same
  // treatment as any other id that is not one of this surface's tabs. Asserted rather than left implied,
  // because "hidden from the strip but still reachable by its link" is the other thing this could
  // plausibly have meant, and it is not what was decided.
  it("does not resolve the hidden AI tab from a link either, on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      expect(tabFromParam("ai", surface)).toBe(DEFAULT[surface]);
    }
  });

  // A deep link is not permission to render something. A phone opening a Cockpit link must land on a real
  // tab, not select one its own strip does not list and its own panel cannot draw.
  it("refuses a Cockpit-only tab on the phone and falls back to the default", () => {
    expect(tabFromParam("injectedtext", "mobile")).toBe(DEFAULT.mobile);
    expect(tabFromParam("injectedtext", "cockpit")).toBe("injectedtext");
  });

  it("no longer resolves the retired machine/telemetry/privacy ids to a tab", () => {
    expect(tabFromParam("machine", "cockpit")).toBe(DEFAULT.cockpit);
    expect(tabFromParam("telemetry", "cockpit")).toBe(DEFAULT.cockpit);
    expect(tabFromParam("privacy", "cockpit")).toBe(DEFAULT.cockpit);
  });
});
