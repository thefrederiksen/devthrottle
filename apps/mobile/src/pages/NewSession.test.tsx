// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

const api = vi.hoisted(() => ({
  createSession: vi.fn(),
  getAgents: vi.fn(),
  getDirectors: vi.fn(),
  getKnownRepositories: vi.fn(),
  // Still mocked although this screen no longer calls it (the one-repository-list mission, phase 5).
  // It is here so the tests below can PROVE it is never called and can show what would appear on
  // screen if it were - see "reads one route for repositories".
  getRepos: vi.fn(),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  ...api,
  gatewayErrorMessage: (error: unknown) => error instanceof Error ? error.message : String(error),
}));

vi.mock("@devthrottle/client-core/sessions/waiting", () => ({
  durationLabel: () => "",
  useNow: () => Date.now(),
}));

import { NewSession } from "./NewSession";

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => {};
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function director(directorId: string, displayName: string, machineName: string) {
  return {
    directorId,
    displayName,
    machineName,
    version: "1.2.3",
    startedAt: "2026-09-01T10:00:00Z",
    lastSeen: "2026-09-01T12:00:00Z",
    controlEndpoint: "",
  };
}

function agent(type: string, displayName: string, modelLabel: string) {
  return { type, displayName, modelLabel, defaultModel: modelLabel.toLocaleLowerCase() };
}

function repository(number: number) {
  return {
    name: "Repository " + number,
    path: "D:\\Repositories\\repository-" + number,
    lastUsed: "2026-08-" + String(20 - number).padStart(2, "0") + "T12:00:00Z",
  };
}

/** A repository nobody has ever opened: the Gateway found it under a registered root folder and it
 * carries no last-used time, which reaches this screen as an empty string. */
function neverOpened(name: string, path: string) {
  return { name, path, lastUsed: "" };
}

/** The repository names on screen, top to bottom, exactly as rendered. */
function renderedRepositoryNames(): string[] {
  return screen
    .getAllByRole("button", { name: /^Select repository / })
    .map((button) => String(button.getAttribute("aria-label")).replace("Select repository ", ""));
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/new"]}>
      <Routes>
        <Route path="/new" element={<NewSession />} />
        <Route path="/session/:sessionId" element={<div>Opened session</div>} />
        <Route path="/" element={<div>Roster</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

async function openRepositoryStep() {
  fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));
  fireEvent.click(await screen.findByRole("button", { name: "Select agent Terminal agent" }));
  await screen.findByLabelText("Search known repositories");
}

describe("mobile new session", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    api.getDirectors.mockResolvedValue([
      director("north", "North Director", "SOREN_NORTH"),
      director("south", "South Director", "SOREN_SOUTH"),
    ]);
    api.getAgents.mockImplementation(async (directorId: string) =>
      directorId === "north"
        ? [agent("DefaultAgent", "Default agent", "Configured model"), agent("RawCli", "Terminal agent", "Shell")]
        : [agent("SouthAgent", "South agent", "Configured model")],
    );
    // Deliberately a repository that is in NO other source: if this screen ever reads the Director's
    // own registry again, it appears on screen and the tests below say so by name.
    api.getRepos.mockResolvedValue([
      { name: "Registry only", path: "D:\\Repositories\\registry-only", lastUsed: "2026-09-19T12:00:00Z" },
    ]);
    api.getKnownRepositories.mockResolvedValue([1, 2, 3, 4, 5, 6, 7, 8].map(repository));
    api.createSession.mockResolvedValue({ sessionId: "created-session" });
  });

  afterEach(() => cleanup());

  it("searches beyond the top five repositories and creates only after explicit review", async () => {
    renderPage();
    await openRepositoryStep();

    expect(screen.getByRole("button", { name: "Select repository Repository 1" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Select repository Repository 8" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Search known repositories"), {
      target: { value: "repository 8" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Select repository Repository 8" }));

    expect(api.createSession).not.toHaveBeenCalled();
    expect(await screen.findByRole("heading", { name: "Review the new session" })).toBeTruthy();
    expect(screen.getByText("D:\\Repositories\\repository-8")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(api.createSession).toHaveBeenCalledTimes(1));
    expect(api.createSession).toHaveBeenCalledWith(
      "north",
      "D:\\Repositories\\repository-8",
      { agent: "RawCli" },
    );
    expect(await screen.findByText("Opened session")).toBeTruthy();
  });

  // THE CASE THAT MATTERS MOST (the one-repository-list mission, phase 5). A machine with a MIX: three
  // repositories the owner has worked in, and two the Gateway found under a registered root folder
  // that nobody has ever opened. The Gateway has already decided that order - most recently used
  // first, never-opened beneath - and the phone's job is to show it, not to have an opinion about it.
  it("shows the Gateway's one list with the never-opened repositories at the bottom", async () => {
    api.getKnownRepositories.mockResolvedValue([
      { name: "Worked in today", path: "/repos/today", lastUsed: "2026-09-20T09:00:00Z" },
      { name: "Worked in last week", path: "/repos/last-week", lastUsed: "2026-09-13T09:00:00Z" },
      { name: "Worked in last month", path: "/repos/last-month", lastUsed: "2026-08-20T09:00:00Z" },
      neverOpened("Kilo", "/roots/kilo"),
      neverOpened("Zulu", "/roots/zulu"),
    ]);

    renderPage();
    await openRepositoryStep();

    await waitFor(() => expect(renderedRepositoryNames()).toHaveLength(5));
    expect(renderedRepositoryNames()).toEqual([
      "Worked in today",
      "Worked in last week",
      "Worked in last month",
      "Kilo",
      "Zulu",
    ]);
  });

  // The guard that stops a client ruling coming back. The order above is also the order a sort would
  // have produced, so it cannot catch one on its own: this test hands the screen an order NO client
  // sort would ever produce - used repositories oldest first, and the never-opened pair reversed out
  // of name order - and the screen must still render it exactly as served. A re-sort of any kind,
  // anywhere between the wire and the list, fails here.
  it("renders the Gateway's order verbatim, even an order no client sort would produce", async () => {
    api.getKnownRepositories.mockResolvedValue([
      { name: "Oldest", path: "/repos/oldest", lastUsed: "2026-01-01T09:00:00Z" },
      neverOpened("Zulu", "/roots/zulu"),
      { name: "Newest", path: "/repos/newest", lastUsed: "2026-09-20T09:00:00Z" },
      neverOpened("Kilo", "/roots/kilo"),
      { name: "Middle", path: "/repos/middle", lastUsed: "2026-05-01T09:00:00Z" },
    ]);

    renderPage();
    await openRepositoryStep();

    await waitFor(() => expect(renderedRepositoryNames()).toHaveLength(5));
    expect(renderedRepositoryNames()).toEqual(["Oldest", "Zulu", "Newest", "Kilo", "Middle"]);
  });

  // Searching filters the one list; it does not re-order it. The rows that survive the filter come
  // back in the order the Gateway served them.
  it("keeps the Gateway's order through a search", async () => {
    api.getKnownRepositories.mockResolvedValue([
      { name: "Match oldest", path: "/repos/oldest", lastUsed: "2026-01-01T09:00:00Z" },
      { name: "Skip me", path: "/repos/other", lastUsed: "2026-06-01T09:00:00Z" },
      { name: "Match newest", path: "/repos/newest", lastUsed: "2026-09-20T09:00:00Z" },
      neverOpened("Match never opened", "/roots/never"),
    ]);

    renderPage();
    await openRepositoryStep();
    fireEvent.change(screen.getByLabelText("Search known repositories"), { target: { value: "match" } });

    await waitFor(() => expect(renderedRepositoryNames()).toHaveLength(3));
    expect(renderedRepositoryNames()).toEqual(["Match oldest", "Match newest", "Match never opened"]);
  });

  // One machine, one answer: this step reads the Gateway's one repository route and nothing else. The
  // Director's own registry is a SECOND record of the same fact, and merging the two here is what made
  // the phone and the Cockpit disagree about one machine.
  it("reads one route for repositories and never the Director's own registry", async () => {
    renderPage();
    await openRepositoryStep();

    await waitFor(() => expect(api.getKnownRepositories).toHaveBeenCalledTimes(1));
    expect(api.getRepos).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Select repository Registry only" })).toBeNull();
  });

  it("ignores late agent and repository responses from a previous Director", async () => {
    const northAgents = deferred<ReturnType<typeof agent>[]>();
    const northRepositories = deferred<ReturnType<typeof repository>[]>();
    api.getAgents.mockImplementation((directorId: string) =>
      directorId === "north"
        ? northAgents.promise
        : Promise.resolve([agent("SouthAgent", "South agent", "Configured model")]),
    );
    api.getKnownRepositories.mockImplementation((directorId: string) =>
      directorId === "north" ? northRepositories.promise : Promise.resolve([repository(21)]),
    );

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director South Director" }));

    expect(await screen.findByRole("button", { name: "Select agent South agent" })).toBeTruthy();
    northAgents.resolve([agent("RawCli", "Stale north agent", "Shell")]);
    northRepositories.resolve([repository(8)]);

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "Select agent Stale north agent" })).toBeNull();
    });
    expect(screen.getByRole("button", { name: "Select agent South agent" })).toBeTruthy();
  });

  it("shows an empty agent list explicitly and does not enable review", async () => {
    api.getAgents.mockResolvedValue([]);

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));

    expect(await screen.findByText(/No agents are configured on this Director/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Review selections" })).toHaveProperty("disabled", true);
  });

  it("reports an agent loading error and retries before allowing selection", async () => {
    api.getAgents
      .mockRejectedValueOnce(new Error("agent inventory unavailable"))
      .mockResolvedValueOnce([agent("RawCli", "Terminal agent", "Shell")]);

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Could not load agents: agent inventory unavailable",
    );
    expect(screen.getByRole("button", { name: "Review selections" })).toHaveProperty("disabled", true);

    fireEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByRole("button", { name: "Select agent Terminal agent" })).toBeTruthy();
    expect(api.getAgents).toHaveBeenCalledTimes(2);
  });

  // FAILURE CASE. The one list is the only list, so when it cannot be loaded the step says so out
  // loud and offers a retry rather than quietly showing a shorter list from somewhere else. A path
  // can still be typed by hand, which is the same escape the step has always had.
  it("says the repository list could not be loaded and still accepts a typed path", async () => {
    api.getKnownRepositories.mockRejectedValue(new Error("repository storage unavailable"));

    renderPage();
    await openRepositoryStep();

    expect((await screen.findByRole("alert")).textContent).toContain(
      "The repository list could not be loaded: repository storage unavailable",
    );
    expect(screen.getByRole("button", { name: "Retry the repository list" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Select repository / })).toBeNull();
    // A list that could not be loaded is not a machine with nothing on it, and the screen must not
    // say it is - nor claim to be showing a top five it does not have.
    expect(screen.queryByText("No repositories are known on this machine yet.")).toBeNull();
    expect(screen.queryByText(/Showing the top five/)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Enter a path manually" }));
    fireEvent.change(screen.getByLabelText("Repository path"), {
      target: { value: "/repos/typed-by-hand" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Use this path" }));

    expect(await screen.findByRole("heading", { name: "Review the new session" })).toBeTruthy();
  });

  // FAILURE CASE. A machine the Gateway knows nothing about is an empty list with a sentence, never a
  // blank panel a reader has to interpret.
  it("says so plainly when the machine has no repositories at all", async () => {
    api.getKnownRepositories.mockResolvedValue([]);

    renderPage();
    await openRepositoryStep();

    expect(await screen.findByText("No repositories are known on this machine yet.")).toBeTruthy();
    expect(screen.queryByText("Loading repositories…")).toBeNull();
  });

  // FAILURE CASE. A search that matches nothing says so, and says it differently from a machine with
  // nothing on it - the two are not the same fact.
  it("distinguishes a search that matches nothing from a machine with nothing on it", async () => {
    renderPage();
    await openRepositoryStep();
    fireEvent.change(screen.getByLabelText("Search known repositories"), {
      target: { value: "nothing is called this" },
    });

    expect(await screen.findByText("No known repositories match that search.")).toBeTruthy();
  });

  it("keeps the loading state honest while the repository list is in flight", async () => {
    const list = deferred<ReturnType<typeof repository>[]>();
    api.getKnownRepositories.mockImplementation(() => list.promise);

    renderPage();
    await openRepositoryStep();

    expect(await screen.findByText("Loading repositories…")).toBeTruthy();
    expect(screen.queryByText("No repositories are known on this machine yet.")).toBeNull();

    list.resolve([repository(8)]);
    expect(await screen.findByRole("button", { name: "Select repository Repository 8" })).toBeTruthy();
  });

  it("uses an immediate in-flight guard so rapid confirmation taps create one session", async () => {
    const creation = deferred<{ sessionId: string }>();
    api.createSession.mockImplementation(() => creation.promise);

    renderPage();
    await openRepositoryStep();
    fireEvent.click(screen.getByRole("button", { name: "Select repository Repository 1" }));
    const createButton = await screen.findByRole("button", { name: "Create session" });

    fireEvent.click(createButton);
    fireEvent.click(createButton);

    expect(api.createSession).toHaveBeenCalledTimes(1);
    creation.resolve({ sessionId: "created-session" });
    expect(await screen.findByText("Opened session")).toBeTruthy();
  });

  it("reports a blank manual path and accepts a populated manual path", async () => {
    renderPage();
    await openRepositoryStep();

    fireEvent.click(screen.getByRole("button", { name: "Enter a path manually" }));
    fireEvent.click(screen.getByRole("button", { name: "Use this path" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Enter a repository path before continuing.",
    );

    fireEvent.change(screen.getByLabelText("Repository path"), {
      target: { value: "/repos/manual-project" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Use this path" }));
    expect(await screen.findByRole("heading", { name: "Review the new session" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));
    await waitFor(() => expect(api.createSession).toHaveBeenCalledWith(
      "north",
      "/repos/manual-project",
      { agent: "RawCli" },
    ));
  });

  it("preserves choices and typed input while retrying the repository list", async () => {
    api.getKnownRepositories
      .mockRejectedValueOnce(new Error("repository storage unavailable"))
      .mockResolvedValueOnce([repository(8)]);

    renderPage();
    await openRepositoryStep();
    await screen.findByText(/The repository list could not be loaded/);

    fireEvent.change(screen.getByLabelText("Search known repositories"), {
      target: { value: "repository 8" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Enter a path manually" }));
    fireEvent.change(screen.getByLabelText("Repository path"), {
      target: { value: "/repos/still-typing" },
    });

    fireEvent.click(screen.getByRole("button", { name: "Retry the repository list" }));

    await waitFor(() => expect(api.getKnownRepositories).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole("button", { name: "Select repository Repository 8" })).toBeTruthy();
    expect((screen.getByLabelText("Search known repositories") as HTMLInputElement).value).toBe("repository 8");
    expect((screen.getByLabelText("Repository path") as HTMLInputElement).value).toBe("/repos/still-typing");
    expect(screen.getByText("Terminal agent")).toBeTruthy();
    expect(api.getAgents).toHaveBeenCalledTimes(1);
  });

  it("caps broad search rendering, reports the full match count, and keeps every result reachable", async () => {
    api.getKnownRepositories.mockResolvedValue(
      Array.from({ length: 60 }, (_, index) => ({
        name: "Match " + String(index + 1).padStart(2, "0"),
        path: "/repos/match-" + String(index + 1).padStart(2, "0"),
        lastUsed: "2026-08-20T12:00:00Z",
      })),
    );

    renderPage();
    await openRepositoryStep();
    fireEvent.change(screen.getByLabelText("Search known repositories"), { target: { value: "match" } });

    expect(await screen.findByText("Showing 50 of 60 matches. Type more to narrow the results.")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /Select repository Match/ })).toHaveLength(50);
    expect(screen.queryByRole("button", { name: "Select repository Match 60" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Search known repositories"), { target: { value: "match 60" } });
    expect(await screen.findByRole("button", { name: "Select repository Match 60" })).toBeTruthy();
  });

  // Two Unix paths that differ only in case are two repositories, and they arrive as two rows the
  // Gateway has already decided are distinct. The phone renders both - it does not fold them together
  // by a rule of its own about what a path means.
  it("keeps case-distinct Unix repository paths as separate choices", async () => {
    api.getKnownRepositories.mockResolvedValue([
      { name: "Uppercase project", path: "/repos/Project", lastUsed: "2026-08-20T12:00:00Z" },
      { name: "Lowercase project", path: "/repos/project", lastUsed: "2026-08-19T12:00:00Z" },
    ]);

    renderPage();
    await openRepositoryStep();

    expect(await screen.findByRole("button", { name: "Select repository Uppercase project" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Select repository Lowercase project" })).toBeTruthy();
  });
});
