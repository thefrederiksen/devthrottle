import { describe, it, expect, vi, afterEach } from "vitest";
import { getMyTeams, TEAMS_NOT_RELEASED_REASON } from "./teamsClient";

// GET /teams (devthrottle_internal#2300) as the team switcher reads it (#2312). Three Gateways answer it three ways,
// and only one of them has teams: a released hosted Gateway answers JSON; a Gateway with Teams dark maps no route,
// so the read falls through to the Cockpit's page fallback and gets HTTP 200 with the app's HTML shell; a
// self-hosted Gateway answers 404 with a sentence. The last two must read as "no teams offered" - never as a
// failure, and never as a list.

const APP_SHELL = '<!doctype html><html><head><title>DevThrottle Cockpit</title></head><body><div id="root"></div></body></html>';

const FULL = { full: true, pages: [], landing: null, elsewhere: null };
const COLLABORATOR_APP = {
  full: false,
  pages: [
    { id: "questions", label: "Questions", path: "/questions" },
    { id: "requests", label: "Requests", path: "/requests" },
    { id: "reports", label: "Reports", path: "/reports" },
  ],
  landing: "/questions",
  elsewhere: "This page is not available to Collaborators.",
};
const FULL_JSON = JSON.stringify(FULL);

function respond(body: string, contentType: string, status = 200): Response {
  return new Response(body, { status, headers: { "Content-Type": contentType } });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getMyTeams", () => {
  it("GetMyTeams_ReleasedGateway_ReturnsTheTeamsWithTheirRoles", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      respond(
        JSON.stringify({
          count: 2,
          teams: [
            { id: "t1", name: "DevThrottle", role: "Owner", memberCount: 5, people: "5 people", app: FULL },
            { id: "t2", name: "Paul's project", role: "Collaborator", memberCount: 1, people: "1 person", app: COLLABORATOR_APP },
          ],
        }),
        "application/json; charset=utf-8",
      ),
    );
    vi.stubGlobal("fetch", fetchMock);

    const answer = await getMyTeams();

    expect(answer).toEqual({
      kind: "teams",
      teams: [
        { id: "t1", name: "DevThrottle", role: "Owner", memberCount: 5, people: "5 people", app: FULL },
        { id: "t2", name: "Paul's project", role: "Collaborator", memberCount: 1, people: "1 person", app: COLLABORATOR_APP },
      ],
    });
    expect(fetchMock.mock.calls[0][0]).toBe("/teams");
  });

  it("GetMyTeams_NoTeams_ReturnsAnEmptyList", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond('{"count":0,"teams":[]}', "application/json")));
    expect(await getMyTeams()).toEqual({ kind: "teams", teams: [] });
  });

  it("GetMyTeams_TeamsDark_AppShellIsNotOffered", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond(APP_SHELL, "text/html; charset=utf-8")));
    expect(await getMyTeams()).toEqual({ kind: "not-offered", reason: TEAMS_NOT_RELEASED_REASON });
  });

  it("GetMyTeams_SelfHosted404_IsNotOfferedWithTheGatewaysSentence", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(respond('{"error":"Teams are part of the hosted DevThrottle service."}', "application/json", 404)),
    );
    expect(await getMyTeams()).toEqual({ kind: "not-offered", reason: "Teams are part of the hosted DevThrottle service." });
  });

  it("GetMyTeams_Refused_ThrowsWithTheGatewaysReason", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(respond('{"error":"No DevThrottle account is bound to this request."}', "application/json", 403)),
    );
    await expect(getMyTeams()).rejects.toThrow(/No DevThrottle account is bound/);
  });

  it("GetMyTeams_UnlabelledBody_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("{}", { status: 200 })));
    await expect(getMyTeams()).rejects.toThrow(/instead of team data/);
  });

  it("GetMyTeams_NoTeamsArray_Throws", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond('{"count":0}', "application/json")));
    await expect(getMyTeams()).rejects.toThrow(/had no teams in it/);
  });

  // Review finding F5: the role is the Gateway's label, shown verbatim. A role the client has never heard of is still
  // shown, and never costs the person their whole list of teams.
  it("GetMyTeams_ARoleTheClientDoesNotKnow_IsShownAsSent", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond('{"teams":[{"id":"t","name":"n","role":"Billing admin","memberCount":1,"people":"1 person","app":' + FULL_JSON + '}]}', "application/json"),
      ),
    );
    expect(await getMyTeams()).toEqual({
      kind: "teams",
      teams: [{ id: "t", name: "n", role: "Billing admin", memberCount: 1, people: "1 person", app: FULL }],
    });
  });

  it("GetMyTeams_AnEmptyRole_Throws", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(respond('{"teams":[{"id":"t","name":"n","role":" ","memberCount":1,"people":"1 person","app":' + FULL_JSON + '}]}', "application/json")),
    );
    await expect(getMyTeams()).rejects.toThrow(/cannot read/);
  });

  // devthrottle_internal#2306: the page verdict is the Gateway's and is carried verbatim; a team without one cannot be
  // drawn, so the read fails loudly rather than guessing a page list from the role.
  it("GetMyTeams_ATeamWithNoPageVerdict_Throws", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond('{"teams":[{"id":"t","name":"n","role":"Collaborator","memberCount":1,"people":"1 person"}]}', "application/json"),
      ),
    );
    await expect(getMyTeams()).rejects.toThrow(/pages its member may open/);
  });

  it("GetMyTeams_ALimitedAppWithNoLandingOrSentence_Throws", async () => {
    const app = JSON.stringify({ ...COLLABORATOR_APP, landing: null });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond(`{"teams":[{"id":"t","name":"n","role":"Collaborator","memberCount":1,"people":"1 person","app":${app}}]}`, "application/json"),
      ),
    );
    await expect(getMyTeams()).rejects.toThrow(/pages its member may open/);
  });

  it("GetMyTeams_APageWithoutAnAddress_Throws", async () => {
    const app = JSON.stringify({ ...COLLABORATOR_APP, pages: [{ id: "questions", label: "Questions" }] });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        respond(`{"teams":[{"id":"t","name":"n","role":"Collaborator","memberCount":1,"people":"1 person","app":${app}}]}`, "application/json"),
      ),
    );
    await expect(getMyTeams()).rejects.toThrow(/pages its member may open/);
  });
});
