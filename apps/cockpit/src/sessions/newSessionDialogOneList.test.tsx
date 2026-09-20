// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";

// The Cockpit's New Session dialog against the ONE repository list (the one-repository-list mission,
// phase 4). There was no rendered test of this screen at all before this file.
//
// What is held down here:
//   * the dialog reads GET /directors/{id}/known-repositories - the ONE list, already ordered - and NOT
//     GET /directors/{id}/repos, which is the Director's own registry and is half of that list. The half
//     that empties out on a machine where nobody uses the desktop dialog's Start button is the whole
//     reason this mission exists;
//   * the rows are RENDERED IN THE ORDER THE GATEWAY HANDED THEM, index for index, including a
//     never-opened repository sitting beneath the used ones. The order is the Gateway's ruling and this
//     screen does not get a vote (Critical Rule 7 in CLAUDE.md). The list is fed in the Gateway's order
//     and asserted in it; any re-ordering by the screen shows up as a different sequence on the page;
//   * the status line describes THE LIST rather than promising anything about what a click does;
//   * an empty machine and a failed read each say so, rather than rendering a silent empty list.
//
// Only the Gateway calls are replaced. Everything the dialog does with what it is handed is the real code.
const getDirectorsMock = vi.fn();
const getKnownRepositoriesMock = vi.fn();
const getReposMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: (...args: unknown[]) => getDirectorsMock(...args),
  getKnownRepositories: (...args: unknown[]) => getKnownRepositoriesMock(...args),
  getRepos: (...args: unknown[]) => getReposMock(...args),
  getAgents: () => Promise.resolve([]),
  createSession: () => Promise.resolve({ sessionId: "new-session" }),
  gatewayErrorMessage: (err: unknown) => String(err),
}));

import { NewSessionDialog } from "./NewSessionDialog";

function director(directorId: string) {
  return {
    directorId,
    machineName: "SOREN_NORTH",
    displayName: "North",
    version: "2.6.0",
    startedAt: "2026-09-19T08:00:00Z",
    lastSeen: "2026-09-20T08:00:00Z",
    controlEndpoint: "http://127.0.0.1:7801",
    sessions: 0,
  };
}

// The Gateway's own order: most recently used first, never-opened beneath, by name then path. This is
// the array the route returns and the array the screen must render unchanged.
const THE_GATEWAYS_ORDER = [
  { name: "Used yesterday", path: "/repositories/yesterday", lastUsed: "2026-09-19T09:00:00Z", neverOpened: false },
  { name: "Used last month", path: "/repositories/last-month", lastUsed: "2026-08-19T09:00:00Z", neverOpened: false },
  { name: "Never opened", path: "/roots/never-opened", lastUsed: "", neverOpened: true },
];

// A SECOND fixture, in an order NO client-side rule over lastUsed would ever produce, and that is the
// whole point of it. The realistic list above cannot catch a screen that sorts for itself: it is already
// in recency order with the timeless row last, so a comparator on lastUsed is a no-op over it and the
// assertion passes while the defect sits there. That was watched happening before this fixture existed.
//
// These four rows defeat every comparator a screen might reach for - newest-first, oldest-first,
// timeless-first, timeless-last - because a never-opened repository sits ABOVE a used one and the two
// used ones are not in recency order. Any rule applied to lastUsed moves at least one row, so the
// rendered sequence stops matching and the test says so. Nothing but "render exactly what arrived"
// passes it.
const AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN = [
  { name: "Zulu never opened", path: "/roots/zulu", lastUsed: "", neverOpened: true },
  { name: "Used in September", path: "/repositories/september", lastUsed: "2026-09-01T00:00:00Z", neverOpened: false },
  { name: "Alpha never opened", path: "/roots/alpha", lastUsed: "", neverOpened: true },
  { name: "Used in August", path: "/repositories/august", lastUsed: "2026-08-01T00:00:00Z", neverOpened: false },
];

function renderDialog() {
  return render(<NewSessionDialog onClose={() => {}} onCreated={() => {}} />);
}

// The repository rows, in the order the page lays them out. The dialog draws each as a button carrying
// the repository's name and its path, so the paths read off the page in order are the rendered order.
function renderedPaths(): string[] {
  return Array.from(document.querySelectorAll(".newsess-list .newsess-pick-path")).map(
    (node) => node.textContent ?? "",
  );
}

describe("the Cockpit New Session dialog reads the one ordered repository list", () => {
  beforeEach(() => {
    getDirectorsMock.mockResolvedValue([director("north-1")]);
    getKnownRepositoriesMock.mockResolvedValue(THE_GATEWAYS_ORDER);
    getReposMock.mockResolvedValue([]);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("asks for the one list on the selected machine, and never for the Director's registry half", async () => {
    renderDialog();

    await waitFor(() => expect(getKnownRepositoriesMock).toHaveBeenCalledTimes(1));
    expect(getKnownRepositoriesMock.mock.calls[0][0]).toBe("north-1");
    // The half-list route is the defect this phase removes from this screen. Reading it again, even
    // beside the one list, would put the Cockpit back to showing a different list from the phone.
    expect(getReposMock).not.toHaveBeenCalled();
  });

  it("renders the rows in the Gateway's order, with the never-opened repository beneath the used ones", async () => {
    renderDialog();

    await waitFor(() => expect(screen.getByText("Never opened")).toBeTruthy());
    // Index for index against the served array: the screen re-orders nothing, and in particular does not
    // decide where a repository with no last-used time belongs. The Gateway decided that.
    expect(renderedPaths()).toEqual(THE_GATEWAYS_ORDER.map((repository) => repository.path));
  });

  it("renders an order no client rule would produce, because the order is not this screen's to decide", async () => {
    getKnownRepositoriesMock.mockResolvedValue(AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN);
    renderDialog();

    await waitFor(() => expect(screen.getByText("Zulu never opened")).toBeTruthy());
    expect(renderedPaths()).toEqual(
      AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN.map((repository) => repository.path),
    );
  });

  it("describes the list in the status line, and promises nothing about the click", async () => {
    renderDialog();

    const status = await screen.findByText(
      "3 repositories on this machine, most recently used first.",
    );
    expect(status).toBeTruthy();
    expect(status.textContent).not.toContain("recent repo(s)");
    expect(status.textContent).not.toContain("start");
  });

  it("counts one repository as one repository rather than as one repositories", async () => {
    getKnownRepositoriesMock.mockResolvedValue([THE_GATEWAYS_ORDER[0]]);
    renderDialog();

    expect(
      await screen.findByText("1 repository on this machine, most recently used first."),
    ).toBeTruthy();
  });

  it("says a machine has no repositories rather than showing an empty list in silence", async () => {
    getKnownRepositoriesMock.mockResolvedValue([]);
    renderDialog();

    expect(
      await screen.findByText("No repositories known on this machine. Enter a path below."),
    ).toBeTruthy();
    expect(renderedPaths()).toEqual([]);
  });

  it("says the read failed, and says what the Gateway said, rather than looking like an empty machine", async () => {
    getKnownRepositoriesMock.mockRejectedValue(new Error("repository storage unavailable"));
    renderDialog();

    expect(
      await screen.findByText(/Could not load repositories: .*repository storage unavailable/),
    ).toBeTruthy();
    expect(renderedPaths()).toEqual([]);
  });
});
