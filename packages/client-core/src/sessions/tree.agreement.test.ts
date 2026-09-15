import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  attentionSections,
  buildSessionTree,
  childrenOf,
  crewAge,
  crewSummary,
  crewSummaryLine,
  descendantsOf,
  isOnAnotherMachine,
} from "./tree";

// THE TYPESCRIPT HALF OF THE CROSS-LANGUAGE AGREEMENT.
//
// The ownership tree exists twice: here, and in C# (src/CcDirector.Gateway.Contracts/SessionTree.cs, the
// Director rail's copy). Two folds of one question drift - that is the defect the Session List Views
// mission exists to stop, so it must not be the shape of its own fix.
//
// Neither implementation owns the answer. tree-agreement.json does. This file runs the REAL TypeScript
// fold over its sessions and asserts its answers; CcDirector.StateAgreementCheck.TreeAgreement runs the
// REAL C# fold over the same file and asserts the same answers (and is itself run at commit time by
// TreeAgreementTests). Change either fold alone and that language goes red. Change a fold and the file
// together and the OTHER language goes red.
//
// WHAT THIS HALF DOES NOT CHECK: each session in the file carries BOTH its raw facts and the Gateway's
// stamp, because the two folds take different input - this one reads the stamped triageBucket, while the
// C# folds the same answer from activityState, onHold and hasLiveSupervisor. That the two really do agree
// per session is asserted on the C# side, which is the only side that can compute both. A green run here
// says nothing about it.
//
// tree.test.ts remains the place where each RULE is explained and revert-proved one at a time. This file
// is the drift guard, and it deliberately asserts the same answers a second way rather than sharing a
// helper with it.

interface CrewExpectation {
  root: string;
  descendants: string[];
  count: number;
  needsYou: number;
  working: number;
  stopped: number;
  since: string | null;
  line: string;
  ageAt: string;
  age: string;
}

interface Case {
  name: string;
  proves: string;
  sessions: SessionDto[];
  roots: string[];
  children: Record<string, string[]>;
  onAnotherMachine?: Record<string, boolean>;
  crews: CrewExpectation[];
  attention: { key: string; title: string; roots: string[] }[];
}

const fixtures = JSON.parse(
  readFileSync(new URL("./tree-agreement.json", import.meta.url), "utf8"),
) as { cases: Case[] };

const ids = (sessions: SessionDto[]) => sessions.map((s) => s.sessionId);
const byId = (c: Case) => new Map(c.sessions.map((s) => [String(s.sessionId), s]));

describe("the ownership tree agrees with the answers the Director rail is measured by", () => {
  // A file that parsed to nothing would make every assertion below vacuous - a check that cannot fail.
  it("reads a shared file with cases in it", () => {
    expect(fixtures.cases.length).toBeGreaterThanOrEqual(9);
    expect(fixtures.cases.flatMap((c) => c.sessions).length).toBeGreaterThanOrEqual(20);
  });

  for (const c of fixtures.cases) {
    describe(`${c.name} - ${c.proves}`, () => {
      const lookup = byId(c);
      const tree = buildSessionTree(c.sessions);

      it("puts the same sessions at the top level", () => {
        expect(ids(tree.roots)).toEqual(c.roots);
      });

      it("puts the same sessions under each parent, in the same order", () => {
        for (const [parent, kids] of Object.entries(c.children)) {
          expect(ids(childrenOf(tree, lookup.get(parent)!))).toEqual(kids);
        }
        // And nothing else has children: a fold that invented an extra crew would otherwise pass,
        // because nothing would ask about it.
        const actualParents = c.sessions
          .filter((s) => childrenOf(tree, s).length > 0)
          .map((s) => String(s.sessionId))
          .sort();
        expect(actualParents).toEqual(Object.keys(c.children).sort());
      });

      for (const crew of c.crews) {
        it(`answers the same crew for ${crew.root}`, () => {
          const root = lookup.get(crew.root)!;
          const descendants = descendantsOf(tree, root);

          expect(descendants.map((d) => `${d.session.sessionId}@${d.parent.sessionId}@${d.depth}`))
            .toEqual(crew.descendants);

          const sum = crewSummary(root, descendants.map((d) => d.session));
          expect(sum.count).toBe(crew.count);
          expect(sum.needsYou).toBe(crew.needsYou);
          expect(sum.working).toBe(crew.working);
          expect(sum.stopped).toBe(crew.stopped);
          expect(sum.sinceMs === null ? null : new Date(sum.sinceMs).toISOString()).toBe(crew.since);
          expect(crewSummaryLine(sum)).toBe(crew.line);
          expect(crewAge(sum, Date.parse(crew.ageAt))).toBe(crew.age);
        });
      }

      if (c.onAnotherMachine) {
        it("gives the same answer about a child living on another Director", () => {
          for (const [edge, expected] of Object.entries(c.onAnotherMachine!)) {
            const [parent, child] = edge.split(">");
            expect(isOnAnotherMachine(lookup.get(parent)!, lookup.get(child)!)).toBe(expected);
          }
        });
      }

      it("orders the top level the same way for attention", () => {
        const sections = attentionSections(tree.roots);
        expect(sections.map((s) => s.title)).toEqual(c.attention.map((a) => a.title));
        // The machine-readable bucket too, not just the heading a person reads: a consumer selects on
        // the bucket, so a section wearing the right title over the wrong bucket would send the phone's
        // badge and Car Mode to the wrong set of sessions while the screen still looked correct.
        expect(sections.map((s) => s.key)).toEqual(c.attention.map((a) => a.key));
        sections.forEach((s, i) => expect(ids(s.roots)).toEqual(c.attention[i].roots));
      });
    });
  }
});
