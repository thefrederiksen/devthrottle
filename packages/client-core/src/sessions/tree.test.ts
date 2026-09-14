import { beforeEach, describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  attentionSections,
  buildSessionTree,
  childrenOf,
  crewAge,
  crewSummary,
  crewSummaryLine,
  descendantsOf,
  isCrewExpanded,
  isOnAnotherMachine,
  resetCrewExpandedForTests,
  setCrewExpanded,
} from "./tree";

function session(fields: Partial<SessionDto> & { sessionId: string }): SessionDto {
  return {
    createdAt: "2026-09-14T16:00:00Z",
    sortOrder: 0,
    effectiveColor: "blue",
    triageBucket: "active",
    ...fields,
  } as unknown as SessionDto;
}

// The Rule Factory crew of 2026-09-14, in miniature: an Architect (108) with a working Worker, two
// stopped ones, and a standalone session beside it.
const architect = session({ sessionId: "108", sortOrder: 2, effectiveColor: "red", triageBucket: "needsYou", needsYouSince: "2026-09-14T21:39:20Z", createdAt: "2026-09-14T16:12:42Z" });
const w106 = session({ sessionId: "106", sortOrder: 5, controllerSessionId: "108", createdAt: "2026-09-14T17:00:08Z" });
const w102 = session({ sessionId: "102", sortOrder: 3, controllerSessionId: "108", effectiveColor: "supporting", triageBucket: "onHold", createdAt: "2026-09-14T16:22:01Z" });
const w101 = session({ sessionId: "101", sortOrder: 4, controllerSessionId: "108", effectiveColor: "supporting", triageBucket: "onHold", createdAt: "2026-09-14T16:39:20Z" });
const s103 = session({ sessionId: "103", sortOrder: 1, effectiveColor: "red", triageBucket: "needsYou", needsYouSince: "2026-09-14T21:41:13Z" });
const s100 = session({ sessionId: "100", sortOrder: 0, effectiveColor: "grey", triageBucket: "onHold" });
const s112 = session({ sessionId: "112", sortOrder: 12 });

describe("buildSessionTree", () => {
  it("nests a session under the supervisor named by controllerSessionId, in desktop order", () => {
    const tree = buildSessionTree([s100, s103, architect, w106, w102, w101, s112]);

    expect(tree.roots.map((s) => s.sessionId)).toEqual(["100", "103", "108", "112"]);
    expect(childrenOf(tree, architect).map((s) => s.sessionId)).toEqual(["102", "101", "106"]);
    expect(childrenOf(tree, s112)).toEqual([]);
  });

  it("keeps the roots in the order it was given, so the caller owns my-order versus attention", () => {
    const tree = buildSessionTree([s112, architect, s100, w106]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["112", "108", "100"]);
  });

  it("makes a child whose supervisor is absent a top-level row - a dead supervisor surfaces its sessions", () => {
    const tree = buildSessionTree([s100, w106, w102]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["100", "106", "102"]);
    expect(tree.childrenOf.size).toBe(0);
  });

  it("never nests a session under itself", () => {
    const loop = session({ sessionId: "9", controllerSessionId: "9" });
    const tree = buildSessionTree([loop]);
    expect(tree.roots).toEqual([loop]);
    expect(tree.childrenOf.size).toBe(0);
  });
});

describe("crewSummary", () => {
  it("counts the children by the Gateway's own triage bucket and carries a zero needs-you count", () => {
    const sum = crewSummary(architect, [w102, w101, w106]);
    expect(sum).toEqual({ count: 3, needsYou: 0, working: 1, stopped: 2, sinceMs: Date.parse("2026-09-14T16:12:42Z") });
    expect(crewSummaryLine(sum)).toBe("3 under it: 1 working, 2 stopped, 0 need you");
  });

  it("counts a surfaced red child, the dead-supervisor case, so a collapsed crew cannot hide it", () => {
    const red = session({ sessionId: "7", controllerSessionId: "108", effectiveColor: "red", triageBucket: "needsYou" });
    expect(crewSummary(architect, [red, w106]).needsYou).toBe(1);
    expect(crewSummaryLine(crewSummary(architect, [red, w106]))).toBe("2 under it: 1 working, 0 stopped, 1 need you");
  });

  it("ages the crew from its oldest session, root included", () => {
    const sum = crewSummary(architect, [w106]);
    expect(crewAge(sum, Date.parse("2026-09-14T21:42:00Z"))).toBe("5h 29m");
  });

  it("gives no age when nothing parses", () => {
    const sum = crewSummary(session({ sessionId: "x", createdAt: "" }), [session({ sessionId: "y", createdAt: "not a date" })]);
    expect(sum.sinceMs).toBeNull();
    expect(crewAge(sum, Date.now())).toBe("");
  });
});

describe("attentionSections", () => {
  it("puts the longest wait on top, then working, then snoozed, and drops empty sections", () => {
    const sections = attentionSections([s100, s103, architect, s112]);
    expect(sections.map((g) => g.title)).toEqual(["Needs you", "Working", "Snoozed"]);
    // 108 has waited since 21:39:20, 103 since 21:41:13 - 108 is first.
    expect(sections[0].roots.map((s) => s.sessionId)).toEqual(["108", "103"]);
    expect(sections[1].roots.map((s) => s.sessionId)).toEqual(["112"]);
    expect(sections[2].roots.map((s) => s.sessionId)).toEqual(["100"]);
  });

  it("omits a section with nothing in it", () => {
    expect(attentionSections([s112]).map((g) => g.title)).toEqual(["Working"]);
  });
});

describe("expanded state", () => {
  beforeEach(() => resetCrewExpandedForTests());

  it("starts collapsed and remembers an expansion", () => {
    expect(isCrewExpanded("108")).toBe(false);
    setCrewExpanded("108", true);
    expect(isCrewExpanded("108")).toBe(true);
    setCrewExpanded("108", false);
    expect(isCrewExpanded("108")).toBe(false);
  });
});

describe("the inspection's three cases (pull request 2852 review)", () => {
  const manager = session({ sessionId: "M", sortOrder: 6, controllerSessionId: "108", createdAt: "2026-09-14T17:30:00Z" });
  const deepWorker = session({ sessionId: "D", sortOrder: 7, controllerSessionId: "M", createdAt: "2026-09-14T17:40:00Z", effectiveColor: "supporting", triageBucket: "onHold" });

  it("keeps a second ownership level reachable and counts it in the crew", () => {
    const tree = buildSessionTree([architect, manager, deepWorker, w106]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["108"]);
    expect(childrenOf(tree, architect).map((s) => s.sessionId)).toEqual(["106", "M"]);
    expect(childrenOf(tree, manager).map((s) => s.sessionId)).toEqual(["D"]);
    expect(descendantsOf(tree, architect).map((d) => `${d.session.sessionId}@${d.depth}`)).toEqual(["106@1", "M@1", "D@2"]);
    // Each descendant names the session it is DIRECTLY under, not the root.
    expect(descendantsOf(tree, architect).map((d) => d.parent.sessionId)).toEqual(["108", "108", "M"]);
    const sum = crewSummary(architect, descendantsOf(tree, architect).map((d) => d.session));
    expect(crewSummaryLine(sum)).toBe("3 under it: 2 working, 1 stopped, 0 need you");
  });

  it("nests a child on another Director under its parent, and says it is elsewhere", () => {
    const remote = session({ sessionId: "R", sortOrder: 0, controllerSessionId: "108", directorId: "d2", machineName: "SORENLAPTOP" });
    const local = { ...architect, directorId: "d1", machineName: "SOREN_NORTH" } as SessionDto;
    const tree = buildSessionTree([local, remote]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["108"]);
    expect(childrenOf(tree, local).map((s) => s.sessionId)).toEqual(["R"]);
    expect(isOnAnotherMachine(local, remote)).toBe(true);
    expect(isOnAnotherMachine(local, { ...w106, directorId: "d1" } as SessionDto)).toBe(false);
  });

  it("renders every member of an ownership loop exactly once, as roots", () => {
    const a = session({ sessionId: "a", sortOrder: 0, controllerSessionId: "b" });
    const b = session({ sessionId: "b", sortOrder: 1, controllerSessionId: "a" });
    const tree = buildSessionTree([s112, a, b]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["112", "a", "b"]);
    expect(descendantsOf(tree, a)).toEqual([]);
    expect(descendantsOf(tree, b)).toEqual([]);
  });

  it("promotes a loop's whole subtree with it, so nothing under a loop is lost", () => {
    const a = session({ sessionId: "a", sortOrder: 0, controllerSessionId: "b" });
    const b = session({ sessionId: "b", sortOrder: 1, controllerSessionId: "a" });
    const under = session({ sessionId: "u", sortOrder: 2, controllerSessionId: "b" });
    const tree = buildSessionTree([a, b, under]);
    expect(tree.roots.map((s) => s.sessionId)).toEqual(["a", "b"]);
    expect(childrenOf(tree, b).map((s) => s.sessionId)).toEqual(["u"]);
  });
});
