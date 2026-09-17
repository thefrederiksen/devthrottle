import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "@devthrottle/client-core/fleet/fleetClient";
import {
  agentBadgeText,
  directorLabelOf,
  directorsByMachine,
  elsewhereTag,
  FLEET_TREE_PIVOTS,
  groupByDirector,
  machineKeyOf,
  modelChip,
  modelKeyOf,
  nestedElsewhereText,
  laneTree,
} from "./fleetMapFormat";
import { buildSessionTree, childrenOf, type SessionTree } from "@devthrottle/client-core/sessions/tree";

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s1", agent: "ClaudeCode", activityState: "Working", ...overrides } as SessionDto;
}

// A child of `controller`. The two fields travel together on the wire, so the fixture keeps them together.
function child(sessionId: string, controllerSessionId: string, overrides: Partial<SessionDto> = {}): SessionDto {
  return session({ sessionId, isControlled: true, controllerSessionId, ...overrides });
}

// Order roots/siblings by session id, so assertions are about grouping and not about sorting.
const byId = (a: SessionDto, b: SessionDto): number => (a.sessionId ?? "").localeCompare(b.sessionId ?? "");

const ids = (list: SessionDto[]): string[] => list.map((s) => s.sessionId ?? "");

// Every card a column draws, depth-first, as "id@depth" - what the view renders when every crew is open.
function drawn(tree: SessionTree): string[] {
  const out: string[] = [];
  const walk = (s: SessionDto, depth: number): void => {
    out.push(`${s.sessionId}@${depth}`);
    for (const k of childrenOf(tree, s)) walk(k, depth + 1);
  };
  for (const r of tree.roots) walk(r, 0);
  return out;
}

describe("laneTree - the Fleet Map draws the Sessions list's tree", () => {
  // The owner's case, 16 September: Architect 102 on the Mac Mini started Worker 146 on the Mac Mini AND
  // Worker 124 on SOREN_NORTH. The Sessions list showed both under 102; the map showed only 146, and 124
  // sat loose at the top of SOREN_NORTH's column.
  const mac = { machineName: "devthrottle-mac-mini", directorId: "mac-1" };
  const north = { machineName: "SOREN_NORTH", directorId: "north-1" };
  const a102 = session({ sessionId: "102", ...mac });
  const w146 = child("146", "102", mac);
  const w124 = child("124", "102", north);
  const loose = session({ sessionId: "144", ...north });
  const fleet = [a102, w146, w124, loose];

  it("puts a crew member from another machine under its parent, in the parent's column", () => {
    const tree = buildSessionTree(fleet);
    const macColumn = laneTree([a102, w146], tree);
    expect(drawn(macColumn)).toEqual(["102@0", "146@1", "124@1"]);
  });

  it("does not draw that crew member again in its own machine's column", () => {
    const tree = buildSessionTree(fleet);
    const northColumn = laneTree([w124, loose], tree);
    expect(drawn(northColumn)).toEqual(["144@0"]);
  });

  it("draws every session exactly once across all the columns", () => {
    const tree = buildSessionTree(fleet);
    const all = [...drawn(laneTree([a102, w146], tree)), ...drawn(laneTree([w124, loose], tree))];
    expect(all.map((x) => x.split("@")[0]).sort()).toEqual(["102", "124", "144", "146"]);
  });

  it("agrees with the Sessions list about who is under whom", () => {
    const tree = buildSessionTree(fleet);
    const macColumn = laneTree([a102, w146], tree);
    expect(ids(childrenOf(macColumn, a102)).sort()).toEqual(ids(childrenOf(tree, a102)).sort());
  });

  it("keeps the column's own order for the top-level cards", () => {
    const tree = buildSessionTree(fleet);
    expect(ids(laneTree([loose, w124], tree).roots)).toEqual(["144"]);
    const b = session({ sessionId: "b", ...north });
    expect(ids(laneTree([b, loose], buildSessionTree([...fleet, b])).roots)).toEqual(["b", "144"]);
  });

  it("without a fleet tree (by agent, by model, a search) nests only inside the column", () => {
    // A Codex Worker under a Claude Architect: the agent pivot puts them in different columns, and each
    // must show on its own there rather than hide inside the other column's crew.
    expect(drawn(laneTree([a102, w146], null))).toEqual(["102@0", "146@1"]);
    expect(drawn(laneTree([w124], null))).toEqual(["124@0"]);
  });

  it("nests deeper than two levels", () => {
    const f = [session({ sessionId: "a" }), child("b", "a"), child("c", "b"), child("d", "c")];
    expect(drawn(laneTree(f, buildSessionTree(f)))).toEqual(["a@0", "b@1", "c@2", "d@3"]);
  });

  it("never hangs or loses a card on an ownership loop", () => {
    const f = [child("a", "b"), child("b", "a"), session({ sessionId: "c" })];
    const out = drawn(laneTree(f, buildSessionTree(f)));
    expect(out.map((x) => x.split("@")[0]).sort()).toEqual(["a", "b", "c"]);
  });

  it("is used by the machine, director, repository and working tree pivots only", () => {
    expect([...FLEET_TREE_PIVOTS].sort()).toEqual(["director", "machine", "repo", "worktree"]);
  });
});

describe("nestedElsewhereText", () => {
  it("says why a column with only crew from elsewhere has no cards", () => {
    expect(nestedElsewhereText(1)).toBe("1 session here is shown under the session that started it, in another column");
    expect(nestedElsewhereText(2)).toBe("2 sessions here are shown under the sessions that started them, in other columns");
  });

  it("says nothing when there is nothing drawn elsewhere", () => {
    expect(nestedElsewhereText(0)).toBe("");
  });
});

describe("elsewhereTag", () => {
  it("names the machine when the child runs on another machine", () => {
    const p = session({ sessionId: "102", machineName: "devthrottle-mac-mini", directorId: "mac-1" });
    const c = child("124", "102", { machineName: "SOREN_NORTH", directorId: "north-1" });
    expect(elsewhereTag(p, c, undefined)).toEqual({ k: "on", v: "SOREN_NORTH" });
  });

  it("names the Director when the child runs on another Director of the same machine", () => {
    const p = session({ sessionId: "p", machineName: "SOREN_NORTH", directorId: "SOREN_NORTH-1" });
    const c = child("c", "p", { machineName: "SOREN_NORTH", directorId: "SOREN_NORTH-2" });
    const reach = { directorId: "SOREN_NORTH-2", machineName: "SOREN_NORTH", state: REACHABILITY_ONLINE, displayName: "DevThrottle_2" } as DirectorReachability;
    expect(elsewhereTag(p, c, reach)).toEqual({ k: "on", v: "DevThrottle_2" });
  });

  it("says nothing when the child runs where its parent does", () => {
    const p = session({ sessionId: "p", machineName: "SOREN_NORTH", directorId: "north-1" });
    const c = child("c", "p", { machineName: "SOREN_NORTH", directorId: "north-1" });
    expect(elsewhereTag(p, c, undefined)).toBeNull();
  });
});

function director(overrides: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId: "d1", machineName: "SOREN", state: REACHABILITY_ONLINE, ...overrides };
}

describe("machineKeyOf", () => {
  it("keys a session and a Director with the same machine name to the same lane", () => {
    // The whole join depends on this: a session on "SOREN" and a Director advertising " soren " must
    // collapse onto one key, or the idle Director would open a second, empty "SOREN" lane.
    expect(machineKeyOf("SOREN").key).toBe(machineKeyOf(" soren ").key);
  });

  it("falls back to (unknown machine) when the name is blank", () => {
    expect(machineKeyOf("").title).toBe("(unknown machine)");
    expect(machineKeyOf(null).title).toBe("(unknown machine)");
    expect(machineKeyOf(undefined).title).toBe("(unknown machine)");
  });
});

describe("directorsByMachine", () => {
  it("groups Directors by their machine key", () => {
    const out = directorsByMachine([
      director({ directorId: "a", machineName: "SOREN" }),
      director({ directorId: "b", machineName: "SOREN" }),
      director({ directorId: "c", machineName: "MAC" }),
    ]);
    const soren = out.find((m) => m.key === "soren");
    const mac = out.find((m) => m.key === "mac");
    expect(soren?.directors.map((d) => d.directorId)).toEqual(["a", "b"]);
    expect(mac?.directors.map((d) => d.directorId)).toEqual(["c"]);
  });

  it("INCLUDES an offline Director - an unreachable machine is dimmed on the map, never dropped from it", () => {
    // This used to drop the offline entry, which deleted the machine from the Fleet Map entirely once its
    // tunnel went down. The Gateway now serves that machine's sessions with their age, so the map has to
    // show it: the state and the last-seen line are what the sub-header renders instead of a free slot.
    const out = directorsByMachine([
      director({ directorId: "on", machineName: "M", state: REACHABILITY_ONLINE }),
      director({ directorId: "wob", machineName: "M", state: REACHABILITY_WOBBLY }),
      director({ directorId: "off", machineName: "M", state: REACHABILITY_OFFLINE }),
    ]);
    const m = out.find((x) => x.key === "m");
    expect(m?.directors.map((d) => d.directorId)).toEqual(["on", "wob", "off"]);
  });

  it("gives an offline-only machine a lane of its own, so the machine still appears", () => {
    const out = directorsByMachine([
      director({ directorId: "off", machineName: "ASLEEP", state: REACHABILITY_OFFLINE }),
    ]);
    expect(out.map((m) => m.title)).toEqual(["ASLEEP"]);
  });
});

describe("groupByDirector", () => {
  it("folds an idle Director in as an empty group - a free slot", () => {
    const sessions = [session({ sessionId: "s1", directorId: "busy" })];
    const out = groupByDirector(sessions, byId, [
      director({ directorId: "busy" }),
      director({ directorId: "idle" }),
    ]);
    const busy = out.find((g) => g.key === "busy");
    const idle = out.find((g) => g.key === "idle");
    expect(busy?.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(idle?.sessions).toEqual([]); // idle Director renders as a free slot
  });

  it("does not duplicate a Director that already has sessions", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [director({ directorId: "d1" })]);
    expect(out.filter((g) => g.key === "d1")).toHaveLength(1);
    expect(out[0].sessions).toHaveLength(1);
  });

  it("skips a Director with no id - it is not an addressable slot", () => {
    const out = groupByDirector([], byId, [director({ directorId: "" })]);
    expect(out).toEqual([]);
  });

  it("folds in an OFFLINE Director and carries its state, so the panel can dim and date it", () => {
    const out = groupByDirector([], byId, [
      director({ directorId: "off", state: REACHABILITY_OFFLINE, lastSeenAgeSeconds: 400 }),
    ]);
    expect(out.map((g) => g.key)).toEqual(["off"]);
    expect(out[0].reachability?.state).toBe(REACHABILITY_OFFLINE);
    expect(out[0].reachability?.lastSeenAgeSeconds).toBe(400);
  });

  it("carries the state onto a group that already has sessions, without duplicating it", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [director({ directorId: "d1", state: REACHABILITY_OFFLINE })]);
    expect(out).toHaveLength(1);
    expect(out[0].sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(out[0].reachability?.state).toBe(REACHABILITY_OFFLINE);
  });

  it("groups sessions by Director when no idle list is given", () => {
    const sessions = [
      session({ sessionId: "s1", directorId: "a" }),
      session({ sessionId: "s2", directorId: "a" }),
      session({ sessionId: "s3", directorId: "b" }),
    ];
    const out = groupByDirector(sessions, byId);
    expect(out.map((g) => g.key)).toEqual(["a", "b"]);
    expect(out[0].sessions).toHaveLength(2);
  });

  it("labels a group with the Director's display name when the envelope reports one (devthrottle_internal#1176)", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [
      director({ directorId: "d1", displayName: "SOREN_NORTH_SLOT_2" }),
    ]);
    expect(out[0].label).toBe("SOREN_NORTH_SLOT_2");
  });
});

describe("directorLabelOf", () => {
  it("prefers the user-editable display name", () => {
    expect(directorLabelOf("abcd1234-guid", director({ displayName: "SOREN_NORTH_SLOT_2" }))).toBe(
      "SOREN_NORTH_SLOT_2",
    );
  });

  it("falls back to the historical short-id label when unnamed or when reachability is missing", () => {
    // An unnamed Director (or one behind an older Gateway that strips the field) must render exactly
    // as it always did - the display name is additive, never a regression.
    expect(directorLabelOf("32c4851e", director({ displayName: "" }))).toBe("Director 32c4851e");
    expect(directorLabelOf("32c4851e", undefined)).toBe("Director 32c4851e");
  });

  it("ignores a whitespace-only display name", () => {
    expect(directorLabelOf("32c4851e", director({ displayName: "   " }))).toBe("Director 32c4851e");
  });

  it("labels an empty id as unknown", () => {
    expect(directorLabelOf("", undefined)).toBe("Director (unknown)");
  });
});

describe("agentBadgeText", () => {
  it("shows the agent on the machine, repo, and list pivots", () => {
    for (const pivot of ["machine", "repo", "list"]) {
      expect(agentBadgeText(session(), pivot)).toBe("ClaudeCode");
    }
  });

  it("shows nothing on the agent pivot, where the lane header already states it", () => {
    expect(agentBadgeText(session(), "agent")).toBeNull();
  });

  it("shows a question mark rather than nothing when the agent is unknown", () => {
    expect(agentBadgeText(session({ agent: "" }), "machine")).toBe("?");
    expect(agentBadgeText(session({ agent: "   " }), "machine")).toBe("?");
    expect(agentBadgeText(session({ agent: undefined }), "machine")).toBe("?");
  });
});

// Issue devthrottle_internal#1340. The card renders the Gateway's model verdict and composes none of it;
// what these pin is WHEN a chip appears and WHICH lane a card lands in - never the wording, which is the
// Gateway's and is tested once, in C#, where it is decided.
describe("modelChip", () => {
  const reported = session({
    modelDisplay: { kind: "reported", text: "fable-5", modelId: "claude-fable-5", tooltip: "claude-fable-5" },
  });

  it("renders the Gateway's words verbatim on every pivot but its own", () => {
    for (const pivot of ["machine", "repo", "agent", "list"]) {
      expect(modelChip(reported, pivot)).toEqual({ text: "fable-5", title: "claude-fable-5", absent: false });
    }
  });

  it("shows nothing on the model pivot, where the lane header already states it", () => {
    expect(modelChip(reported, "model")).toBeNull();
  });

  it("marks either absence as absent, and keeps the two sets of words apart", () => {
    const notYet = session({
      modelDisplay: { kind: "notRecordedYet", text: "no model yet", modelId: null, tooltip: "not yet", isAbsent: true },
    });
    const never = session({
      modelDisplay: { kind: "notReported", text: "model not reported", modelId: null, tooltip: "never", isAbsent: true },
    });
    expect(modelChip(notYet, "machine")?.absent).toBe(true);
    expect(modelChip(never, "machine")?.absent).toBe(true);
    expect(modelChip(notYet, "machine")?.text).not.toBe(modelChip(never, "machine")?.text);
  });

  it("renders nothing when the Gateway stamped no verdict - a missing VERDICT is not a missing model", () => {
    expect(modelChip(session(), "machine")).toBeNull();
    expect(modelChip(session({ modelDisplay: null }), "machine")).toBeNull();
    expect(modelChip(session({ modelDisplay: { kind: "reported", text: "  " } }), "machine")).toBeNull();
  });
});

describe("modelKeyOf", () => {
  it("names a lane with the FULL recorded id, not the shortened badge text", () => {
    const s = session({ modelDisplay: { kind: "reported", text: "fable-5", modelId: "claude-fable-5" } });
    expect(modelKeyOf(s)).toEqual({ key: "claude-fable-5", title: "claude-fable-5" });
  });

  it("folds two spellings of one model into one lane", () => {
    const a = session({ modelDisplay: { kind: "reported", text: "opus-5", modelId: "claude-opus-5" } });
    const b = session({ modelDisplay: { kind: "reported", text: "opus-5", modelId: "Claude-Opus-5" } });
    expect(modelKeyOf(a).key).toBe(modelKeyOf(b).key);
  });

  it("gives the two absences SEPARATE lanes - pooling them would undo the distinction", () => {
    const notYet = session({ modelDisplay: { kind: "notRecordedYet", text: "no model yet", isAbsent: true } });
    const never = session({ modelDisplay: { kind: "notReported", text: "model not reported", isAbsent: true } });
    expect(modelKeyOf(notYet).key).not.toBe(modelKeyOf(never).key);
    expect(modelKeyOf(notYet).title).toBe("no model yet");
    expect(modelKeyOf(never).title).toBe("model not reported");
  });

  it("puts an unstamped session in its own lane - 'we were not told' is neither absence", () => {
    const key = modelKeyOf(session()).key;
    expect(key).not.toBe(modelKeyOf(session({ modelDisplay: { kind: "notReported", text: "x" } })).key);
  });

  it("keys an absence so it can never collide with a real model id", () => {
    // A recorded id is always trimmed and non-empty, so the reserved prefix is enough on its own; nothing
    // here is trying to influence lane ORDER, which is by session count and then title like every pivot.
    expect(modelKeyOf(session()).key.startsWith("absent:")).toBe(true);
    expect(modelKeyOf(session({ modelDisplay: { kind: "notReported", text: "x" } })).key.startsWith("absent:")).toBe(
      true,
    );
  });
});
