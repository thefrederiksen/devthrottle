// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The page a message link request's push opens (issue #4373): the shared card names both sessions in the Gateway's
// words, Approve and Refuse go through the same Gateway call as the Director and the Cockpit, a request answered
// elsewhere leaves the screen, and its notification is closed.

const { listMessageLinkRequests, answerMessageLinkRequest } = vi.hoisted(() => ({
  listMessageLinkRequests: vi.fn(),
  answerMessageLinkRequest: vi.fn(),
}));

vi.mock("@devthrottle/client-core/fleet/messageLinksClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/fleet/messageLinksClient");
  return { ...actual, listMessageLinkRequests, answerMessageLinkRequest };
});

import { LinkRequests } from "./LinkRequests";
import { closeAnsweredNotifications } from "../components/LinkRequestNotifications";

const REQUEST = {
  requestId: "req-1",
  requesterSessionId: "aaaaaaaa-0000-0000-0000-000000000121",
  targetSessionId: "bbbbbbbb-0000-0000-0000-000000000125",
  requesterLabel: "121 Fix the Release - Manager",
  targetLabel: "125 Release - Release Manager",
  reason: "I need to hand the merge over",
  requestedAmount: "once-with-reply",
  status: "pending",
  askedAtUtc: "2026-10-23T10:00:00Z",
};

function renderPage() {
  return render(
    <MemoryRouter>
      <LinkRequests />
    </MemoryRouter>,
  );
}

describe("phone message requests page", () => {
  beforeEach(() => {
    listMessageLinkRequests.mockReset().mockResolvedValue([REQUEST]);
    answerMessageLinkRequest.mockReset().mockResolvedValue({ request: { ...REQUEST, status: "allowed" }, link: null });
  });
  afterEach(() => cleanup());

  it("LinkRequests_Card_NamesBothSessionsByNumberAndName_NeverTheId", async () => {
    renderPage();
    const card = await screen.findByTestId("link-request-card");
    expect(card.textContent).toContain("121 Fix the Release - Manager");
    expect(card.textContent).toContain("125 Release - Release Manager");
    expect(card.textContent).toContain('"I need to hand the merge over"');
    expect(card.textContent).toContain("one message and a reply");
    expect(card.textContent).not.toContain("aaaaaaaa");
    expect(card.textContent).not.toContain("bbbbbbbb");
  });

  it("LinkRequests_Approve_AnswersThroughTheSameGatewayCall", async () => {
    // The Gateway lists the request as allowed once it has been answered.
    listMessageLinkRequests.mockResolvedValueOnce([REQUEST]).mockResolvedValue([{ ...REQUEST, status: "allowed" }]);
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith("req-1", { approve: true }));
    expect(await screen.findByText("Approved. The session has been told in its inbox.")).toBeTruthy();
  });

  it("LinkRequests_Refuse_DeclinesThroughTheSameGatewayCall", async () => {
    answerMessageLinkRequest.mockResolvedValue({ request: { ...REQUEST, status: "declined" }, link: null });
    listMessageLinkRequests.mockResolvedValueOnce([REQUEST]).mockResolvedValue([{ ...REQUEST, status: "declined" }]);
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Refuse" }));
    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith("req-1", { decline: true }));
    expect(await screen.findByText("Refused. The session has been told in its inbox.")).toBeTruthy();
  });

  it("LinkRequests_AnsweredElsewhere_LeavesTheScreen", async () => {
    listMessageLinkRequests.mockResolvedValue([{ ...REQUEST, status: "allowed" }]);
    renderPage();
    expect(await screen.findByText("No session is waiting for your answer.")).toBeTruthy();
    expect(screen.queryByTestId("link-request-card")).toBeNull();
  });
});

describe("closing the notification of an answered request", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("closes only link request notifications whose request no longer waits", async () => {
    const make = (tag: string) => ({ tag, close: vi.fn() });
    const answered = make("devthrottle-link-request-req-old");
    const waiting = make("devthrottle-link-request-req-1");
    const other = make("devthrottle-secret-transfer-tr-1");
    const registration = { getNotifications: vi.fn(async () => [answered, waiting, other]) };
    vi.stubGlobal("navigator", { serviceWorker: { getRegistration: vi.fn(async () => registration) } });

    const closed = await closeAnsweredNotifications(new Set(["req-1"]));

    expect(closed).toBe(1);
    expect(answered.close).toHaveBeenCalled();
    expect(waiting.close).not.toHaveBeenCalled();
    expect(other.close).not.toHaveBeenCalled();
  });
});
