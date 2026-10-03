import { describe, expect, it } from "vitest";
import {
  accountedFor,
  buildLineage,
  indexReport,
  nodeCount,
  originLabel,
  sessionLabel,
  tallyOrigins,
  type LineageNode,
} from "./lineage";
import type { WorkHistoryReport, WorkHistorySession } from "./historyClient";

// The lineage rules (internal#989). Almost every test here is about a parent that ISN'T in the
// group, because that is the normal case on this fleet - `session spawn <other-repo>` puts the child
// under a different repository - and each way of getting it wrong is its own quiet lie.

function session(id: string, over: Partial<WorkHistorySession> = {}): WorkHistorySession {
  return {
    sessionId: id,
    startedAtUtc: "2026-07-27T10:00:00Z",
    lastSeenUtc: "2026-07-27T11:00:00Z",
    endingTone: "neutral",
    descriptionLine: `session ${id}`,
    ...over,
  };
}

function report(sessions: WorkHistorySession[]): WorkHistoryReport {
  return {
    fromDay: "2026-07-27",
    toDay: "2026-07-27",
    repos: [
      {
        repoKey: "r",
        displayName: "r",
        days: [{ day: "2026-07-27", sessions }],
      },
    ],
  };
}

const ids = (nodes: LineageNode[]) => nodes.map((n) => n.session.sessionId);

describe("buildLineage", () => {
  it("nests a child under a parent in the same group", () => {
    const parent = session("p");
    const child = session("c", { parentSessionId: "p" });

    const roots = buildLineage([parent, child]);

    expect(ids(roots)).toEqual(["p"]);
    expect(ids(roots[0].children)).toEqual(["c"]);
  });

  it("turns twenty-two rows into the three things that were actually started", () => {
    // The whole point of the feature, in one assertion.
    const sessions: WorkHistorySession[] = [];
    for (const root of ["a", "b", "c"]) {
      sessions.push(session(root));
      for (let i = 0; i < 6; i++) sessions.push(session(`${root}${i}`, { parentSessionId: root }));
    }
    sessions.push(session("d"));

    const roots = buildLineage(sessions);

    expect(roots).toHaveLength(4);
    expect(sessions).toHaveLength(22);
    expect(roots.reduce((sum, r) => sum + nodeCount(r), 0)).toBe(22);
  });

  it("nests grandchildren, so delegation depth survives", () => {
    const roots = buildLineage([
      session("a"),
      session("b", { parentSessionId: "a" }),
      session("c", { parentSessionId: "b" }),
    ]);

    expect(ids(roots)).toEqual(["a"]);
    expect(ids(roots[0].children)).toEqual(["b"]);
    expect(ids(roots[0].children[0].children)).toEqual(["c"]);
    expect(nodeCount(roots[0])).toBe(3);
  });

  it("orders children oldest first, so a parent reads as the sequence it set off", () => {
    const roots = buildLineage([
      session("p"),
      session("late", { parentSessionId: "p", startedAtUtc: "2026-07-27T12:00:00Z" }),
      session("early", { parentSessionId: "p", startedAtUtc: "2026-07-27T09:00:00Z" }),
    ]);

    expect(ids(roots[0].children)).toEqual(["early", "late"]);
  });

  it("keeps a child at top level when its parent is in another group, and says who", () => {
    // The common case: an agent in one repository spawned work in another. Nesting it here would
    // file that work under a repository it never touched; dropping the note would make it look like
    // nobody started it.
    const elsewhere = session("p", { sessionName: "Release manager" });
    const child = session("c", { parentSessionId: "p" });
    const index = indexReport(report([elsewhere, child]));

    const roots = buildLineage([child], index);

    expect(ids(roots)).toEqual(["c"]);
    expect(roots[0].parentElsewhere).toEqual({ sessionId: "p", label: "Release manager" });
  });

  it("distinguishes a parent outside the report from one merely in another group", () => {
    // Pruned by retention, or started before the window. A null label is not a missing value to
    // hide - "started by a session we no longer keep" is true and worth showing.
    const child = session("c", { parentSessionId: "gone" });

    const roots = buildLineage([child], indexReport(report([child])));

    expect(roots[0].parentElsewhere).toEqual({ sessionId: "gone", label: null });
  });

  it("never invents a root: a child with an absent parent is still marked", () => {
    // Roots are the thing being counted. A child quietly promoted to root would add one to
    // "things you started" for a session you did not start.
    const roots = buildLineage([session("c", { parentSessionId: "gone" })]);

    expect(roots).toHaveLength(1);
    expect(roots[0].parentElsewhere?.sessionId).toBe("gone");
  });

  it("treats a session claiming itself as a root, without a note", () => {
    // The id is minted on another machine and arrives over the wire. A node that is its own child
    // would hang the render rather than show a wrong number.
    const roots = buildLineage([session("a", { parentSessionId: "a" })]);

    expect(ids(roots)).toEqual(["a"]);
    expect(roots[0].parentElsewhere).toBeUndefined();
    expect(roots[0].children).toHaveLength(0);
  });

  it("survives a two-session cycle instead of looping forever", () => {
    const roots = buildLineage([
      session("a", { parentSessionId: "b" }),
      session("b", { parentSessionId: "a" }),
    ]);

    // Neither can attach without closing the loop, so both stay at top level carrying their notes.
    // Corrupt data renders FLAT. The failure this pins is not cosmetic: the first version walked the
    // half-built tree, so at the moment each was examined the other had no children yet, both
    // attached, the forest came out with ZERO roots, and the first thing to walk it looped forever.
    expect(roots).toHaveLength(2);
    expect(roots.every((r) => r.children.length === 0)).toBe(true);
    expect(roots.every((r) => r.parentElsewhere != null)).toBe(true);
    const total = roots.reduce((sum, r) => sum + nodeCount(r), 0);
    expect(total).toBe(2);
  });

  it("survives a three-session cycle", () => {
    const roots = buildLineage([
      session("a", { parentSessionId: "c" }),
      session("b", { parentSessionId: "a" }),
      session("c", { parentSessionId: "b" }),
    ]);

    // Every session appears exactly once, and nothing loops.
    expect(roots.reduce((sum, r) => sum + nodeCount(r), 0)).toBe(3);
  });

  it("a long legitimate chain still nests", () => {
    // The cycle guard must not mistake depth for corruption.
    const sessions = [session("s0")];
    for (let i = 1; i < 20; i++) sessions.push(session(`s${i}`, { parentSessionId: `s${i - 1}` }));

    const roots = buildLineage(sessions);

    expect(ids(roots)).toEqual(["s0"]);
    expect(nodeCount(roots[0])).toBe(20);
  });

  it("keeps every session exactly once", () => {
    // The invariant that matters for any count drawn off this tree.
    const sessions = [
      session("a"),
      session("b", { parentSessionId: "a" }),
      session("c", { parentSessionId: "missing" }),
      session("d", { parentSessionId: "b" }),
    ];

    const roots = buildLineage(sessions);
    const seen: string[] = [];
    const walk = (n: LineageNode) => {
      seen.push(n.session.sessionId);
      n.children.forEach(walk);
    };
    roots.forEach(walk);

    expect(seen.sort()).toEqual(["a", "b", "c", "d"]);
  });

  it("ignores a duplicate row for the same session", () => {
    const roots = buildLineage([session("a"), session("a")]);
    expect(roots).toHaveLength(1);
  });

  it("handles an empty group", () => {
    expect(buildLineage([])).toEqual([]);
  });
});

describe("tallyOrigins", () => {
  it("keeps unrecorded apart from unknown, and neither becomes human", () => {
    // THE fold this feature exists to prevent. These fields only start being written on
    // 2026-07-27, so for any window reaching back further the unrecorded rows are the majority -
    // counting them as human would report the exact opposite of the truth.
    const tally = tallyOrigins([
      session("1", { originKind: "human" }),
      session("2", { originKind: "agent" }),
      session("3", { originKind: "agent" }),
      session("4", { originKind: "schedule" }),
      session("5", { originKind: "unknown" }),
      session("6"), // predates the field
    ]);

    expect(tally).toEqual({ human: 1, agent: 2, schedule: 1, unknown: 1, notRecorded: 1, total: 6 });
    expect(accountedFor(tally)).toBe(4);
    expect(tally.total).not.toBe(accountedFor(tally));
  });

  it("counts an all-unrecorded set as nothing accounted for", () => {
    const tally = tallyOrigins([session("1"), session("2")]);
    expect(accountedFor(tally)).toBe(0);
    expect(tally.notRecorded).toBe(2);
  });
});

describe("originLabel", () => {
  it("says a person without claiming which person", () => {
    // The Gateway is multi-tenant and the record does not store WHICH human, so "you" would be
    // inventing a fact to make a nicer sentence.
    expect(originLabel(session("1", { originKind: "human" }))).toBe("started by hand");
    expect(originLabel(session("1", { originKind: "human" }))).not.toContain("you");
  });

  it("shows nothing rather than a hedge when the record cannot say", () => {
    expect(originLabel(session("1", { originKind: "unknown" }))).toBeNull();
    expect(originLabel(session("1"))).toBeNull();
  });

  it("labels agents and schedules", () => {
    expect(originLabel(session("1", { originKind: "agent" }))).toBe("started by an agent");
    expect(originLabel(session("1", { originKind: "schedule" }))).toBe("started by a schedule");
  });
});

describe("sessionLabel", () => {
  it("prefers the name, then the number, then a short id", () => {
    expect(sessionLabel(session("abcdef123456", { sessionName: "Wingman" }))).toBe("Wingman");
    expect(sessionLabel(session("abcdef123456", { sessionNumber: 104 }))).toBe("#104");
    expect(sessionLabel(session("abcdef123456"))).toBe("abcdef12");
  });
});
