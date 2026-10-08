import type { FactoryListRow } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { sortFactoryRows, type FactorySortOrder } from "./factoriesSort";

// The Factories list as cards (owner decision, 8 Oct 2026, mockup B "grouped by state"): the rows the Gateway
// stamped are laid out as cards under three headings - what needs a person, what is paused, what runs on its own -
// with a strip of counts above them. The Gateway still decides every status, word and tone (rule 7); this module
// only ARRANGES the rows it was given, by the statusRank the Gateway stamped on each, and counts them. The owner's
// Sort by choice (factoriesSort.ts) orders the cards inside each group. Cards or Table is this browser's choice,
// remembered next to the sort order; Cards the first time.

export type FactoriesViewMode = "cards" | "table";

export const FACTORY_VIEW_STORAGE_KEY = "cockpit.factoriesView";

export const DEFAULT_FACTORIES_VIEW: FactoriesViewMode = "cards";

export const FACTORY_VIEW_MODES: ReadonlyArray<{ key: FactoriesViewMode; label: string }> = [
  { key: "cards", label: "Cards" },
  { key: "table", label: "Table" },
];

export type FactoryGroupKey = "fixing" | "paused" | "running";

export interface FactoryGroup {
  key: FactoryGroupKey;
  /** "Needs fixing", "Paused", "Running on its own". */
  heading: string;
  rows: FactoryListRow[];
}

/** The groups in the order the screen shows them. */
const GROUPS: ReadonlyArray<{ key: FactoryGroupKey; heading: string }> = [
  { key: "fixing", heading: "Needs fixing" },
  { key: "paused", heading: "Paused" },
  { key: "running", heading: "Running on its own" },
];

/**
 * Which group a row belongs in, read off the Gateway's statusRank (0 FAILING, 1 NEEDS YOU, 2 PAUSED, 3 RUNNING).
 * FAILING and NEEDS YOU both need a person, so both are "Needs fixing".
 */
export function groupKeyOf(row: FactoryListRow): FactoryGroupKey {
  if (row.statusRank <= 1) return "fixing";
  if (row.statusRank === 2) return "paused";
  return "running";
}

/** The non-empty groups, in screen order, each with its rows in the owner's sort order. */
export function groupFactoryRows(rows: ReadonlyArray<FactoryListRow>, order: FactorySortOrder): FactoryGroup[] {
  const sorted = sortFactoryRows(rows, order);
  return GROUPS.map((g) => ({ key: g.key, heading: g.heading, rows: sorted.filter((r) => groupKeyOf(r) === g.key) })).filter(
    (g) => g.rows.length > 0,
  );
}

export interface FactoryCounts {
  total: number;
  running: number;
  /** FAILING plus NEEDS YOU: the rows under "Needs fixing". */
  needsFixing: number;
  paused: number;
  /** The open items waiting on the owner, summed over every factory. */
  waiting: number;
}

/** The count strip's numbers. */
export function countFactoryRows(rows: ReadonlyArray<FactoryListRow>): FactoryCounts {
  let running = 0;
  let needsFixing = 0;
  let paused = 0;
  let waiting = 0;
  for (const r of rows) {
    const g = groupKeyOf(r);
    if (g === "running") running += 1;
    else if (g === "fixing") needsFixing += 1;
    else paused += 1;
    waiting += r.waitingCount;
  }
  return { total: rows.length, running, needsFixing, paused, waiting };
}

/** "Nora Hale" -> "NH"; "Cher" -> "C". At most two letters. */
export function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter((p) => p.length > 0);
  if (parts.length === 0) return "";
  if (parts.length === 1) return parts[0].slice(0, 1).toUpperCase();
  return (parts[0].slice(0, 1) + parts[parts.length - 1].slice(0, 1)).toUpperCase();
}

/** The view this browser remembers, or Cards when it remembers none (or storage is off or holds junk). */
export function loadFactoriesView(): FactoriesViewMode {
  let raw: string | null;
  try {
    raw = window.localStorage.getItem(FACTORY_VIEW_STORAGE_KEY);
  } catch {
    return DEFAULT_FACTORIES_VIEW;
  }
  if (raw === null) return DEFAULT_FACTORIES_VIEW;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (FACTORY_VIEW_MODES.some((m) => m.key === parsed)) return parsed as FactoriesViewMode;
  } catch {
    // An unreadable value is treated as nothing remembered; the next pick overwrites it.
  }
  return DEFAULT_FACTORIES_VIEW;
}

export function saveFactoriesView(mode: FactoriesViewMode): void {
  try {
    window.localStorage.setItem(FACTORY_VIEW_STORAGE_KEY, JSON.stringify(mode));
  } catch {
    // A browser with storage turned off still switches; it just forgets on the next load.
  }
}
