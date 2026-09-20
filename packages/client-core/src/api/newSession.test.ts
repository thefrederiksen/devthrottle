import { afterEach, describe, expect, it, vi } from "vitest";
import { createSession, GatewayError, getKnownRepositories } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("new session client", () => {
  const realFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
  });

  it("reads the one list from the known-repository route for the selected Director", async () => {
    const fetchMock = vi.fn(async () => jsonResponse([
      { name: "Newest", path: "/repositories/newest", lastUsed: "2026-09-01T00:00:00Z" },
      { name: "Older", path: "/repositories/older", lastUsed: "2026-08-01T00:00:00Z" },
      { name: "Invalid", path: "", lastUsed: "2026-09-02T00:00:00Z" },
    ]));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const repositories = await getKnownRepositories("Director / one");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/directors/Director%20%2F%20one/known-repositories");
    expect(init.method).toBe("GET");
    // A row with no path is dropped: nothing can be started in it. That is not a ruling on the order -
    // it is a row the screen has no way to render or act on.
    expect(repositories.map((repository) => repository.name)).toEqual(["Newest", "Older"]);
  });

  // THE GUARD AGAINST THE RE-SORT COMING BACK. This reader used to call list.sort on lastUsed, and the
  // one-repository-list mission exists because clients decided the order for themselves. The Gateway now
  // decides it once, and every client renders what it was handed (Critical Rule 7 in CLAUDE.md).
  //
  // THE FIXTURE IS THE WHOLE TEST, so it is built deliberately rather than realistically. A comparator is
  // a no-op over a list that already agrees with it, so a fixture that happens to sit in some sensible
  // order certifies nothing: the sort goes back in, the rows do not move, and the test whose NAME promises
  // "never re-sorts on lastUsed" is the one that does not notice. That is not hypothetical - the first
  // version of this fixture read ["", August, September], which IS ascending by lastUsed, and an ascending
  // re-sort walked straight through it.
  //
  // These four rows are in an order NO rule over lastUsed produces, and each one is there to break a
  // different rule a reader might reach for:
  //   * newest-first moves the two timeless rows to the end;
  //   * oldest-first moves them to the front and reverses the used pair;
  //   * timeless-first-then-newest lifts Alpha above September;
  //   * timeless-last, or anything ordering by name, moves Alpha above Zulu.
  // Nothing but "return exactly what arrived" survives it. A never-opened repository sitting ABOVE used
  // ones is of course not an order the Gateway would ever serve, and that is the point: what is being
  // proved is that this reader does not CORRECT what it is handed, whatever it is handed.
  //
  // The assertion is index for index, not a set comparison, because the defect held down is a reordering
  // and nothing else.
  it("serves the Gateway's order untouched and never re-sorts on lastUsed", async () => {
    const served = [
      { name: "Zulu, never opened", path: "/roots/zulu", lastUsed: null, neverOpened: true },
      { name: "Used in September", path: "/repositories/september", lastUsed: "2026-09-01T00:00:00Z", neverOpened: false },
      { name: "Alpha, never opened", path: "/roots/alpha", lastUsed: null, neverOpened: true },
      { name: "Used in August", path: "/repositories/august", lastUsed: "2026-08-01T00:00:00Z", neverOpened: false },
    ];
    globalThis.fetch = (async () => jsonResponse(served)) as unknown as typeof fetch;

    const repositories = await getKnownRepositories("director-one");

    expect(repositories.map((repository) => repository.name)).toEqual([
      "Zulu, never opened",
      "Used in September",
      "Alpha, never opened",
      "Used in August",
    ]);
    expect(repositories.map((repository) => repository.path)).toEqual(served.map((row) => row.path));
  });

  // The one-repository-list mission, phase 3: the Gateway now serves BOTH halves of the one list on this
  // route, already ordered - most recently used first, with the repositories a Director found under a
  // registered root folder and nobody ever opened beneath them, each carrying a null lastUsed and saying
  // so in neverOpened. This route is the one the phone reads TODAY, so what it does with that shape is a
  // thing to prove rather than to assume: a never-opened repository must read as an ordinary entry with no
  // time, and it must stay at the bottom where the Gateway put it.
  it("reads the Gateway's one ordered list, never-opened repositories and all", async () => {
    globalThis.fetch = (async () => jsonResponse([
      { name: "Newest", path: "/repositories/newest", lastUsed: "2026-09-01T00:00:00Z", neverOpened: false },
      { name: "Older", path: "/repositories/older", lastUsed: "2026-08-01T00:00:00Z", neverOpened: false },
      { name: "Kilo", path: "/roots/kilo", lastUsed: null, neverOpened: true },
      { name: "Zulu", path: "/roots/zulu", lastUsed: null, neverOpened: true },
    ])) as unknown as typeof fetch;

    const repositories = await getKnownRepositories("director-one");

    expect(repositories.map((repository) => repository.name))
      .toEqual(["Newest", "Older", "Kilo", "Zulu"]);
    // A missing time reads as no time - not as a crash, and not as a date nobody chose.
    expect(repositories.map((repository) => repository.lastUsed))
      .toEqual(["2026-09-01T00:00:00Z", "2026-08-01T00:00:00Z", "", ""]);
    // And the Gateway's verdict ARRIVES, row by row, rather than being thrown away and re-derived by
    // each screen from the absent time. This is the field that lets a client render "never opened"
    // without deciding for itself what an empty date means.
    expect(repositories.map((repository) => repository.neverOpened))
      .toEqual([false, false, true, true]);
  });

  // A row the Gateway did not stamp is not a row to guess about. The verdict reads as false - "this
  // client has no verdict for you" - and nothing infers one from the missing time, which would be the
  // client ruling for itself by the back door.
  it("does not invent the never-opened verdict for a row that arrives without one", async () => {
    globalThis.fetch = (async () => jsonResponse([
      { name: "Unstamped", path: "/repositories/unstamped" },
    ])) as unknown as typeof fetch;

    const repositories = await getKnownRepositories("director-one");

    expect(repositories).toHaveLength(1);
    expect(repositories[0].neverOpened).toBe(false);
    expect(repositories[0].lastUsed).toBe("");
  });

  it("surfaces a known-repository route failure instead of returning an empty list", async () => {
    globalThis.fetch = (async () => jsonResponse(
      { error: "repository storage unavailable" },
      503,
    )) as unknown as typeof fetch;

    await expect(getKnownRepositories("director")).rejects.toBeInstanceOf(GatewayError);
    await expect(getKnownRepositories("director")).rejects.toMatchObject({ status: 503 });
  });

  it("creates with the explicitly selected agent and leaves permission behavior to the Director", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ sessionId: "created-session" }, 201));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await createSession("director-one", "  D:\\Repositories\\project  ", { agent: "RawCli" });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/directors/director-one/sessions");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      repoPath: "D:\\Repositories\\project",
      agent: "RawCli",
      wingmanEnabled: false,
    });
  });
});
