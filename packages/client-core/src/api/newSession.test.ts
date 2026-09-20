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
  // The rows below are fed in the order a client would most be tempted to "correct": a never-opened
  // repository, which carries no time at all, sitting ABOVE two that have been used. A reader that sorted
  // on lastUsed would push it to one end or the other. The assertion is index for index, not a set
  // comparison, because the defect being held down is a reordering and nothing else.
  it("serves the Gateway's order untouched and never re-sorts on lastUsed", async () => {
    const served = [
      { name: "Never opened", path: "/roots/never-opened", lastUsed: null, neverOpened: true },
      { name: "Used in August", path: "/repositories/august", lastUsed: "2026-08-01T00:00:00Z", neverOpened: false },
      { name: "Used in September", path: "/repositories/september", lastUsed: "2026-09-01T00:00:00Z", neverOpened: false },
    ];
    globalThis.fetch = (async () => jsonResponse(served)) as unknown as typeof fetch;

    const repositories = await getKnownRepositories("director-one");

    expect(repositories.map((repository) => repository.name)).toEqual([
      "Never opened",
      "Used in August",
      "Used in September",
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
