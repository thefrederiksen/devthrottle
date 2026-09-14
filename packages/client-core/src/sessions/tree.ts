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
import { inBucket, inDesktopOrder, inWaitingOrder } from "./ordering";
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
 * back); children are in desktop order under their parent.
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
    // vanishing into a cycle nobody can expand.
    if (sup.length > 0 && ids.has(sup) && sup !== String(s.sessionId ?? "").trim()) {
      const list = childrenOf.get(sup);
      if (list) list.push(s);
      else childrenOf.set(sup, [s]);
    } else {
      roots.push(s);
    }
  }
  for (const [id, kids] of childrenOf) childrenOf.set(id, inDesktopOrder(kids));
  return { roots, childrenOf };
}

/** The sessions under a root, or an empty list when it has none. */
export function childrenOf(tree: SessionTree, root: SessionDto): SessionDto[] {
  return tree.childrenOf.get(String(root.sessionId ?? "").trim()) ?? [];
}

/**
 * What a collapsed crew row must carry so that collapsing hides nothing that matters: how many
 * sessions are under it and what each is doing, by the Gateway's own triage bucket.
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

/** One attention section: its heading, and the roots in it. */
export interface AttentionSection {
  key: "needsYou" | "active" | "onHold";
  title: string;
  roots: SessionDto[];
}

/**
 * The attention order, as sections: "Needs you" as a waiting line (longest wait on top), then
 * "Working" in desktop order, then "Snoozed" in desktop order. Empty sections are omitted. Applied
 * to top-level rows only - pass the tree's roots, never the whole roster.
 */
export function attentionSections(roots: SessionDto[]): AttentionSection[] {
  const sections: AttentionSection[] = [
    { key: "needsYou", title: "Needs you", roots: inWaitingOrder(roots) },
    { key: "active", title: "Working", roots: inBucket(roots, "active") },
    { key: "onHold", title: "Snoozed", roots: inBucket(roots, "onHold") },
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
