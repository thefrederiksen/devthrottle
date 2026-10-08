import { describe, it, expect, vi, afterEach } from "vitest";
import { changeTeamGovernance, getTeamGovernance } from "./governanceClient";
import { gatewayErrorMessage } from "../api/client";

// The Governance tab's client (Teams v1): each call reaches its route with its method, a change sends only what it
// names, a refusal reaches the tab in the Gateway's own words, and a Gateway with Teams off is never read as rules.

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("the Governance client", () => {
  it("reads and changes the rules at the team's governance route, sending only what changes", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => json({ canChange: true }));
    vi.stubGlobal("fetch", fetchMock);

    await getTeamGovernance("team 1");
    await changeTeamGovernance("team 1", { agents: { codex: false }, limits: { sessionsAtOnce: null } });

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls.map(([url, init]) => `${init.method} ${url}`)).toEqual([
      "GET /teams/team%201/governance",
      "PUT /teams/team%201/governance",
    ]);
    expect(calls[0][1].body).toBeUndefined();
    expect(JSON.parse(calls[1][1].body as string)).toEqual({ agents: { codex: false }, limits: { sessionsAtOnce: null } });
  });

  it("passes the Gateway's refusal through in its own words", async () => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () =>
      json({ error: "A Developer may not change the team's governance rules." }, 403)));

    const err = await changeTeamGovernance("t", { review: { noSelfMerge: true } }).catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "change the team's rules")).toContain("may not change the team's governance rules");
  });

  it("never reads the app's own page, which a Gateway with Teams off answers, as the rules", async () => {
    vi.stubGlobal("fetch", vi.fn().mockImplementation(async () =>
      new Response("<!doctype html><html></html>", { status: 200, headers: { "Content-Type": "text/html" } })));

    await expect(getTeamGovernance("t")).rejects.toMatchObject({ status: 404 });
  });
});
