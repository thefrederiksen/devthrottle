import { afterEach, describe, expect, it, vi } from "vitest";
import { sendPrompt, uploadDictationToSession, type DictationUploadArgs } from "./client";

// The client half of the phase 2 wire contract (voice delivery, #3398):
//   - the complete call carries the Send time as `sentAtUtc`, and neither the register nor the complete
//     call carries the old byte baseline;
//   - a 202 answer is "still delivering": not terminal, not acknowledged, not a failure;
//   - a moved-on answer carries its `reason` through to the driver, at complete and at register.
// Driven against a mocked fetch that records every request body.

const UPLOAD_ID = "33333333-3333-3333-3333-333333333333";
const SENT_AT_UTC = "2026-09-25T09:05:12.345Z";

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
    sentAtUtc: SENT_AT_UTC,
    resumed: false,
  };
}

interface Call {
  url: string;
  method: string;
  body: Record<string, unknown> | undefined;
}

function mockFetch(route: (url: string) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string; body?: unknown }) => {
      const url = String(input);
      const body = typeof init?.body === "string" ? (JSON.parse(init.body) as Record<string, unknown>) : undefined;
      calls.push({ url, method: init?.method ?? "GET", body });
      return route(url);
    }),
  );
  return calls;
}

describe("the Send time and the still-delivering answer on the wire (#3398)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("the complete call carries sentAtUtc, and neither register nor complete carries baselineBufferBytes", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(200, { submitted: true, movedOn: false, transcript: "hi" });
      return fakeResponse(200, { ok: true });
    });
    // A caller holding an old durable record could still pass the field through a loose object; the wire
    // must not carry it.
    const args = { ...baseArgs(), baselineBufferBytes: 48213 } as DictationUploadArgs;

    await uploadDictationToSession(args);

    const register = calls.find((c) => c.url.includes("/dictation/upload"));
    const complete = calls.find((c) => c.url.includes("/complete"));
    expect(complete?.body?.sentAtUtc).toBe(SENT_AT_UTC);
    expect(register?.body).toBeDefined();
    expect(register?.body).not.toHaveProperty("baselineBufferBytes");
    expect(complete?.body).not.toHaveProperty("baselineBufferBytes");
  });

  it("a 202 answer is still delivering: not terminal, not acknowledged, and says so", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) return fakeResponse(202, { delivering: true, directorState: "no-answer" });
      return fakeResponse(200, { ok: true });
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.submitted).toBe(false);
    expect(result.delivering).toBe(true);
    expect(result.directorState).toBe("no-answer");
    expect(result.error).toBeUndefined(); // not a failure
    // Not resolved, so the Gateway's record must not be retired.
    expect(calls.some((c) => c.url.includes("/ack"))).toBe(false);
  });

  it("a moved-on answer at COMPLETE carries its reason", async () => {
    mockFetch((url) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete"))
        return fakeResponse(200, { submitted: false, movedOn: true, reason: "too-old", transcript: "old words" });
      return fakeResponse(200, { ok: true });
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.movedOn).toBe(true);
    expect(result.movedOnReason).toBe("too-old");
    expect(result.transcript).toBe("old words");
  });

  it("a moved-on answer at REGISTER (a cached tombstone) carries its reason", async () => {
    mockFetch((url) => {
      if (url.includes("/dictation/upload"))
        return fakeResponse(200, { upload_id: UPLOAD_ID, terminal: true, movedOn: true, reason: "session-exited", transcript: "w" });
      return fakeResponse(200, { ok: true });
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.movedOn).toBe(true);
    expect(result.movedOnReason).toBe("session-exited");
  });
});

describe("Send anyway through the prompt route answers 202 still delivering (contract section 4)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("sendPrompt reports a 202 as delivering, and a 200 as not", async () => {
    let status = 202;
    mockFetch(() => (status === 202 ? fakeResponse(202, { delivering: true, directorState: "no-answer" }) : fakeResponse(200, {})));

    const pending = await sendPrompt("sid", "words", true, undefined, undefined, undefined, UPLOAD_ID);
    status = 200;
    const done = await sendPrompt("sid", "words", true, undefined, undefined, undefined, UPLOAD_ID);

    expect(pending).toEqual({ delivering: true, directorState: "no-answer" });
    expect(done).toEqual({ delivering: false });
  });
});
