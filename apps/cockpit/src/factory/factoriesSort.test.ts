// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { FactoryListRow } from "@devthrottle/client-core/factory/factoriesScreenClient";
import {
  DEFAULT_FACTORY_SORT,
  FACTORY_SORT_STORAGE_KEY,
  clickHeading,
  directionLabel,
  headingArrow,
  loadFactorySort,
  saveFactorySort,
  sortFactoryRows,
  sortFooter,
} from "./factoriesSort";

// The Factories list's sort order (owner decision, 8 Oct 2026). What is held down: every key in both directions,
// the tie-breaks, the remembered choice, the first-run default of Name A to Z, and the heading click toggle.

function row(id: string, title: string, statusRank: number, waitingCount = 0): FactoryListRow {
  return {
    id,
    title,
    statusWord: ["FAILING", "NEEDS YOU", "PAUSED", "RUNNING"][statusRank],
    statusTone: "ok",
    statusRank,
    statusReason: "",
    statusLine: null,
    statusHref: null,
    waitingText: waitingCount === 0 ? "-" : `${waitingCount} questions`,
    waitingCount,
    waitingHref: null,
    href: `/factories/${id}`,
    talk: null,
    noCeoText: null,
  };
}

const ids = (rows: FactoryListRow[]) => rows.map((r) => r.id);

describe("sortFactoryRows", () => {
  const rows = [
    row("web", "Website Business", 0),
    row("cf", "clickFunnels", 3),
    row("bpm", "M-BPM Studio", 2, 1),
    row("warm", "WarmForward", 1, 2),
    row("ar", "Alpha Research", 3),
  ];

  it("Name: case-insensitive title A to Z, and Z to A reversed", () => {
    expect(ids(sortFactoryRows(rows, { key: "name", reversed: false }))).toEqual(["ar", "cf", "bpm", "warm", "web"]);
    expect(ids(sortFactoryRows(rows, { key: "name", reversed: true }))).toEqual(["web", "warm", "bpm", "cf", "ar"]);
  });

  it("Name: two rows with the same title fall back to the id", () => {
    const twins = [row("b", "Same", 3), row("a", "same", 3)];
    expect(ids(sortFactoryRows(twins, { key: "name", reversed: false }))).toEqual(["a", "b"]);
  });

  it("Status: worst first, ties by name; reversed is best first, ties still by name A to Z", () => {
    expect(ids(sortFactoryRows(rows, { key: "status", reversed: false }))).toEqual(["web", "warm", "bpm", "ar", "cf"]);
    expect(ids(sortFactoryRows(rows, { key: "status", reversed: true }))).toEqual(["ar", "cf", "bpm", "warm", "web"]);
  });

  it("Waiting on you: most first, ties by worse status then name; reversed is fewest first with the same tie-breaks", () => {
    expect(ids(sortFactoryRows(rows, { key: "waiting", reversed: false }))).toEqual(["warm", "bpm", "web", "ar", "cf"]);
    expect(ids(sortFactoryRows(rows, { key: "waiting", reversed: true }))).toEqual(["web", "ar", "cf", "bpm", "warm"]);
  });

  it("does not reorder the rows it was given", () => {
    const before = ids(rows);
    sortFactoryRows(rows, { key: "name", reversed: false });
    expect(ids(rows)).toEqual(before);
  });
});

describe("the words and the arrow", () => {
  it("names each direction", () => {
    expect(directionLabel({ key: "name", reversed: false })).toBe("A to Z");
    expect(directionLabel({ key: "name", reversed: true })).toBe("Z to A");
    expect(directionLabel({ key: "status", reversed: false })).toBe("Worst first");
    expect(directionLabel({ key: "status", reversed: true })).toBe("Best first");
    expect(directionLabel({ key: "waiting", reversed: false })).toBe("Most first");
    expect(directionLabel({ key: "waiting", reversed: true })).toBe("Fewest first");
  });

  it("points the ASCII arrow up for A to Z and down for worst first and most first", () => {
    expect(headingArrow({ key: "name", reversed: false })).toBe("^");
    expect(headingArrow({ key: "name", reversed: true })).toBe("v");
    expect(headingArrow({ key: "status", reversed: false })).toBe("v");
    expect(headingArrow({ key: "status", reversed: true })).toBe("^");
    expect(headingArrow({ key: "waiting", reversed: false })).toBe("v");
    expect(headingArrow({ key: "waiting", reversed: true })).toBe("^");
  });

  it("states the order under the table, in ASCII only", () => {
    expect(sortFooter({ key: "name", reversed: false })).toBe("Sorted by name, A to Z.");
    expect(sortFooter({ key: "name", reversed: true })).toBe("Sorted by name, Z to A.");
    for (const key of ["name", "status", "waiting"] as const)
      for (const reversed of [false, true]) expect(sortFooter({ key, reversed })).toMatch(/^[\x20-\x7e]+$/);
  });
});

describe("clickHeading", () => {
  it("reverses the active column and starts another column at its own first direction", () => {
    expect(clickHeading({ key: "name", reversed: false }, "name")).toEqual({ key: "name", reversed: true });
    expect(clickHeading({ key: "name", reversed: true }, "name")).toEqual({ key: "name", reversed: false });
    expect(clickHeading({ key: "name", reversed: true }, "status")).toEqual({ key: "status", reversed: false });
  });
});

describe("the remembered order", () => {
  beforeEach(() => window.localStorage.clear());

  it("is Name A to Z the first time ever", () => {
    expect(loadFactorySort()).toEqual({ key: "name", reversed: false });
    expect(DEFAULT_FACTORY_SORT).toEqual({ key: "name", reversed: false });
  });

  it("comes back as it was saved", () => {
    saveFactorySort({ key: "waiting", reversed: true });
    expect(window.localStorage.getItem(FACTORY_SORT_STORAGE_KEY)).toBe('{"key":"waiting","reversed":true}');
    expect(loadFactorySort()).toEqual({ key: "waiting", reversed: true });
  });

  it("falls back to Name A to Z when the stored value is junk or an unknown key", () => {
    window.localStorage.setItem(FACTORY_SORT_STORAGE_KEY, "not json");
    expect(loadFactorySort()).toEqual(DEFAULT_FACTORY_SORT);
    window.localStorage.setItem(FACTORY_SORT_STORAGE_KEY, '{"key":"colour","reversed":false}');
    expect(loadFactorySort()).toEqual(DEFAULT_FACTORY_SORT);
  });

  it("still sorts, without throwing, when the browser refuses to save", () => {
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage off");
    });
    try {
      expect(() => saveFactorySort({ key: "status", reversed: false })).not.toThrow();
    } finally {
      spy.mockRestore();
    }
  });

  it("still opens on Name A to Z when the browser refuses storage", () => {
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage off");
    });
    try {
      expect(loadFactorySort()).toEqual(DEFAULT_FACTORY_SORT);
    } finally {
      spy.mockRestore();
    }
  });
});
