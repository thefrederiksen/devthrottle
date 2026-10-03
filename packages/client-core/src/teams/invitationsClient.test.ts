import { describe, it, expect, vi, afterEach } from "vitest";
import { acceptInvitation, getInviteOptions, openInvitation, sendInvitation } from "./invitationsClient";
import { gatewayErrorMessage } from "../api/client";

// The invitations client (devthrottle_internal#2301). Two properties matter beyond "it calls the route": the link's
// secret never travels in an address (the Gateway's access log records every path and query), and a Gateway that
// answers with its own web page - what one with Teams not released does - is never read as data.

const TOKEN = "q3J8vZ_x-0aB1cD2eF3gH4iJ5kL6mN7oP8qR9sT0uVw";

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("the invitations client", () => {
  it("sends the link's secret in the request body, never in the address", async () => {
    const fetchMock = vi.fn().mockImplementation(async () => json({ invitation: { id: "inv-1" } }));
    vi.stubGlobal("fetch", fetchMock);

    await openInvitation(TOKEN);
    await acceptInvitation(TOKEN);

    for (const [url, init] of fetchMock.mock.calls as [string, RequestInit][]) {
      expect(url).not.toContain(TOKEN);
      expect(init.method).toBe("POST");
      expect(JSON.parse(init.body as string)).toEqual({ token: TOKEN });
    }
    expect(fetchMock.mock.calls.map((c) => c[0])).toEqual(["/team-invitations/open", "/team-invitations/accept"]);
  });

  it("posts the address and role the person chose to the team's invitations", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json({ invitation: { id: "inv-1" }, email: { sent: true, message: "ok" } }, 201));
    vi.stubGlobal("fetch", fetchMock);

    await sendInvitation("team 1", "anna@any.io", "Developer");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/teams/team%201/invitations");
    expect(JSON.parse(init.body as string)).toEqual({ email: "anna@any.io", role: "Developer" });
  });

  it("carries the Gateway's own refusal sentence to the page", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(json({ error: "Only the Owner can invite a Manager." }, 403)));

    const err = await sendInvitation("t", "a@b.io", "Manager").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "send the invitation")).toBe("Only the Owner can invite a Manager.");
  });

  it("refuses the app's own web page instead of reading it as an invitation", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("<!doctype html><div id=root></div>", {
      status: 200, headers: { "Content-Type": "text/html" },
    })));

    const err = await getInviteOptions("t").catch((e: unknown) => e);

    expect(gatewayErrorMessage(err, "load the invite form")).toBe("Teams are not available on this DevThrottle Gateway yet.");
  });
});
