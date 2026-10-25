// The three answer blocks the Alerts mission moved out of the restart, secret transfer and message request cards,
// rendered against a fake Gateway. The restart tests here are the restart card's own, moved with the code they test.
//
// What these prove that a Gateway test cannot: each block offers its actions only while the Gateway says the record can
// be answered, SENDS the answer to the feature's existing route (a restart only after the in-place confirmation, a
// secret transfer with the place it was answered), and shows a refusal in the Gateway's words.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RestartRequestActions } from "./RestartRequestActions";
import { SecretTransferActions } from "./SecretTransferActions";
import { MessageRequestActions } from "./MessageRequestActions";
import { restartRequest, secretTransfer, messageRequest } from "../alertsFixtures.testkit";

let calls: { url: string; method: string; body?: string }[] = [];

/** A fake Gateway: every POST answers with the given status and body. */
function fakeGateway(status = 200, body: unknown = {}) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: init?.method ?? "GET", body: typeof init?.body === "string" ? init.body : undefined });
      return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    }),
  );
}

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("RestartRequestActions", () => {
  it("offers Accept and Decline only while the Gateway says an accept would be honoured", () => {
    const { unmount } = render(<RestartRequestActions request={restartRequest()} />);
    expect(screen.getByText("Accept")).toBeTruthy();
    expect(screen.getByText("Decline")).toBeTruthy();
    unmount();

    render(<RestartRequestActions request={restartRequest({ canAccept: false, state: "Expired" })} />);
    expect(screen.queryByText("Accept")).toBeNull();
    expect(screen.queryByText("Decline")).toBeNull();
  });

  it("accepts only after the in-place confirmation, and posts to the accept route", async () => {
    fakeGateway(200, { ...restartRequest(), state: "Accepted", canAccept: false });
    const answered = vi.fn();
    render(<RestartRequestActions request={restartRequest()} onAnswered={answered} />);

    fireEvent.click(screen.getByText("Accept"));
    // One tap is not enough: nothing has gone over the wire yet, and the promise shown is the Gateway's.
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(0);
    expect(screen.getByText("Yes, restart it")).toBeTruthy();
    expect(screen.getByText(/asks its 7 live sessions to write a handover/)).toBeTruthy();

    fireEvent.click(screen.getByText("Yes, restart it"));
    await waitFor(() => expect(answered).toHaveBeenCalled());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/machines/SOREN_NORTH/director/restart-requests/req-1/accept", method: "POST" }));
  });

  it("Not now steps back out of the confirmation without sending anything", () => {
    fakeGateway();
    render(<RestartRequestActions request={restartRequest()} />);
    fireEvent.click(screen.getByText("Accept"));
    fireEvent.click(screen.getByText("Not now"));
    expect(screen.getByText("Accept")).toBeTruthy();
    expect(calls).toHaveLength(0);
  });

  it("shows the Gateway's refusal sentence when the accept is refused", async () => {
    fakeGateway(409, {
      code: "request_expired",
      error: "request req-1 expired at 12:30 UTC and can no longer be accepted: an approval that outlives its request could restart a Director nobody currently wants restarted. Ask again.",
    });
    render(<RestartRequestActions request={restartRequest()} />);
    fireEvent.click(screen.getByText("Accept"));
    fireEvent.click(screen.getByText("Yes, restart it"));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("can no longer be accepted"));
  });

  it("declines with one tap to the decline route", async () => {
    fakeGateway(200, { ...restartRequest(), state: "Declined", canAccept: false });
    const answered = vi.fn();
    render(<RestartRequestActions request={restartRequest()} onAnswered={answered} />);
    fireEvent.click(screen.getByText("Decline"));
    await waitFor(() => expect(answered).toHaveBeenCalled());
    expect(calls).toContainEqual(expect.objectContaining({ url: "/machines/SOREN_NORTH/director/restart-requests/req-1/decline", method: "POST" }));
  });
});

describe("SecretTransferActions", () => {
  it.each([
    ["Approve", { approve: true, where: "phone" }, "phone"],
    ["Deny", { deny: true, where: "phone" }, "phone"],
    ["Approve", { approve: true, where: "cockpit" }, "cockpit"],
  ] as const)("'%s' posts %j to the transfer's answer route", async (label, sent, where) => {
    const answeredTransfer = secretTransfer({ canAnswer: false, state: "approved" });
    fakeGateway(200, { transfer: answeredTransfer, note: "" });
    const answering = vi.fn();
    const answered = vi.fn();
    render(<SecretTransferActions transfer={secretTransfer()} where={where} onAnswering={answering} onAnswered={answered} />);
    fireEvent.click(screen.getByRole("button", { name: label }));
    await waitFor(() => expect(answered).toHaveBeenCalledWith(answeredTransfer));
    expect(answering).toHaveBeenCalled();
    const post = calls.find((c) => c.method === "POST");
    expect(post?.url).toBe("/gateway/secrets/transfers/tr-1/answer");
    expect(JSON.parse(post?.body ?? "{}")).toEqual(sent);
  });

  it("offers no Approve or Deny once the Gateway says the transfer cannot be answered", () => {
    render(<SecretTransferActions transfer={secretTransfer({ canAnswer: false, state: "approved" })} where="phone" />);
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Deny" })).toBeNull();
  });

  it("shows the Gateway's refusal sentence", async () => {
    fakeGateway(409, { error: "This transfer was already approved in the cc-secrets window. Nothing changed." });
    render(<SecretTransferActions transfer={secretTransfer()} where="phone" />);
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("already approved in the cc-secrets window"));
  });
});

describe("MessageRequestActions", () => {
  it("says how much talking was asked for, and Approve posts approve to the request's answer route", async () => {
    const allowed = messageRequest({ status: "allowed" });
    fakeGateway(200, { request: allowed, link: null, note: "Approved." });
    const answered = vi.fn();
    render(<MessageRequestActions request={messageRequest()} onAnswered={answered} />);
    expect(screen.getByText("Asks for: one message and a reply")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(answered).toHaveBeenCalledWith(allowed, "Approved."));
    const post = calls.find((c) => c.method === "POST");
    expect(post?.url).toBe("/fleet/link-requests/lr-1/answer");
    expect(JSON.parse(post?.body ?? "{}")).toEqual({ approve: true });
  });

  it("Deny posts decline", async () => {
    fakeGateway(200, { request: messageRequest({ status: "declined" }), link: null, note: "Denied." });
    render(<MessageRequestActions request={messageRequest()} onAnswered={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "Deny" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST")).toBe(true));
    expect(JSON.parse(calls.find((c) => c.method === "POST")?.body ?? "{}")).toEqual({ decline: true });
  });

  it("offers no buttons once the request is answered, and shows a refusal in the Gateway's words", async () => {
    const { unmount } = render(<MessageRequestActions request={messageRequest({ status: "allowed" })} />);
    expect(screen.queryByRole("button")).toBeNull();
    unmount();

    fakeGateway(409, { error: "This request was already allowed. Nothing changed." });
    render(<MessageRequestActions request={messageRequest()} />);
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("already allowed"));
  });
});
