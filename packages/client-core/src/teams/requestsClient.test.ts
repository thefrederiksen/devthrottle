import { describe, it, expect, vi, afterEach } from "vitest";
import { acceptRequest, declineRequest, forwardRequest, getTeamRequestList, listMyRequests, listTeamRequests, markRequestDone, sendRequest } from "./requestsClient";
import { gatewayErrorMessage } from "../api/client";

// The requests client (devthrottle_internal#2308): each call reaches its route with the right body, the Gateway's own
// refusal sentence reaches the page, and a Gateway that answers with its own web page - what one with Teams not
// released does - is never read as data.

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("the requests client", () => {
  it("sends the person's words in the body and nothing that names them", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ request: { id: "r1" } }, 201));
    vi.stubGlobal("fetch", fetchMock);

    const request = await sendRequest("team 1", "Add a dark mode");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/teams/team%201/requests");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ text: "Add a dark mode" });
    expect(request.id).toBe("r1");
  });

  it("reads the reader's own list and the team's list from their two routes", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => json({ count: 0, requests: [] }));
    vi.stubGlobal("fetch", fetchMock);

    await listMyRequests("t");
    await listTeamRequests("t");

    expect(fetchMock.mock.calls.map((c) => [c[0], (c[1] as RequestInit).method])).toEqual([
      ["/teams/t/requests/mine", "GET"],
      ["/teams/t/requests", "GET"],
    ]);
  });

  it("posts each decision to its own route, with the reason for Not doing this", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => json({ request: { id: "r1" } }));
    vi.stubGlobal("fetch", fetchMock);

    await acceptRequest("t", "r 1");
    await declineRequest("t", "r 1", "Not this quarter");
    await markRequestDone("t", "r 1");

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls.map((c) => c[0])).toEqual(["/teams/t/requests/r%201/accept", "/teams/t/requests/r%201/decline", "/teams/t/requests/r%201/done"]);
    expect(JSON.parse(calls[1][1].body as string)).toEqual({ reason: "Not this quarter" });
  });

  it("posts the Owner's forward to its route with the factory and the note, and reads the whole team list", async () => {
    const fetchMock = vi.fn().mockImplementation(async (url: string) =>
      url.endsWith("/forward") ? json({ request: { id: "r1", state: "forwarded" } }) : json({ teamId: "t", teamLabel: "Acme", count: 0, requests: [], forward: { factories: [] } }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const forwarded = await forwardRequest("t", "r 1", "website", "Worth doing.");
    const whole = await getTeamRequestList("t");

    const calls = fetchMock.mock.calls as [string, RequestInit][];
    expect(calls.map((c) => [c[0], c[1].method])).toEqual([
      ["/teams/t/requests/r%201/forward", "POST"],
      ["/teams/t/requests", "GET"],
    ]);
    expect(JSON.parse(calls[0][1].body as string)).toEqual({ factory: "website", note: "Worth doing." });
    expect(forwarded.state).toBe("forwarded");
    expect(whole.teamLabel).toBe("Acme");
  });

  it("carries the Gateway's own refusal sentence to the page", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "In this team you are a Developer, and a Developer may not do that." }, 403)));

    const err = await acceptRequest("t", "r1").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "accept the request")).toBe("In this team you are a Developer, and a Developer may not do that.");
  });

  it("refuses the app's own web page instead of reading it as requests", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("<!doctype html><div id=root></div>", {
      status: 200, headers: { "Content-Type": "text/html" },
    })));

    const err = await listMyRequests("t").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "load your requests")).toBe("This DevThrottle Gateway does not offer teams yet.");
  });
});
