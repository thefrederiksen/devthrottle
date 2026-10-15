import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  EMPTY_FILTER,
  applyFilter,
  emptyFilterMessage,
  filterSearchOf,
  filteredDocumentTitle,
  findSavedFilter,
  parseFilterQuery,
  reposFilterFirst,
  resolveFilterQuery,
  sameSelection,
  selectedValueCount,
  writeFilterQuery,
} from "./filter";

// The Cockpit's filter lives in each tab's address (issue #4009). These hold the address round trip, the saved-filter
// resolution (including the name that no longer exists, which must never read as "all sessions").

function session(fields: Partial<SessionDto> & { sessionId?: string } = {}): SessionDto {
  return { sessionId: "s1", createdAt: "2026-07-08T00:00:00Z", sortOrder: 0, ...fields } as unknown as SessionDto;
}

const saved = [
  { id: "1", name: "Product", repos: ["devthrottle", "website"], machines: [] },
  { id: "2", name: "Client", repos: ["acme"], machines: ["BUILD_BOX"] },
];

describe("parseFilterQuery", () => {
  it("reads no filter from an empty address", () => {
    expect(parseFilterQuery("")).toEqual({ kind: "none" });
    expect(parseFilterQuery("?tab=chat")).toEqual({ kind: "none" });
  });

  it("reads a saved filter by name", () => {
    expect(parseFilterQuery("?filter=Product")).toEqual({ kind: "saved", name: "Product" });
    expect(parseFilterQuery("?filter=My%20Client")).toEqual({ kind: "saved", name: "My Client" });
  });

  it("reads a quick filter, comma separated, dropping blanks and duplicates", () => {
    expect(parseFilterQuery("?repo=a,b,,a&machine=x")).toEqual({
      kind: "quick",
      filter: { repos: ["a", "b"], machines: ["x"] },
    });
  });

  it("accepts a repeated parameter as well as commas", () => {
    expect(parseFilterQuery("?repo=a&repo=b,c")).toEqual({ kind: "quick", filter: { repos: ["a", "b", "c"], machines: [] } });
  });

  it("prefers the saved name when both are present", () => {
    expect(parseFilterQuery("?filter=Product&repo=a")).toEqual({ kind: "saved", name: "Product" });
  });

  it("treats a blank saved name as no saved name", () => {
    expect(parseFilterQuery("?filter=%20&repo=a")).toEqual({ kind: "quick", filter: { repos: ["a"], machines: [] } });
  });

  it("takes URLSearchParams too", () => {
    expect(parseFilterQuery(new URLSearchParams("machine=DEVLINUX"))).toEqual({
      kind: "quick",
      filter: { repos: [], machines: ["DEVLINUX"] },
    });
  });
});

describe("writeFilterQuery", () => {
  it("writes a quick filter with bare commas", () => {
    expect(writeFilterQuery("", { kind: "quick", filter: { repos: ["a", "b"], machines: ["x"] } })).toBe("repo=a,b&machine=x");
  });

  it("writes a saved filter by name", () => {
    expect(writeFilterQuery("", { kind: "saved", name: "My Client" })).toBe("filter=My+Client");
  });

  it("keeps every other parameter and replaces the old filter", () => {
    expect(writeFilterQuery("?tab=chat&repo=old&filter=Old", { kind: "quick", filter: { repos: ["new"], machines: [] } })).toBe(
      "tab=chat&repo=new",
    );
  });

  it("clears the filter and leaves the rest", () => {
    expect(writeFilterQuery("?tab=reports&report=r1&repo=a&machine=x", { kind: "none" })).toBe("tab=reports&report=r1");
  });

  it("round trips through parse, including names with spaces and commas in other params", () => {
    for (const q of [
      { kind: "saved" as const, name: "Client, Europe" },
      { kind: "quick" as const, filter: { repos: ["dev-throttle", "web.site"], machines: ["SOREN_NORTH"] } },
      { kind: "none" as const },
    ]) {
      expect(parseFilterQuery(writeFilterQuery("?tab=chat", q))).toEqual(q);
    }
  });
});

describe("filterSearchOf", () => {
  it("keeps only the filter's parameters, with a leading question mark", () => {
    expect(filterSearchOf("?tab=chat&repo=a,b&report=r")).toBe("?repo=a,b");
    expect(filterSearchOf("?filter=Product&tab=chat")).toBe("?filter=Product");
  });

  it("is empty when no filter is set", () => {
    expect(filterSearchOf("?tab=chat")).toBe("");
  });
});

describe("findSavedFilter and resolveFilterQuery", () => {
  it("finds a saved filter ignoring case and surrounding spaces", () => {
    expect(findSavedFilter(saved, " product ")?.id).toBe("1");
    expect(findSavedFilter(saved, "nope")).toBeNull();
  });

  it("resolves no filter", () => {
    expect(resolveFilterQuery({ kind: "none" }, saved)).toEqual({ status: "none" });
  });

  it("resolves a quick filter without waiting for the saved list", () => {
    expect(resolveFilterQuery({ kind: "quick", filter: { repos: ["a"], machines: [] } }, null)).toEqual({
      status: "active",
      filter: { repos: ["a"], machines: [] },
      saved: null,
    });
  });

  it("is loading - not 'all sessions' - while the saved list has not arrived", () => {
    expect(resolveFilterQuery({ kind: "saved", name: "Product" }, null)).toEqual({ status: "loading", name: "Product" });
  });

  it("applies a saved filter's selection and names the filter", () => {
    const r = resolveFilterQuery({ kind: "saved", name: "client" }, saved);
    expect(r.status).toBe("active");
    if (r.status !== "active") return;
    expect(r.filter).toEqual({ repos: ["acme"], machines: ["BUILD_BOX"] });
    expect(r.saved?.id).toBe("2");
  });

  it("says missing for a saved name that no longer exists, never falling back to every session", () => {
    expect(resolveFilterQuery({ kind: "saved", name: "Gone" }, saved)).toEqual({ status: "missing", name: "Gone" });
    expect(resolveFilterQuery({ kind: "saved", name: "Gone" }, [])).toEqual({ status: "missing", name: "Gone" });
  });

  it("does not share the saved filter's arrays with the selection", () => {
    const r = resolveFilterQuery({ kind: "saved", name: "Product" }, saved);
    if (r.status !== "active") throw new Error("expected active");
    r.filter.repos.push("mutated");
    expect(saved[0].repos).toEqual(["devthrottle", "website"]);
  });
});

describe("selectedValueCount", () => {
  it("counts ticked values across both facets", () => {
    expect(selectedValueCount(EMPTY_FILTER)).toBe(0);
    expect(selectedValueCount({ repos: ["a", "b"], machines: ["x"] })).toBe(3);
  });
});

describe("reposFilterFirst", () => {
  const known = ["/home/s/zeta", "/home/s/devthrottle", "/home/s/acme", "C:/repos/devthrottle"];

  it("puts the filter's repos first, in the filter's order, then the rest in their order", () => {
    expect(reposFilterFirst(known, { repos: ["acme", "devthrottle"], machines: [] }, (p) => p)).toEqual([
      "/home/s/acme",
      "/home/s/devthrottle",
      "C:/repos/devthrottle",
      "/home/s/zeta",
    ]);
  });

  it("leaves the order alone with no repo selected", () => {
    expect(reposFilterFirst(known, { repos: [], machines: ["x"] }, (p) => p)).toEqual(known);
  });

  it("reads the path from an object", () => {
    const rows = known.map((path) => ({ path }));
    expect(reposFilterFirst(rows, { repos: ["acme"], machines: [] }, (r) => r.path)[0]).toBe(rows[2]);
  });

  it("does not invent a repo the picker does not know", () => {
    expect(reposFilterFirst(known, { repos: ["nowhere"], machines: [] }, (p) => p)).toEqual(known);
  });
});

describe("emptyFilterMessage", () => {
  it("names the repos", () => {
    expect(emptyFilterMessage({ repos: ["devthrottle", "website"], machines: [] })).toBe("No sessions in devthrottle, website right now");
  });

  it("names the machines", () => {
    expect(emptyFilterMessage({ repos: [], machines: ["DEVLINUX"] })).toBe("No sessions on DEVLINUX right now");
  });

  it("names both", () => {
    expect(emptyFilterMessage({ repos: ["acme"], machines: ["BUILD_BOX"] })).toBe("No sessions in acme on BUILD_BOX right now");
  });
});

describe("filteredDocumentTitle", () => {
  it("prefixes the saved filter's name", () => {
    expect(filteredDocumentTitle("DevThrottle Cockpit", "Product")).toBe("Product - DevThrottle Cockpit");
  });

  it("leaves the title alone without a saved filter", () => {
    expect(filteredDocumentTitle("DevThrottle Cockpit", null)).toBe("DevThrottle Cockpit");
  });
});

describe("sameSelection", () => {
  it("ignores order within a facet", () => {
    expect(sameSelection({ repos: ["a", "b"], machines: [] }, { repos: ["b", "a"], machines: [] })).toBe(true);
    expect(sameSelection({ repos: ["a"], machines: [] }, { repos: ["a"], machines: ["x"] })).toBe(false);
  });
});

describe("a saved filter matches exactly what the same quick filter matches", () => {
  it("uses the one shared rule", () => {
    const roster = [
      session({ sessionId: "a", repoPath: "/r/devthrottle", machineName: "M1" }),
      session({ sessionId: "b", repoPath: "/r/website", machineName: "M2" }),
      session({ sessionId: "c", repoPath: "/r/acme", machineName: "M1" }),
    ];
    const r = resolveFilterQuery({ kind: "saved", name: "Product" }, saved);
    if (r.status !== "active") throw new Error("expected active");
    const quick = parseFilterQuery("?repo=devthrottle,website");
    if (quick.kind !== "quick") throw new Error("expected quick");
    expect(applyFilter(roster, r.filter).map((s) => s.sessionId)).toEqual(["a", "b"]);
    expect(applyFilter(roster, quick.filter)).toEqual(applyFilter(roster, r.filter));
  });
});
