import { describe, it, expect, vi, afterEach } from "vitest";
import { changeMemberRole, getTeamPage, removeMember } from "./teamPageClient";
import { gatewayErrorMessage } from "../api/client";

// The Team page client (devthrottle_internal#2303): each call reaches its route with its method, a refusal reaches the
// page in the Gateway's own words, and a Gateway with Teams not released is never read as a page.

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("the Team page client", () => {
  it("reads the page, changes a role and removes a member at their routes", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => json({ done: true }));
    vi.stubGlobal("fetch", fetchMock);

    await getTeamPage("team 1");
    await changeMemberRole("team 1", "m/1", "Collaborator");
    await removeMember("team 1", "m/1");

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls.map(([url, init]) => `${init.method} ${url}`)).toEqual([
      "GET /teams/team%201/page",
      "PUT /teams/team%201/members/m%2F1/role",
      "DELETE /teams/team%201/members/m%2F1",
    ]);
    expect(JSON.parse(calls[1][1].body as string)).toEqual({ role: "Collaborator" });
    expect(calls[2][1].body).toBeUndefined();
  });

  it("carries the Gateway's own refusal sentence to the page", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "Only the Owner can remove a Manager." }, 403)));

    const err = await removeMember("t", "m").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "remove the member")).toBe("Only the Owner can remove a Manager.");
  });

  it("refuses the app's own web page instead of reading it as the Team page", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("<!doctype html><div id=root></div>", {
      status: 200, headers: { "Content-Type": "text/html" },
    })));

    const err = await getTeamPage("t").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "load the Team page")).toBe("Teams are not available on this DevThrottle Gateway yet.");
  });
});
