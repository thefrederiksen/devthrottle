import { afterEach, describe, expect, it, vi } from "vitest";

// Voice mode auto-off, step 1 (owner ruling 2026-09-28): the player tells the Gateway a narration started playing.
// A download is not a play, so this report is the only way the Gateway learns a narration was heard. These prove the
// request shape (path, method, the clip's generatedAt stamp), that pausing and resuming one clip reports it once, and
// that a failed report is not remembered - the next play of the same clip reports it again.
const health = vi.hoisted(() => ({ reachable: vi.fn(), unreachable: vi.fn() }));
vi.mock("../connection/health", () => ({
  reportGatewayReachable: () => health.reachable(),
  reportGatewayUnreachable: () => health.unreachable(),
}));

import { reportClipPlaying } from "./clips";

function jsonResponse(body: unknown, status = 200): Response {
  return {
    status,
    ok: status >= 200 && status < 300,
    json: async () => body,
    text: async () => JSON.stringify(body),
    headers: new Headers(),
  } as unknown as Response;
}

// Let the report's whole promise chain (fetch, error read, then/catch) finish before asserting.
async function settle(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe("reportClipPlaying", () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it("POSTs the clip's generatedAt to /sessions/{sid}/wingman/voice/played, once per clip", async () => {
    const calls: { url: string; init?: RequestInit }[] = [];
    globalThis.fetch = ((input: unknown, init?: RequestInit) => {
      calls.push({ url: String(input), init });
      return Promise.resolve(jsonResponse({ played: true }));
    }) as unknown as typeof fetch;

    reportClipPlaying("sid-once", "2026-09-28T10:00:00.1234567Z");
    reportClipPlaying("sid-once", "2026-09-28T10:00:00.1234567Z"); // resumed after a pause: the same clip
    await settle();

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toContain("/sessions/sid-once/wingman/voice/played");
    expect(calls[0].init?.method).toBe("POST");
    expect(JSON.parse(String(calls[0].init?.body))).toEqual({ generatedAt: "2026-09-28T10:00:00.1234567Z" });
  });

  it("reports a newer clip of the same session as its own play", async () => {
    const bodies: unknown[] = [];
    globalThis.fetch = ((_input: unknown, init?: RequestInit) => {
      bodies.push(JSON.parse(String(init?.body)));
      return Promise.resolve(jsonResponse({ played: true }));
    }) as unknown as typeof fetch;

    reportClipPlaying("sid-two", "2026-09-28T10:00:00Z");
    reportClipPlaying("sid-two", "2026-09-28T10:05:00Z");
    await settle();

    expect(bodies).toEqual([{ generatedAt: "2026-09-28T10:00:00Z" }, { generatedAt: "2026-09-28T10:05:00Z" }]);
  });

  it("reports again after a failed report", async () => {
    let calls = 0;
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    globalThis.fetch = (() => {
      calls++;
      return Promise.resolve(calls === 1 ? jsonResponse({ error: "down" }, 503) : jsonResponse({ played: true }));
    }) as unknown as typeof fetch;

    reportClipPlaying("sid-retry", "2026-09-28T10:00:00Z");
    await settle();
    reportClipPlaying("sid-retry", "2026-09-28T10:00:00Z");
    await settle();

    expect(calls).toBe(2);
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });

  it("reports again when the Gateway could not match the clip (played=false), since it may have raced the narration", async () => {
    let calls = 0;
    globalThis.fetch = (() => {
      calls++;
      return Promise.resolve(jsonResponse({ played: calls > 1 }));
    }) as unknown as typeof fetch;

    reportClipPlaying("sid-unmatched", "2026-09-28T10:00:00Z");
    await settle();
    reportClipPlaying("sid-unmatched", "2026-09-28T10:00:00Z");
    await settle();
    reportClipPlaying("sid-unmatched", "2026-09-28T10:00:00Z"); // now accepted: remembered
    await settle();

    expect(calls).toBe(2);
  });

  it("reports nothing without a session or a clip stamp", async () => {
    let calls = 0;
    globalThis.fetch = (() => {
      calls++;
      return Promise.resolve(jsonResponse({ played: true }));
    }) as unknown as typeof fetch;

    reportClipPlaying("", "2026-09-28T10:00:00Z");
    reportClipPlaying("sid-empty", "");
    await settle();

    expect(calls).toBe(0);
  });
});
