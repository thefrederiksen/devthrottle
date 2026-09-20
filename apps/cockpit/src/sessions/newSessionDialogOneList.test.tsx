// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";

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
const createSessionMock = vi.fn();
const addRepoMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: (...args: unknown[]) => getDirectorsMock(...args),
  getKnownRepositories: (...args: unknown[]) => getKnownRepositoriesMock(...args),
  getRepos: (...args: unknown[]) => getReposMock(...args),
  getAgents: () => Promise.resolve([]),
  createSession: (...args: unknown[]) => createSessionMock(...args),
  addRepo: (...args: unknown[]) => addRepoMock(...args),
  gatewayErrorMessage: (err: unknown) => String(err),
}));

import { NewSessionDialog, lastUsedAgo, orderRepositories, nextRepoSort } from "./NewSessionDialog";

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
  // Its NAME is deliberately not the words the Last Used cell uses ("Never opened"), so an assertion
  // naming either one can only have found the one it meant.
  { name: "Not yet opened", path: "/roots/never-opened", lastUsed: "", neverOpened: true },
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
    createSessionMock.mockResolvedValue({ sessionId: "new-session" });
    addRepoMock.mockResolvedValue({ added: true, name: "", path: "" });
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

    await waitFor(() => expect(screen.getByText("Not yet opened")).toBeTruthy());
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

// ==================================================================================================
// Phase 4: the repository half of this dialog redrawn to the desktop New Session TAB's layout.
//
// What is held down below, beyond the reader above:
//   * the machine row - the one thing the desktop tab has no need of, because it IS the machine -
//     still carries every fact the tall buttons it replaced carried;
//   * the Name / Path / Last Used table's SORTABLE HEADINGS, and above all that the default is the
//     Gateway's array returned untouched and that a round trip through the other headings comes back
//     to that exact array. If this screen ever compares lastUsed to decide an order, these fail;
//   * the Last Used cell's wording, which is the desktop tab's own ladder, and the never-opened
//     verdict being READ rather than inferred from an absent time;
//   * the search box filtering on name OR path;
//   * A CLICK SELECTS AND DOES NOT START A SESSION. That is the defect this phase was told to fix;
//   * the first-run empty state, and that no Browse button is drawn anywhere on this screen;
//   * the Add button, and that what it says afterwards is what POST /directors/{id}/repos actually did.
// ==================================================================================================

// The Last Used cells, in the order the page lays them out - the third column of each row.
function renderedWhen(): string[] {
  return Array.from(document.querySelectorAll(".newsess-list .newsess-pick-when")).map(
    (node) => node.textContent ?? "",
  );
}

function heading(label: string): HTMLElement {
  return screen.getByRole("button", { name: new RegExp(`^${label}`) });
}

function pathBox(): HTMLInputElement {
  return screen.getByLabelText("Or enter a path") as HTMLInputElement;
}

function isoAgo(milliseconds: number): string {
  return new Date(Date.now() - milliseconds).toISOString();
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

// One repository per rung of the desktop tab's ladder, plus the never-opened verdict at the bottom.
const EVERY_RUNG_OF_THE_LADDER = [
  { name: "Seconds", path: "/r/seconds", lastUsed: isoAgo(5_000), neverOpened: false },
  { name: "Minutes", path: "/r/minutes", lastUsed: isoAgo(5 * MINUTE), neverOpened: false },
  { name: "Hours", path: "/r/hours", lastUsed: isoAgo(3 * HOUR), neverOpened: false },
  { name: "Days", path: "/r/days", lastUsed: isoAgo(2 * DAY), neverOpened: false },
  { name: "Months", path: "/r/months", lastUsed: isoAgo(124 * DAY), neverOpened: false },
  { name: "Years", path: "/r/years", lastUsed: isoAgo(400 * DAY), neverOpened: false },
  { name: "Found under a root folder", path: "/r/found", lastUsed: "", neverOpened: true },
];

// Name and path deliberately disagree about which rows match: a filter of "gateway" matches the FIRST
// row by its name and the SECOND by its path, and the third not at all. A screen that searched only one
// of the two fields would return one row here, not two.
const NAME_SAYS_ONE_THING_AND_PATH_ANOTHER = [
  { name: "gateway", path: "/work/alpha", lastUsed: "2026-09-19T09:00:00Z", neverOpened: false },
  { name: "cockpit", path: "/work/gateway-notes", lastUsed: "2026-09-18T09:00:00Z", neverOpened: false },
  { name: "mobile", path: "/work/bravo", lastUsed: "2026-09-17T09:00:00Z", neverOpened: false },
];

describe("the Cockpit New Session dialog draws the desktop tab's repository table", () => {
  beforeEach(() => {
    getDirectorsMock.mockResolvedValue([director("north-1")]);
    getKnownRepositoriesMock.mockResolvedValue(THE_GATEWAYS_ORDER);
    getReposMock.mockResolvedValue([]);
    createSessionMock.mockResolvedValue({ sessionId: "new-session" });
    addRepoMock.mockResolvedValue({ added: true, name: "", path: "" });
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  // ---- the machine row ---------------------------------------------------------------------------

  it("says which machine the repositories are on, keeping the name, the port, the uptime and the version", async () => {
    renderDialog();

    await waitFor(() => expect(document.querySelector(".newsess-machine")).toBeTruthy());
    const chip = document.querySelector(".newsess-machine") as HTMLElement;
    // Every fact the tall machine buttons carried is still here; the row got smaller, not poorer.
    expect(chip.textContent).toContain("North");
    expect(chip.textContent).toContain(":7801");
    expect(chip.textContent).toMatch(/up \d/);
    expect(chip.textContent).toContain("v2.6.0");
    expect(chip.getAttribute("aria-pressed")).toBe("true");
  });

  // ---- the ordering, which is the Gateway's ------------------------------------------------------

  it("hands back the served array itself for the order it opens in, without sorting anything", () => {
    // Not merely equal - THE SAME ARRAY. A screen that sorted and happened to agree would fail this,
    // which is the point: the default state is not a sort that produces the Gateway's order, it is the
    // absence of a sort.
    expect(orderRepositories(AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN, "LastUsed", false)).toBe(
      AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN,
    );
  });

  it("flips Last Used by reversing that array, with no key and no rule about a missing time", () => {
    expect(orderRepositories(AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN, "LastUsed", true)).toEqual(
      [...AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN].reverse(),
    );
  });

  it("starts a newly clicked heading ascending, except Last Used, which starts in the Gateway's order", () => {
    // The desktop tab's RepoHeader_Click, transition for transition.
    expect(nextRepoSort("LastUsed", false, "Name")).toEqual({ column: "Name", ascending: true });
    expect(nextRepoSort("Name", true, "Name")).toEqual({ column: "Name", ascending: false });
    expect(nextRepoSort("Name", false, "Path")).toEqual({ column: "Path", ascending: true });
    expect(nextRepoSort("Path", true, "LastUsed")).toEqual({ column: "LastUsed", ascending: false });
    expect(nextRepoSort("LastUsed", false, "LastUsed")).toEqual({ column: "LastUsed", ascending: true });
  });

  it("returns to the Gateway's exact array after a round trip through Name and Path", async () => {
    getKnownRepositoriesMock.mockResolvedValue(AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN);
    renderDialog();
    const served = AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN.map((r) => r.path);

    await waitFor(() => expect(renderedPaths()).toEqual(served));

    // Every one of these three orders differs from the served one and from each other, which is what
    // makes the round trip worth running: the list genuinely moves and then genuinely comes back.
    fireEvent.click(heading("Name"));
    expect(renderedPaths()).toEqual([
      "/roots/alpha",
      "/repositories/august",
      "/repositories/september",
      "/roots/zulu",
    ]);

    fireEvent.click(heading("Path"));
    expect(renderedPaths()).toEqual([
      "/repositories/august",
      "/repositories/september",
      "/roots/alpha",
      "/roots/zulu",
    ]);

    fireEvent.click(heading("Last Used"));
    expect(renderedPaths()).toEqual(served);
  });

  it("reverses the served array when Last Used is clicked again, and comes back on the next click", async () => {
    getKnownRepositoriesMock.mockResolvedValue(AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN);
    renderDialog();
    const served = AN_ORDER_ONLY_THE_GATEWAY_COULD_HAVE_CHOSEN.map((r) => r.path);

    await waitFor(() => expect(renderedPaths()).toEqual(served));

    fireEvent.click(heading("Last Used"));
    // A PURE REVERSAL. Two never-opened rows are interleaved with two used ones here, so any rule
    // about where a missing time belongs would produce a different sequence from this one.
    expect(renderedPaths()).toEqual([...served].reverse());

    fireEvent.click(heading("Last Used"));
    expect(renderedPaths()).toEqual(served);
  });

  it("names the order it is actually in, so the line does not claim recency over a list sorted by name", async () => {
    renderDialog();

    await screen.findByText("3 repositories on this machine, most recently used first.");
    fireEvent.click(heading("Name"));
    expect(screen.getByText("3 repositories on this machine, by name.")).toBeTruthy();
    fireEvent.click(heading("Name"));
    expect(screen.getByText("3 repositories on this machine, by name, reversed.")).toBeTruthy();
    fireEvent.click(heading("Last Used"));
    expect(screen.getByText("3 repositories on this machine, most recently used first.")).toBeTruthy();
  });

  // ---- the Last Used cell ------------------------------------------------------------------------

  it("words a last-used time on the desktop tab's own ladder", () => {
    const now = Date.parse("2026-09-20T12:00:00Z");
    const ago = (milliseconds: number) => lastUsedAgo(new Date(now - milliseconds).toISOString(), now);

    expect(ago(59 * 1000)).toBe("just now");
    expect(ago(MINUTE)).toBe("1m ago");
    expect(ago(59 * MINUTE)).toBe("59m ago");
    expect(ago(HOUR)).toBe("1h ago");
    expect(ago(23 * HOUR)).toBe("23h ago");
    expect(ago(DAY)).toBe("1d ago");
    expect(ago(29 * DAY)).toBe("29d ago");
    expect(ago(30 * DAY)).toBe("1mo ago");
    expect(ago(364 * DAY)).toBe("12mo ago");
    expect(ago(365 * DAY)).toBe("1y ago");
    // A clock ahead of the Gateway's reads "just now" rather than a negative span, exactly as the C# does.
    expect(ago(-5 * MINUTE)).toBe("just now");
    // Nothing to say is said as nothing, never as a guess.
    expect(lastUsedAgo("", now)).toBe("");
    expect(lastUsedAgo("not a date", now)).toBe("");
  });

  it("shows each rung of that ladder on the page, and Never opened for the verdict the Gateway stamped", async () => {
    getKnownRepositoriesMock.mockResolvedValue(EVERY_RUNG_OF_THE_LADDER);
    renderDialog();

    await waitFor(() => expect(renderedWhen()).toHaveLength(EVERY_RUNG_OF_THE_LADDER.length));
    expect(renderedWhen()).toEqual([
      "just now",
      "5m ago",
      "3h ago",
      "2d ago",
      "4mo ago",
      "1y ago",
      "Never opened",
    ]);
  });

  it("reads the never-opened verdict instead of deciding for itself what a missing time means", async () => {
    // Two rows the Gateway and an inference would disagree about. A screen writing `lastUsed ? ... : ...`
    // would get BOTH of them the wrong way round; a screen reading neverOpened gets both right.
    getKnownRepositoriesMock.mockResolvedValue([
      // Stamped never-opened, yet carrying a time. The verdict is the Gateway's and it wins.
      { name: "Stamped", path: "/r/stamped", lastUsed: "2026-09-19T09:00:00Z", neverOpened: true },
      // Not stamped, and carrying no time. This screen has no verdict for it, so it says nothing -
      // it does not promote itself to deciding that the repository has never been opened.
      { name: "Unstamped", path: "/r/unstamped", lastUsed: "", neverOpened: false },
    ]);
    renderDialog();

    await waitFor(() => expect(renderedWhen()).toHaveLength(2));
    expect(renderedWhen()).toEqual(["Never opened", ""]);
  });

  // ---- the search box ----------------------------------------------------------------------------

  it("filters on the name OR the path, the way the desktop tab's search box does", async () => {
    getKnownRepositoriesMock.mockResolvedValue(NAME_SAYS_ONE_THING_AND_PATH_ANOTHER);
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    const search = screen.getByLabelText("Filter repositories by name or path");

    fireEvent.change(search, { target: { value: "gateway" } });
    // One match by NAME and one by PATH. A screen searching a single field would show one row.
    expect(renderedPaths()).toEqual(["/work/alpha", "/work/gateway-notes"]);

    // Case does not matter, matching the desktop tab's OrdinalIgnoreCase Contains.
    fireEvent.change(search, { target: { value: "GATEWAY" } });
    expect(renderedPaths()).toEqual(["/work/alpha", "/work/gateway-notes"]);

    fireEvent.change(search, { target: { value: "bravo" } });
    expect(renderedPaths()).toEqual(["/work/bravo"]);
  });

  it("says a filter matched nothing, and does NOT mistake that for a machine with no repositories", async () => {
    getKnownRepositoriesMock.mockResolvedValue(NAME_SAYS_ONE_THING_AND_PATH_ANOTHER);
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.change(screen.getByLabelText("Filter repositories by name or path"), {
      target: { value: "nothing here is called this" },
    });

    expect(renderedPaths()).toEqual([]);
    expect(screen.getByText("No repository here matches that.")).toBeTruthy();
    // The first-run empty state belongs to an empty CATALOGUE. This machine has three repositories.
    expect(screen.queryByText("No repositories yet")).toBeNull();
  });

  // ---- the click ---------------------------------------------------------------------------------

  it("SELECTS the repository that was clicked, and starts no session", async () => {
    const onCreated = vi.fn();
    render(<NewSessionDialog onClose={() => {}} onCreated={onCreated} />);

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    const row = screen.getByText("/repositories/yesterday").closest("button") as HTMLElement;
    fireEvent.click(row);

    // The defect this phase was told to fix: the row used to be a button whose click created a session,
    // so a mis-aimed click launched an agent in the wrong repository.
    expect(createSessionMock).not.toHaveBeenCalled();
    expect(onCreated).not.toHaveBeenCalled();
    // It reads as selected, and the path box - the one place a chosen path lives - now holds it.
    expect(row.className).toContain("sel");
    expect(row.getAttribute("aria-pressed")).toBe("true");
    expect(pathBox().value).toBe("/repositories/yesterday");
  });

  it("starts the session the user selected only when Create session is pressed", async () => {
    const onCreated = vi.fn();
    render(<NewSessionDialog onClose={() => {}} onCreated={onCreated} />);

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.click(screen.getByText("/repositories/last-month").closest("button") as HTMLElement);
    // Nothing has started yet. The row click chose the repository and did no more than that.
    expect(createSessionMock).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    expect(createSessionMock.mock.calls[0][0]).toBe("north-1");
    expect(createSessionMock.mock.calls[0][1]).toBe("/repositories/last-month");
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith("new-session"));
  });

  it("asks for a repository in the words that now describe the screen, rather than a click that starts one", async () => {
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    expect(
      await screen.findByText("Select a repository above, or enter a path below."),
    ).toBeTruthy();
    expect(createSessionMock).not.toHaveBeenCalled();
  });

  // ---- the empty state, and the Browse button that is deliberately absent -------------------------

  it("offers the first-run empty state when the catalogue is empty, with Add where the desktop puts Browse", async () => {
    getKnownRepositoriesMock.mockResolvedValue([]);
    renderDialog();

    expect(await screen.findByText("No repositories yet")).toBeTruthy();
    expect(
      screen.getByText(/A session runs your coding agent inside one repository\./),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add a repository to this machine" })).toBeTruthy();
    expect(
      screen.getByText("Repositories you have used appear in this list so next time it is one click."),
    ).toBeTruthy();
  });

  it("draws no Browse button, because this screen is never the machine the path is on", async () => {
    renderDialog();
    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    // A folder picker here would offer the VIEWER's file system for a path that has to exist on the
    // Director's machine - this mission's recurring path-comparison defect wearing a button.
    expect(screen.queryByRole("button", { name: /browse/i })).toBeNull();

    cleanup();
    getKnownRepositoriesMock.mockResolvedValue([]);
    renderDialog();
    expect(await screen.findByText("No repositories yet")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /browse/i })).toBeNull();
  });

  // ---- Add --------------------------------------------------------------------------------------

  it("registers the typed path on the selected machine and re-reads the one list", async () => {
    addRepoMock.mockResolvedValue({ added: true, name: "my-project", path: "/work/my-project" });
    getKnownRepositoriesMock
      .mockResolvedValueOnce(THE_GATEWAYS_ORDER)
      .mockResolvedValueOnce([
        ...THE_GATEWAYS_ORDER,
        { name: "my-project", path: "/work/my-project", lastUsed: "", neverOpened: true },
      ]);
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.change(pathBox(), { target: { value: "/work/my-project" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to this machine" }));

    await waitFor(() => expect(addRepoMock).toHaveBeenCalledWith("north-1", "/work/my-project"));
    expect(
      await screen.findByText("Added my-project to this machine. It is in the list above."),
    ).toBeTruthy();
    // It went and looked rather than assuming: the list on the page is the list the Gateway now serves.
    expect(getKnownRepositoriesMock).toHaveBeenCalledTimes(2);
    expect(renderedPaths()).toContain("/work/my-project");
  });

  it("reports the act and what it then FOUND, without predicting when a missing row will appear", async () => {
    // POST /directors/{id}/repos writes the DIRECTOR'S registry. This list is the GATEWAY'S CATALOGUE -
    // two stores, which is why an added path can be genuinely absent a moment later. The screen reports
    // the act and the list it read back, and explains nothing by mechanism: the feed that will join the
    // two stores is being built by another seat, and a sentence like "it joins the list once a session
    // has run in it" would be true today and wrong the week that lands.
    addRepoMock.mockResolvedValue({ added: true, name: "elsewhere", path: "/elsewhere" });
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.change(pathBox(), { target: { value: "/elsewhere" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to this machine" }));

    const note = await screen.findByText(
      "Added elsewhere to this machine. The list above is the one the Gateway serves, and it does not show this path.",
    );
    expect(note).toBeTruthy();
    // Nothing here is a forecast. A wording that dated would say WHEN, and none of these words do.
    expect(note.textContent).not.toMatch(/once|until|will|soon|yet/i);
    expect(renderedPaths()).not.toContain("/elsewhere");
  });

  it("says a repository the machine already had was already there, rather than claiming to have added it", async () => {
    // 201 and 200 are two different true outcomes and the screen must not report them as one.
    addRepoMock.mockResolvedValue({ added: false, name: "Used yesterday", path: "/repositories/yesterday" });
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.change(pathBox(), { target: { value: "/repositories/yesterday" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to this machine" }));

    expect(
      await screen.findByText(
        "Used yesterday was already on this machine. It is in the list above.",
      ),
    ).toBeTruthy();
  });

  it("asks for a path before adding one, and asks the Gateway for nothing until it has one", async () => {
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.click(screen.getByRole("button", { name: "Add to this machine" }));

    expect(
      await screen.findByText("Enter the path of a repository on this machine, then add it."),
    ).toBeTruthy();
    expect(addRepoMock).not.toHaveBeenCalled();
  });

  it("draws no table at all when the read failed, rather than a heading row over nothing", async () => {
    getKnownRepositoriesMock.mockRejectedValue(new Error("Director not connected"));
    renderDialog();

    await screen.findByText(/Could not load repositories: .*Director not connected/);
    // No rows, no first-run empty state, and no sortable headings offering to order a list this
    // screen does not have.
    expect(document.querySelector(".newsess-repotable")).toBeNull();
    expect(screen.queryByText("No repositories yet")).toBeNull();
    expect(renderedPaths()).toEqual([]);
  });

  it("says what the Gateway said when the add fails, rather than looking as though it worked", async () => {
    addRepoMock.mockRejectedValue(new Error("directory not found: /nowhere"));
    renderDialog();

    await waitFor(() => expect(renderedPaths()).toHaveLength(3));
    fireEvent.change(pathBox(), { target: { value: "/nowhere" } });
    fireEvent.click(screen.getByRole("button", { name: "Add to this machine" }));

    expect(await screen.findByText(/directory not found: \/nowhere/)).toBeTruthy();
    // The list is untouched, and nothing claims a repository was added.
    expect(renderedPaths()).toEqual(THE_GATEWAYS_ORDER.map((r) => r.path));
  });
});
