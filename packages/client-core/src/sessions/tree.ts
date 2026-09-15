// The session list is ALWAYS the ownership tree (owner ruling, 2026-09-14 - the Session List Views
// design in devthrottle_internal/docs/design/session-list-views/). Any session that started another
// session is a parent; the sessions it started sit UNDER it, collapsed by default. A session with
// no children is an ordinary top-level row, so for most sessions nothing changes.
//
// The relationship is the Gateway's own: SessionDto.controllerSessionId, the supervisor a session
// answers to (Session.ControllerSessionId on the Director). It is NOT Mission - the owner was
// explicit: "anything that has a parent and a child". A child nests only while its supervisor is
// in the same list. When the supervisor is gone (killed, or on a machine the roster no longer
// carries) the child becomes a top-level row again, which is exactly when it goes red on its own
// (#2826) and must be seen.
//
// Two orders, applied to the TOP LEVEL only. "My order" is the owning Director's drag order
// (inDesktopOrder). "Attention" is the phone's waiting line: the session that has needed you the
// longest on top, then the rest that need you, then the working ones, then the snoozed ones at the
// bottom. Children never re-sort: a child with a live supervisor never goes red, so there is
// nothing for attention to reorder under a parent.
//
// Expanded or collapsed is remembered per crew in this browser (localStorage). The Gateway-owned
// preference that makes the three surfaces agree is a later step, taken before the Director joins.
import type { SessionDto } from "../api/client";
import { classify, inBucket, inDesktopOrder, inWaitingOrder, isInCalmBand } from "./ordering";
import { durationFromMs } from "./waiting";

export interface SessionTree {
  /** The top-level rows: every session whose supervisor is absent from the list, in the caller's order. */
  roots: SessionDto[];
  /** sessionId -> the sessions it supervises, in desktop order. Absent for a session with none. */
  childrenOf: Map<string, SessionDto[]>;
}

function supervisorOf(s: SessionDto): string {
  return String(s.controllerSessionId ?? "").trim();
}

/**
 * Nest every session under its supervisor when that supervisor is in the same list. Roots keep the
 * order of `sessions` (so the caller decides my-order versus attention by ordering the roots it gets
 * back); children are in desktop order under their parent. Ownership can go more than one level
 * deep (an Architect's Manager's Workers), and every level is kept.
 *
 * THE CROSS-DIRECTOR RULE, stated once for both shells and both orders: the tree is built over the
 * WHOLE roster, never per machine. A session may supervise a session on another machine (the
 * command line's --controlled-by takes any session id, and the Gateway resolves liveness fleet-wide),
 * so a child nests under its parent wherever the parent lives, and a child on another machine says
 * so on its own row (see isOnAnotherMachine). A view that groups by machine groups the ROOTS.
 *
 * EVERY SESSION RENDERS EXACTLY ONCE. A malformed ownership loop (a supervises b supervises a) puts
 * neither in roots and would hide both; every session not reachable from a root is promoted to a
 * root instead, the same invariant the Fleet Map keeps.
 */
export function buildSessionTree(sessions: SessionDto[]): SessionTree {
  const ids = new Set<string>();
  for (const s of sessions) {
    const id = String(s.sessionId ?? "").trim();
    if (id.length > 0) ids.add(id);
  }
  const childrenOf = new Map<string, SessionDto[]>();
  const roots: SessionDto[] = [];
  for (const s of sessions) {
    const sup = supervisorOf(s);
    // A session that names itself as its own supervisor is malformed; it stays a root rather than
    // vanishing into a loop nobody can expand.
    if (sup.length > 0 && ids.has(sup) && sup !== String(s.sessionId ?? "").trim()) {
      const list = childrenOf.get(sup);
      if (list) list.push(s);
      else childrenOf.set(sup, [s]);
    } else {
      roots.push(s);
    }
  }
  // Promote every member of an ownership loop (a supervises b supervises a) to a root, so each
  // renders exactly once. A session that merely hangs off a loop stays under its parent.
  const byId = new Map<string, SessionDto>();
  for (const s of sessions) byId.set(String(s.sessionId ?? "").trim(), s);
  const reached = new Set<string>();
  const walk = (s: SessionDto) => {
    const id = String(s.sessionId ?? "").trim();
    if (reached.has(id)) return;
    reached.add(id);
    for (const k of childrenOf.get(id) ?? []) walk(k);
  };
  for (const r of roots) walk(r);
  const detachFromParent = (s: SessionDto) => {
    const siblings = childrenOf.get(supervisorOf(s));
    if (siblings) childrenOf.set(supervisorOf(s), siblings.filter((x) => x !== s));
  };
  for (const s of sessions) {
    if (reached.has(String(s.sessionId ?? "").trim())) continue;
    // Follow the supervisor chain until it revisits itself: that is the loop.
    const path: SessionDto[] = [];
    const at = new Map<string, number>();
    let cur: SessionDto | undefined = s;
    while (cur && !reached.has(String(cur.sessionId ?? "").trim()) && !at.has(String(cur.sessionId ?? "").trim())) {
      at.set(String(cur.sessionId ?? "").trim(), path.length);
      path.push(cur);
      cur = byId.get(supervisorOf(cur));
    }
    const loopStart = cur ? at.get(String(cur.sessionId ?? "").trim()) : undefined;
    const promoted = loopStart === undefined ? [] : path.slice(loopStart);
    for (const m of promoted) detachFromParent(m);
    for (const m of promoted) {
      roots.push(m);
      walk(m);
    }
    // Whatever led into the loop (or into an already-reached session) is under it and now reached.
    for (const m of path) walk(m);
  }
  for (const [id, kids] of childrenOf) {
    if (kids.length === 0) childrenOf.delete(id);
    else childrenOf.set(id, inDesktopOrder(kids));
  }
  return { roots, childrenOf };
}

/** The sessions directly under a session, or an empty list when it has none. */
export function childrenOf(tree: SessionTree, root: SessionDto): SessionDto[] {
  return tree.childrenOf.get(String(root.sessionId ?? "").trim()) ?? [];
}

/** One session under a root at some depth (1 = a direct child), for a shell that flattens a crew.
 *  `parent` is the session it is DIRECTLY under - not the root - so a shell that flattens the crew
 *  can still answer per-edge questions (is this one on another machine than the session above it?). */
export interface Descendant {
  session: SessionDto;
  parent: SessionDto;
  depth: number;
}

/**
 * Everything under a session, depth-first in desktop order at each level, with its depth. This is
 * what a crew IS - not just the direct children - so the crew summary and a flattened crew list both
 * read it and cannot disagree about who is under whom.
 */
export function descendantsOf(tree: SessionTree, root: SessionDto): Descendant[] {
  const out: Descendant[] = [];
  const seen = new Set<string>();
  const walk = (s: SessionDto, depth: number) => {
    for (const k of childrenOf(tree, s)) {
      const id = String(k.sessionId ?? "").trim();
      if (seen.has(id)) continue;
      seen.add(id);
      out.push({ session: k, parent: s, depth });
      walk(k, depth + 1);
    }
  };
  walk(root, 1);
  return out;
}

/** True when a child lives on a different Director from its parent - its row must then say where it is. */
export function isOnAnotherMachine(parent: SessionDto, child: SessionDto): boolean {
  const a = String(parent.directorId ?? "").trim();
  const b = String(child.directorId ?? "").trim();
  if (a.length > 0 && b.length > 0) return a !== b;
  return String(parent.machineName ?? "").trim() !== String(child.machineName ?? "").trim();
}

/**
 * What a collapsed crew row must carry so that collapsing hides nothing that matters: how many
 * sessions are under it - at EVERY level, not just the direct children - and what each is doing,
 * by the Gateway's own triage bucket. Pass descendantsOf(tree, root).map((d) => d.session).
 *
 * needsYou is zero for every live crew - a child with a live supervisor never goes red (#2826) -
 * and turns non-zero only when the supervisor has died and its sessions have surfaced. That is
 * exactly when it must be seen, so the count is always carried, never omitted when zero.
 */
export interface CrewSummary {
  count: number;
  needsYou: number;
  working: number;
  stopped: number;
  /** The earliest createdAt across the root and its children, epoch ms; null when none parse. */
  sinceMs: number | null;
}

export function crewSummary(root: SessionDto, kids: SessionDto[]): CrewSummary {
  const needsYou = inBucket(kids, "needsYou").length;
  const working = inBucket(kids, "active").length;
  const stopped = inBucket(kids, "onHold").length;
  let sinceMs: number | null = null;
  for (const s of [root, ...kids]) {
    const ms = Date.parse(String(s.createdAt ?? ""));
    if (Number.isNaN(ms) || ms < Date.parse("2000-01-01T00:00:00Z")) continue;
    sinceMs = sinceMs === null ? ms : Math.min(sinceMs, ms);
  }
  return { count: kids.length, needsYou, working, stopped, sinceMs };
}

/** The crew line's words: "9 under it: 4 working, 5 stopped, 0 need you". One formatter, so the
 *  Cockpit and the phone cannot word the same crew two different ways. */
export function crewSummaryLine(sum: CrewSummary): string {
  return `${sum.count} under it: ${sum.working} working, ${sum.stopped} stopped, ${sum.needsYou} need you`;
}

/** How long the crew has been going, from its oldest session: "5h 29m". Empty when unknown. */
export function crewAge(sum: CrewSummary, now: number): string {
  if (sum.sinceMs === null) return "";
  return durationFromMs(Math.max(0, now - sum.sinceMs));
}

/** One attention section: its heading, the roots in it, and where its calm band starts. */
export interface AttentionSection {
  key: "needsYou" | "active" | "onHold";
  title: string;
  roots: SessionDto[];
  /**
   * The index in roots of the first calm-band row (the rows the Wingman judged a report), listed after every
   * red row of the needs-you section and not counted in it. Equal to roots.length when there is no band,
   * which is always so for the other two sections. The rows that need you are roots.slice(0, bandStart), and
   * a heading counts those.
   */
  bandStart: number;
}

/** The heading over the calm band, one set of words for every surface. */
export const CALM_BAND_TITLE = "Done or carrying on";

/**
 * The attention order, as sections: "Needs you" as a waiting line (longest wait on top) followed by its
 * calm band, then "Working" in desktop order, then "Snoozed" in desktop order. Empty sections are omitted.
 * Applied to top-level rows only - pass the tree's roots, never the whole roster.
 */
export function attentionSections(roots: SessionDto[]): AttentionSection[] {
  // The waiting line carries the calm band after its reds, so the reds are exactly its leading rows.
  const waiting = inWaitingOrder(roots);
  const needsYouCount = waiting.filter((s) => classify(s) === "needsYou").length;
  // A calm row's bucket is "active", but it is listed in the band and is not working, so it is not listed
  // under "Working" as well.
  const active = inBucket(roots.filter((s) => !isInCalmBand(s)), "active");
  const onHold = inBucket(roots, "onHold");
  const sections: AttentionSection[] = [
    { key: "needsYou", title: "Needs you", roots: waiting, bandStart: needsYouCount },
    { key: "active", title: "Working", roots: active, bandStart: active.length },
    { key: "onHold", title: "Snoozed", roots: onHold, bandStart: onHold.length },
  ];
  return sections.filter((s) => s.roots.length > 0);
}

// ---- expanded / collapsed, remembered per crew in this browser ----

const EXPANDED_KEY = "dt.sessions.crewExpanded";
let _mem: Set<string> | null = null;

function loadExpanded(): Set<string> {
  if (_mem) return _mem;
  _mem = new Set<string>();
  try {
    if (typeof localStorage !== "undefined") {
      const raw = localStorage.getItem(EXPANDED_KEY);
      if (raw) {
        const parsed: unknown = JSON.parse(raw);
        if (Array.isArray(parsed)) for (const id of parsed) if (typeof id === "string") _mem.add(id);
      }
    }
  } catch {
    // Storage unavailable or holding something unparseable: every crew starts collapsed, which is
    // the default anyway, and the in-memory set still works for the life of the page.
  }
  return _mem;
}

/** Whether this crew is expanded on this device. Collapsed is the default. */
export function isCrewExpanded(sessionId: string): boolean {
  return loadExpanded().has(sessionId);
}

/** Remember that this crew is expanded (or collapsed) on this device. */
export function setCrewExpanded(sessionId: string, expanded: boolean): void {
  const set = loadExpanded();
  if (expanded) set.add(sessionId);
  else set.delete(sessionId);
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(EXPANDED_KEY, JSON.stringify([...set]));
  } catch {
    // The in-memory set still holds it for the life of the page.
  }
}

/** Test hook: forget every remembered expansion (in memory and on this device). */
export function resetCrewExpandedForTests(): void {
  _mem = null;
  try {
    if (typeof localStorage !== "undefined") localStorage.removeItem(EXPANDED_KEY);
  } catch {
    // nothing to forget
  }
}
