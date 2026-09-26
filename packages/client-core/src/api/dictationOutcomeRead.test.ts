import { afterEach, describe, expect, it, vi } from "vitest";
import { readDictationOutcome, readPromptOutcome } from "./client";

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

  // THE HANDBACK ANSWERS (voice delivery phase 5, review round): a Gateway-driven attempt that hit something
  // only the client can act on. The outcome read answers the SAME bodies the complete path gives - never 404
  // "the server has lost track" - so each is read by the complete path's own rules.
  it("a 402 handback is out-of-credits, carrying the Gateway's mapped copy (rule 7), not acknowledged", async () => {
    const calls = mockFetch(() => fakeResponse(402, {
      error: "Out of credits", state: "NeedsCredits", text: "Out of credits",
      ctaLabel: "Add credits", ctaAction: "Url", ctaUrl: "/credits",
    }));

    const read = await readDictationOutcome(UPLOAD_ID);

    expect(read.kind).toBe("out-of-credits");
    if (read.kind !== "out-of-credits") throw new Error("not out-of-credits");
    expect(read.message).toContain("Out of transcription credits");
    expect(calls.some((c) => c.url.endsWith("/ack"))).toBe(false);
  });

  it("a 402 handback with a non-credits state shows the Gateway's own mapped copy, never credits wording", async () => {
    mockFetch(() => fakeResponse(402, {
      error: "A subscription is required", state: "SubscriptionRequired", text: "A subscription is required",
      ctaLabel: "", ctaAction: "None", ctaUrl: null,
    }));

    const read = await readDictationOutcome(UPLOAD_ID);

    if (read.kind !== "out-of-credits") throw new Error("not out-of-credits");
    expect(read.message).toContain("A subscription is required");
    expect(read.message).not.toContain("credits");
  });

  it("a 422 handback is a permanent failure with the allow-listed reason", async () => {
    mockFetch(() => fakeResponse(422, { permanent: true, reason: "unsupported-format" }));

    const read = await readDictationOutcome(UPLOAD_ID);

    expect(read).toEqual({ kind: "permanent", reason: "unsupported-format" });
  });

  it("a 409 handback is incomplete with the chunks to send again", async () => {
    mockFetch(() => fakeResponse(409, { status: "incomplete", missing: [0, 2] }));

    const read = await readDictationOutcome(UPLOAD_ID);

    expect(read).toEqual({ kind: "incomplete", missing: [0, 2] });
  });

  it("a 409 that is not an incomplete handback is the record refusal, thrown with its reason", async () => {
    mockFetch(() => fakeResponse(409, {
      error: "the delivery record is Malformed; refusing to re-open it",
      record: "Malformed", file: "C:/record.json",
    }));

    await expect(readDictationOutcome(UPLOAD_ID)).rejects.toThrow("refusing to re-open");
  });

  it("the prompt outcome route never answers a handback: its 402 still throws", async () => {
    mockFetch(() => fakeResponse(402, { error: "no credits", state: "Unavailable" }));

    await expect(readPromptOutcome("sid", UPLOAD_ID)).rejects.toThrow();
  });

  it("any other answer is a read that did not happen, and throws", async () => {
    mockFetch(() => fakeResponse(500, { error: "boom" }));

    await expect(readDictationOutcome(UPLOAD_ID)).rejects.toThrow();
  });
});
