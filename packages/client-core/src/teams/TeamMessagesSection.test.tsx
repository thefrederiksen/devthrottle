import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { TeamRequest, TeamRequestList } from "./requestsClient";
import type { MyTeamsAnswer, TeamSummary } from "./teamsClient";

// THE TEAM OWNER'S MESSAGES FROM CONTRIBUTORS (Team piece 4.3) against Gateway answers whose words are deliberately odd: a
// card that writes a team label, a sender, a state, a trail line, the forward control's words or a factory name of its
// own fails here (rule 7). Which teams take part, and whether a card may be forwarded, are the Gateway's too.

const gw = vi.hoisted(() => ({
  teams: null as (() => Promise<MyTeamsAnswer>) | null,
  list: null as ((teamId: string) => Promise<TeamRequestList>) | null,
  forward: null as ((teamId: string, id: string, factory: string, note: string) => Promise<TeamRequest>) | null,
}));
vi.mock("./teamsClient", () => ({ getMyTeams: vi.fn(() => gw.teams!()) }));
vi.mock("./requestsClient", () => ({
  getTeamRequestList: vi.fn((teamId: string) => gw.list!(teamId)),
  forwardRequest: vi.fn((teamId: string, id: string, factory: string, note: string) => gw.forward!(teamId, id, factory, note)),
}));

import { forwardRequest, getTeamRequestList } from "./requestsClient";
import { TeamMessagesSection } from "./TeamMessagesSection";

function team(id: string, pages: string[]): TeamSummary {
  return {
    id,
    name: `Team ${id}`,
    role: "Owner (gateway)",
    memberCount: 2,
    people: "2 people",
    app: { full: true, pages: pages.map((p) => ({ id: p, label: `${p} label (gateway)`, path: "/x", countPath: null })), landing: null, elsewhere: null },
  };
}

function request(overrides: Partial<TeamRequest> = {}): TeamRequest {
  return {
    id: "r-1",
    text: "Could the menu show prices? (contributor words)",
    state: "sent",
    stateLabel: "Waiting on you (gateway)",
    sentBy: "carla@client.example",
    fromLabel: "From Carla (gateway)",
    privacyNote: "No agent has seen this (gateway)",
    isYours: false,
    sentAtUtc: "2026-11-09T14:05:00Z",
    updatedAtUtc: "2026-11-09T14:05:00Z",
    trail: [{ state: "sent", label: "Sent", by: "Carla", atUtc: "2026-11-09T14:05:00Z", reason: null, sentence: "Sent by Carla (gateway)" }],
    canAccept: true,
    canDecline: true,
    canMarkDone: true,
    canForward: true,
    ...overrides,
  };
}

function list(overrides: Partial<TeamRequestList> = {}): TeamRequestList {
  return {
    teamId: "acme",
    teamLabel: "Acme label (gateway)",
    count: 1,
    requests: [request()],
    forward: {
      label: "Send it on (gateway)",
      noteLabel: "Your words first (gateway)",
      hint: "Quoted and marked (gateway)",
      factories: [
        { factory: "website", name: "Erik's website (gateway)" },
        { factory: "menu", name: "Menu factory (gateway)" },
      ],
      emptyText: null,
    },
    ...overrides,
  };
}

beforeEach(() => {
  gw.teams = () => Promise.resolve({ kind: "teams", teams: [team("acme", ["questions", "messages"])], start: { where: "own-account" } });
  gw.list = () => Promise.resolve(list());
  gw.forward = () => Promise.reject(new Error("no forward staged"));
  vi.mocked(getTeamRequestList).mockClear();
  vi.mocked(forwardRequest).mockClear();
});

afterEach(cleanup);

describe("TeamMessagesSection", () => {
  it("TeamMessagesSection_AnOwnedTeam_PrintsTheGatewaysHeadingTeamSenderWordsStateNoteAndTrail", async () => {
    render(<TeamMessagesSection />);

    const card = await screen.findByTestId("team-message");
    expect(screen.getByRole("heading", { name: "messages label (gateway)" })).toBeTruthy();
    expect(within(card).getByTestId("team-message-team").textContent).toBe("Acme label (gateway)");
    expect(within(card).getByText("From Carla (gateway)")).toBeTruthy();
    expect(within(card).getByText("Could the menu show prices? (contributor words)")).toBeTruthy();
    expect(within(card).getByTestId("team-message-state").textContent).toBe("Waiting on you (gateway)");
    expect(within(card).getByText("No agent has seen this (gateway)")).toBeTruthy();
    expect(within(card).getByText(/Sent by Carla \(gateway\)/)).toBeTruthy();
    expect(within(card).getByTestId("team-message-forward").textContent).toBe("Send it on (gateway)");
  });

  it("TeamMessagesSection_OnlyTheTeamsTheGatewayNames_AreRead", async () => {
    gw.teams = () =>
      Promise.resolve({
        kind: "teams",
        teams: [team("acme", ["messages"]), team("other", ["questions", "requests", "reports"])],
        start: { where: "own-account" },
      });

    render(<TeamMessagesSection />);

    await screen.findByTestId("team-message");
    expect(vi.mocked(getTeamRequestList).mock.calls.map((c) => c[0])).toEqual(["acme"]);
  });

  it("TeamMessagesSection_NoOwnedTeamOrTeamsOff_ShowsNothing", async () => {
    gw.teams = () => Promise.resolve({ kind: "not-offered", reason: "off (gateway)" });

    const { container } = render(<TeamMessagesSection />);

    await waitFor(() => expect(container.querySelector("[data-testid='team-messages']")).toBeNull());
    expect(getTeamRequestList).not.toHaveBeenCalled();
  });

  it("TeamMessageCard_Forward_SendsTheChosenFactoryAndNoteAndShowsTheGatewaysAnswer", async () => {
    const forwarded = request({
          state: "forwarded",
          stateLabel: "Sent on (gateway)",
          canForward: false,
          privacyNote: null,
          trail: [
            ...request().trail,
            { state: "forwarded", label: "Forwarded", by: "You", atUtc: "2026-11-09T15:00:00Z", reason: null, sentence: "Sent on by you (gateway)", forwardedTo: "Menu factory (gateway)" },
          ],
        });
    // After the forward the Gateway's list says the same as its answer did.
    gw.forward = () => {
      gw.list = () => Promise.resolve(list({ requests: [forwarded] }));
      return Promise.resolve(forwarded);
    };
    render(<TeamMessagesSection />);
    fireEvent.click(await screen.findByTestId("team-message-forward"));

    fireEvent.change(screen.getByTestId("team-message-factory"), { target: { value: "menu" } });
    fireEvent.change(screen.getByTestId("team-message-note"), { target: { value: "  Worth doing.  " } });
    expect(screen.getByText("Quoted and marked (gateway)")).toBeTruthy();
    fireEvent.click(screen.getByTestId("team-message-forward-send"));

    await waitFor(() => expect(screen.getByTestId("team-message-state").textContent).toBe("Sent on (gateway)"));
    expect(forwardRequest).toHaveBeenCalledWith("acme", "r-1", "menu", "Worth doing.");
    expect(screen.getByText(/Sent on by you \(gateway\)/)).toBeTruthy();
    expect(screen.getByText(/Menu factory \(gateway\)/)).toBeTruthy();
    expect(screen.queryByTestId("team-message-forward")).toBeNull();
  });

  it("TeamMessageCard_TheGatewayRefusesTheForward_ShowsItsSentence", async () => {
    const { GatewayError } = await import("../api/client");
    gw.forward = () => Promise.reject(new GatewayError(403, "Only the Owner (gateway)", { reason: "Only the Owner (gateway)" }));
    render(<TeamMessagesSection />);
    fireEvent.click(await screen.findByTestId("team-message-forward"));

    fireEvent.click(screen.getByTestId("team-message-forward-send"));

    expect((await screen.findByRole("alert")).textContent).toContain("Only the Owner (gateway)");
  });

  it("TeamMessageCard_NotForwardable_OffersNoForward", async () => {
    gw.list = () => Promise.resolve(list({ requests: [request({ canForward: false, privacyNote: null })] }));

    render(<TeamMessagesSection />);

    await screen.findByTestId("team-message");
    expect(screen.queryByTestId("team-message-forward")).toBeNull();
  });

  it("TeamMessageCard_NoFactoryToForwardTo_PrintsTheGatewaysSentence", async () => {
    gw.list = () => Promise.resolve(list({ forward: { ...list().forward, factories: [], emptyText: "No factory yet (gateway)" } }));

    render(<TeamMessagesSection />);

    expect((await screen.findByTestId("team-message-cannot-forward")).textContent).toBe("No factory yet (gateway)");
    expect(screen.queryByTestId("team-message-forward")).toBeNull();
  });
});
