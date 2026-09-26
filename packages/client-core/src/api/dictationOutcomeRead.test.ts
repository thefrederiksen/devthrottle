import { afterEach, describe, expect, it, vi } from "vitest";
import { readDictationOutcome } from "./client";

// GET /dictation/{uploadId}/outcome (voice delivery phase 5, #3398): the client's only call for a delivery the
// Gateway owns. Read-only on the Gateway; the client reads its 200 bodies by the complete call's own rules.
// Driven against a mocked fetch that records every request.

const UPLOAD_ID = "44444444-4444-4444-4444-444444444444";

function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => body,
    text: async () => JSON.stringify(body),
    clone() {
      return this;
    },
  } as unknown as Response;
}

interface Call {
  url: string;
  method: string;
}

function mockFetch(route: (url: string) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      calls.push({ url, method: init?.method ?? "GET" });
      return route(url);
    }),
  );
  return calls;
}

describe("readDictationOutcome", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("reads the outcome route with a GET and nothing else when the Gateway is still delivering (202)", async () => {
    const calls = mockFetch(() => fakeResponse(202, { delivering: true, directorState: "waiting-for-director" }));

    const read = await readDictationOutcome(UPLOAD_ID);

    expect(read).toEqual({ kind: "delivering", directorState: "waiting-for-director" });
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("GET");
    expect(calls[0].url).toContain(`/dictation/${UPLOAD_ID}/outcome`);
  });

  it("a delivered 200 is resolved and acknowledged, as a final complete answer is", async () => {
    const calls = mockFetch((url) =>
      url.endsWith("/ack") ? fakeResponse(200, {}) : fakeResponse(200, { submitted: true, movedOn: false, transcript: "the words" }),
    );

    const read = await readDictationOutcome(UPLOAD_ID);

    expect(read.kind).toBe("resolved");
    if (read.kind !== "resolved") throw new Error("not resolved");
    expect(read.result.submitted).toBe(true);
    expect(read.result.transcript).toBe("the words");
    expect(calls.some((c) => c.url.endsWith(`/dictation/${UPLOAD_ID}/ack`) && c.method === "POST")).toBe(true);
  });

  it("a shown-back 200 carries the Gateway's reason and offer through", async () => {
    mockFetch(() =>
      fakeResponse(200, { submitted: false, movedOn: true, reason: "unconfirmed", offerSendAnyway: false, transcript: "said" }),
    );

    const read = await readDictationOutcome(UPLOAD_ID);

    if (read.kind !== "resolved") throw new Error("not resolved");
    expect(read.result.movedOn).toBe(true);
    expect(read.result.movedOnReason).toBe("unconfirmed");
    expect(read.result.offerSendAnyway).toBe(false);
    expect(read.result.transcript).toBe("said");
  });

  it("a shown-back 200 without the Gateway's offer is an error naming the upload id, and is not acknowledged", async () => {
    const calls = mockFetch(() => fakeResponse(200, { submitted: false, movedOn: true, reason: "too-old", transcript: "said" }));

    await expect(readDictationOutcome(UPLOAD_ID)).rejects.toThrow(UPLOAD_ID);
    expect(calls.some((c) => c.url.endsWith("/ack"))).toBe(false);
  });

  it("a 404 is not-found", async () => {
    mockFetch(() => fakeResponse(404, { error: "no such upload" }));

    expect(await readDictationOutcome(UPLOAD_ID)).toEqual({ kind: "not-found" });
  });

  it("any other answer is a read that did not happen, and throws", async () => {
    mockFetch(() => fakeResponse(500, { error: "boom" }));

    await expect(readDictationOutcome(UPLOAD_ID)).rejects.toThrow();
  });
});
