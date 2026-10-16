import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  EMPTY_FILTER,
  MAIN_CHECKOUT,
  applyFilter,
  filterIsActive,
  filterSummary,
  machineFacet,
  pruneFilter,
  repoFacet,
  repoOf,
  sessionMatchesFilter,
  toggleValue,
  worktreeOf,
} from "./filter";

function session(fields: Partial<SessionDto> & { sessionId?: string } = {}): SessionDto {
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...fields,
  } as unknown as SessionDto;
}

const roster = [
  session({ sessionId: "a", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/devthrottle" }),
  session({ sessionId: "b", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/mindzieWeb" }),
  session({ sessionId: "c", machineName: "SOREN_SOUTH", repoPath: "C:/repos/devthrottle" }),
  session({ sessionId: "d", machineName: "BUILD_BOX", repoPath: "C:/repos/devthrottle" }),
];

describe("filterIsActive", () => {
  it("is false for the empty filter", () => {
    expect(filterIsActive(EMPTY_FILTER)).toBe(false);
  });

  it("is true when a machine or repo is selected", () => {
    expect(filterIsActive({ machines: ["SOREN_NORTH"], repos: [] })).toBe(true);
    expect(filterIsActive({ machines: [], repos: ["devthrottle"] })).toBe(true);
  });
});

describe("applyFilter", () => {
  it("returns the roster unchanged when inactive", () => {
    expect(applyFilter(roster, EMPTY_FILTER)).toBe(roster);
  });

  it("filters by machine (union within the facet)", () => {
    const out = applyFilter(roster, { machines: ["SOREN_NORTH", "BUILD_BOX"], repos: [] });
    expect(out.map((s) => s.sessionId)).toEqual(["a", "b", "d"]);
  });

  it("filters by repo leaf", () => {
    const out = applyFilter(roster, { machines: [], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["a", "c", "d"]);
  });

  it("combines machine AND repo", () => {
    const out = applyFilter(roster, { machines: ["SOREN_NORTH"], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["a"]);
  });

  it("preserves the input ordering", () => {
    const reversed = [...roster].reverse();
    const out = applyFilter(reversed, { machines: [], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["d", "c", "a"]);
  });
});

describe("sessionMatchesFilter", () => {
  it("treats an empty facet as no restriction", () => {
    expect(sessionMatchesFilter(roster[0], EMPTY_FILTER)).toBe(true);
  });
});

describe("facets", () => {
  it("counts machines, sorted by name", () => {
    expect(machineFacet(roster)).toEqual([
      { value: "BUILD_BOX", count: 1 },
      { value: "SOREN_NORTH", count: 2 },
      { value: "SOREN_SOUTH", count: 1 },
    ]);
  });

  it("counts repo leaves, sorted by name", () => {
    expect(repoFacet(roster)).toEqual([
      { value: "devthrottle", count: 3, worktrees: [] },
      { value: "mindzieWeb", count: 1, worktrees: [] },
    ]);
  });

  it("ignores sessions with no machine name", () => {
    expect(machineFacet([session({ machineName: "" }), session({ machineName: "X" })])).toEqual([
      { value: "X", count: 1 },
    ]);
  });
});

describe("filterSummary", () => {
  it("lists machines then repos", () => {
    expect(filterSummary({ machines: ["SOREN_NORTH"], repos: ["devthrottle"] })).toBe("SOREN_NORTH, devthrottle");
  });

  it("is empty for the empty filter", () => {
    expect(filterSummary(EMPTY_FILTER)).toBe("");
  });
});

describe("toggleValue", () => {
  it("adds a missing value and removes a present one", () => {
    expect(toggleValue([], "a")).toEqual(["a"]);
    expect(toggleValue(["a", "b"], "a")).toEqual(["b"]);
  });
});

describe("pruneFilter", () => {
  it("drops selections that no longer exist in the roster", () => {
    const pruned = pruneFilter({ machines: ["SOREN_NORTH", "GONE"], repos: ["devthrottle", "old-repo"] }, roster);
    expect(pruned).toEqual({ machines: ["SOREN_NORTH"], repos: ["devthrottle"] });
  });

  it("returns the same reference when nothing changed", () => {
    const f = { machines: ["SOREN_NORTH"], repos: [] };
    expect(pruneFilter(f, roster)).toBe(f);
  });
});

// The owner, 2026-10-15, looking at the panel listing fd-p5-p6, fd-p5-p7, fd-p5-p8 ... as repos: "what we really want to
// show is repos ... we need to just filter on repos, not on work trees because they come and go." The Director stamps
// each session in a worktree with the repository it came from; the filter groups and matches on that.
describe("worktrees fold into their repository", () => {
  const fleet = [
    session({ sessionId: "main", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/devthrottle" }),
    session({ sessionId: "w1", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/_wt/fd-p5-p8", primaryRepoPath: "D:/ReposFred/devthrottle" }),
    session({ sessionId: "w2", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/_wt/fd-p5-p8", primaryRepoPath: "D:/ReposFred/devthrottle" }),
    session({ sessionId: "w3", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/_wt/fd-p7", primaryRepoPath: "D:/ReposFred/devthrottle" }),
    session({
      sessionId: "pool",
      machineName: "SOREN_NORTH",
      repoPath: "D:/pool/devthrottle-3",
      pooledWorktree: { repo: "D:/ReposFred/devthrottle", slot: "3", path: "D:/pool/devthrottle-3" },
    }),
    session({ sessionId: "web", machineName: "SOREN_NORTH", repoPath: "D:/ReposMindzie/mindzieWeb" }),
  ];

  it("names the repository, never the worktree folder", () => {
    expect(fleet.map(repoOf)).toEqual(["devthrottle", "devthrottle", "devthrottle", "devthrottle", "devthrottle", "mindzieWeb"]);
  });

  it("names the worktree a session is in, or the main checkout", () => {
    expect(fleet.map(worktreeOf)).toEqual([MAIN_CHECKOUT, "fd-p5-p8", "fd-p5-p8", "fd-p7", "devthrottle-3", MAIN_CHECKOUT]);
  });

  it("lists one row per repository, its worktrees under it with counts that add up", () => {
    expect(repoFacet(fleet)).toEqual([
      {
        value: "devthrottle",
        count: 5,
        worktrees: [
          { value: MAIN_CHECKOUT, count: 1 },
          { value: "devthrottle-3", count: 1 },
          { value: "fd-p5-p8", count: 2 },
          { value: "fd-p7", count: 1 },
        ],
      },
      { value: "mindzieWeb", count: 1, worktrees: [] },
    ]);
  });

  it("matches every worktree of a ticked repository, including one made after the filter was saved", () => {
    const later = session({ sessionId: "new", repoPath: "D:/ReposFred/_wt/fd-p9", primaryRepoPath: "D:/ReposFred/devthrottle" });
    const shown = applyFilter([...fleet, later], { machines: [], repos: ["devthrottle"] }).map((s) => s.sessionId);
    expect(shown).toEqual(["main", "w1", "w2", "w3", "pool", "new"]);
  });

  it("keeps a repo filter when the worktree it was seen in is removed", () => {
    const f = { machines: [], repos: ["devthrottle"] };
    expect(pruneFilter(f, fleet.filter((s) => s.sessionId === "w3"))).toBe(f);
  });

  it("treats a session from an older Director, with no stamp, as its own folder", () => {
    const old = session({ repoPath: "D:/ReposFred/_wt/fd-p5-p6" });
    expect(repoOf(old)).toBe("fd-p5-p6");
    expect(worktreeOf(old)).toBe(MAIN_CHECKOUT);
  });

  it("does not call the repository a worktree of itself when the stamp names its own folder", () => {
    const self = session({ repoPath: "D:\\ReposFred\\devthrottle\\", primaryRepoPath: "d:/reposfred/devthrottle" });
    expect(worktreeOf(self)).toBe(MAIN_CHECKOUT);
    expect(repoOf(self)).toBe("devthrottle");
  });
});
