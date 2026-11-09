import { describe, expect, it } from "vitest";
import fixture from "./factoryWatchView.fixture.json";
import { FACTORY_WATCH_KEYS, FACTORY_WATCH_STEP_KINDS, type FactoryWatchView } from "./factoryWatchTypes";

// THE CONTRACT GUARD, client side. factoryWatchView.fixture.json is written by the Gateway's own fold
// (FactoryWatchContractFixtureTests in Gateway.UnitTests, which fails when the fold's JSON stops matching it). Here the
// same file must have exactly the keys the TypeScript types declare - no more, no fewer - so a DTO change that is not
// carried into factoryWatchTypes.ts fails one side or the other.

import contributorFixture from "./factoryWatchView.contributor.fixture.json";

const samples = fixture as unknown as Record<string, FactoryWatchView>;
// Hand-written in the contributor audience's shape until piece 3 lands it on the Gateway; then the contract test on
// the Gateway covers both audiences and this file is replaced by the fold's own output.
const contributor = contributorFixture as unknown as FactoryWatchView;

const keysOf = (o: object) => Object.keys(o).sort();
const sorted = (k: readonly PropertyKey[]) => [...k].map(String).sort();

/** Every required field present, nothing that is not a declared field. */
function expectShape(o: object, k: { required: readonly PropertyKey[]; optional: readonly PropertyKey[] }) {
  const have = keysOf(o);
  for (const r of k.required) expect(have, `missing ${String(r)}`).toContain(r);
  for (const h of have) expect([...k.required, ...k.optional], `undeclared field ${h}`).toContain(h);
}

function expectViewShape(view: FactoryWatchView) {
  expectShape(view, FACTORY_WATCH_KEYS.view);
  expectShape(view.window, FACTORY_WATCH_KEYS.window);
  for (const s of view.stations) expectShape(s, FACTORY_WATCH_KEYS.station);
  for (const l of view.lines) expectShape(l, FACTORY_WATCH_KEYS.line);
  for (const c of view.characters) expectShape(c, FACTORY_WATCH_KEYS.character);
  for (const s of view.steps) expectShape(s, FACTORY_WATCH_KEYS.step);
}

function expectValueTypes(view: FactoryWatchView) {
  expect(["workshop", "hub", "lines"]).toContain(view.layout);
  expect(typeof view.floorWidth).toBe("number");
  expect(view.emptyText === null || typeof view.emptyText === "string").toBe(true);
  for (const s of view.stations) {
    expect(typeof s.isBoss).toBe("boolean");
    expect(s.line === null || typeof s.line === "string").toBe(true);
  }
  for (const c of view.characters) expect(c.colour).toMatch(/^#[0-9a-f]{6}$/);
  for (const s of view.steps) {
    expect(FACTORY_WATCH_STEP_KINDS).toContain(s.kind);
    expect(s.toUtc === null || typeof s.toUtc === "string").toBe(true);
    expect(Number.isInteger(s.emptyChecks)).toBe(true);
  }
}

describe("the watch view contract", () => {
  it("holds the three owner samples the player is tested against", () => {
    expect(Object.keys(samples).sort()).toEqual(["empty", "hub", "lines"]);
  });

  for (const [name, view] of Object.entries(samples)) {
    it(`owner ${name}: exactly the fields the types declare, every owner field present`, () => {
      expectViewShape(view);
      // The owner audience sends every field of the view, and no contributor-only one.
      expect(keysOf(view)).toEqual(sorted([...FACTORY_WATCH_KEYS.view.required, ...FACTORY_WATCH_KEYS.view.optional]));
      for (const s of view.steps) expect(keysOf(s)).toEqual(sorted(FACTORY_WATCH_KEYS.step.required));
    });

    it(`owner ${name}: every value has the type the types declare`, () => {
      expectValueTypes(view);
      expect(typeof view.truncatedText === "string" || view.truncatedText === null).toBe(true);
      expect(Array.isArray(view.notes)).toBe(true);
    });
  }

  it("contributor: the shape piece 3 sends - no factory id, notes or truncated sentence; mine on every step", () => {
    expectViewShape(contributor);
    expectValueTypes(contributor);
    expect(contributor).not.toHaveProperty("factoryId");
    expect(contributor).not.toHaveProperty("notes");
    expect(contributor).not.toHaveProperty("truncatedText");
    for (const s of contributor.steps) expect(typeof s.mine).toBe("boolean");
    // A step that is not the contributor's carries no words.
    for (const s of contributor.steps.filter((s) => s.mine === false)) expect(s.words).toBe("");
  });

  it("between them the samples cover every step kind the player animates", () => {
    const kinds = new Set([...Object.values(samples), contributor].flatMap((v) => v.steps.map((s) => s.kind)));
    expect([...kinds].sort()).toEqual([...FACTORY_WATCH_STEP_KINDS].sort());
  });
});
