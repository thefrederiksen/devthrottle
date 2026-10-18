import { afterEach, describe, expect, it, vi } from "vitest";

// takeWheel and handBackWheel are the hop from the Terminal tab's button to the Gateway's wheel verbs (mission
// #4070). Pinned: the route and method each sends, and that a refusal's own sentence and code survive the hop.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { GatewayError, handBackWheel, takeWheel } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: new Headers(),
    json: async () => body,
    text: async () => JSON.stringify(body),
    clone() { return this; },
  } as unknown as Response;
}

describe("the wheel verbs", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  function capture(response: Response): { url: () => string; method: () => string } {
    let url = "";
    let method = "";
    globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      url = String(input);
      method = init?.method ?? "";
      return response;
    }) as typeof fetch;
    return { url: () => url, method: () => method };
  }

  it("takeWheel posts to /sessions/{sid}/wheel/take", async () => {
    const seen = capture(jsonResponse({ accepted: true }));
    await takeWheel("a b");
    expect(seen.method()).toBe("POST");
    expect(seen.url()).toBe("/sessions/a%20b/wheel/take");
  });

  it("handBackWheel posts to /sessions/{sid}/wheel/hand-back", async () => {
    const seen = capture(jsonResponse({ accepted: true }));
    await handBackWheel("s-1");
    expect(seen.method()).toBe("POST");
    expect(seen.url()).toBe("/sessions/s-1/wheel/hand-back");
  });

  it("a refusal throws a GatewayError carrying the Gateway's sentence and code", async () => {
    const reason = "You already have the wheel of this session: its terminal is open on the conversation.";
    capture(jsonResponse({ error: reason, code: "wheel_already_held" }, 409));
    const err = await takeWheel("s-1").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(GatewayError);
    expect((err as GatewayError).serverReason).toBe(reason);
    expect((err as GatewayError).code).toBe("wheel_already_held");
    expect((err as GatewayError).status).toBe(409);
  });
});
