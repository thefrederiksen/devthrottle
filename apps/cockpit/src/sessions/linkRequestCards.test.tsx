// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// A session asking to talk to another (issue #3548). What these tests hold down: a waiting request shows who asks,
// whom it wants to talk to and why, by name; each of the four answers sends exactly that answer for that request; an
// answered request leaves the screen; a refusal is shown on its own card; and with nothing waiting nothing is drawn.

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

import { LinkRequestCards } from "./LinkRequestCards";
import { GatewayError } from "@devthrottle/client-core/api/client";

describe("LinkRequestCards", () => {
  beforeEach(() => {
    listMessageLinkRequests.mockReset().mockResolvedValue([REQUEST]);
    answerMessageLinkRequest.mockReset();
  });
  afterEach(() => cleanup());

  it("shows who asks, whom it wants to talk to, and why", async () => {
    render(<LinkRequestCards sessions={SESSIONS} />);
    await waitFor(() => expect(screen.getByText(/wants to talk to/)).toBeTruthy());
    const card = screen.getByRole("group");
    expect(card.textContent).toContain("Investigator (11111111) wants to talk to Coordinator (66666666).");
    expect(card.textContent).toContain('"I need the open tickets list"');
  });

  it.each([
    ["One message", { amount: "once" }],
    ["One message and a reply", { amount: "once-with-reply" }],
    ["As much as they need", { amount: "ongoing" }],
    ["No", { decline: true }],
  ])("'%s' sends that answer for that request, and the card leaves", async (label, answer) => {
    answerMessageLinkRequest.mockResolvedValue({ request: { ...REQUEST, status: "allowed" } });
    render(<LinkRequestCards sessions={SESSIONS} />);
    const button = await screen.findByRole("button", { name: label });

    listMessageLinkRequests.mockResolvedValue([{ ...REQUEST, status: "allowed" }]);
    fireEvent.click(button);

    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith(REQUEST.requestId, answer));
    await waitFor(() => expect(screen.queryByRole("group")).toBeNull());
  });

  it("shows a refusal on the card, in the Gateway's words", async () => {
    answerMessageLinkRequest.mockRejectedValue(
      new GatewayError(409, "answer failed", { reason: "This request was already declined. Nothing changed." }),
    );
    render(<LinkRequestCards sessions={SESSIONS} />);
    fireEvent.click(await screen.findByRole("button", { name: "No" }));
    await waitFor(() => expect(screen.getByText(/already declined\. Nothing changed\./)).toBeTruthy());
  });

  it("draws nothing when nothing waits", async () => {
    listMessageLinkRequests.mockResolvedValue([{ ...REQUEST, status: "declined" }]);
    const { container } = render(<LinkRequestCards sessions={SESSIONS} />);
    await waitFor(() => expect(listMessageLinkRequests).toHaveBeenCalled());
    expect(container.innerHTML).toBe("");
  });
});
