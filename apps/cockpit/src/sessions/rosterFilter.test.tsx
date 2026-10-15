// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, fireEvent, screen, act, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { SavedFiltersState, SavedSessionFilter } from "@devthrottle/client-core/sessions/savedFilters";

// The Cockpit's Sessions filter (issue #4009), driven through the real roster, the real panel and the real address
// hook: the Filter button's three faces, "6 of 28", the line under the header, no word about sessions outside the
// filter, the empty filter, a saved filter by name (and one that no longer exists), the filter riding on a
// session link, and the panel's quick filter, save, edit and delete. The address is read back after every action,
// because the address IS the filter - nothing is written to shared storage.

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(({ GatewayError, gatewayErrorMessage, authHeaders }) => ({ GatewayError, gatewayErrorMessage, authHeaders }))(
    await importOriginal<typeof import("@devthrottle/client-core/api/client")>(),
  ),
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => {}),
}));

vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));

const savedState: { current: SavedFiltersState } = { current: undefined as unknown as SavedFiltersState };
vi.mock("@devthrottle/client-core/sessions/savedFilters", () => ({
  useSavedFilters: () => savedState.current,
}));

import { SessionRoster } from "./SessionRoster";
import { useRosterFilter } from "./useRosterFilter";

const COLOURS = [
  { key: "blue", label: "Blue", hex: "#3B82F6" },
  { key: "green", label: "Green", hex: "#22C55E" },
];

function savedFilter(id: string, name: string, repos: string[], machines: string[] = [], colour = "green"): SavedSessionFilter {
  return {
    id,
    name,
    colour,
    colourHex: COLOURS.find((c) => c.key === colour)?.hex ?? "#000000",
    repos,
    machines,
    createdAtUtc: "2026-10-15T12:00:00Z",
    updatedAtUtc: "2026-10-15T12:00:00Z",
  };
}

function setSaved(filters: SavedSessionFilter[] | null, error: string | null = null) {
  savedState.current = {
    filters,
    colours: COLOURS,
    error,
    reload: vi.fn(),
    create: vi.fn(async (input) => savedFilter("new", input.name, input.repos, input.machines, input.colour)),
    update: vi.fn(async (id, input) => savedFilter(id, input.name, input.repos, input.machines, input.colour)),
    remove: vi.fn(async () => {}),
  };
}

function session(sessionId: string, repo: string, machine: string, bucket: "needsYou" | "active" = "active", number = ""): SessionDto {
  return {
    sessionId,
    name: sessionId,
    number,
    createdAt: "2026-10-15T08:00:00Z",
    sortOrder: 0,
    directorId: machine === "DEVLINUX" ? "d1" : "d2",
    machineName: machine,
    repoPath: `/home/s/${repo}`,
    effectiveColor: bucket === "needsYou" ? "red" : "green",
    effectiveColorHex: bucket === "needsYou" ? "#EF4444" : "#22C55E",
    stateLabel: bucket === "needsYou" ? "Needs you" : "Working",
    triageBucket: bucket,
    needsYouSince: bucket === "needsYou" ? "2026-10-15T09:00:00Z" : undefined,
  } as unknown as SessionDto;
}

const ROSTER = [
  session("dt-one", "devthrottle", "DEVLINUX"),
  session("dt-two", "devthrottle", "BUILD_BOX"),
  session("site", "website", "DEVLINUX"),
  session("acme-work", "acme", "BUILD_BOX", "needsYou", "412"),
];

function Harness({ view = "my-order" as "my-order" | "attention" }) {
  const rosterFilter = useRosterFilter();
  const location = useLocation();
  return (
    <>
      <SessionRoster
        sessions={ROSTER}
        directors={[]}
        portByDirector={new Map()}
        selectedId={undefined}
        view={view}
        onView={() => {}}
        error={null}
        onNewSession={() => {}}
        rosterFilter={rosterFilter}
      />
      <output data-testid="address">{location.search}</output>
    </>
  );
}

function renderAt(entry: string, view: "my-order" | "attention" = "my-order") {
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/" element={<Harness view={view} />} />
        <Route path="/session/:sessionId" element={<Harness view={view} />} />
      </Routes>
    </MemoryRouter>,
  );
}

const address = () => screen.getByTestId("address").textContent;
const rowNames = () => Array.from(document.querySelectorAll(".roster-name-text")).map((el) => el.textContent);
const filterButton = () => screen.getByTestId("roster-filter-btn");

describe("the Sessions filter on the Cockpit roster", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    setSaved([savedFilter("p", "Product", ["devthrottle", "website"])]);
  });

  it("with no filter: the button reads Filter, the count is the whole roster, and every session shows", () => {
    renderAt("/");

    expect(filterButton().textContent).toBe("Filter");
    expect(screen.getByTestId("roster-count").textContent).toBe("4");
    expect(screen.queryByTestId("roster-filter-strip")).toBeNull();
    expect(rowNames()).toHaveLength(4);
  });

  it("a quick filter from the address: Filter (n), 'n of total', the strip, and only matching rows", () => {
    renderAt("/?repo=devthrottle");

    expect(filterButton().textContent).toBe("Filter (1)");
    expect(screen.getByTestId("roster-count").textContent).toBe("2 of 4");
    expect(screen.getByTestId("roster-filter-strip").textContent).toContain("devthrottle (2 of 4)");
    expect(rowNames().sort()).toEqual(["dt-one", "dt-two"]);
  });

  it("repo AND machine: union within a facet, intersection across", () => {
    renderAt("/?repo=devthrottle,website&machine=DEVLINUX");

    expect(rowNames().sort()).toEqual(["dt-one", "site"]);
    expect(filterButton().textContent).toBe("Filter (3)");
  });

  it("a session link carries the filter, so opening a session keeps the list", () => {
    renderAt("/?repo=devthrottle&tab=chat");

    const hrefs = Array.from(document.querySelectorAll(".roster-row")).map((a) => a.getAttribute("href")).sort();
    expect(hrefs).toEqual(["/session/dt-one?repo=devthrottle", "/session/dt-two?repo=devthrottle"]);
  });

  it("says nothing about sessions outside the filter, even one that needs you (owner ruling, 15 October 2026)", () => {
    renderAt("/?repo=devthrottle");

    expect(screen.queryByTestId("roster-filter-outside")).toBeNull();
    expect(document.body.textContent).not.toContain("outside this filter");
    expect(document.body.textContent).not.toContain("acme-work");
  });

  it("an empty filter says so and keeps the filter", () => {
    renderAt("/?repo=nowhere");

    expect(screen.getByTestId("roster-filter-empty").textContent).toBe("No sessions in nowhere right now");
    expect(screen.getByTestId("roster-count").textContent).toBe("0 of 4");
    expect(filterButton().textContent).toBe("Filter (1)");
    expect(address()).toBe("?repo=nowhere");
  });

  it("a saved filter by name: the button wears its name, the strip names it, and its repos show", () => {
    renderAt("/?filter=product");

    expect(filterButton().textContent).toBe("Product");
    expect(screen.getByTestId("roster-filter-strip").textContent).toContain("Product:");
    expect(rowNames().sort()).toEqual(["dt-one", "dt-two", "site"]);
  });

  it("a saved name that no longer exists says so and shows no sessions - never every session", () => {
    renderAt("/?filter=Gone");

    expect(screen.getByTestId("roster-filter-problem").textContent).toContain('There is no saved filter named "Gone" any more');
    expect(rowNames()).toHaveLength(0);
    expect(screen.getByTestId("roster-count").textContent).toBe("0 of 4");
    fireEvent.click(screen.getByText("Show all sessions"));
    expect(address()).toBe("");
  });

  it("while the saved filters load, a saved name shows nothing yet and says it is loading", () => {
    setSaved(null);
    renderAt("/?filter=Product");

    expect(screen.getByTestId("roster-filter-problem").textContent).toContain('Loading the saved filter "Product"');
    expect(rowNames()).toHaveLength(0);
  });

  it("when the saved filters cannot be read, says so instead of showing every session", () => {
    setSaved(null, "the Gateway is not reachable");
    renderAt("/?filter=Product");

    expect(screen.getByTestId("roster-filter-problem").textContent).toContain("Could not load the saved filter");
    expect(rowNames()).toHaveLength(0);
  });

  it("offers Try again when the saved filters could not be read", () => {
    setSaved(null, "the Gateway is not reachable");
    renderAt("/?filter=Product");

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(savedState.current.reload).toHaveBeenCalledTimes(1);
  });

  it("Attention keeps working inside the filter", () => {
    renderAt("/?repo=acme,devthrottle", "attention");

    expect(rowNames().sort()).toEqual(["acme-work", "dt-one", "dt-two"]);
  });

  it("the strip's Clear shows every session again", () => {
    renderAt("/?repo=devthrottle");

    fireEvent.click(screen.getByText("Clear"));
    expect(address()).toBe("");
    expect(filterButton().textContent).toBe("Filter");
  });

  it("writes nothing to shared storage, so two tabs cannot overwrite each other", () => {
    renderAt("/");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: /^website/ }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(address()).toBe("?repo=website");
    expect(Object.keys(window.localStorage).filter((k) => k.toLowerCase().includes("filter"))).toEqual([]);
  });
});

describe("the filter panel", () => {
  beforeEach(() => {
    cleanup();
    setSaved([savedFilter("p", "Product", ["devthrottle", "website"])]);
  });

  it("opens straight on the quick filter, with counts from the whole roster", () => {
    renderAt("/?repo=acme");
    fireEvent.click(filterButton());

    const dialog = screen.getByRole("dialog", { name: "Filter sessions" });
    expect(dialog.textContent).toContain("All repos");
    expect(dialog.textContent).toContain("All machines");
    // devthrottle has 2 sessions in the WHOLE roster even though the tab shows only acme.
    expect(screen.getByRole("button", { name: /^devthrottle\s*2$/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /^acme/ }).getAttribute("aria-pressed")).toBe("true");
  });

  it("Apply writes the ticked repos and machines to the address", () => {
    renderAt("/");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: /^devthrottle/ }));
    fireEvent.click(screen.getByRole("button", { name: /^BUILD_BOX/ }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(address()).toBe("?repo=devthrottle&machine=BUILD_BOX");
    expect(rowNames()).toEqual(["dt-two"]);
  });

  it("Cancel changes nothing", () => {
    renderAt("/?repo=acme");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: /^devthrottle/ }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(address()).toBe("?repo=acme");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("Clear shows every session", () => {
    renderAt("/?repo=acme");
    fireEvent.click(filterButton());
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Clear" }));

    expect(address()).toBe("");
  });

  it("lists the saved filters with their current count, and one click applies one", () => {
    renderAt("/");
    fireEvent.click(filterButton());

    const row = screen.getByRole("button", { name: /^Product\s*3$/ });
    fireEvent.click(row);
    expect(address()).toBe("?filter=Product");
    expect(filterButton().textContent).toBe("Product");
  });

  it("Save as filter names the ticked selection with a colour, saves it, and shows it by name", async () => {
    renderAt("/");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: /^acme/ }));
    fireEvent.click(screen.getByText("Save as filter..."));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Client" } });
    fireEvent.click(screen.getByRole("radio", { name: "Green" }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save filter" }));
    });

    expect(savedState.current.create).toHaveBeenCalledWith({ name: "Client", colour: "green", repos: ["acme"], machines: [] });
    expect(address()).toBe("?filter=Client");
  });

  it("Save as filter is not offered for an empty selection", () => {
    renderAt("/");
    fireEvent.click(filterButton());

    expect((screen.getByText("Save as filter...") as HTMLButtonElement).disabled).toBe(true);
  });

  it("shows the Gateway's sentence when a save is refused, and keeps the panel open", async () => {
    renderAt("/?repo=acme");
    savedState.current.create = vi.fn(async () => {
      throw new Error('A filter named "Product" already exists.');
    });
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByText("Save as filter..."));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Product" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save filter" }));
    });

    expect(within(screen.getByRole("dialog")).getByRole("alert").textContent).toContain("already exists");
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(address()).toBe("?repo=acme");
  });

  it("Edit renames and changes a saved filter, and the tab follows it by its new name", async () => {
    renderAt("/?filter=Product");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: "Edit Product" }));
    const name = screen.getByLabelText("Name") as HTMLInputElement;
    expect(name.value).toBe("Product");
    fireEvent.change(name, { target: { value: "Products" } });
    fireEvent.click(screen.getByRole("button", { name: /^acme/ }));
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    });

    expect(savedState.current.update).toHaveBeenCalledWith("p", {
      name: "Products",
      colour: "green",
      repos: ["devthrottle", "website", "acme"],
      machines: [],
    });
    expect(address()).toBe("?filter=Products");
  });

  it("editing a filter this tab is NOT showing changes it without switching the tab", async () => {
    setSaved([savedFilter("p", "Product", ["devthrottle", "website"]), savedFilter("c", "Client", ["acme"])]);
    renderAt("/?filter=Product");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: "Edit Client" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Client X" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save changes" }));
    });

    expect(savedState.current.update).toHaveBeenCalledWith("c", expect.objectContaining({ name: "Client X" }));
    expect(address()).toBe("?filter=Product");
    expect(filterButton().textContent).toBe("Product");
  });

  it("Delete asks once in the row, deletes, and clears this tab when it was showing that filter", async () => {
    renderAt("/?filter=Product");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: "Delete Product" }));
    expect(screen.getByText('Delete "Product"?')).toBeTruthy();
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    });

    expect(savedState.current.remove).toHaveBeenCalledWith("p");
    expect(address()).toBe("");
  });

  it("Keep backs out of a delete without deleting", () => {
    renderAt("/");
    fireEvent.click(filterButton());
    fireEvent.click(screen.getByRole("button", { name: "Delete Product" }));
    fireEvent.click(screen.getByRole("button", { name: "Keep" }));

    expect(savedState.current.remove).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: /^Product\s*3$/ })).toBeTruthy();
  });
});
