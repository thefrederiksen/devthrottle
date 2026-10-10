// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// The Factories tab as CARDS grouped by state (owner decision, 8 Oct 2026, mockup B). What is held down:
//   * Cards is the view the first time; the Cards / Table switch is remembered in this browser next to the sort;
//   * the count strip: factories, running, need fixing (FAILING plus NEEDS YOU), paused, waiting on you;
//   * every row lands in its group - FAILING and NEEDS YOU under "Needs fixing", PAUSED under "Paused", RUNNING under
//     "Running on its own" - each heading with its count, and an empty group has no heading;
//   * inside a group the cards follow the owner's Sort by choice;
//   * the purpose line is shown when the registry has one and omitted when it has none;
//   * the boss's role word and its initial, never a person's name, with the Gateway's Talk words, or its "no boss" words;
//   * the status line is cut to three lines by the stylesheet, and at phone width the grid is one column;
//   * Table is the row list as it was.

const screenClient = vi.hoisted(() => ({
  getFactoriesList: vi.fn(),
  getFactoryPage: vi.fn(),
  getFactorySeats: vi.fn(),
  getFactoryFloor: vi.fn(() => new Promise(() => {})),
  startFactoryTalk: vi.fn(),
  runFactoryOwnerAction: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoriesScreenClient", () => screenClient);

const agentsClient = vi.hoisted(() => ({
  getFactoryActivity: vi.fn(),
  getFactoryReports: vi.fn(),
  getFactoryWaiting: vi.fn(),
  getFactoryAgent: vi.fn(),
  markFactoryItemHandled: vi.fn(),
  setFactoryPaused: vi.fn(),
  saveFactoryReport: vi.fn(),
  downloadFactoryCsv: vi.fn(),
}));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => agentsClient);

import type { FactoriesListView, FactoryListRow } from "@devthrottle/client-core/factory/factoriesScreenClient";
import { FactoriesView } from "./FactoriesView";
import { FACTORY_LIST } from "./fixtures";
import {
  FACTORY_VIEW_STORAGE_KEY,
  countFactoryRows,
  groupFactoryRows,
  initialsOf,
  loadFactoriesView,
} from "./factoriesCards";

function Where() {
  const loc = useLocation();
  return <div data-testid="where">{loc.pathname + loc.search}</div>;
}

function renderList() {
  return render(
    <MemoryRouter initialEntries={["/factories"]}>
      <Routes>
        <Route path="/factories" element={<FactoriesView />} />
        <Route path="/factories/:factory" element={<Where />} />
        <Route path="/session/:id" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

function row(id: string, title: string, statusRank: number, extra: Partial<FactoryListRow> = {}): FactoryListRow {
  return {
    id,
    title,
    statusWord: ["FAILING", "NEEDS YOU", "PAUSED", "RUNNING"][statusRank],
    statusTone: (["red", "amber", "paused", "ok"] as const)[statusRank],
    statusRank,
    statusReason: `reason ${id}`,
    statusLine: statusRank === 3 ? null : `line ${id}`,
    statusHref: statusRank === 3 ? null : `/factories/${id}#status`,
    waitingText: "-",
    waitingCount: 0,
    waitingHref: null,
    href: `/factories/${id}`,
    talk: null,
    noBossText: "No boss named",
    purpose: null,
    bossName: null,
    leftOpenText: null,
    leftOpenHref: null,
    ...extra,
  };
}

function listOf(rows: FactoryListRow[]): FactoriesListView {
  return { ...FACTORY_LIST, rows };
}

beforeEach(() => {
  cleanup();
  vi.clearAllMocks();
  window.localStorage.clear();
  screenClient.getFactoriesList.mockResolvedValue(FACTORY_LIST);
});

describe("Factories as cards - the view and the switch", () => {
  it("opens as cards the first time, with Table one click away, and remembers the choice", async () => {
    renderList();
    expect(await screen.findByTestId("fa-factories-cards")).toBeTruthy();
    expect(screen.queryByTestId("fa-factories-list")).toBeNull();
    expect(screen.getByTestId("fa-view-cards").getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(screen.getByTestId("fa-view-table"));

    expect(screen.getByTestId("fa-factories-list")).toBeTruthy();
    expect(screen.queryByTestId("fa-factories-cards")).toBeNull();
    expect(window.localStorage.getItem(FACTORY_VIEW_STORAGE_KEY)).toBe(JSON.stringify("table"));
    // The table is the row list as it was: the column headings sort, the rows carry their test ids.
    expect(screen.getByTestId("fa-sort-heading-status")).toBeTruthy();
    expect(screen.getByTestId("fa-factory-warmforward")).toBeTruthy();

    cleanup();
    renderList();
    expect(await screen.findByTestId("fa-factories-list")).toBeTruthy();
    expect(screen.getByTestId("fa-view-table").getAttribute("aria-pressed")).toBe("true");
  });

  it("falls back to cards when storage holds junk", () => {
    window.localStorage.setItem(FACTORY_VIEW_STORAGE_KEY, "not json");
    expect(loadFactoriesView()).toBe("cards");
    window.localStorage.setItem(FACTORY_VIEW_STORAGE_KEY, JSON.stringify("list"));
    expect(loadFactoriesView()).toBe("cards");
  });
});

describe("Factories as cards - the count strip", () => {
  it("counts the fixture: 3 factories, 1 running, 2 need fixing (failing plus needs you), 0 paused, 1 waiting", async () => {
    renderList();
    await screen.findByTestId("fa-strip");
    expect(screen.getByTestId("fa-strip-total").textContent).toBe("3factories");
    expect(screen.getByTestId("fa-strip-running").textContent).toBe("1running");
    expect(screen.getByTestId("fa-strip-fixing").textContent).toBe("2need fixing");
    expect(screen.getByTestId("fa-strip-paused").textContent).toBe("0paused");
    expect(screen.getByTestId("fa-strip-waiting").textContent).toBe("1waiting on you");
  });

  it("sums waiting over every factory and counts each state once", () => {
    const counts = countFactoryRows([
      row("a", "A", 0, { waitingCount: 2 }),
      row("b", "B", 1, { waitingCount: 1 }),
      row("c", "C", 2),
      row("d", "D", 3),
      row("e", "E", 3),
    ]);
    expect(counts).toEqual({ total: 5, running: 2, needsFixing: 2, paused: 1, waiting: 3 });
  });
});

describe("Factories as cards - the groups", () => {
  it("puts FAILING and NEEDS YOU under Needs fixing, PAUSED under Paused, RUNNING under Running on its own, each with its count", async () => {
    screenClient.getFactoriesList.mockResolvedValue(
      listOf([row("fail", "Fail", 0), row("ask", "Ask", 1), row("nap", "Nap", 2), row("run", "Run", 3), row("run2", "Run Two", 3)]),
    );
    renderList();
    await screen.findByTestId("fa-factories-cards");

    const fixing = screen.getByTestId("fa-group-fixing");
    expect(screen.getByTestId("fa-group-heading-fixing").textContent).toBe("Needs fixing (2)");
    expect(within(fixing).getByTestId("fa-card-fail")).toBeTruthy();
    expect(within(fixing).getByTestId("fa-card-ask")).toBeTruthy();

    const paused = screen.getByTestId("fa-group-paused");
    expect(screen.getByTestId("fa-group-heading-paused").textContent).toBe("Paused (1)");
    expect(within(paused).getByTestId("fa-card-nap")).toBeTruthy();

    const running = screen.getByTestId("fa-group-running");
    expect(screen.getByTestId("fa-group-heading-running").textContent).toBe("Running on its own (2)");
    expect(within(running).getByTestId("fa-card-run")).toBeTruthy();
    expect(within(running).getByTestId("fa-card-run2")).toBeTruthy();

    // The groups read in that order.
    const headings = screen.getAllByTestId(/fa-group-heading-/).map((h) => h.textContent);
    expect(headings).toEqual(["Needs fixing (2)", "Paused (1)", "Running on its own (2)"]);
  });

  it("hides the heading of an empty group", async () => {
    screenClient.getFactoriesList.mockResolvedValue(listOf([row("run", "Run", 3)]));
    renderList();
    await screen.findByTestId("fa-factories-cards");
    expect(screen.queryByTestId("fa-group-heading-fixing")).toBeNull();
    expect(screen.queryByTestId("fa-group-heading-paused")).toBeNull();
    expect(screen.getByTestId("fa-group-heading-running").textContent).toBe("Running on its own (1)");
  });

  it("groups by the Gateway's statusRank and drops empty groups", () => {
    const groups = groupFactoryRows([row("b", "B", 3), row("a", "A", 0)], { key: "name", reversed: false });
    expect(groups.map((g) => g.key)).toEqual(["fixing", "running"]);
    expect(groups[0].rows.map((r) => r.id)).toEqual(["a"]);
  });

  it("orders the cards inside a group by the owner's Sort by choice", async () => {
    screenClient.getFactoriesList.mockResolvedValue(
      listOf([row("zed", "Zed", 3), row("amy", "Amy", 3, { waitingCount: 0 }), row("mid", "Mid", 3, { waitingCount: 4, waitingText: "4 questions", waitingHref: "/factories/mid#waiting" })]),
    );
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const names = () => within(screen.getByTestId("fa-group-running")).getAllByTestId("fa-card-name").map((n) => n.textContent);
    expect(names()).toEqual(["Amy", "Mid", "Zed"]);

    fireEvent.click(screen.getByTestId("fa-sort-direction"));
    expect(names()).toEqual(["Zed", "Mid", "Amy"]);

    fireEvent.click(screen.getByTestId("fa-sort-waiting"));
    expect(names()).toEqual(["Mid", "Amy", "Zed"]);
    expect(window.localStorage.getItem("cockpit.factoriesSort")).toBe(JSON.stringify({ key: "waiting", reversed: false }));
  });
});

describe("Factories as cards - one card", () => {
  it("shows the purpose when the registry has one and nothing where it has none", async () => {
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const warm = screen.getByTestId("fa-card-warmforward");
    expect(within(warm).getByTestId("fa-card-purpose").textContent).toBe("Heating monitoring for homeowners (fixture)");
    expect(within(screen.getByTestId("fa-card-devthrottle")).queryByTestId("fa-card-purpose")).toBeNull();
    expect(within(screen.getByTestId("fa-card-mindzie-web")).queryByTestId("fa-card-purpose")).toBeNull();
  });

  it("carries the name as a link to the factory's page, the status pill with its reason, and the status line", async () => {
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const warm = screen.getByTestId("fa-card-warmforward");
    expect(within(warm).getByTestId("fa-card-name").getAttribute("href")).toBe("/factories/warmforward");
    expect(within(warm).getByText("NEEDS YOU-X").getAttribute("title")).toBe("1 question waiting on you.");
    expect(within(warm).getByTestId("fa-card-status-link").getAttribute("href")).toBe("/factories/warmforward#waiting");
    expect(within(warm).getByTestId("fa-card-line").textContent).toBe(
      "Boss, today 06:20: Is the bunkie meant to be at 20 C? (fixture)",
    );
    // RUNNING has no line from the Gateway, and the card invents none (rule 7).
    expect(within(screen.getByTestId("fa-card-devthrottle")).queryByTestId("fa-card-line")).toBeNull();

    fireEvent.click(within(warm).getByTestId("fa-card-name"));
    expect(screen.getByTestId("where").textContent).toBe("/factories/warmforward");
  });

  it("shows what is waiting on you, linked, only when something is", async () => {
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const link = within(screen.getByTestId("fa-card-warmforward")).getByTestId("fa-card-waiting");
    expect(link.textContent).toBe("1 question (fixture) waiting on you");
    expect(link.getAttribute("href")).toBe("/factories/warmforward#waiting");
    expect(within(screen.getByTestId("fa-card-devthrottle")).queryByTestId("fa-card-waiting")).toBeNull();
  });

  it("shows the boss's role word and its initial with the Gateway's Talk words, never a name, or the Gateway's no-boss words", async () => {
    // The owner, 8 October 2026: the boss has no name of its own.
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const warm = screen.getByTestId("fa-card-warmforward");
    expect(within(warm).getByText("B")).toBeTruthy();
    expect(within(warm).getByTestId("fa-card-boss-name").textContent).toBe("Boss");
    expect(within(warm).getByText("runs it")).toBeTruthy();
    expect(within(warm).getByTestId("fa-talk-warmforward-nora-hale").textContent).toBe("Talk to the boss");

    const noBoss = screen.getByTestId("fa-card-mindzie-web");
    expect(within(noBoss).getByTestId("fa-card-no-boss").textContent).toBe("No boss named");
    expect(within(noBoss).queryByText("runs it")).toBeNull();
  });

  it("makes the avatar's initials from the word it is given", () => {
    expect(initialsOf("Boss")).toBe("B");
    expect(initialsOf("Cost Watch")).toBe("CW");
  });

  it("marks the purpose, status and waiting text dt-private on the card and in the table (owner ruling, 8 Oct 2026)", async () => {
    renderList();
    await screen.findByTestId("fa-factories-cards");
    const warm = screen.getByTestId("fa-card-warmforward");
    expect(within(warm).getByTestId("fa-card-purpose").className).toContain("dt-private");
    expect(within(warm).getByTestId("fa-card-line").className).toContain("dt-private");
    expect(within(warm).getByTestId("fa-card-waiting").className).toContain("dt-private");
    expect(within(warm).getByTestId("fa-card-status-link").parentElement!.className).toContain("dt-private");

    fireEvent.click(screen.getByTestId("fa-view-table"));
    const row = screen.getByTestId("fa-factory-warmforward");
    expect(row.querySelector(".fa-flist-status")!.className).toContain("dt-private");
    expect(row.querySelector(".fa-flist-waiting")!.className).toContain("dt-private");
  });

  it("colours a card by its group", async () => {
    screenClient.getFactoriesList.mockResolvedValue(listOf([row("fail", "Fail", 0), row("nap", "Nap", 2), row("run", "Run", 3)]));
    renderList();
    await screen.findByTestId("fa-factories-cards");
    expect(screen.getByTestId("fa-card-fail").className).toContain("fa-card-fixing");
    expect(screen.getByTestId("fa-card-nap").className).toContain("fa-card-paused");
    expect(screen.getByTestId("fa-card-run").className).toContain("fa-card-running");
  });
});

describe("Factories as cards - the stylesheet", () => {
  // jsdom applies no stylesheet, so the proof of the three-line cut and the phone layout is the stylesheet itself.
  const css = readFileSync(join(__dirname, "factory.css"), "utf8");

  it("cuts the status line to three lines with an ellipsis", () => {
    const rule = /\.fa-card-line \{([\s\S]*?)\}/.exec(css);
    expect(rule).not.toBeNull();
    expect(rule![1]).toContain("-webkit-line-clamp: 3");
    expect(rule![1]).toContain("overflow: hidden");
  });

  it("lays the cards out on a filling grid of at least 260px, and one column at phone width", () => {
    const grid = /\n\.fa-cards \{([\s\S]*?)\}/.exec(css);
    expect(grid).not.toBeNull();
    expect(grid![1]).toContain("repeat(auto-fill, minmax(260px, 1fr))");
    const strip = /\n\.fa-strip \{([\s\S]*?)\}/.exec(css);
    expect(strip).not.toBeNull();
    expect(strip![1]).toContain("repeat(5, minmax(0, 1fr))");
    // The phone block that overrides them must come AFTER the base rules: at equal specificity the later rule wins, so
    // a phone block placed before them is dead (review finding, 8 Oct 2026). Find the block that holds the override
    // and check its position, not just its presence.
    const blocks = [...css.matchAll(/@media \(max-width: 640px\) \{([\s\S]*?)\n\}/g)];
    const phone = blocks.find((b) => /\.fa-cards \{\s*grid-template-columns: minmax\(0, 1fr\);/.test(b[1]));
    expect(phone).toBeDefined();
    expect(phone![1]).toMatch(/\.fa-strip \{\s*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\);/);
    expect(phone!.index!).toBeGreaterThan(grid!.index!);
    expect(phone!.index!).toBeGreaterThan(strip!.index!);
  });

  it("uses only ASCII in the card source and styles", () => {
    const view = readFileSync(join(__dirname, "FactoriesView.tsx"), "utf8");
    const cards = readFileSync(join(__dirname, "factoriesCards.ts"), "utf8");
    for (const text of [css, view, cards]) expect(/[^\x00-\x7f]/.test(text)).toBe(false);
  });
});
