import { describe, expect, it } from "vitest";
import fixture from "./factoryWatchView.fixture.json";
import contributorFixture from "./factoryWatchView.contributor.fixture.json";
import { FACTORY_WATCH_KEYS, FACTORY_WATCH_STEP_KINDS, type FactoryWatchView } from "./factoryWatchTypes";

// THE CONTRACT GUARD, client side. Both fixtures are written by the Gateway's own fold (FactoryWatchContractFixtureTests
// in Gateway.UnitTests, which fails when the fold's JSON stops matching them): factoryWatchView.fixture.json for the
// owner, factoryWatchView.contributor.fixture.json for a contributor. Here the same files must have exactly the keys the
// TypeScript types declare - no more, no fewer - so a DTO change that is not carried into factoryWatchTypes.ts fails one
// side or the other.

const owner = fixture as unknown as Record<string, FactoryWatchView>;
const contributor = contributorFixture as unknown as Record<string, FactoryWatchView>;

const keysOf = (o: object) => Object.keys(o).sort();
const sorted = (k: readonly PropertyKey[]) => [...k].map(String).sort();

function expectViewShape(view: FactoryWatchView) {
  expect(keysOf(view)).toEqual(sorted(FACTORY_WATCH_KEYS.view));
  expect(keysOf(view.window)).toEqual(sorted(FACTORY_WATCH_KEYS.window));
  for (const s of view.stations) expect(keysOf(s)).toEqual(sorted(FACTORY_WATCH_KEYS.station));
  for (const l of view.lines) expect(keysOf(l)).toEqual(sorted(FACTORY_WATCH_KEYS.line));
  for (const c of view.characters) expect(keysOf(c)).toEqual(sorted(FACTORY_WATCH_KEYS.character));
  for (const s of view.steps) expect(keysOf(s)).toEqual(sorted(FACTORY_WATCH_KEYS.step));
}

function expectValueTypes(view: FactoryWatchView) {
  expect(typeof view.factoryId).toBe("string");
  expect(["workshop", "hub", "lines"]).toContain(view.layout);
  expect(typeof view.floorWidth).toBe("number");
  expect(view.emptyText === null || typeof view.emptyText === "string").toBe(true);
  expect(view.truncatedText === null || typeof view.truncatedText === "string").toBe(true);
  expect(Array.isArray(view.notes)).toBe(true);
  for (const s of view.stations) {
    expect(typeof s.isBoss).toBe("boolean");
    expect(s.line === null || typeof s.line === "string").toBe(true);
  }
  for (const c of view.characters) expect(c.colour).toMatch(/^#[0-9a-f]{6}$/);
  for (const s of view.steps) {
    expect(FACTORY_WATCH_STEP_KINDS).toContain(s.kind);
    expect(s.toUtc === null || typeof s.toUtc === "string").toBe(true);
    expect(Number.isInteger(s.emptyChecks)).toBe(true);
    expect(typeof s.mine).toBe("boolean");
    expect(typeof s.alarm).toBe("boolean");
    expect(typeof s.plain).toBe("boolean");
  }
}

describe("the watch view contract", () => {
  it("holds the owner samples and the contributor samples the player is tested against", () => {
    expect(Object.keys(owner).sort()).toEqual(["empty", "hub", "lines"]);
    expect(Object.keys(contributor).sort()).toEqual(["own", "shared"]);
  });

  for (const [name, view] of Object.entries(owner)) {
    it(`owner ${name}: exactly the fields the types declare, each with its declared type`, () => {
      expectViewShape(view);
      expectValueTypes(view);
      // The owner's alarm is raised on a hiccup and on nothing else.
      for (const s of view.steps) expect(s.alarm, s.key).toBe(s.kind === "hiccup");
    });
  }

  for (const [name, view] of Object.entries(contributor)) {
    it(`contributor ${name}: exactly the fields the types declare - no factory id, notes or truncated sentence, never an alarm`, () => {
      expectViewShape(view);
      expectValueTypes(view);
      expect(view.factoryId).toBe("");
      expect(view.notes).toEqual([]);
      expect(view.truncatedText).toBeNull();
      for (const s of view.steps) expect(s.alarm, s.key).toBe(false);
      // A step that is not the contributor's carries no words.
      for (const s of view.steps.filter((s) => !s.mine)) expect(s.words).toBe("");
    });
  }

  it("contributor: the own sample is all their work, the shared sample all other people's", () => {
    expect(contributor.own.steps.every((s) => s.mine)).toBe(true);
    expect(contributor.shared.steps.every((s) => !s.mine)).toBe(true);
  });

  it("between them the samples cover every step kind the player animates", () => {
    const kinds = new Set([...Object.values(owner), ...Object.values(contributor)].flatMap((v) => v.steps.map((s) => s.kind)));
    expect([...kinds].sort()).toEqual([...FACTORY_WATCH_STEP_KINDS].sort());
  });
});
