// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The page a message link request's push opens (issue #4373): the shared card names both sessions in the Gateway's
// words, Approve and Deny go through the same Gateway call as the Director and the Cockpit, the Gateway's sentence says
// what the answer did, a request answered elsewhere leaves the screen on the next read, and its notification is closed
// only once the Gateway lists it as answered.

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
import { LINK_REQUESTS_PANEL_POLL_MS } from "@devthrottle/client-core/fleet/LinkRequestsPanel";

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

const APPROVED_NOTE = "Approved. 121 Fix the Release - Manager may now message 125 Release - Release Manager, and has been told in its inbox.";
const DENIED_NOTE = "Denied. 121 Fix the Release - Manager has been told in its inbox.";

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
    answerMessageLinkRequest.mockReset();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

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

  it("LinkRequests_Approve_AnswersThroughTheSameGatewayCall_AndShowsTheGatewaysSentence", async () => {
    const allowed = { ...REQUEST, status: "allowed" };
    answerMessageLinkRequest.mockResolvedValue({ request: allowed, link: null, note: APPROVED_NOTE });
    listMessageLinkRequests.mockResolvedValueOnce([REQUEST]).mockResolvedValue([allowed]);
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith("req-1", { approve: true }));
    expect(await screen.findByText(APPROVED_NOTE)).toBeTruthy();
  });

  it("LinkRequests_Deny_DeclinesThroughTheSameGatewayCall_AndShowsTheGatewaysSentence", async () => {
    const declined = { ...REQUEST, status: "declined" };
    answerMessageLinkRequest.mockResolvedValue({ request: declined, link: null, note: DENIED_NOTE });
    listMessageLinkRequests.mockResolvedValueOnce([REQUEST]).mockResolvedValue([declined]);
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Deny" }));
    await waitFor(() => expect(answerMessageLinkRequest).toHaveBeenCalledWith("req-1", { decline: true }));
    expect(await screen.findByText(DENIED_NOTE)).toBeTruthy();
  });

  it("LinkRequests_AnsweredElsewhere_LeavesTheScreenOnTheNextRead", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    listMessageLinkRequests.mockResolvedValueOnce([REQUEST]).mockResolvedValue([{ ...REQUEST, status: "allowed" }]);
    renderPage();
    expect(await screen.findByTestId("link-request-card")).toBeTruthy();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(LINK_REQUESTS_PANEL_POLL_MS);
    });

    expect(await screen.findByText("No session is waiting for your answer.")).toBeTruthy();
    expect(screen.queryByTestId("link-request-card")).toBeNull();
  });

  it("LinkRequests_ARowWithoutLabels_IsAReportedErrorNotWordsOfOurOwn", async () => {
    listMessageLinkRequests.mockResolvedValue([{ ...REQUEST, requesterLabel: null }]);
    renderPage();
    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.queryByTestId("link-request-card")).toBeNull();
  });
});

describe("closing the notification of an answered request", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("closes only the notifications of requests the Gateway lists as answered", async () => {
    const make = (tag: string) => ({ tag, close: vi.fn() });
    const answered = make("devthrottle-link-request-req-old");
    const unlisted = make("devthrottle-link-request-req-brand-new");
    const other = make("devthrottle-secret-transfer-tr-1");
    const registration = { getNotifications: vi.fn(async () => [answered, unlisted, other]) };
    vi.stubGlobal("navigator", { serviceWorker: { getRegistration: vi.fn(async () => registration) } });

    const closed = await closeAnsweredNotifications(new Set(["req-old"]));

    expect(closed).toBe(1);
    expect(answered.close).toHaveBeenCalled();
    // A request the list has not shown yet - made just after the read left - keeps its fresh notification.
    expect(unlisted.close).not.toHaveBeenCalled();
    expect(other.close).not.toHaveBeenCalled();
  });
});
