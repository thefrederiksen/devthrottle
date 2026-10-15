// TEST SUPPORT ONLY - the Your Throttle contract fixtures (final inspection finding F-01), read from the
// product repository's tools/throttle-conformance/contract directory. Never import this from page code:
// it reads the file system.
//
// Each fixture is one hostile GET /stats/data "throttle" object and the answer a correct consumer renders
// from it. The browser tests feed the object through the REAL client (getThrottle over a stubbed fetch) and
// the REAL pages; the mentor report's tests feed the same object through its real checker, adapter and
// renderer. Both must print the same headline or refuse the same way. The manifest carries each fixture's
// SHA-256 so a fixture edited by hand, or a copy that drifted from the other repository's, is a red here.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

export interface ContractRowShares {
  turnShare: number | null;
  turnPercent: number | null;
  sessionShare: number | null;
  sessionPercent: number | null;
  voiceShare: number | null;
  voicePercent: number | null;
}

/** THE WHOLE RENDERED ANSWER a fixture records (fix-round finding F-01): every value a page or the report
 * puts in front of the reader from the wire object. The browser tests read these off the rendered DOM; the
 * report's tests read the headline part off its rendered page and email parts. */
export interface ContractRendered {
  denominator: number;
  hasData: boolean;
  voiceTurns: number;
  typedTurns: number;
  phoneTurns: number;
  phoneRemainder: number;
  voiceShare: number | null;
  phoneShare: number | null;
  voicePercent: number | null;
  typedPercent: number | null;
  phonePercent: number | null;
  surfaces: { surface: string; label: string; turns: number; share: number | null; percent: number | null; remainder: number }[];
  hourly: { hour: string; turns: number; voiceTurns: number; typedTurns: number; voiceShare: number | null; typedShare: number | null }[];
  agents: ({ agentName: string; turns: number; sessions: number; agentDrivenTurns: number } & ContractRowShares)[];
  agentsSummary: {
    agentCount: number; totalTurns: number; totalSessions: number; voiceTurns: number;
    voiceShare: number | null; voicePercent: number | null; topAgentName: string | null;
    topShare: number | null; topPercent: number | null; agentDrivenTurns: number;
    leverage: number | null; leverageText: string | null; hasData: boolean;
  };
  repos: ({ repoName: string; turns: number; sessions: number } & ContractRowShares)[];
  reposSummary: {
    repoCount: number; totalTurns: number; totalSessions: number; voiceTurns: number;
    voiceShare: number | null; voicePercent: number | null; topRepoName: string | null;
    topShare: number | null; topPercent: number | null; hasData: boolean;
  };
  systems: ({
    osName: string; turns: number; sessions: number; agentDrivenTurns: number;
    agentDrivenShare: number | null; agentDrivenPercent: number | null;
    machines: { displayName: string; turns: number; sessions: number; agentDrivenTurns: number;
      turnPercent: number | null; sessionPercent: number | null; agentDrivenPercent: number | null; voicePercent: number | null }[];
  } & ContractRowShares)[];
  systemsSummary: {
    systemCount: number; machineCount: number; totalTurns: number; totalSessions: number;
    topSystemName: string | null; topShare: number | null; topPercent: number | null;
    topMachineName: string | null; topMachineTurns: number | null; topMachineOsName: string | null;
    agentDrivenTurns: number; topAgentDrivenSystemName: string | null; topAgentDrivenPercent: number | null;
    unknownTurns: number; hasData: boolean;
  };
}

/** What the recorded answer looks like from the browser's side: shares rounded the way a page's DOM lets
 * them be read back (ten places, through a style's percentage), and the surface split as the pages draw
 * it - surfaces with a turn, largest first. The report-only field (a surface's remainder) is left out. */
export type BrowserRendered = Omit<ContractRendered, "voiceShare" | "phoneShare" | "surfaces" | "hourly" | "agents" | "repos" | "systems"> & {
  voiceShare: number | null;
  phoneShare: number | null;
  surfaces: { surface: string; label: string; turns: number; share: number | null; percent: number | null }[];
  segments: { surface: string; label: string; turns: number; share: number | null; percent: number | null }[];
  hourly: ContractRendered["hourly"];
  agents: ContractRendered["agents"];
  repos: ContractRendered["repos"];
  systems: ContractRendered["systems"];
};

function round10(n: number | null): number | null {
  return n === null ? null : Math.round(n * 1e10) / 1e10;
}

export function browserRenderedAnswer(rendered: ContractRendered): BrowserRendered {
  const surfaces = rendered.surfaces.map((s) => ({ surface: s.surface, label: s.label, turns: s.turns, share: round10(s.share), percent: s.percent }));
  return {
    ...rendered,
    voiceShare: round10(rendered.voiceShare),
    phoneShare: round10(rendered.phoneShare),
    surfaces,
    segments: surfaces.filter((s) => s.turns > 0).sort((a, b) => b.turns - a.turns),
    hourly: rendered.hourly.map((h) => ({ ...h, voiceShare: round10(h.voiceShare), typedShare: round10(h.typedShare) })),
    agents: rendered.agents.map((a) => ({ ...a, voiceShare: round10(a.voiceShare) })),
    repos: rendered.repos.map((r) => ({ ...r, voiceShare: round10(r.voiceShare) })),
    systems: rendered.systems.map((s) => ({ ...s, voiceShare: round10(s.voiceShare) })),
  };
}

export interface ContractFixture {
  name: string;
  why: string;
  wire: Record<string, unknown>;
  expected: { outcome: "rendered" | "empty" | "refused"; rendered?: ContractRendered };
}

export interface FieldInventory {
  /** Dotted path (with [] for a list) to the consumers that read it: "browser" and/or "report". */
  fields: Record<string, string[]>;
}

/** The contract directory, found by walking up from the test's working directory to the repository root -
 * the three web workspaces each run vitest from their own folder, and under jsdom import.meta.url is not a
 * file URL, so the path is located rather than assumed. Throws when no root holds the contract. */
function findContractDir(): string {
  // The product's cross-repository runner (tools/throttle-conformance/contract/run_contract.py) points
  // both consumers' suites at ONE directory of fixtures through this variable.
  const override = process.env.THROTTLE_CONTRACT_DIR;
  if (override !== undefined && override.length > 0) {
    if (!existsSync(join(override, "manifest.json"))) throw new Error("THROTTLE_CONTRACT_DIR has no manifest.json: " + override);
    return override;
  }
  let dir = resolve(process.cwd());
  for (;;) {
    const candidate = join(dir, "tools", "throttle-conformance", "contract");
    if (existsSync(join(candidate, "manifest.json"))) return candidate;
    const parent = dirname(dir);
    if (parent === dir) throw new Error("no tools/throttle-conformance/contract/manifest.json above " + process.cwd());
    dir = parent;
  }
}

export const CONTRACT_DIR = findContractDir();

/** The digest of a fixture's TEXT with line endings normalised to LF: this repository stores LF and git
 * writes CRLF into a fresh checkout on Windows, so a raw digest would be a digest of the checkout rather
 * than of the fixture (the mentor harness's settled-module pins learnt the same lesson). */
function sha256(text: string): string {
  return createHash("sha256").update(text.replace(/\r\n/g, "\n"), "utf8").digest("hex");
}

/** Every fixture, checked against manifest.json. A fixture whose digest is not the manifest's, or a manifest
 * entry with no file, throws: a fixture set that cannot prove it is the shared set proves nothing. */
export function loadContractFixtures(): ContractFixture[] {
  const manifest = JSON.parse(readFileSync(join(CONTRACT_DIR, "manifest.json"), "utf8")) as {
    fixtures: Record<string, string>;
  };
  const names = Object.keys(manifest.fixtures).sort();
  if (names.length === 0) throw new Error("manifest.json names no fixtures; an empty contract is not a contract");
  const onDisk = readdirSync(CONTRACT_DIR).filter((f) => f.endsWith(".json") && !["manifest.json", "field-inventory.json"].includes(f)).sort();
  if (JSON.stringify(onDisk) !== JSON.stringify(names)) {
    throw new Error(`the contract directory holds ${JSON.stringify(onDisk)} but manifest.json names ${JSON.stringify(names)}; run make_fixtures.py`);
  }
  return names.map((name) => {
    const text = readFileSync(join(CONTRACT_DIR, name), "utf8");
    const digest = sha256(text);
    if (digest !== manifest.fixtures[name]) {
      throw new Error(`${name} has digest ${digest} but manifest.json says ${manifest.fixtures[name]}; the fixture was edited by hand`);
    }
    return JSON.parse(text) as ContractFixture;
  });
}

/** The field inventory (finding F-08), checked against the manifest the same way. */
export function loadFieldInventory(): FieldInventory {
  const manifest = JSON.parse(readFileSync(join(CONTRACT_DIR, "manifest.json"), "utf8")) as { inventory: string };
  const text = readFileSync(join(CONTRACT_DIR, "field-inventory.json"), "utf8");
  if (sha256(text) !== manifest.inventory) throw new Error("field-inventory.json does not match manifest.json; run make_fixtures.py");
  return JSON.parse(text) as FieldInventory;
}

/** Every value at a dotted inventory path, in document order; "[]" fans out over a list. */
export function valuesAt(root: unknown, path: string): unknown[] {
  const parts = path.split(".");
  let current: unknown[] = [root];
  for (const part of parts) {
    const next: unknown[] = [];
    const isList = part.endsWith("[]");
    const key = isList ? part.slice(0, -2) : part;
    for (const node of current) {
      const value = key === "" ? node : (node as Record<string, unknown> | null)?.[key];
      if (isList) {
        if (!Array.isArray(value)) throw new Error(`${path}: ${key} is not a list`);
        next.push(...value);
      } else {
        if (value === undefined) throw new Error(`${path}: ${key} is absent`);
        next.push(value);
      }
    }
    current = next;
  }
  return current;
}

/** A served GET /stats/data body around one fixture's throttle object. */
export function servedBodyFor(wire: Record<string, unknown>): Record<string, unknown> {
  return {
    available: true,
    generatedAtUtc: "2026-09-05T16:00:00Z",
    timeZone: "America/Toronto",
    throttle: wire,
    concurrency: null,
    statisticsUnavailableReason: "no store",
    notCaptured: [],
  };
}
