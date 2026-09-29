import { afterEach, describe, expect, it, vi } from "vitest";

// The fleet-wide voice switch client (issue #1765): setVoiceModeAllSessions posts { enabled } to the
// Gateway's one fan-out endpoint and hands back the whole-fleet result. These prove the request shape
// (path, method, body), the parse of the per-session result, and that an out-of-credits 402 surfaces the
// shared credits error rather than a raw GatewayError. The connection-health module is mocked so the
// shared transport does not try to touch a real signal.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { GatewayError, getVoiceModeAllState, setVoiceModeAllSessions } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    json: async () => body,
  } as unknown as Response;
}

describe("setVoiceModeAllSessions (issue #1765)", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("POSTs { enabled } to /sessions/voice-mode/all and parses the fleet result", async () => {
    let seenUrl = "";
    let seenInit: RequestInit | undefined;
    globalThis.fetch = ((input: unknown, init?: RequestInit) => {
      seenUrl = String(input);
      seenInit = init;
      return Promise.resolve(
        jsonResponse({
          enabled: true,
          total: 3,
          changed: 2,
          skipped: 1,
          sessions: [
            { sessionId: "a", name: "Alpha", ok: true, reason: null },
            { sessionId: "b", name: "Bravo", ok: true, reason: null },
            { sessionId: "c", name: "Charlie", ok: false, reason: "SOREN_NORTH looks offline." },
          ],
        }),
      );
    }) as unknown as typeof fetch;

    const result = await setVoiceModeAllSessions(true);

    expect(seenUrl).toContain("/sessions/voice-mode/all");
    expect(seenInit?.method).toBe("POST");
    expect(JSON.parse(String(seenInit?.body))).toEqual({ enabled: true });
    expect(result.enabled).toBe(true);
    expect(result.total).toBe(3);
    expect(result.changed).toBe(2);
    expect(result.skipped).toBe(1);
    expect(result.sessions).toHaveLength(3);
    expect(result.sessions[2]).toMatchObject({ sessionId: "c", ok: false });
  });

  it("sends enabled=false for the turn-everything-off direction", async () => {
    let body: unknown;
    globalThis.fetch = ((_input: unknown, init?: RequestInit) => {
      body = JSON.parse(String(init?.body));
      return Promise.resolve(jsonResponse({ enabled: false, total: 0, changed: 0, skipped: 0, sessions: [] }));
    }) as unknown as typeof fetch;

    const result = await setVoiceModeAllSessions(false);
    expect(body).toEqual({ enabled: false });
    expect(result.enabled).toBe(false);
  });

  it("defaults missing numeric fields to 0 and a missing sessions array to empty", async () => {
    globalThis.fetch = (() => Promise.resolve(jsonResponse({ enabled: true }))) as unknown as typeof fetch;
    const result = await setVoiceModeAllSessions(true);
    expect(result.total).toBe(0);
    expect(result.changed).toBe(0);
    expect(result.skipped).toBe(0);
    expect(result.sessions).toEqual([]);
  });

  it("surfaces an out-of-credits 402 as the shared credits error, not a raw GatewayError", async () => {
    globalThis.fetch = (() =>
      Promise.resolve(jsonResponse({ error: "out of credits" }, 402))) as unknown as typeof fetch;
    await expect(setVoiceModeAllSessions(true)).rejects.toMatchObject({ name: "CreditsError", status: 402 });
  });

  it("throws a GatewayError on any other non-success status", async () => {
    globalThis.fetch = (() => Promise.resolve(jsonResponse({}, 500))) as unknown as typeof fetch;
    await expect(setVoiceModeAllSessions(true)).rejects.toBeInstanceOf(GatewayError);
  });
});

describe("getVoiceModeAllState (voice mode auto-off, step 5)", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("GETs /sessions/voice-mode/all and hands back the Gateway's line word for word", async () => {
    const line = "Voice mode switched off at 14:32 - you answered five sessions without listening";
    let seenUrl = "";
    let seenInit: RequestInit | undefined;
    globalThis.fetch = ((input: unknown, init?: RequestInit) => {
      seenUrl = String(input);
      seenInit = init;
      return Promise.resolve(jsonResponse({ enabled: false, note: line }));
    }) as unknown as typeof fetch;

    const result = await getVoiceModeAllState();

    expect(seenUrl).toContain("/sessions/voice-mode/all");
    expect(seenInit?.method).toBe("GET");
    expect(result).toEqual({ enabled: false, note: line });
  });

  it("reads no line when the Gateway sends none", async () => {
    globalThis.fetch = (() => Promise.resolve(jsonResponse({ enabled: true }))) as unknown as typeof fetch;

    expect(await getVoiceModeAllState()).toEqual({ enabled: true, note: null });
  });
});
