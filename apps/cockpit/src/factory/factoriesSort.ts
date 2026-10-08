import type { FactoryListRow } from "@devthrottle/client-core/factory/factoriesScreenClient";

// The Factories list's sort order (owner decision, 8 Oct 2026, mockups A and B): the owner picks the order with the
// "Sort by" control or by clicking a column heading, and this browser remembers it. First time ever: Name, A to Z.
// What each status MEANS stays the Gateway's - it stamps statusRank and waitingCount on every row - so the Cockpit only
// arranges rows it was given; it never decides which status is worse.

export type FactorySortKey = "name" | "status" | "waiting";

/** The order the list is in. reversed=false is each key's own first direction: A to Z, worst first, most first. */
export interface FactorySortOrder {
  key: FactorySortKey;
  reversed: boolean;
}

export const FACTORY_SORT_STORAGE_KEY = "cockpit.factoriesSort";

export const DEFAULT_FACTORY_SORT: FactorySortOrder = { key: "name", reversed: false };

/** The "Sort by" choices, in the order the control shows them. */
export const FACTORY_SORT_KEYS: ReadonlyArray<{ key: FactorySortKey; label: string }> = [
  { key: "name", label: "Name" },
  { key: "status", label: "Status (worst first)" },
  { key: "waiting", label: "Waiting on you" },
];

/**
 * Which key each column heading sorts by, matched on the Gateway's heading words, never on position: a column the
 * Gateway adds or moves can never sort by the wrong key. A heading not listed here is plain text, not a button.
 */
export const FACTORY_COLUMN_KEYS: Readonly<Record<string, FactorySortKey>> = {
  Factory: "name",
  "Waiting on you": "waiting",
  Status: "status",
};

const DIRECTION_LABELS: Record<FactorySortKey, [string, string]> = {
  name: ["A to Z", "Z to A"],
  status: ["Worst first", "Best first"],
  waiting: ["Most first", "Fewest first"],
};

/** What the direction toggle says for the current order. */
export function directionLabel(order: FactorySortOrder): string {
  return DIRECTION_LABELS[order.key][order.reversed ? 1 : 0];
}

/** The ASCII arrow beside the active column heading: "^" for A to Z, "v" for worst first and most first. */
export function headingArrow(order: FactorySortOrder): string {
  const up = order.key === "name" ? !order.reversed : order.reversed;
  return up ? "^" : "v";
}

/** The line under the table, stating the order the rows are in. */
export function sortFooter(order: FactorySortOrder): string {
  switch (order.key) {
    case "name":
      return order.reversed ? "Sorted by name, Z to A." : "Sorted by name, A to Z.";
    case "status":
      return order.reversed
        ? "Sorted by status, best first: running, then paused, then needs you, then failing. Same status: by name."
        : "Sorted by status, worst first: failing, then needs you, then paused, then running. Same status: by name.";
    case "waiting":
      return order.reversed
        ? "Sorted by what is waiting on you, fewest first. Same count: worse status first, then by name."
        : "Sorted by what is waiting on you, most first. Same count: worse status first, then by name.";
  }
}

function byName(a: FactoryListRow, b: FactoryListRow): number {
  const t = a.title.localeCompare(b.title, undefined, { sensitivity: "base" });
  if (t !== 0) return t;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/**
 * The rows in the given order. Only the chosen key is reversed; the tie-breaks always read the same way (worse status
 * first, then name A to Z), so equal rows never jump around when the direction flips.
 */
export function sortFactoryRows(rows: ReadonlyArray<FactoryListRow>, order: FactorySortOrder): FactoryListRow[] {
  const sign = order.reversed ? -1 : 1;
  const compare = (a: FactoryListRow, b: FactoryListRow): number => {
    switch (order.key) {
      case "name":
        return sign * byName(a, b);
      case "status":
        return sign * (a.statusRank - b.statusRank) || byName(a, b);
      case "waiting":
        return sign * (b.waitingCount - a.waitingCount) || a.statusRank - b.statusRank || byName(a, b);
    }
  };
  return [...rows].sort(compare);
}

/** The order after a heading is clicked: the active heading flips its direction, another starts at its own first. */
export function clickHeading(order: FactorySortOrder, key: FactorySortKey): FactorySortOrder {
  return order.key === key ? { key, reversed: !order.reversed } : { key, reversed: false };
}

/** The order this browser remembers, or Name A to Z when it remembers none (or storage is off or holds junk). */
export function loadFactorySort(): FactorySortOrder {
  let raw: string | null;
  try {
    raw = window.localStorage.getItem(FACTORY_SORT_STORAGE_KEY);
  } catch {
    return DEFAULT_FACTORY_SORT;
  }
  if (raw === null) return DEFAULT_FACTORY_SORT;
  try {
    const parsed = JSON.parse(raw) as Partial<FactorySortOrder>;
    if (FACTORY_SORT_KEYS.some((k) => k.key === parsed.key) && typeof parsed.reversed === "boolean")
      return { key: parsed.key as FactorySortKey, reversed: parsed.reversed };
  } catch {
    // An unreadable value is treated as nothing remembered; the next pick overwrites it.
  }
  return DEFAULT_FACTORY_SORT;
}

export function saveFactorySort(order: FactorySortOrder): void {
  try {
    window.localStorage.setItem(FACTORY_SORT_STORAGE_KEY, JSON.stringify(order));
  } catch {
    // A browser with storage turned off still sorts; it just forgets on the next load.
  }
}
