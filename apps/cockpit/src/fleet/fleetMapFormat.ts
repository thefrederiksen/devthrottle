import type { SessionDto } from "@devthrottle/client-core/api/client";
import { type DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";
import { modelChipOf, type ModelChip } from "@devthrottle/client-core/sessions/model";
import { buildSessionTree, descendantsOf, isOnAnotherMachine, type SessionTree } from "@devthrottle/client-core/sessions/tree";
import { repoBasename, repoIdentity } from "./format";

/**
 * Pure helpers for the Fleet Map's card rendering, kept out of FleetMapView.tsx so they can be unit
 * tested. The Cockpit's vitest run has no DOM environment, so a helper that lives inside the component
 * file is a helper that cannot be tested; anything with a rule worth stating belongs here.
 */

/**
 * The pivots whose columns hold TOP-LEVEL sessions only, each with its whole crew under it, wherever in
 * the fleet the crew runs. This is the Sessions list's rule (client-core/sessions/tree: "a view that
 * groups by machine groups the ROOTS"), so the Fleet Map and the Sessions list cannot disagree about who
 * is under whom: a Worker on SOREN_NORTH started by an Architect on the Mac Mini sits under that
 * Architect, in the Mac Mini's column, dotted and saying where it runs - and solid in SOREN_NORTH's own
 * column, under a tag naming the Architect (see laneTree).
 *
 * "By agent" and "By model" are NOT in the set, on purpose. Those pivots exist to show what runs a given
 * agent or model; hiding a Codex Worker inside its Claude Architect's column would defeat the pivot. There
 * a session nests only under a parent in the same column.
 */
export const FLEET_TREE_PIVOTS: ReadonlySet<string> = new Set(["machine", "director", "repo", "worktree"]);

/** The tree one column draws, plus what it needs to tell a session that runs here from one that does not. */
export interface LaneTree extends SessionTree {
  /** The ids of the sessions the column holds - the ones that run here. Any other card it draws is dotted. */
  here: ReadonlySet<string>;
  /** For a top-level card whose parent runs in another column: that parent, keyed by the card's id. */
  parentElsewhere: ReadonlyMap<string, SessionDto>;
}

const idOf = (s: SessionDto): string => String(s.sessionId ?? "").trim();

/**
 * The tree one column (or one Director group inside a column) draws. `laneSessions` are the sessions
 * that belong to the column, in the column's order; the roots come back in that same order.
 *
 * With `fleetTree` (built over the WHOLE roster by buildSessionTree), a crew stays one tree wherever its
 * members run, and the rule is SOLID RUNS HERE, DOTTED RUNS ELSEWHERE (owner ruling, 21 September 2026):
 *
 * - A parent carries its whole crew from the fleet tree. A crew member that runs in another column is
 *   still drawn under it, dotted, saying where it runs - and so is everything under that member.
 * - A session whose parent runs in another column is ALSO drawn in its own column, solid, under a small
 *   tag naming that parent (parentElsewhere). Its column therefore accounts for every session it counts.
 *
 * Every session is drawn solid exactly once on the map; a dotted card is only ever a pointer to it.
 *
 * With `fleetTree` null (the agent and model pivots, and any search), the tree is built over the column
 * alone, so a session nests only under a parent the column also holds, and a search match is never
 * hidden inside a collapsed crew.
 */
export function laneTree(laneSessions: SessionDto[], fleetTree: SessionTree | null): LaneTree {
  const here = new Set(laneSessions.map(idOf));
  if (fleetTree === null) return { ...buildSessionTree(laneSessions), here, parentElsewhere: new Map() };
  // The parent of every session, as the fleet tree placed it (an ownership loop's members are already
  // promoted to roots there, so this cannot loop).
  const byId = new Map<string, SessionDto>();
  for (const r of fleetTree.roots) byId.set(idOf(r), r);
  for (const kids of fleetTree.childrenOf.values()) for (const k of kids) byId.set(idOf(k), k);
  const parentOf = new Map<string, SessionDto>();
  for (const [pid, kids] of fleetTree.childrenOf) {
    const p = byId.get(pid);
    if (p !== undefined) for (const k of kids) parentOf.set(idOf(k), p);
  }
  const parentElsewhere = new Map<string, SessionDto>();
  const roots = laneSessions.filter((s) => {
    const p = parentOf.get(idOf(s));
    if (p === undefined) return true;
    if (here.has(idOf(p))) return false;
    parentElsewhere.set(idOf(s), p);
    return true;
  });
  return { roots, childrenOf: fleetTree.childrenOf, here, parentElsewhere };
}

/**
 * Where a session is drawn solid, in the words of the pivot's column: "SOREN_NORTH / DevThrottle_1" on
 * the machine and Director pivots (the Director too, because one machine can run several), the
 * repository or the working tree on theirs. A dotted card and a parent tag both say this.
 */
export function homeLabelOf(s: SessionDto, pivot: string, reach: DirectorReachability | undefined): string {
  if (pivot === "repo") return repoIdentity(s.repoName, s.repoPath);
  if (pivot === "worktree") return repoBasename(s.repoPath);
  const machine = (s.machineName ?? "").trim();
  const director = directorLabelOf((s.directorId ?? "").trim(), reach);
  return machine.length === 0 ? director : `${machine} / ${director}`;
}

/** The line on a dotted card: where the session really is. */
export function awayText(pivot: string, home: string): string {
  return pivot === "repo" || pivot === "worktree" ? `drawn in full in ${home}` : `runs on ${home}`;
}

/**
 * The part of a collapsed crew that runs in another column, counted by where it runs, so a collapsed
 * parent still says that some of its crew is elsewhere: "1 runs on devthrottle-mac-mini / DevThrottle_1".
 * Empty when the whole crew runs here.
 */
export function crewElsewhereText(tree: LaneTree, root: SessionDto, homeOf: (s: SessionDto) => string): string {
  const counts = new Map<string, number>();
  for (const d of descendantsOf(tree, root)) {
    if (tree.here.has(idOf(d.session))) continue;
    const home = homeOf(d.session);
    counts.set(home, (counts.get(home) ?? 0) + 1);
  }
  return [...counts.entries()].map(([home, n]) => `${n} ${n === 1 ? "runs" : "run"} on ${home}`).join(", ");
}

/**
 * The "on ..." tag a child card carries when it runs somewhere other than the session it is drawn under
 * (client-core isOnAnotherMachine). The machine name when the machines differ; the Director's label when
 * it is another Director on the same machine. Null when the child runs where its parent does.
 */
export function elsewhereTag(
  parent: SessionDto,
  child: SessionDto,
  childReach: DirectorReachability | undefined,
): { k: string; v: string } | null {
  if (!isOnAnotherMachine(parent, child)) return null;
  const machine = (child.machineName ?? "").trim();
  const sameMachine = machine.length > 0 && machine.toLowerCase() === (parent.machineName ?? "").trim().toLowerCase();
  if (machine.length > 0 && !sameMachine) return { k: "on", v: machine };
  return { k: "on", v: directorLabelOf((child.directorId ?? "").trim(), childReach) };
}

/**
 * The machine identity used by the "By machine" pivot: the trimmed machine name, or "(unknown machine)"
 * when a session/Director carries no name. Both sides of the machine join - the lanes built from sessions
 * AND the reachable-Director list folded in on top of them - MUST key through this one function, or an
 * idle Director could land in a machine lane that its sessions never key to (a session on "SOREN" and a
 * Director advertising "soren " would split into two lanes). One rule, one key.
 */
export function machineKeyOf(machineName: string | null | undefined): { key: string; title: string } {
  const name = (machineName ?? "").trim();
  const title = name.length === 0 ? "(unknown machine)" : name;
  return { key: title.toLowerCase(), title };
}

/** A Director id shortened to its last segment (Directors are "<machine>-<n>" or a guid); keeps a
 * sub-header compact without losing which Director it is. */
export function shortDir(directorId: string): string {
  const dash = directorId.lastIndexOf("-");
  const tail = dash >= 0 ? directorId.slice(dash + 1) : directorId;
  return tail.length > 8 ? tail.slice(0, 8) : tail;
}

/**
 * The human label of a Director group or lane (devthrottle_internal#1176): the user-editable display
 * name when the envelope reports one, else the historical "Director <short-id>". The short id stays the
 * fallback - never the primary - so a renamed Director finally reads as its name and an unnamed or
 * older-Gateway Director renders exactly as before.
 */
export function directorLabelOf(
  directorId: string,
  reachability: DirectorReachability | undefined,
): string {
  const name = (reachability?.displayName ?? "").trim();
  if (name.length > 0) return name;
  return `Director ${directorId.length === 0 ? "(unknown)" : shortDir(directorId)}`;
}

/** One machine's reachable Directors, keyed exactly as the machine pivot's lanes are (machineKeyOf). */
export interface MachineDirectors {
  key: string;
  title: string;
  directors: DirectorReachability[];
}

/**
 * EVERY Director the Gateway reports, grouped by machine, keyed EXACTLY as the machine pivot's lanes are
 * (machineKeyOf), so a machine that has a Director but no sessions still resolves to a lane.
 *
 * An offline Director used to be dropped here, on the argument that it cannot host a session so it is not
 * a free slot. That argument deleted the machine from the map entirely - lane, sessions and all - which is
 * the same delete-instead-of-dim defect the Gateway has now stopped committing: it serves an unreachable
 * machine's sessions, dimmed and dated, and a map that throws them away puts the hole straight back. A
 * machine you cannot reach is a fact the owner needs on the map, not an absence.
 *
 * The "free slot" claim is what actually had to go, and it went where it belongs - at the point of
 * rendering. Each group carries its Director's state so the panel can dim it, date it, and offer no
 * "start a session here" action it could not honour.
 */
export function directorsByMachine(directors: DirectorReachability[]): MachineDirectors[] {
  const byKey = new Map<string, MachineDirectors>();
  for (const d of directors) {
    const { key, title } = machineKeyOf(d.machineName);
    let g = byKey.get(key);
    if (g === undefined) {
      g = { key, title, directors: [] };
      byKey.set(key, g);
    }
    g.directors.push(d);
  }
  return [...byKey.values()];
}

/** One Director sub-group inside a machine lane; `sessions` is empty for an idle Director (a free slot). */
export interface DirectorGroup {
  key: string;
  label: string;
  sessions: SessionDto[];
  /**
   * This Director's state, when the envelope reported one - so the panel can dim an unreachable slot,
   * date it, and withhold an action it could not honour. Undefined when the envelope carries no entry
   * for this Director (an older Gateway, or a session whose owner is not in the list); the panel then
   * renders the group exactly as it always did.
   */
  reachability?: DirectorReachability;
}

/**
 * Group a machine lane's sessions by their owning Director, then FOLD IN every Director on the machine
 * that currently has no sessions, so an idle Director renders as an empty group - a free slot - and an
 * unreachable one still renders as a machine that exists. This is what makes "By machine" show capacity
 * (machine -> Director -> session) rather than only the Directors that happen to be busy. `directors` is
 * the machine's Director list; a Director already present via a session is never duplicated (it does pick
 * up that Director's state), and an unidentified Director (no id) is skipped because it is not an
 * addressable slot. Groups are ordered by Director id so a lane never reflows.
 */
export function groupByDirector(
  sessions: SessionDto[],
  sort: (a: SessionDto, b: SessionDto) => number,
  directors: DirectorReachability[] = [],
): DirectorGroup[] {
  const byDir = new Map<string, SessionDto[]>();
  for (const s of sessions) {
    const key = (s.directorId ?? "").trim();
    const arr = byDir.get(key);
    if (arr === undefined) byDir.set(key, [s]);
    else arr.push(s);
  }
  const reachByDir = new Map<string, DirectorReachability>();
  for (const d of directors) {
    const key = (d.directorId ?? "").trim();
    if (key.length === 0) continue; // an unidentified Director is not an addressable slot
    reachByDir.set(key, d);
    if (!byDir.has(key)) byDir.set(key, []); // Director with no sessions -> empty group
  }
  return [...byDir.entries()]
    .map(([key, arr]) => ({
      key: key.length === 0 ? "(unknown)" : key,
      label: directorLabelOf(key, reachByDir.get(key)),
      sessions: [...arr].sort(sort),
      reachability: reachByDir.get(key),
    }))
    .sort((a, b) => a.key.localeCompare(b.key));
}

/** The agent label to show on a card's meta row, or null when the card must not show one. */
export function agentBadgeText(s: SessionDto, pivot: string): string | null {
  // The agent pivot's lane header already states the agent for every card in the lane; repeating it per
  // card is noise, and it was the reason the badge existed on the title row at all.
  if (pivot === "agent") return null;
  const agent = (s.agent ?? "").trim();
  // An unknown agent still renders "?" rather than vanishing: a card with no agent is a fact worth
  // seeing, and a silently absent badge reads as "this card is fine" (issue #1625).
  return agent.length === 0 ? "?" : agent;
}

/**
 * The model chip for a card's meta row (issue devthrottle_internal#1340), or null when the card must not
 * show one.
 *
 * The chip's WORDS come from the shared reader (client-core `modelChipOf`), which reads the Gateway's fold
 * and composes nothing - so this Fleet Map card, the session roster and the desktop rail cannot word one
 * session three ways. All this function adds is the map's own suppression, the same one the agent badge
 * has: on the "By model" pivot the lane header already states the model for every card in it, and a
 * per-card chip would only repeat the lane.
 */
export function modelChip(s: SessionDto, pivot: string): ModelChip | null {
  if (pivot === "model") return null;
  return modelChipOf(s);
}

/**
 * The model identity used by the "By model" pivot: the FULL recorded id, so a lane is named the way the
 * agent's own records name the model, never the shortened badge form.
 *
 * Both absences get their own lane rather than being pooled into one "(no model)" bucket - pooling them
 * would undo, in the layout, exactly the distinction the fold exists to make. A card whose Gateway stamped
 * no verdict at all lands in its own third lane, because "we were not told" is not the same as either.
 */
export function modelKeyOf(s: SessionDto): { key: string; title: string } {
  const d = s.modelDisplay;
  // The two absences and the unstamped case key through a reserved "absent:" prefix so they can never
  // collide with a real model id. It buys nothing in lane ORDER - lanes sort by session count and then by
  // title, exactly like every other pivot - and it is not trying to.
  if (d === null || d === undefined) return { key: "absent:unstamped", title: "Gateway sent no model verdict" };
  const id = (d.modelId ?? "").trim();
  if (id.length > 0) return { key: id.toLowerCase(), title: id };
  // An absent model keeps the fold's own words as the lane title, so the lane states which absence it is.
  const text = (d.text ?? "").trim();
  return { key: "absent:" + (d.kind ?? "unknown"), title: text.length === 0 ? "No model" : text };
}
