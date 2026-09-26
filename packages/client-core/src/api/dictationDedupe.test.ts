import { afterEach, describe, expect, it, vi } from "vitest";
import { uploadDictationToSession, type DictationUploadArgs } from "./client";

// Client half of the durable dictation de-dupe (issue #1183): after a terminal delivered/abandoned
// outcome the client acknowledges it (best-effort, idempotent, keyed by upload id) so the Gateway can
// retire its durable tombstone, and it treats a server that DEDUPED a delivery it had already made (a
// cached-delivered outcome returned at register or complete) exactly like a fresh success. These tests
// drive uploadDictationToSession against a mocked fetch and assert the outcome AND that the ack fired.

const UPLOAD_ID = "22222222-2222-2222-2222-222222222222";

function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

function baseArgs(): DictationUploadArgs {
  return {
    sessionId: "11111111-1111-1111-1111-111111111111",
    uploadId: UPLOAD_ID,
    audio: new Blob(["hello"], { type: "audio/webm" }),
    before: "",
    after: "",
    prefix: "",
    sentAtUtc: "2026-09-25T09:05:12.345Z",
    resumed: true,
  };
}

interface Call {
  url: string;
  method: string;
}

// Install a fetch mock that records every call and answers by URL. Returns the recorded call list so a
// test can assert the ack POST fired (and that no chunk upload happened on a short-circuit path).
function mockFetch(route: (url: string, method: string) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      calls.push({ url, method });
      return route(url, method);
    }),
  );
  return calls;
}

function ackFired(calls: Call[]): boolean {
  return calls.some((c) => c.url.includes(`/dictation/${UPLOAD_ID}/ack`) && c.method === "POST");
}

describe("durable dictation de-dupe (client ack + cached-delivered handling)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("a cached-delivered outcome at REGISTER is treated as a fresh success and is acknowledged", async () => {
    // The server already delivered this upload id (our earlier response was lost). Register returns the
    // terminal outcome, so the client must NOT re-upload or re-complete - it acks and returns terminal.
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload"))
        return fakeResponse(200, { upload_id: UPLOAD_ID, terminal: true, submitted: true, transcript: "cached hello" });
      if (url.includes("/ack")) return fakeResponse(200, { ok: true, retired: true });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(true);
    expect(result.transcript).toBe("cached hello");
    expect(ackFired(calls)).toBe(true);
    // A short-circuit at register never uploads chunks nor calls complete.
    expect(calls.some((c) => c.url.includes("/complete"))).toBe(false);
    expect(calls.some((c) => c.url.includes("/chunk/"))).toBe(false);
  });

  it("a terminal delivered outcome at COMPLETE fires the ack and returns submitted", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(200, { submitted: true, movedOn: false, transcript: "live hello" });
      if (url.includes("/ack")) return fakeResponse(200, { ok: true, retired: true });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(true);
    expect(result.transcript).toBe("live hello");
    expect(ackFired(calls)).toBe(true);
  });

  it("an ABANDONED outcome at COMPLETE returns terminal+abandoned, drops nothing to submit, and acks", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(200, { dropped: true, reason: "user cancelled" });
      if (url.includes("/ack")) return fakeResponse(200, { ok: true, retired: true });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(false);
    expect(result.abandoned).toBe(true);
    expect(ackFired(calls)).toBe(true);
  });

  it("a failed ack never turns a delivered turn into an error (best-effort, idempotent)", async () => {
    // The ack fetch rejects (connection dropped). The delivered outcome must still be returned terminal;
    // the lost ack simply leaves the tombstone and a later re-complete re-acks.
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(200, { submitted: true, transcript: "held-ack hello" });
      if (url.includes("/ack")) throw new Error("network down");
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(true);
    expect(result.transcript).toBe("held-ack hello");
    expect(ackFired(calls)).toBe(true); // it was attempted, and its failure did not throw
  });
});
