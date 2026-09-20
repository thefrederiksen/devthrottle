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

  it("loads and sorts the complete known-repository route for the selected Director", async () => {
    const fetchMock = vi.fn(async () => jsonResponse([
      { name: "Older", path: "/repositories/older", lastUsed: "2026-08-01T00:00:00Z" },
      { name: "Newest", path: "/repositories/newest", lastUsed: "2026-09-01T00:00:00Z" },
      { name: "Invalid", path: "", lastUsed: "2026-09-02T00:00:00Z" },
    ]));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const repositories = await getKnownRepositories("Director / one");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/directors/Director%20%2F%20one/known-repositories");
    expect(init.method).toBe("GET");
    expect(repositories.map((repository) => repository.name)).toEqual(["Newest", "Older"]);
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
