// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// A session asking to talk to another, shown on the session that asked (issue #3631). The requests are read from the
// shared Alerts list now (the Alerts mission). What these tests hold down: the asking session's card carries the amber
// badge and the "wants to talk to" line in the Gateway's name for the other session, and no other session's card does;
// the open session's chip opens the request as the Alerts tray shows it; Approve sends { approve: true } and Deny sends
// { decline: true } for that request, and an answered request leaves; a refusal shows in the Gateway's words; and with
// nothing waiting nothing is drawn.

const INVESTIGATOR = "11111111-2222-3333-4444-555555555555";
const COORDINATOR = "66666666-7777-8888-9999-000000000000";

const { listAlerts, answerMessageLinkRequest } = vi.hoisted(() => ({
  listAlerts: vi.fn(),
  answerMessageLinkRequest: vi.fn(),
}));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts };
});
vi.mock("@devthrottle/client-core/fleet/messageLinksClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/fleet/messageLinksClient");
  return { ...actual, answerMessageLinkRequest };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { messageAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { LinkRequestChip, RosterLinkBadge, RosterLinkLine } from "./LinkRequests";

const REQUEST_ID = "fedcba9876543210fedcba9876543210";

function waiting() {
  return messageAlert(
    { id: `message-request:${REQUEST_ID}`, summary: "Its reason: I need the open tickets list" },
    {
      requestId: REQUEST_ID,
      requesterSessionId: INVESTIGATOR,
      targetSessionId: COORDINATOR,
      requesterLabel: "121 Investigator",
      targetLabel: "125 Coordinator",
      reason: "I need the open tickets list",
      requestedAmount: "ongoing",
    },
  );
}

function answered() {
  const a = waiting();
  return { ...a, needsAnswer: false, status: "Allowed", detail: { ...a.detail, status: "allowed" } };
}

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
    <AlertsProvider>
      <Card id={INVESTIGATOR} />
      <Card id={COORDINATOR} />
      <LinkRequestChip sessionId={openSession} />
    </AlertsProvider>,
  );
}

describe("link requests on the asking session", () => {
  beforeEach(() => {
    listAlerts.mockReset().mockResolvedValue({ alerts: [waiting()], needsAnswerCount: 1 });
    answerMessageLinkRequest.mockReset();
  });
  afterEach(() => cleanup());

  it("marks the asking session's card, and only that card", async () => {
    renderScreen();
    await waitFor(() => expect(screen.getByTestId(`card-${INVESTIGATOR}`).textContent).toContain("wants to talk to"));
    const asker = screen.getByTestId(`card-${INVESTIGATOR}`);
    expect(asker.textContent).toContain("wants to talk to 125 Coordinator");
    expect(asker.querySelector("[data-testid=roster-link-badge]")?.textContent).toBe("1");
    expect(screen.getByTestId(`card-${COORDINATOR}`).textContent).toBe("");
  });

  it("the chip opens the request as the Alerts tray shows it, with the reason once and what the session asked for", async () => {
    renderScreen();
    fireEvent.click(await screen.findByTestId("link-ask-chip"));
    const detail = screen.getByTestId("alert-detail");
    // The reason is in the Gateway's one sentence, and never quoted a second time.
    expect(detail.textContent).toContain("Its reason: I need the open tickets list");
    expect(detail.textContent?.split("I need the open tickets list")).toHaveLength(2);
    expect(detail.textContent).toContain("Asks for: as much as they need");
  });

  it.each([
    ["Approve", { approve: true }],
    ["Deny", { decline: true }],
  ])("'%s' sends that answer for that request, and the request leaves", async (label, answer) => {
    answerMessageLinkRequest.mockResolvedValue({ request: answered().detail, note: "Done." });
    renderScreen();
    fireEvent.click(await screen.findByTestId("link-ask-chip"));
    const button = screen.getByRole("button", { name: label });

    listAlerts.mockResolvedValue({ alerts: [answered()], needsAnswerCount: 0 });
    fireEvent.click(button);

    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith(REQUEST_ID, answer));
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
    await waitFor(() => expect(screen.getByTestId(`card-${INVESTIGATOR}`).textContent).toContain("wants to talk to"));
    expect(screen.queryByTestId("link-ask-chip")).toBeNull();
  });

  it("draws nothing when no request waits", async () => {
    listAlerts.mockResolvedValue({ alerts: [answered()], needsAnswerCount: 0 });
    renderScreen();
    await waitFor(() => expect(listAlerts).toHaveBeenCalled());
    expect(screen.queryByTestId("link-ask-chip")).toBeNull();
    expect(screen.queryByTestId("roster-link-badge")).toBeNull();
  });
});
