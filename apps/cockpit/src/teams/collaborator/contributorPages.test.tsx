// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// A CONTRIBUTOR'S FACTORIES AND INBOX (Team implementation piece 4.1). Both pages show every team the person is a
// contributor on, together, in the Gateway's order, each item with its team's name - and every word on screen is the
// Gateway's. The words below are deliberately odd, so a label a page wrote for itself would fail.

function team(id: string, name: string): TeamSummary {
  return {
    id, name, role: "Contributor", memberCount: 2, people: "2 people",
    app: {
      full: false,
      pages: [
        { id: "factories", label: "Factories", path: "/contributor/factories", countPath: null },
        { id: "inbox", label: "Inbox", path: "/contributor/inbox", countPath: null },
      ],
      landing: "/contributor/factories",
      elsewhere: "Not for you.",
    },
  };
}

const state = vi.hoisted(() => ({
  teams: [] as TeamSummary[],
  factories: {} as Record<string, unknown>,
  inbox: {} as Record<string, unknown>,
  page: null as unknown,
  sent: [] as { teamId: string; text: string }[],
}));

vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({
  useCurrentTeam: () => ({ teams: state.teams, current: state.teams[0] ?? null }),
}));
vi.mock("@devthrottle/client-core/teams/contributorClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getContributorFactories: vi.fn(async (teamId: string) => state.factories[teamId]),
  getContributorInbox: vi.fn(async (teamId: string) => state.inbox[teamId]),
  getCustomerPage: vi.fn(async () => state.page),
}));
vi.mock("@devthrottle/client-core/teams/requestsClient", () => ({
  sendRequest: vi.fn(async (teamId: string, text: string) => {
    state.sent.push({ teamId, text });
    return { stateLabel: "Sent~odd" };
  }),
}));
vi.mock("@devthrottle/client-core/teams/teamQuestionsClient", () => ({
  getMyQuestions: vi.fn(async () => ({ waiting: [], answered: [] })),
  answerQuestion: vi.fn(),
}));
vi.mock("./TeamReportViews", () => ({ ReceivedReportView: ({ reportId }: { reportId: string }) => <div>report view {reportId}</div> }));

import { ContributorFactoriesPage } from "./ContributorFactoriesPage";
import { ContributorInboxPage } from "./ContributorInboxPage";

function card(teamId: string, teamLabel: string, factory: string, name: string, order: string, needs: string | null = null) {
  return { teamId, teamLabel, factory, name, nowText: `now~${factory}`, needsYouLabel: needs, order };
}

function item(teamId: string, teamLabel: string, id: string, title: string, order: string, kind = "report") {
  return {
    kind, id, teamId, teamLabel, title, text: kind === "message" ? `words~${id}` : null, fromLabel: `from~${id}`,
    atUtc: "2026-11-09T10:00:00Z", stateLabel: `state~${id}`, tone: "waiting", trail: [], order,
  };
}

function inbox(teamId: string, teamLabel: string, items: unknown[]) {
  return {
    teamId, teamLabel, count: items.length, emptyText: "Empty~inbox", intro: "Intro~odd", items,
    write: { label: `Write~${teamLabel}`, placeholder: "Type~odd", sendLabel: "Go~odd", note: "Note~odd" },
  };
}

function at(path: string, page: JSX.Element) {
  return render(<MemoryRouter initialEntries={[path]}>{page}</MemoryRouter>);
}

beforeEach(() => {
  state.teams = [team("acme", "Acme"), team("beta", "Beta")];
  state.sent = [];
});
afterEach(() => cleanup());

describe("Factories", () => {
  it("ContributorFactoriesPage_TwoTeams_ShowsBothTogetherInTheGatewaysOrder_WithEachTeamsLabel", async () => {
    state.factories = {
      acme: { teamId: "acme", teamLabel: "Acme~label", count: 1, emptyText: "x", factories: [card("acme", "Acme~label", "website", "Website~odd", "1|b")] },
      beta: { teamId: "beta", teamLabel: "Beta~label", count: 1, emptyText: "x", factories: [card("beta", "Beta~label", "shop", "Shop~odd", "0|a", "2 things~odd")] },
    };

    at("/contributor/factories", <ContributorFactoriesPage />);

    const cards = await screen.findAllByTestId("contributor-factory-card");
    expect(cards.map((c) => within(c).getByTestId("contributor-team-label").textContent)).toEqual(["Beta~label", "Acme~label"]);
    expect(cards[0].textContent).toContain("Shop~odd");
    expect(within(cards[0]).getByTestId("contributor-needs-you").textContent).toBe("2 things~odd");
    expect(cards[1].textContent).toContain("now~website");
  });

  it("ContributorFactoriesPage_NothingSent_ShowsTheGatewaysEmptySentence", async () => {
    state.factories = {
      acme: { teamId: "acme", teamLabel: "Acme", count: 0, emptyText: "Nothing~sent~odd", factories: [] },
      beta: { teamId: "beta", teamLabel: "Beta", count: 0, emptyText: "Nothing~sent~odd", factories: [] },
    };

    at("/contributor/factories", <ContributorFactoriesPage />);

    expect(await screen.findByText("Nothing~sent~odd")).toBeTruthy();
  });

  it("CustomerPage_RendersEveryPartInTheGatewaysWords", async () => {
    state.page = {
      teamId: "acme", teamLabel: "Acme~label", factory: "website", name: "Website~odd", writeLabel: "Write~odd",
      needsYou: { title: "Needs~odd", emptyText: "x", items: [{ reportId: "r1", title: "Menu~odd", actionLabel: "Answer~odd" }] },
      stage: { title: "Where~odd", steps: [{ label: "Idea~odd", state: "done" }, { label: "Live~odd", state: "later" }], note: "Not set~odd" },
      today: { title: "Today~odd", emptyText: "x", items: [{ text: "Version 23~odd", atUtc: "2026-11-09T14:00:00Z", timeLabel: "14:00~odd" }] },
      soFar: { title: "SoFar~odd", emptyText: "Nothing yet~odd", items: [] },
      sentToYou: { title: "Sent~odd", emptyText: "x", items: [{ reportId: "r1", title: "Menu~odd", stateLabel: "Waiting~odd", tone: "waiting" }] },
      canDo: { title: "CanDo~odd", text: "Text~odd", note: "Note~odd" },
    };

    at("/contributor/factories?team=acme&factory=website", <ContributorFactoriesPage />);

    const page = await screen.findByTestId("contributor-customer");
    for (const words of ["Website~odd", "Acme~label", "Write~odd", "Needs~odd", "Answer~odd", "Where~odd", "Idea~odd", "Not set~odd",
      "Today~odd", "Version 23~odd", "14:00~odd", "SoFar~odd", "Nothing yet~odd", "Sent~odd", "Waiting~odd", "CanDo~odd", "Text~odd", "Note~odd"]) {
      expect(page.textContent).toContain(words);
    }
    expect(screen.getByTestId("contributor-answer").getAttribute("href")).toBe("/contributor/inbox?team=acme&item=r1");
  });

  it("CustomerPage_NotTheirsToSee_ShowsTheGatewaysOneSentence", async () => {
    state.page = { refused: "No such~odd" };

    at("/contributor/factories?team=acme&factory=secret", <ContributorFactoriesPage />);

    expect((await screen.findByTestId("contributor-customer-refused")).textContent).toContain("No such~odd");
  });
});

describe("Inbox", () => {
  it("ContributorInboxPage_TwoTeams_ShowsEveryItemTogetherInTheGatewaysOrder_WithEachTeamsLabel", async () => {
    state.inbox = {
      acme: inbox("acme", "Acme~label", [item("acme", "Acme~label", "a1", "Old~odd", "1|2"), item("acme", "Acme~label", "m1", "Wrote~odd", "1|1", "message")]),
      beta: inbox("beta", "Beta~label", [item("beta", "Beta~label", "b1", "Waiting~odd", "0|9")]),
    };

    at("/contributor/inbox", <ContributorInboxPage />);

    const rows = await screen.findAllByTestId("contributor-inbox-item");
    expect(rows.map((r) => r.textContent?.includes("Waiting~odd") ? "b1" : r.textContent?.includes("Wrote~odd") ? "m1" : "a1")).toEqual(["b1", "m1", "a1"]);
    expect(rows.map((r) => within(r).getByTestId("contributor-team-label").textContent)).toEqual(["Beta~label", "Acme~label", "Acme~label"]);
    expect(within(rows[0]).getByTestId("contributor-item-state").textContent).toBe("state~b1");
    expect(screen.getByText("Intro~odd")).toBeTruthy();
  });

  it("ContributorInboxPage_OpenAMessage_ShowsTheirWordsAndTheGatewaysTrail", async () => {
    state.inbox = {
      acme: inbox("acme", "Acme", [item("acme", "Acme", "m1", "Wrote~odd", "1", "message")]),
      beta: inbox("beta", "Beta", []),
    };

    at("/contributor/inbox?team=acme&item=m1", <ContributorInboxPage />);

    expect((await screen.findByTestId("contributor-message")).textContent).toContain("words~m1");
  });

  it("ContributorInboxPage_OpenAReport_ShowsTheReportSentToThem", async () => {
    state.inbox = { acme: inbox("acme", "Acme", [item("acme", "Acme", "r9", "Report~odd", "1")]), beta: inbox("beta", "Beta", []) };

    at("/contributor/inbox?team=acme&item=r9", <ContributorInboxPage />);

    expect(await screen.findByText("report view r9")).toBeTruthy();
  });

  it("WriteBox_TwoTeams_SendsToTheChosenTeamsOwner_InTheGatewaysWords", async () => {
    state.inbox = { acme: inbox("acme", "Acme", []), beta: inbox("beta", "Beta", []) };

    at("/contributor/inbox", <ContributorInboxPage />);

    const box = await screen.findByTestId("contributor-write-box");
    fireEvent.change(within(box).getByTestId("contributor-write-team"), { target: { value: "beta" } });
    expect(box.textContent).toContain("Write~Beta");
    fireEvent.change(within(box).getByTestId("contributor-write-text"), { target: { value: "Could the menu show prices?" } });
    fireEvent.click(within(box).getByTestId("contributor-write-send"));

    await waitFor(() => expect(state.sent).toEqual([{ teamId: "beta", text: "Could the menu show prices?" }]));
    expect(await within(box).findByText("Sent~odd")).toBeTruthy();
  });
});
