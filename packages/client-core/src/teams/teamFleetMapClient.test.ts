import { describe, it, expect, vi, afterEach } from "vitest";
import { getTeamFleetMap } from "./teamFleetMapClient";

// GET /teams/{teamId}/fleet-map (devthrottle_internal#2312) as the Cockpit reads it. The Gateway cuts the answer by
// role; this client reads it strictly, and keeps only the allowed fields of each session, so nothing that could open
// a session reaches the page even if a Gateway ever sent it.

function respond(body: unknown, status = 200, contentType = "application/json"): Response {
  return new Response(typeof body === "string" ? body : JSON.stringify(body), { status, headers: { "Content-Type": contentType } });
}

const MAP = {
  teamId: "t1",
  teamName: "DevThrottle",
  role: "Manager",
  scope: "everyone",
  summary: "Everyone's Directors on this team. Names and status only; sessions do not open.",
  layouts: ["by-person", "by-director"],
  emptyText: "No Director is on this team yet.",
  people: [
    {
      person: "rob@example.com",
      isYou: false,
      directors: [{ name: "Rob - laptop", machine: "ROB-XPS", sessions: [{ name: "Signup", status: "working" }] }],
    },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getTeamFleetMap", () => {
  it("GetTeamFleetMap_AMember_ReturnsTheMapAsSent", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond(MAP));
    vi.stubGlobal("fetch", fetchMock);

    expect(await getTeamFleetMap("t1")).toEqual(MAP);
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t1/fleet-map");
  });

  it("GetTeamFleetMap_TheTeamIdIsEncoded", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond(MAP));
    vi.stubGlobal("fetch", fetchMock);

    await getTeamFleetMap("a/b");
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/a%2Fb/fleet-map");
  });

  it("GetTeamFleetMap_ExtraFieldsOnASession_AreDropped", async () => {
    const leaky = structuredClone(MAP) as typeof MAP & { people: { directors: { sessions: Record<string, unknown>[] }[] }[] };
    leaky.people[0].directors[0].sessions[0] = { name: "Signup", status: "working", sessionId: "s-1", transcript: "secret" };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(leaky)));

    const map = await getTeamFleetMap("t1");
    expect(map.people[0].directors[0].sessions[0]).toEqual({ name: "Signup", status: "working" });
  });

  it("GetTeamFleetMap_Collaborator403_ThrowsWithTheGatewaysSentence", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond({ error: "In this team you are a Collaborator, and a Collaborator may not see the team's Fleet Map.", code: "team_action_refused" }, 403),
      ),
    );
    await expect(getTeamFleetMap("t1")).rejects.toMatchObject({ status: 403, code: "team_action_refused" });
  });

  it("GetTeamFleetMap_NotTheirTeam404_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "There is no team with that id that you are a member of." }, 404)));
    await expect(getTeamFleetMap("t1")).rejects.toMatchObject({ status: 404 });
  });

  it("GetTeamFleetMap_AppShell_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond("<!doctype html><html></html>", 200, "text/html")));
    await expect(getTeamFleetMap("t1")).rejects.toThrow(/cannot read/);
  });

  it("GetTeamFleetMap_AnUnknownStatus_Throws", async () => {
    const bad = structuredClone(MAP);
    bad.people[0].directors[0].sessions[0].status = "dreaming";
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(bad)));
    await expect(getTeamFleetMap("t1")).rejects.toThrow(/working, waiting or done/);
  });

  it("GetTeamFleetMap_AnUnknownLayout_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ ...MAP, layouts: ["by-mood"] })));
    await expect(getTeamFleetMap("t1")).rejects.toThrow(/by-mood/);
  });

  it("GetTeamFleetMap_NoLayout_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ ...MAP, layouts: [] })));
    await expect(getTeamFleetMap("t1")).rejects.toThrow(/no layout/);
  });

  it("GetTeamFleetMap_MissingPeople_Throws", async () => {
    const { people: _people, ...noPeople } = MAP;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(noPeople)));
    await expect(getTeamFleetMap("t1")).rejects.toThrow(/cannot read/);
  });
});
