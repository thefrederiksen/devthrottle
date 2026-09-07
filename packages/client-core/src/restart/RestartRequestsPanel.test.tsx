// The restart-request card (issue #2725), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the card SHOWS the Gateway's sentences verbatim, offers
// the two actions only when the Gateway says an accept would be honoured, and SENDS the accept to the
// right route only after the in-card confirmation. A card that paraphrased a refusal, or accepted on one
// tap, would leave every Gateway test green while the owner saw something else.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RestartRequestCard, RestartRequestsPanel } from "./RestartRequestsPanel";
import { visibleRestartRequests, type DirectorRestartRequest } from "./restartRequests";

function request(overrides: Partial<DirectorRestartRequest> = {}): DirectorRestartRequest {
  return {
    id: "req-1",
    machine: "SOREN_NORTH",
    directorId: "d-1",
    directorName: "DevThrottle_1",
    requestedBySessionId: "11111111-2222-3333-4444-555555555555",
    requestedBySessionName: "Rig Alpha - Architect (11111111)",
    reason: "the launcher has a staged update",
    requestedAtUtc: "2026-09-07T12:00:00Z",
    expiresAtUtc: "2026-09-07T12:30:00Z",
    state: "Pending",
    stateReason: "",
    progress: "",
    liveSessionCount: 7,
    liveSessionsSentence: "That Director holds 7 live sessions right now; each will be asked to write a handover and close, leaf-first.",
    capability: {
      verdict: "CanRestart",
      reason: "SOREN_NORTH can be restarted: its launcher (version 2.1.0) holds a live command stream and declares director/restart.",
      guardedRestart: "Available",
      guardedRestartReason: "the launcher on SOREN_NORTH (version 2.1.0) declares director/restart:only-if-empty, so it will refuse a restart while its Director still holds live sessions and say how many.",
    },
    title: "Restart the Director on SOREN_NORTH?",
    askedBySentence: "Rig Alpha - Architect (11111111) asks: the launcher has a staged update",
    canAccept: true,
    ...overrides,
  };
}

let calls: { url: string; method: string }[] = [];

function fakeGateway(list: DirectorRestartRequest[], acceptStatus = 200, acceptBody: unknown = null) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      calls.push({ url, method });
      if (url === "/gateway/director-restart-requests") {
        return new Response(JSON.stringify({ requests: list }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      if (url.endsWith("/accept")) {
        return new Response(JSON.stringify(acceptBody ?? { ...list[0], state: "Accepted", canAccept: false }), {
          status: acceptStatus,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url.endsWith("/decline")) {
        return new Response(JSON.stringify({ ...list[0], state: "Declined", canAccept: false }), { status: 200, headers: { "Content-Type": "application/json" } });
      }
      throw new Error(`unexpected request: ${method} ${url}`);
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

describe("RestartRequestCard", () => {
  it("renders the Gateway's sentences verbatim, including Phase 1's reason", () => {
    render(<RestartRequestCard request={request()} />);
    expect(screen.getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy();
    expect(screen.getByText("Rig Alpha - Architect (11111111) asks: the launcher has a staged update")).toBeTruthy();
    expect(screen.getByText(/holds 7 live sessions right now/)).toBeTruthy();
    expect(screen.getByText(/declares director\/restart:only-if-empty/)).toBeTruthy();
    expect(screen.getByText("Pending")).toBeTruthy();
  });

  it("offers Accept and Decline only while the Gateway says an accept would be honoured", () => {
    const { unmount } = render(<RestartRequestCard request={request()} />);
    expect(screen.getByText("Accept")).toBeTruthy();
    expect(screen.getByText("Decline")).toBeTruthy();
    unmount();

    render(<RestartRequestCard request={request({ canAccept: false, state: "Expired", stateReason: "nobody accepted within 30 minutes" })} />);
    expect(screen.queryByText("Accept")).toBeNull();
    expect(screen.queryByText("Decline")).toBeNull();
    expect(screen.getByText("nobody accepted within 30 minutes")).toBeTruthy();
  });

  it("accepts only after the in-card confirmation, and posts to the accept route", async () => {
    fakeGateway([request()]);
    const changed = vi.fn();
    render(<RestartRequestCard request={request()} onChanged={changed} />);

    fireEvent.click(screen.getByText("Accept"));
    // One tap is not enough: nothing has gone over the wire yet.
    expect(calls.filter((c) => c.method === "POST")).toHaveLength(0);
    expect(screen.getByText("Yes, restart it")).toBeTruthy();

    fireEvent.click(screen.getByText("Yes, restart it"));
    await waitFor(() => expect(changed).toHaveBeenCalled());
    expect(calls).toContainEqual({ url: "/machines/SOREN_NORTH/director/restart-requests/req-1/accept", method: "POST" });
  });

  it("shows the Gateway's refusal sentence when the accept is refused", async () => {
    fakeGateway([request()], 409, {
      code: "request_expired",
      error: "request req-1 expired at 12:30 UTC and can no longer be accepted: an approval that outlives its request could restart a Director nobody currently wants restarted. Ask again.",
    });
    render(<RestartRequestCard request={request()} />);
    fireEvent.click(screen.getByText("Accept"));
    fireEvent.click(screen.getByText("Yes, restart it"));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("can no longer be accepted"));
  });

  it("declines with one tap to the decline route", async () => {
    fakeGateway([request()]);
    const changed = vi.fn();
    render(<RestartRequestCard request={request()} onChanged={changed} />);
    fireEvent.click(screen.getByText("Decline"));
    await waitFor(() => expect(changed).toHaveBeenCalled());
    expect(calls).toContainEqual({ url: "/machines/SOREN_NORTH/director/restart-requests/req-1/decline", method: "POST" });
  });

  it("shows the Director's progress line while the cycle runs and its reason when it stopped", () => {
    const { unmount } = render(<RestartRequestCard request={request({ state: "Accepted", canAccept: false, progress: "draining: every session is asked to write a handover and close, leaf-first" })} />);
    expect(screen.getByRole("status").textContent).toContain("draining");
    unmount();

    render(<RestartRequestCard request={request({ state: "Abandoned", canAccept: false, stateReason: "the drain stopped: seat e1d7291b never declared it finished", workspaceId: "soren-north-2026-09-07" })} />);
    expect(screen.getByText(/e1d7291b/)).toBeTruthy();
    expect(screen.getByText(/workspace soren-north-2026-09-07/)).toBeTruthy();
  });
});

describe("RestartRequestsPanel", () => {
  it("renders nothing when the Gateway holds no requests", async () => {
    fakeGateway([]);
    const { container } = render(<RestartRequestsPanel />);
    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/director-restart-requests")).toBe(true));
    expect(container.querySelector(".restart-requests")).toBeNull();
  });

  it("renders the pending request the Gateway returns", async () => {
    fakeGateway([request()]);
    render(<RestartRequestsPanel />);
    await waitFor(() => expect(screen.getByText("Restart the Director on SOREN_NORTH?")).toBeTruthy());
  });
});

describe("visibleRestartRequests", () => {
  const now = Date.parse("2026-09-07T13:00:00Z");

  it("keeps open requests, drops closed ones after thirty minutes, and keeps an unparseable close time", () => {
    const list = [
      request({ id: "open", state: "Pending" }),
      request({ id: "running", state: "Accepted", canAccept: false }),
      request({ id: "fresh", state: "Declined", canAccept: false, closedAtUtc: "2026-09-07T12:45:00Z" }),
      request({ id: "old", state: "Completed", canAccept: false, closedAtUtc: "2026-09-07T12:00:00Z" }),
      request({ id: "odd", state: "Abandoned", canAccept: false, closedAtUtc: "not a time" }),
    ];
    expect(visibleRestartRequests(list, now).map((r) => r.id)).toEqual(["open", "running", "fresh", "odd"]);
  });
});
