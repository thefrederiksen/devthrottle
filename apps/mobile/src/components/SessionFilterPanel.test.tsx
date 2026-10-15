// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, fireEvent, screen } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { SavedFiltersState } from "@devthrottle/client-core/sessions/savedFilters";

// The phone's filter panel (issue #4009): the account's saved filters - named and coloured in the Cockpit - are at
// the top, and one tap applies one's repos and machines. The panel's own checklists are unchanged.

const savedState: { current: SavedFiltersState } = { current: undefined as unknown as SavedFiltersState };
vi.mock("@devthrottle/client-core/sessions/savedFilters", () => ({
  useSavedFilters: () => savedState.current,
}));

import { SessionFilterPanel } from "./SessionFilterPanel";

function session(sessionId: string, repo: string, machine: string): SessionDto {
  return { sessionId, createdAt: "2026-10-15T08:00:00Z", sortOrder: 0, machineName: machine, repoPath: `/r/${repo}` } as unknown as SessionDto;
}

const ROSTER = [session("a", "devthrottle", "M1"), session("b", "website", "M1"), session("c", "acme", "M2")];

function state(filters: SavedFiltersState["filters"], error: string | null = null): SavedFiltersState {
  return { filters, colours: [], error, reload: vi.fn(), create: vi.fn(), update: vi.fn(), remove: vi.fn() };
}

describe("the phone filter panel's saved filters", () => {
  beforeEach(() => cleanup());

  it("lists each saved filter with its count, and a tap applies its selection", () => {
    savedState.current = state([
      {
        id: "p",
        name: "Product",
        colour: "green",
        colourHex: "#22C55E",
        repos: ["devthrottle", "website"],
        machines: [],
        createdAtUtc: "",
        updatedAtUtc: "",
      },
    ]);
    const onApply = vi.fn();
    render(<SessionFilterPanel sessions={ROSTER} filter={{ machines: [], repos: [] }} onApply={onApply} onClose={() => {}} />);

    const row = screen.getByRole("button", { name: /^Product\s*2$/ });
    fireEvent.click(row);
    expect(onApply).toHaveBeenCalledWith({ repos: ["devthrottle", "website"], machines: [] });
  });

  it("shows no saved section when the account has none, and the checklists still work", () => {
    savedState.current = state([]);
    const onApply = vi.fn();
    render(<SessionFilterPanel sessions={ROSTER} filter={{ machines: [], repos: [] }} onApply={onApply} onClose={() => {}} />);

    expect(screen.queryByText("Saved filters")).toBeNull();
    fireEvent.click(screen.getByText("acme"));
    fireEvent.click(screen.getByText("Apply filter"));
    expect(onApply).toHaveBeenCalledWith({ machines: [], repos: ["acme"] });
  });

  it("says so when the saved filters cannot be read", () => {
    savedState.current = state(null, "the Gateway is not reachable");
    render(<SessionFilterPanel sessions={ROSTER} filter={{ machines: [], repos: [] }} onApply={() => {}} onClose={() => {}} />);

    expect(screen.getByText(/Could not load your saved filters/).textContent).toContain("not reachable");
  });
});
