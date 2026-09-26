import { afterEach, describe, expect, it, vi } from "vitest";
import { readPromptOutcome, sendPrompt } from "./client";

// Voice delivery phase 5, contract section 7 (T2, T5): a typed prompt now carries a delivery id the Gateway mints,
// a typed send can be answered 202 "still delivering", and the client reads what became of it through
// GET /sessions/{sid}/prompts/{deliveryId}/outcome - read by the SAME reader as a recording's outcome, and
// acknowledged through POST /sessions/{sid}/prompts/{deliveryId}/ack. Driven against a mocked fetch that records
// every request.

const SID = "sess-7";
const DELIVERY_ID = "0f0e0d0c0b0a09080706050403020100";

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
  body?: string;
}

function mockFetch(route: (url: string) => Response): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string; body?: string }) => {
      const url = String(input);
      calls.push({ url, method: init?.method ?? "GET", body: init?.body });
      return route(url);
    }),
  );
  return calls;
}

describe("readPromptOutcome", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("reads the typed prompt's outcome route with a GET and nothing else while the Gateway is still delivering (202)", async () => {
    const calls = mockFetch(() => fakeResponse(202, { delivering: true, directorState: "no-answer" }));

    const read = await readPromptOutcome(SID, DELIVERY_ID);

    expect(read).toEqual({ kind: "delivering", directorState: "no-answer" });
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("GET");
    expect(calls[0].url).toContain(`/sessions/${SID}/prompts/${DELIVERY_ID}/outcome`);
  });

  it("a delivered 200 is resolved and acknowledged at the typed prompt's ack route", async () => {
    const calls = mockFetch((url) =>
      url.endsWith("/ack") ? fakeResponse(200, {}) : fakeResponse(200, { submitted: true, movedOn: false, transcript: "hi" }),
    );

    const read = await readPromptOutcome(SID, DELIVERY_ID);

    expect(read).toMatchObject({ kind: "resolved", result: { submitted: true, movedOn: false } });
    expect(calls.map((c) => `${c.method} ${c.url.slice(c.url.indexOf("/sessions"))}`)).toEqual([
      `GET /sessions/${SID}/prompts/${DELIVERY_ID}/outcome`,
      `POST /sessions/${SID}/prompts/${DELIVERY_ID}/ack`,
    ]);
  });

  it("not delivered carries the Gateway's reason, offer and words through, and is acknowledged", async () => {
    const calls = mockFetch((url) =>
      url.endsWith("/ack")
        ? fakeResponse(200, {})
        : fakeResponse(200, { submitted: false, movedOn: true, reason: "not-delivered", offerSendAnyway: true, transcript: "the text" }),
    );

    const read = await readPromptOutcome(SID, DELIVERY_ID);

    expect(read).toMatchObject({
      kind: "resolved",
      result: { movedOn: true, movedOnReason: "not-delivered", offerSendAnyway: true, transcript: "the text" },
    });
    expect(calls.some((c) => c.url.endsWith("/ack"))).toBe(true);
  });

  it("unconfirmed carries offerSendAnyway false", async () => {
    mockFetch((url) =>
      url.endsWith("/ack")
        ? fakeResponse(200, {})
        : fakeResponse(200, { submitted: false, movedOn: true, reason: "unconfirmed", offerSendAnyway: false, transcript: "t" }),
    );

    const read = await readPromptOutcome(SID, DELIVERY_ID);

    expect(read).toMatchObject({ kind: "resolved", result: { movedOnReason: "unconfirmed", offerSendAnyway: false } });
  });

  it("a shown-back answer without the Gateway's offer is an error naming the typed message, and is not acknowledged", async () => {
    const calls = mockFetch(() => fakeResponse(200, { submitted: false, movedOn: true, reason: "not-delivered", transcript: "t" }));

    await expect(readPromptOutcome(SID, DELIVERY_ID)).rejects.toThrow(`typed message ${DELIVERY_ID}`);
    expect(calls.some((c) => c.url.endsWith("/ack"))).toBe(false);
  });

  it("404 is not-found", async () => {
    mockFetch(() => fakeResponse(404, { error: "no such delivery" }));

    expect(await readPromptOutcome(SID, DELIVERY_ID)).toEqual({ kind: "not-found" });
  });

  it("any other status throws: the read did not happen", async () => {
    mockFetch(() => fakeResponse(500, { error: "boom" }));

    await expect(readPromptOutcome(SID, DELIVERY_ID)).rejects.toBeTruthy();
  });
});

describe("sendPrompt reads the Gateway's delivery id", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("a 202 on a typed prompt is delivering, with the delivery id and the Director's state", async () => {
    mockFetch(() => fakeResponse(202, { delivering: true, directorState: "no-answer", deliveryId: DELIVERY_ID }));

    expect(await sendPrompt(SID, "typed", true)).toEqual({ delivering: true, directorState: "no-answer", deliveryId: DELIVERY_ID });
  });

  it("a 200 is delivered, and still names the delivery id", async () => {
    mockFetch(() => fakeResponse(200, { accepted: true, deliveryId: DELIVERY_ID }));

    expect(await sendPrompt(SID, "typed", true)).toEqual({ delivering: false, deliveryId: DELIVERY_ID });
  });

  it("a 502 throws exactly as before", async () => {
    mockFetch(() => fakeResponse(502, { error: "refused", deliveryId: DELIVERY_ID }));

    await expect(sendPrompt(SID, "typed", true)).rejects.toMatchObject({ status: 502 });
  });

  it("a typed prompt sends no delivery id or claim of its own - the Gateway mints it", async () => {
    const calls = mockFetch(() => fakeResponse(200, {}));

    await sendPrompt(SID, "typed", true);

    const body = JSON.parse(calls[0].body ?? "{}") as Record<string, unknown>;
    expect(body).not.toHaveProperty("deliveryId");
    expect(body).not.toHaveProperty("deliveryIdClaim");
  });
});
