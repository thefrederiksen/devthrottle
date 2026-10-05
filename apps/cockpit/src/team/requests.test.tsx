// @vitest-environment jsdom

// REQUESTS IN THE COCKPIT (devthrottle_internal#2308, screen S9): the sender's page and the Owner and Managers' list.
//
// The two properties that matter beyond "it renders": every label, trail line and button comes from the Gateway's
// request verbatim (rule 7 - a button shows exactly when its verdict is true, whatever the state says), and
// "Not doing this" cannot be sent without a reason.

import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, fireEvent, within } from "@testing-library/react";
import type { TeamRequest } from "@devthrottle/client-core/teams/requestsClient";
import { GatewayError } from "@devthrottle/client-core/api/client";

function refusal(status: number, reason: string): GatewayError {
  return new GatewayError(status, reason, { reason });
}

const TEAM = "3f1d2c9e-0000-4000-8000-000000000001";

const client = {
  listMyRequests: vi.fn<(teamId: string) => Promise<TeamRequest[]>>(),
  listTeamRequests: vi.fn<(teamId: string) => Promise<TeamRequest[]>>(),
  sendRequest: vi.fn<(teamId: string, text: string) => Promise<TeamRequest>>(),
  acceptRequest: vi.fn<(teamId: string, id: string) => Promise<TeamRequest>>(),
  declineRequest: vi.fn<(teamId: string, id: string, reason: string) => Promise<TeamRequest>>(),
  markRequestDone: vi.fn<(teamId: string, id: string) => Promise<TeamRequest>>(),
};
vi.mock("@devthrottle/client-core/teams/requestsClient", () => ({
  listMyRequests: (t: string) => client.listMyRequests(t),
  listTeamRequests: (t: string) => client.listTeamRequests(t),
  sendRequest: (t: string, x: string) => client.sendRequest(t, x),
  acceptRequest: (t: string, id: string) => client.acceptRequest(t, id),
  declineRequest: (t: string, id: string, r: string) => client.declineRequest(t, id, r),
  markRequestDone: (t: string, id: string) => client.markRequestDone(t, id),
}));

import { RequestsView, TeamRequestsView } from "./RequestsView";

function request(overrides: Partial<TeamRequest> = {}): TeamRequest {
  return {
    id: "r1",
    text: "Export the event log as CSV from the dashboard",
    state: "sent",
    stateLabel: "Sent",
    sentBy: "You",
    isYours: true,
    sentAtUtc: "2026-09-29T10:00:00Z",
    updatedAtUtc: "2026-09-29T10:00:00Z",
    trail: [{ state: "sent", label: "Sent", by: "You", atUtc: "2026-09-29T10:00:00Z", reason: null, sentence: "Sent by You" }],
    canAccept: false,
    canDecline: false,
    canMarkDone: false,
    ...overrides,
  };
}

const declined = request({
  id: "r2",
  text: "Dark mode for the report viewer",
  state: "declined",
  stateLabel: "Not doing this",
  trail: [
    { state: "sent", label: "Sent", by: "You", atUtc: "2026-09-22T10:00:00Z", reason: null, sentence: "Sent by You" },
    {
      state: "declined", label: "Not doing this", by: "priya@acme.example", atUtc: "2026-09-23T10:00:00Z",
      reason: "Not this quarter: the viewer is being replaced in November.", sentence: "Not doing this - priya@acme.example",
    },
  ],
});

beforeEach(() => {
  for (const fn of Object.values(client)) fn.mockReset();
});
afterEach(() => cleanup());

describe("the sender's Requests page", () => {
  it("shows each request's trail and, for Not doing this, who said so and why", async () => {
    const accepted = request({
      state: "accepted",
      stateLabel: "Accepted",
      trail: [
        { state: "sent", label: "Sent", by: "You", atUtc: "2026-09-29T10:00:00Z", reason: null, sentence: "Sent by You" },
        { state: "accepted", label: "Accepted", by: "priya@acme.example", atUtc: "2026-09-30T10:00:00Z", reason: null, sentence: "Accepted by priya@acme.example" },
      ],
    });
    client.listMyRequests.mockResolvedValue([accepted, declined]);

    render(<RequestsView teamId={TEAM} />);

    await screen.findByText("Accepted by priya@acme.example");
    expect(client.listMyRequests).toHaveBeenCalledWith(TEAM);
    const card = screen.getByRole("article", { name: "Dark mode for the report viewer" });
    expect(within(card).getAllByText("Not doing this").length).toBeGreaterThan(0);
    expect(within(card).getByText("Not doing this - priya@acme.example")).toBeTruthy();
    expect(within(card).getByRole("note").textContent).toContain("Not this quarter: the viewer is being replaced in November.");
    // The sender decides nothing: no decision button on their own page.
    expect(screen.queryByRole("button", { name: "Accept" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Not doing this" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
  });

  it("sends the words as written and shows the request in the list", async () => {
    client.listMyRequests.mockResolvedValueOnce([]).mockResolvedValueOnce([request()]);
    client.sendRequest.mockResolvedValue(request());

    render(<RequestsView teamId={TEAM} />);
    await screen.findByText(/No requests from you yet/);
    const send = screen.getByRole("button", { name: "Send request" }) as HTMLButtonElement;
    expect(send.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/What would you like/), { target: { value: "  Export the event log as CSV from the dashboard " } });
    fireEvent.click(send);

    await screen.findByText(/Sent\. The team's Owner and Managers see it/);
    expect(client.sendRequest).toHaveBeenCalledWith(TEAM, "Export the event log as CSV from the dashboard");
    expect(await screen.findByRole("article", { name: "Export the event log as CSV from the dashboard" })).toBeTruthy();
  });

  it("shows the Gateway's own refusal when a send fails", async () => {
    client.listMyRequests.mockResolvedValue([]);
    client.sendRequest.mockRejectedValue(refusal(400, "A request can be at most 4000 characters. This one is 4001."));

    render(<RequestsView teamId={TEAM} />);
    await screen.findByText(/No requests from you yet/);
    fireEvent.change(screen.getByLabelText(/What would you like/), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: "Send request" }));

    expect(await screen.findByText(/at most 4000 characters/)).toBeTruthy();
  });
});

describe("the Owner and Managers' Requests list", () => {
  it("shows exactly the buttons the Gateway's verdicts allow, whatever the state", async () => {
    client.listTeamRequests.mockResolvedValue([
      request({ id: "a", text: "First", sentBy: "carla@client.example", isYours: false, canAccept: true, canDecline: true, canMarkDone: true }),
      // A state the page might think allows Accept - but the verdict says no, so there is no Accept button.
      request({ id: "b", text: "Second", sentBy: "colin@client.example", isYours: false, canAccept: false, canDecline: true, canMarkDone: true }),
      request({ id: "c", text: "Third", sentBy: "colin@client.example", isYours: false }),
    ]);

    render(<TeamRequestsView teamId={TEAM} />);

    const first = await screen.findByRole("article", { name: "First" });
    expect(within(first).getByText(/carla@client\.example/)).toBeTruthy();
    expect(within(first).getAllByRole("button").map((b) => b.textContent)).toEqual(["Accept", "Not doing this", "Done"]);
    expect(within(screen.getByRole("article", { name: "Second" })).getAllByRole("button").map((b) => b.textContent)).toEqual(["Not doing this", "Done"]);
    expect(within(screen.getByRole("article", { name: "Third" })).queryAllByRole("button")).toEqual([]);
  });

  it("accepts a request and replaces its card with the Gateway's answer", async () => {
    client.listTeamRequests.mockResolvedValue([request({ sentBy: "carla@client.example", isYours: false, canAccept: true, canDecline: true, canMarkDone: true })]);
    client.acceptRequest.mockResolvedValue(request({
      sentBy: "carla@client.example", isYours: false, state: "accepted", stateLabel: "Accepted", canDecline: true, canMarkDone: true,
      trail: [
        { state: "sent", label: "Sent", by: "carla@client.example", atUtc: "2026-09-29T10:00:00Z", reason: null, sentence: "Sent by carla@client.example" },
        { state: "accepted", label: "Accepted", by: "You", atUtc: "2026-09-30T10:00:00Z", reason: null, sentence: "Accepted by You" },
      ],
    }));

    render(<TeamRequestsView teamId={TEAM} />);
    fireEvent.click(await screen.findByRole("button", { name: "Accept" }));

    await screen.findByText("Accepted by You");
    expect(client.acceptRequest).toHaveBeenCalledWith(TEAM, "r1");
    expect(screen.queryByRole("button", { name: "Accept" })).toBeNull();
  });

  it("after a refused decision, shows the refusal and reads the list again, so only the buttons now allowed remain", async () => {
    const before = request({ sentBy: "carla@client.example", isYours: false, canAccept: true, canDecline: true, canMarkDone: true });
    const now = request({
      sentBy: "carla@client.example", isYours: false, state: "accepted", stateLabel: "Accepted", canDecline: true, canMarkDone: true,
      trail: [
        { state: "sent", label: "Sent", by: "carla@client.example", atUtc: "2026-09-29T10:00:00Z", reason: null, sentence: "Sent by carla@client.example" },
        { state: "accepted", label: "Accepted", by: "olivia@acme.example", atUtc: "2026-09-30T10:00:00Z", reason: null, sentence: "Accepted by olivia@acme.example" },
      ],
    });
    client.listTeamRequests.mockResolvedValueOnce([before]).mockResolvedValueOnce([now]);
    client.acceptRequest.mockRejectedValueOnce(refusal(409, "This request has already been accepted."));

    render(<TeamRequestsView teamId={TEAM} />);
    fireEvent.click(await screen.findByRole("button", { name: "Accept" }));

    expect(await screen.findByText("This request has already been accepted.")).toBeTruthy();
    expect(await screen.findByText("Accepted by olivia@acme.example")).toBeTruthy();
    expect(client.listTeamRequests).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("button", { name: "Accept" })).toBeNull();
    expect(screen.getByRole("button", { name: "Not doing this" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Done" })).toBeTruthy();
  });

  it("will not mark Not doing this until a reason is written, then sends the reason", async () => {
    client.listTeamRequests.mockResolvedValue([request({ sentBy: "carla@client.example", isYours: false, canAccept: true, canDecline: true, canMarkDone: true })]);
    client.declineRequest.mockResolvedValue({ ...declined, id: "r1", sentBy: "carla@client.example", isYours: false });

    render(<TeamRequestsView teamId={TEAM} />);
    fireEvent.click(await screen.findByRole("button", { name: "Not doing this" }));

    const confirm = screen.getByRole("button", { name: "Mark Not doing this" }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText(/Why is this not being done/), { target: { value: "   " } });
    expect(confirm.disabled).toBe(true);
    expect(client.declineRequest).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText(/Why is this not being done/), { target: { value: " Not this quarter. " } });
    fireEvent.click(confirm);

    await waitFor(() => expect(client.declineRequest).toHaveBeenCalledWith(TEAM, "r1", "Not this quarter."));
    expect(await screen.findByText("Not doing this - priya@acme.example")).toBeTruthy();
    // The decision is made: the reason box closes with it.
    expect(screen.queryByRole("button", { name: "Mark Not doing this" })).toBeNull();
    expect(screen.queryByLabelText(/Why is this not being done/)).toBeNull();
  });

  it("marks a request Done", async () => {
    client.listTeamRequests.mockResolvedValue([request({ isYours: false, canAccept: true, canDecline: true, canMarkDone: true })]);
    client.markRequestDone.mockResolvedValue(request({ state: "done", stateLabel: "Done" }));

    render(<TeamRequestsView teamId={TEAM} />);
    fireEvent.click(await screen.findByRole("button", { name: "Done" }));

    await waitFor(() => expect(client.markRequestDone).toHaveBeenCalledWith(TEAM, "r1"));
    await waitFor(() => expect(screen.queryAllByRole("button")).toEqual([]));
  });

  it("shows the Gateway's refusal when the list cannot be read, with a way to try again", async () => {
    client.listTeamRequests.mockRejectedValueOnce(refusal(403, "In this team you are a Developer, and a Developer may not read the team's Requests list."));

    render(<TeamRequestsView teamId={TEAM} />);

    expect(await screen.findByText(/a Developer may not read the team's Requests list/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Try again" })).toBeTruthy();
  });
});
