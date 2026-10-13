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
//
// ONE PART OF ONE FIXTURE IS NOT A COPY, AND IT IS HELD TOO. The document's full overview was produced before the
// sign-in routes existed, so the script fills in the sign-in fields of OVERVIEW. The first test below holds OVERVIEW
// WITHOUT those fields to the document, and the second holds the fields to the one thing the script may put there:
// no approver, nothing offered, and the document's own sentence for why.

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

const ACCOUNT_SIGN_IN_FIELDS = ["approver", "approverText", "approverTone"];
const MACHINE_SIGN_IN_FIELDS = ["canAddSpare", "addSpareText", "signInsInProgress"];
const HOLDING_SIGN_IN_FIELDS = ["canRenewNow", "renewNowText"];

function without(value: object, fields: ReadonlyArray<string>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(value).filter(([name]) => !fields.includes(name)));
}

/** OVERVIEW as the document's section 2 shows it: with the sign-in fields the script filled in taken out again. */
function overviewAsInTheDocument(): unknown {
  const overview = fixtures.copyOf(fixtures.OVERVIEW);
  return {
    ...overview,
    accounts: overview.accounts.map((account) => without(account, ACCOUNT_SIGN_IN_FIELDS)),
    machines: overview.machines.map((machine) => ({
      ...without(machine, MACHINE_SIGN_IN_FIELDS),
      active: machine.active === null ? null : without(machine.active, HOLDING_SIGN_IN_FIELDS),
      spares: machine.spares.map((spare) => without(spare, HOLDING_SIGN_IN_FIELDS)),
    })),
  };
}

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
      const fixture = example.fixture === "OVERVIEW" ? overviewAsInTheDocument() : held[example.fixture];
      expect(fixture, `${example.fixture} is not example ${index + 1} of cockpit-api.md`).toEqual(inDocument);
      compared += 1;
    });
    expect(compared).toBe(24);
    // And no fixture exists that came from somewhere else.
    const named = EXAMPLES.flatMap((example) => (example.fixture === null ? [] : [example.fixture]));
    expect(Object.keys(fixtures).filter((name) => name !== "copyOf").sort()).toEqual([...named].sort());
  });

  it("fill the overview's sign-in fields in with nothing but: no approver, nothing offered, and the document's sentence for why", () => {
    // The two sentences are read out of the Gateway's own example of these fields, never typed here.
    const example = fixtures.OVERVIEW_SIGN_IN_FIELDS;
    const noApprover = example.accounts.filter((account) => account.approver === null);
    const notConnected = example.machines.filter((machine) => !machine.canAddSpare);
    expect(noApprover.length).toBe(1);
    expect(notConnected.length).toBe(1);

    expect(fixtures.OVERVIEW.accounts.length).toBe(3);
    for (const account of fixtures.OVERVIEW.accounts) {
      expect(account.approver).toBeNull();
      expect(account.approverText).toBe(noApprover[0].approverText);
      expect(account.approverTone).toBe(noApprover[0].approverTone);
    }
    expect(fixtures.OVERVIEW.machines.length).toBe(2);
    let holdings = 0;
    for (const machine of fixtures.OVERVIEW.machines) {
      // The document's sentence names its own machine; here it names this one, and that is the only difference.
      const sentence = notConnected[0].addSpareText.replace(notConnected[0].machineName, machine.label);
      expect(sentence).toContain(machine.label);
      expect(machine.canAddSpare).toBe(false);
      expect(machine.addSpareText).toBe(sentence);
      expect(machine.signInsInProgress).toEqual([]);
      for (const holding of machine.active === null ? machine.spares : [machine.active, ...machine.spares]) {
        expect(holding.canRenewNow).toBe(false);
        expect(holding.renewNowText).toBe(sentence);
        holdings += 1;
      }
    }
    expect(holdings).toBe(5);
  });
});
