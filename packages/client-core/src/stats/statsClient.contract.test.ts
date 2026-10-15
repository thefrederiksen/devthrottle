import { afterEach, describe, expect, it, vi } from "vitest";
import { getThrottle, summarizeThrottle, formatPercent, type ThrottleFigure } from "./statsClient";
import { loadContractFixtures, loadFieldInventory, servedBodyFor, valuesAt } from "./throttleContractFixtures";

// THE CONTRACT: one hostile wire object, the REAL browser normalizer, the answer the pages print (final
// inspection finding F-01). Every fixture in tools/throttle-conformance/contract is fed through getThrottle
// over a stubbed fetch - the exact code path the Cockpit and the phone take - and what summarizeThrottle
// and formatPercent hand the rings is compared with the answer the fixture records. The mentor report's
// test suite feeds the SAME fixtures through its real checker, adapter and renderer and compares with the
// same recorded answers, so the two consumers are held to one headline by the same file.
//
// The fixtures are hostile on purpose: their headline disagrees with their counts and buckets. A client that
// divides a count, re-totals a bucket, or rounds a share for itself prints a different number here and
// fails. The inspector proved the previous suite could not see that: a "voice" constant changed to "spoken"
// in the bucket normalizer left all 26 tests green. The last fixture is exactly that token.

vi.mock("../api/client", () => ({
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

function stubFetch(body: Record<string, unknown>) {
  vi.stubGlobal("fetch", vi.fn(async () =>
    new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }),
  ));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

/** What the pages print from a figure, in the shape the fixtures record it - THE WHOLE ANSWER (fix-round
 * finding F-01): the headline the rings and split bar read, the hours the Activity chart draws, the rows and
 * summaries the Agents and Repos tabs print. Every value is read off the normalized figure; nothing is
 * computed here either. */
function renderedAnswer(figure: ThrottleFigure) {
  const summary = summarizeThrottle(figure);
  const shares = (row: ThrottleFigure["agents"][number] | ThrottleFigure["repos"][number] | ThrottleFigure["systems"][number]) => ({
    turnShare: row.turnShare, turnPercent: row.turnPercent, sessionShare: row.sessionShare, sessionPercent: row.sessionPercent,
    voiceShare: row.voiceShare, voicePercent: row.voicePercent,
  });
  return {
    denominator: summary.totalTurns,
    hasData: summary.hasData,
    voiceTurns: summary.voiceTurns,
    typedTurns: summary.typedTurns,
    phoneTurns: summary.turnsBySurface.phone,
    phoneRemainder: summary.phoneRemainder,
    voiceShare: summary.voiceShare,
    phoneShare: summary.phoneShare,
    voicePercent: summary.voicePercent,
    typedPercent: figure.headline.typed.percent,
    phonePercent: summary.phonePercent,
    surfaces: summary.surfaces.map((s) => ({ surface: s.surface, label: s.label, turns: s.turns, share: s.share, percent: s.percent, remainder: s.remainder })),
    hourly: figure.hourlyTurns.map((h) => ({ hour: h.hour, turns: h.turns, voiceTurns: h.voiceTurns, typedTurns: h.typedTurns, voiceShare: h.voiceShare, typedShare: h.typedShare })),
    agents: figure.agents.map((a) => ({ agentName: a.agentName, turns: a.turns, sessions: a.sessions, agentDrivenTurns: a.agentDrivenTurns, ...shares(a) })),
    agentsSummary: figure.agentsSummary,
    repos: figure.repos.map((r) => ({ repoName: r.repoName, turns: r.turns, sessions: r.sessions, ...shares(r) })),
    reposSummary: figure.reposSummary,
    systems: figure.systems.map((s) => ({
      osName: s.osName, turns: s.turns, sessions: s.sessions, agentDrivenTurns: s.agentDrivenTurns,
      agentDrivenShare: s.agentDrivenShare, agentDrivenPercent: s.agentDrivenPercent, ...shares(s),
      machines: s.machines.map((m) => ({
        displayName: m.displayName, turns: m.turns, sessions: m.sessions, agentDrivenTurns: m.agentDrivenTurns,
        turnPercent: m.turnPercent, sessionPercent: m.sessionPercent, agentDrivenPercent: m.agentDrivenPercent, voicePercent: m.voicePercent,
      })),
    })),
    systemsSummary: figure.systemsSummary,
  };
}

const fixtures = loadContractFixtures();

describe("the Your Throttle contract, through the real browser client", () => {
  it("has the fixtures the contract names, and at least one of each outcome", () => {
    const outcomes = new Set(fixtures.map((f) => f.expected.outcome));
    expect(outcomes).toEqual(new Set(["rendered", "empty", "refused"]));
    expect(fixtures.length).toBeGreaterThanOrEqual(6);
  });

  for (const fixture of fixtures) {
    it(fixture.name.replace(/-/g, " "), async () => {
      stubFetch(servedBodyFor(fixture.wire));
      if (fixture.expected.outcome === "refused") {
        await expect(getThrottle(undefined)).rejects.toThrow(/GET \/stats\/data answered/);
        return;
      }
      const data = await getThrottle(undefined);
      if (!data.available) throw new Error("expected a served figure");
      const rendered = renderedAnswer(data.throttle);
      expect(rendered).toEqual(fixture.expected.rendered);
      const summary = summarizeThrottle(data.throttle);
      if (fixture.expected.outcome === "empty") {
        // The empty state is the library's ruling: no number at all, never a fabricated 0%.
        expect(summary.hasData).toBe(false);
        expect(formatPercent(summary.voicePercent)).toBe("n/a");
        expect(formatPercent(summary.phonePercent)).toBe("n/a");
      } else {
        expect(summary.hasData).toBe(true);
        expect(formatPercent(summary.voicePercent)).toBe(`${fixture.expected.rendered!.voicePercent}%`);
        expect(formatPercent(summary.phonePercent)).toBe(`${fixture.expected.rendered!.phonePercent}%`);
      }
    });
  }
});

describe("the field inventory, through the real browser normalizer (finding F-08)", () => {
  it("forwards every field the inventory marks for the browser with the wire's own value", async () => {
    const inventory = loadFieldInventory();
    const fixture = fixtures.find((f) => f.expected.outcome === "rendered" && f.name === "the-headline-is-rendered-not-the-counts")!;
    stubFetch(servedBodyFor(fixture.wire));
    const data = await getThrottle(undefined);
    if (!data.available) throw new Error("expected a served figure");

    const browserPaths = Object.entries(inventory.fields).filter(([, readers]) => readers.includes("browser")).map(([p]) => p);
    expect(browserPaths.length).toBeGreaterThan(80);
    for (const path of browserPaths) {
      const wireValues = valuesAt(fixture.wire, path);
      const figureValues = valuesAt(data.throttle, path);
      expect(figureValues, path).toEqual(wireValues);
      expect(wireValues.length, path).toBeGreaterThan(0);
    }
    // The inventory covers the whole wire object: every leaf on the fixture is a path the inventory names.
    const leaves = new Set<string>();
    const walk = (node: unknown, prefix: string) => {
      if (Array.isArray(node)) {
        if (node.length === 0 || typeof node[0] !== "object" || node[0] === null) { leaves.add(prefix + "[]"); return; }
        for (const item of node) walk(item, prefix + "[]");
        return;
      }
      if (node !== null && typeof node === "object") {
        for (const [k, v] of Object.entries(node)) walk(v, prefix === "" ? k : prefix + "." + k);
        return;
      }
      leaves.add(prefix);
    };
    walk(fixture.wire, "");
    const named = new Set(Object.keys(inventory.fields));
    for (const leaf of leaves) expect(named.has(leaf), `inventory lacks ${leaf}`).toBe(true);
  });
});
