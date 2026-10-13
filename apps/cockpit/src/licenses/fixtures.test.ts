import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import * as fixtures from "./fixtures";

// THE FIXTURES ARE STILL THE GATEWAY'S OWN EXAMPLES (the review of pull request 3944, finding 2).
//
// fixtures.ts is written by missions/license-switching/phase-3/copy_fixtures.py from the real answers in
// missions/license-switching/cockpit-api.md. Nothing used to notice when the document moved and the fixtures did not,
// or when a fixture was edited by hand - and from that day "a test that draws these draws what the Gateway sends"
// would quietly be false. This reads the examples out of the document again and holds every fixture to its own.
// When it fails: run copy_fixtures.py; do not edit fixtures.ts.

const DOCUMENT = resolve(dirname(fileURLToPath(import.meta.url)), "../../../../missions/license-switching/cockpit-api.md");

// The document's examples in order: the fixture each one is, or null for an example no test draws. A fragment is a
// run of fields shown without the braces around them.
const EXAMPLES: ReadonlyArray<{ fixture: string | null; fragment?: boolean }> = [
  { fixture: "OVERVIEW" },
  { fixture: "BARRED_FIELDS", fragment: true },
  { fixture: "FORECAST" },
  { fixture: "LOG_PAGE" },
  { fixture: "EMPTY_OVERVIEW" },
  { fixture: null },
  { fixture: null },
  { fixture: "RENEWAL_SET" },
  { fixture: "RENEWAL_CLEARED" },
  { fixture: null },
  { fixture: "RENEWAL_FAILED_FIELDS", fragment: true },
  { fixture: null },
  { fixture: "SWITCH_NOW_ACCEPTED" },
  { fixture: "SWITCH_NOW_REFUSED" },
  { fixture: "RENEWAL_OWNER_ONLY" },
  { fixture: "SWITCH_NOW_OWNER_ONLY" },
  { fixture: "APPROVER_SET_OWNER_ONLY" },
  { fixture: "APPROVER_REMOVE_OWNER_ONLY" },
  { fixture: "SIGN_IN_OWNER_ONLY" },
  { fixture: "APPROVERS_EMPTY" },
  { fixture: "APPROVERS" },
  { fixture: "APPROVER_SET" },
  { fixture: "APPROVER_REFUSED" },
  { fixture: "APPROVER_REMOVED" },
  { fixture: "SIGN_IN_STARTED" },
  { fixture: "SIGN_IN_RUNNING" },
  { fixture: "SIGN_IN_DIRECTOR_OFFLINE" },
  { fixture: "SIGN_IN_ALREADY_HELD" },
  { fixture: "SIGN_IN_UNKNOWN_MACHINE" },
  { fixture: "SIGN_IN_LOG" },
  { fixture: "OVERVIEW_SIGN_IN_FIELDS" },
];

describe("the Licenses fixtures", () => {
  it("are, each one, the example of the shapes document as it stands", () => {
    const text = readFileSync(DOCUMENT, "utf8").replace(/\r\n/g, "\n");
    const blocks = [...text.matchAll(/```json\n([\s\S]*?)\n```/g)].map((match) => match[1]);

    // A document that lost or gained an example is a reason to copy again, never a quiet pass.
    expect(blocks.length).toBe(EXAMPLES.length);
    const held = fixtures as unknown as Record<string, unknown>;
    let compared = 0;
    EXAMPLES.forEach((example, index) => {
      if (example.fixture === null) return;
      const inDocument: unknown = JSON.parse(example.fragment === true ? `{${blocks[index]}}` : blocks[index]);
      expect(held[example.fixture], `${example.fixture} is not example ${index + 1} of cockpit-api.md`).toEqual(inDocument);
      compared += 1;
    });
    expect(compared).toBe(27);
    // And no fixture exists that came from somewhere else.
    const named = EXAMPLES.flatMap((example) => (example.fixture === null ? [] : [example.fixture]));
    expect(Object.keys(fixtures).filter((name) => name !== "copyOf").sort()).toEqual([...named].sort());
  });
});
