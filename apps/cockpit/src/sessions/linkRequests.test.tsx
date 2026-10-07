// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// A session asking to talk to another, shown on the session that asked (issue #3631). What these tests hold down: the
// asking session's card carries the amber badge and the "wants to talk to" line, and no other session's does; the open
// session's chip opens a popover that says what the session asked for; Approve sends { approve: true } and Deny sends
// { decline: true } for that request, and an answered request leaves; a refusal shows in the Gateway's words; and with
// nothing waiting nothing is drawn.

const INVESTIGATOR = "11111111-2222-3333-4444-555555555555";
const COORDINATOR = "66666666-7777-8888-9999-000000000000";

function session(id: string, name: string): SessionDto {
  return { sessionId: id, name } as unknown as SessionDto;
}

const SESSIONS = [session(INVESTIGATOR, "Investigator"), session(COORDINATOR, "Coordinator")];

const REQUEST = {
  requestId: "fedcba9876543210fedcba9876543210",
  requesterSessionId: INVESTIGATOR,
  targetSessionId: COORDINATOR,
  reason: "I need the open tickets list",
  requestedAmount: "ongoing",
  status: "pending",
  askedAtUtc: "2026-10-05T10:00:00Z",
};

const { listMessageLinkRequests, answerMessageLinkRequest } = vi.hoisted(() => ({
  listMessageLinkRequests: vi.fn(),
  answerMessageLinkRequest: vi.fn(),
}));

vi.mock("@devthrottle/client-core/fleet/messageLinksClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/fleet/messageLinksClient");
  return { ...actual, listMessageLinkRequests, answerMessageLinkRequest };
});

import { LinkRequestChip, LinkRequestsProvider, RosterLinkBadge, RosterLinkLine } from "./LinkRequests";
import { GatewayError } from "@devthrottle/client-core/api/client";

function Card({ id }: { id: string }) {
  return (
    <div data-testid={`card-${id}`}>
      <RosterLinkBadge sessionId={id} />
      <RosterLinkLine sessionId={id} />
    </div>
  );
}

function renderScreen(openSession: string = INVESTIGATOR) {
  return render(
    <LinkRequestsProvider sessions={SESSIONS}>
      <Card id={INVESTIGATOR} />
      <Card id={COORDINATOR} />
      <LinkRequestChip sessionId={openSession} />
    </LinkRequestsProvider>,
  );
}

describe("link requests on the asking session", () => {
  beforeEach(() => {
    listMessageLinkRequests.mockReset().mockResolvedValue([REQUEST]);
    answerMessageLinkRequest.mockReset();
  });
  afterEach(() => cleanup());

  it("marks the asking session's card, and only that card", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId(`card-${INVESTIGATOR}`).textContent).toContain("wants to talk to"));
    const asker = screen.getByTestId(`card-${INVESTIGATOR}`);
    expect(asker.textContent).toContain("wants to talk to Coordinator (66666666)");
    expect(asker.querySelector("[data-testid=roster-link-badge]")?.textContent).toBe("1");
    expect(screen.getByTestId(`card-${COORDINATOR}`).textContent).toBe("");
  });

  it("the chip opens a popover with the reason and what the session asked for", async () => {
    renderScreen();
    fireEvent.click(await screen.findByTestId("link-ask-chip"));
    const item = screen.getByRole("group");
    expect(item.textContent).toContain('"I need the open tickets list"');
    expect(item.textContent).toContain("Asks for: as much as they need");
    expect(screen.queryByRole("button", { name: "One message" })).toBeNull();
  });

  it.each([
    ["Approve", { approve: true }],
    ["Deny", { decline: true }],
  ])("'%s' sends that answer for that request, and the request leaves", async (label, answer) => {
    answerMessageLinkRequest.mockResolvedValue({ request: { ...REQUEST, status: "allowed" } });
    renderScreen();
    fireEvent.click(await screen.findByTestId("link-ask-chip"));
    const button = screen.getByRole("button", { name: label });

    listMessageLinkRequests.mockResolvedValue([{ ...REQUEST, status: "allowed" }]);
    fireEvent.click(button);

    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith(REQUEST.requestId, answer));
    await waitFor(() => expect(screen.queryByTestId("link-ask-chip")).toBeNull());
    expect(screen.getByTestId(`card-${INVESTIGATOR}`).textContent).toBe("");
  });

  it("shows a refusal in the popover, in the Gateway's words", async () => {
    answerMessageLinkRequest.mockRejectedValue(
      new GatewayError(409, "answer failed", { reason: "This request was already declined. Nothing changed." }),
    );
    renderScreen();
    fireEvent.click(await screen.findByTestId("link-ask-chip"));
    fireEvent.click(screen.getByRole("button", { name: "Deny" }));
    await waitFor(() => expect(screen.getByText(/already declined\. Nothing changed\./)).toBeTruthy());
  });

  it("the session that was asked for gets no chip", async () => {
    renderScreen(COORDINATOR);
    await waitFor(() => expect(listMessageLinkRequests).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId(`card-${INVESTIGATOR}`).textContent).toContain("wants to talk to"));
    expect(screen.queryByTestId("link-ask-chip")).toBeNull();
  });

  it("draws nothing when no request waits", async () => {
    listMessageLinkRequests.mockResolvedValue([]);
    renderScreen();
    await waitFor(() => expect(listMessageLinkRequests).toHaveBeenCalled());
    expect(screen.queryByTestId("link-ask-chip")).toBeNull();
    expect(screen.queryByTestId("roster-link-badge")).toBeNull();
  });
});
