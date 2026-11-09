import { describe, expect, it } from "vitest";
import fixture from "./factoryWatchView.fixture.json";
import { FACTORY_WATCH_KEYS, FACTORY_WATCH_STEP_KINDS, type FactoryWatchView } from "./factoryWatchTypes";

// THE CONTRACT GUARD, client side. factoryWatchView.fixture.json is written by the Gateway's own fold
// (FactoryWatchContractFixtureTests in Gateway.UnitTests, which fails when the fold's JSON stops matching it). Here the
// same file must have exactly the keys the TypeScript types declare - no more, no fewer - so a DTO change that is not
// carried into factoryWatchTypes.ts fails one side or the other.

const samples = fixture as unknown as Record<string, FactoryWatchView>;

const keysOf = (o: object) => Object.keys(o).sort();
const sorted = (k: readonly string[]) => [...k].sort();

describe("the watch view contract", () => {
  it("holds the three samples the player is tested against", () => {
    expect(Object.keys(samples).sort()).toEqual(["empty", "hub", "lines"]);
  });

  for (const [name, view] of Object.entries(samples)) {
    it(`${name}: every object has exactly the fields the types declare`, () => {
      expect(keysOf(view)).toEqual(sorted(FACTORY_WATCH_KEYS.view));
      expect(keysOf(view.window)).toEqual(sorted(FACTORY_WATCH_KEYS.window));
      for (const s of view.stations) expect(keysOf(s)).toEqual(sorted(FACTORY_WATCH_KEYS.station));
      for (const l of view.lines) expect(keysOf(l)).toEqual(sorted(FACTORY_WATCH_KEYS.line));
      for (const c of view.characters) expect(keysOf(c)).toEqual(sorted(FACTORY_WATCH_KEYS.character));
      for (const s of view.steps) expect(keysOf(s)).toEqual(sorted(FACTORY_WATCH_KEYS.step));
    });

    it(`${name}: every value has the type the types declare`, () => {
      expect(["workshop", "hub", "lines"]).toContain(view.layout);
      expect(typeof view.floorWidth).toBe("number");
      expect(view.emptyText === null || typeof view.emptyText === "string").toBe(true);
      expect(view.truncatedText === null || typeof view.truncatedText === "string").toBe(true);
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
    });
  }

  it("covers every step kind the player animates", () => {
    const kinds = new Set(Object.values(samples).flatMap((v) => v.steps.map((s) => s.kind)));
    expect([...kinds].sort()).toEqual([...FACTORY_WATCH_STEP_KINDS].sort());
  });
});
