import { describe, it, expect, vi, afterEach } from "vitest";
import {
  combineByOrder,
  getContributorFactories,
  getContributorInbox,
  getCustomerPage,
  readCombined,
  teamsWithPage,
} from "./contributorClient";
import type { TeamSummary } from "./teamsClient";

// A contributor's Factories and Inbox (Team implementation piece 4.1), as the two pages read them: one route per team,
// the Gateway's answer carried as sent, combined by the Gateway's own order key, and the one refusal sentence for a
// factory that is not the person's to see.

function respond(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function team(id: string, pages: string[]): TeamSummary {
  return {
    id,
    name: `Team ${id}`,
    role: "Contributor",
    memberCount: 2,
    people: "2 people",
    app: { full: false, pages: pages.map((p) => ({ id: p, label: p, path: `/${p}`, countPath: null, inMenu: true })), landing: "/x", elsewhere: "No." },
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the routes", () => {
  it("GetContributorFactories_AsksTheTeamsOwnAddress_AndReturnsTheAnswerAsSent", async () => {
    const answer = { teamId: "t/1", teamLabel: "Acme", count: 0, emptyText: "Nothing.", factories: [] };
    const fetchMock = vi.fn().mockResolvedValue(respond(answer));
    vi.stubGlobal("fetch", fetchMock);

    expect(await getContributorFactories("t/1")).toEqual(answer);
    expect(fetchMock.mock.calls[0][0]).toBe("/teams/t%2F1/contributor/factories");
  });

  it("GetContributorInbox_AsksTheTeamsOwnAddress", async () => {
    const fetchMock = vi.fn().mockResolvedValue(respond({ teamId: "a", items: [] }));
    vi.stubGlobal("fetch", fetchMock);

    await getContributorInbox("a");

    expect(fetchMock.mock.calls[0][0]).toBe("/teams/a/contributor/inbox");
  });

  it("GetCustomerPage_A404_ReturnsTheGatewaysOneSentence", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(respond({ error: "There is no factory sent to you with that name.", code: "factory_not_found" }, 404)));

    expect(await getCustomerPage("a", "secret")).toEqual({ refused: "There is no factory sent to you with that name." });
  });
});

describe("combined across teams", () => {
  it("TeamsWithPage_KeepsOnlyTheTeamsWhoseVerdictNamesThePage_InTheGatewaysOrder", () => {
    const teams = [team("b", ["factories", "inbox"]), team("x", ["questions"]), team("a", ["inbox"])];

    expect(teamsWithPage(teams, "inbox").map((t) => t.id)).toEqual(["b", "a"]);
    expect(teamsWithPage(teams, "factories").map((t) => t.id)).toEqual(["b"]);
  });

  it("CombineByOrder_SortsEveryTeamsItemsByTheGatewaysKeyAlone", () => {
    const acme = [{ order: "1|b", id: "acme-later" }, { order: "0|z", id: "acme-waiting" }];
    const beta = [{ order: "1|a", id: "beta-newest" }];

    expect(combineByOrder([acme, beta]).map((i) => i.id)).toEqual(["acme-waiting", "beta-newest", "acme-later"]);
  });

  it("ReadCombined_ReadsEachTeam_AndCombinesInTheGatewaysOrder", async () => {
    const read = vi.fn(async (teamId: string) => ({ items: [{ order: teamId === "a" ? "2" : "1", id: teamId }] }));

    const result = await readCombined([team("a", ["inbox"]), team("b", ["inbox"])], read, (r) => r.items);

    expect(read.mock.calls.map((c) => c[0])).toEqual(["a", "b"]);
    expect(result.items.map((i) => i.id)).toEqual(["b", "a"]);
  });

  it("ReadCombined_OneTeamFails_FailsTheWholeRead_RatherThanLeaveATeamOut", async () => {
    const read = vi.fn(async (teamId: string) => {
      if (teamId === "b") throw new Error("Beta could not be read");
      return { items: [] as { order: string }[] };
    });

    await expect(readCombined([team("a", ["inbox"]), team("b", ["inbox"])], read, (r) => r.items)).rejects.toThrow("Beta could not be read");
  });
});
