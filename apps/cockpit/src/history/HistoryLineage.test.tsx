// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, within, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The History page renders the SHAPE the work actually had (internal#989), not a flat list.
//
// The lineage rules themselves are tested in client-core; this file exists because passing those is
// not the same as the screen changing. It asserts what a person looking at the page can see: three
// roots instead of twenty-two rows, a child nested under the session that started it, a
// cross-repository parent named rather than moved, and a share that never quietly counts the rows
// that cannot say who started them.

const { getWorkHistoryReport } = vi.hoisted(() => ({ getWorkHistoryReport: vi.fn() }));

vi.mock("@devthrottle/client-core/history/historyClient", async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>();
  return { ...actual, getWorkHistoryReport };
});
vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

import { HistoryView } from "./HistoryView";
import type { WorkHistoryReport, WorkHistorySession } from "@devthrottle/client-core/history/historyClient";

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function session(id: string, over: Partial<WorkHistorySession> = {}): WorkHistorySession {
  return {
    sessionId: id,
    startedAtUtc: "2026-07-27T10:00:00Z",
    lastSeenUtc: "2026-07-27T11:00:00Z",
    endingTone: "neutral",
    endingKind: "closed",
    endingLabel: "Closed",
    descriptionLine: `work ${id}`,
    ...over,
  };
}

function reportOf(repos: Array<{ key: string; sessions: WorkHistorySession[] }>): WorkHistoryReport {
  return {
    fromDay: "2026-07-27",
    toDay: "2026-07-27",
    repos: repos.map((r) => ({
      repoKey: r.key,
      displayName: r.key,
      days: [{ day: "2026-07-27", sessions: r.sessions }],
    })),
  };
}

async function renderWith(report: WorkHistoryReport) {
  getWorkHistoryReport.mockResolvedValue(report);
  render(
    <MemoryRouter>
      <HistoryView />
    </MemoryRouter>,
  );
  // findAll, not find: a two-repository report renders one repository heading each, and a single-match
  // query would fail on exactly the cross-repo cases this file is here to check.
  await screen.findAllByRole("heading", { level: 2 });
}

// The AI session summary and day paragraph were removed in October 2026 (nobody read them). A Gateway
// that has not been updated yet still sends them; the page must not show them.
describe("History shows no written summaries", () => {
  it("renders the sessions and none of an old Gateway's summary text", async () => {
    const stale = reportOf([
      {
        key: "devthrottle",
        sessions: [
          session("s1", {
            descriptionLine: "Fix the parser",
            summaryText: "An old session summary.",
            whatWasBuilt: ["an old built item"],
          } as Partial<WorkHistorySession>),
        ],
      },
    ]);
    (stale.repos[0].days[0] as unknown as Record<string, unknown>).summaryText = "An old day paragraph.";
    await renderWith(stale);

    expect(screen.getByText("Fix the parser")).toBeTruthy();
    expect(screen.queryByText(/An old session summary/)).toBeNull();
    expect(screen.queryByText(/an old built item/)).toBeNull();
    expect(screen.queryByText(/An old day paragraph/)).toBeNull();
    expect(screen.queryByText(/summary has not been written/i)).toBeNull();
  });
});

describe("History shows the shape of the work", () => {
  it("nests the sessions an agent started under it, and says how many", async () => {
    await renderWith(
      reportOf([
        {
          key: "devthrottle",
          sessions: [
            session("boss", { descriptionLine: "Release manager" }),
            session("w1", { parentSessionId: "boss", descriptionLine: "Worker one", originKind: "agent" }),
            session("w2", { parentSessionId: "boss", descriptionLine: "Worker two", originKind: "agent" }),
          ],
        },
      ]),
    );

    // The parent owns its children: they render INSIDE its list item, not beside it.
    const toggle = screen.getByRole("button", { name: /2 sessions this one started/i });
    expect(toggle).toBeTruthy();

    const parentItem = screen.getByText("Release manager").closest("li")!;
    expect(within(parentItem).getByText("Worker one")).toBeTruthy();
    expect(within(parentItem).getByText("Worker two")).toBeTruthy();
  });

  it("shows the children expanded, and lets them be hidden", async () => {
    // Collapsed-by-default would hide the answer behind a click on every row that has one.
    await renderWith(
      reportOf([
        {
          key: "devthrottle",
          sessions: [session("boss"), session("w1", { parentSessionId: "boss", descriptionLine: "Worker one" })],
        },
      ]),
    );

    expect(screen.getByText("Worker one")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /1 session this one started/i }));
    expect(screen.queryByText("Worker one")).toBeNull();
  });

  it("names a parent in another repository instead of moving the row there", async () => {
    // The fleet's most ordinary move is `session spawn <other-repo>`. Nesting the child under its
    // parent would file that work against a repository it never touched.
    await renderWith(
      reportOf([
        { key: "repo-a", sessions: [session("boss", { sessionName: "Release manager" })] },
        { key: "repo-b", sessions: [session("child", { parentSessionId: "boss", descriptionLine: "Other repo work" })] },
      ]),
    );

    const childItem = screen.getByText("Other repo work").closest("li")!;
    expect(within(childItem).getByText(/started by Release manager, elsewhere in this range/i)).toBeTruthy();

    // And it is still filed under its OWN repository.
    expect(screen.getByText("repo-b")).toBeTruthy();
  });

  it("says so when the parent is outside the range entirely", async () => {
    // Pruned by retention, or older than the window. Silently promoting it to a root would invent a
    // root, and roots are the thing being counted.
    await renderWith(
      reportOf([
        { key: "repo-a", sessions: [session("orphan", { parentSessionId: "long-gone", descriptionLine: "Orphaned work" })] },
      ]),
    );

    const item = screen.getByText("Orphaned work").closest("li")!;
    expect(within(item).getByText(/started by a session outside this range/i)).toBeTruthy();
  });

  it("states the agent share over what it can account for, and names what it cannot", async () => {
    // THE number this whole feature exists to produce - and the one way to get it wrong. These
    // fields only start being written on 2026-07-27, so a window reaching back further is mostly
    // rows that predate them; dividing by the total would report a share far below the truth.
    await renderWith(
      reportOf([
        {
          key: "devthrottle",
          sessions: [
            session("a", { originKind: "agent" }),
            session("b", { originKind: "agent" }),
            session("c", { originKind: "human" }),
            session("old1"), // predates the field
            session("old2"),
          ],
        },
      ]),
    );

    expect(screen.getByText(/2 of 3 started by agents/)).toBeTruthy();
    expect(screen.getByText(/2 older sessions do not record who started them/)).toBeTruthy();
    // Never "2 of 5": the unrecorded rows are not silently counted as human.
    expect(screen.queryByText(/2 of 5/)).toBeNull();
  });

  it("omits the share entirely when nothing can say who started it", async () => {
    await renderWith(
      reportOf([{ key: "devthrottle", sessions: [session("old1"), session("old2")] }]),
    );

    expect(screen.queryByText(/started by agents/)).toBeNull();
  });

  it("marks who started a session in plain words, without claiming which person", async () => {
    await renderWith(
      reportOf([
        {
          key: "devthrottle",
          sessions: [
            session("h", { originKind: "human", descriptionLine: "By hand" }),
            session("s", { originKind: "schedule", descriptionLine: "By schedule" }),
            session("u", { originKind: "unknown", descriptionLine: "Unrecorded" }),
          ],
        },
      ]),
    );

    expect(within(screen.getByText("By hand").closest("li")!).getByText(/started by hand/)).toBeTruthy();
    expect(within(screen.getByText("By schedule").closest("li")!).getByText(/started by a schedule/)).toBeTruthy();
    // A row that cannot say shows nothing rather than a hedge.
    const unknownItem = screen.getByText("Unrecorded").closest("li")!;
    expect(within(unknownItem).queryByText(/started by/)).toBeNull();
  });

  it("shows the interruption count beside the idle clock", async () => {
    await renderWith(
      reportOf([
        {
          key: "devthrottle",
          sessions: [session("a", { descriptionLine: "Needy", waitingStretchCount: 7 })],
        },
      ]),
    );

    expect(within(screen.getByText("Needy").closest("li")!).getByText(/needed you 7x/)).toBeTruthy();
  });
});
