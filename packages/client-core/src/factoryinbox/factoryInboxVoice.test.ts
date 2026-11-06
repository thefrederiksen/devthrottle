import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { discardFactoryRecording, sendFactoryRecording, type FactoryRecording } from "./factoryInboxVoice";

// THE DURABLE SPOKEN SEND (Factory Inbox mission, phase 4) against a stand-in Gateway: it registers the recording under
// its own id, completes first and uploads only the chunks the Gateway names as missing, and acknowledges only a final
// answer. A failure that can pass on a retry is HELD (the recording stays); a refusal is REFUSED in the Gateway's words;
// "nothing was heard" is final. It never sends words for a recording - only audio (the verbatim rule).

const ID = "33333333-3333-4333-8333-333333333333";

interface Call {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: unknown;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function mockGateway(route: (url: string, method: string, calls: Call[]) => Response | Promise<Response>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      calls.push({ url, method, headers: (init?.headers ?? {}) as Record<string, string>, body: init?.body });
      return route(url, method, calls);
    }),
  );
  return calls;
}

function recording(): FactoryRecording {
  return {
    id: ID,
    factory: "zeta-works",
    note: "bad",
    blob: new Blob(["some-audio-bytes"], { type: "audio/webm" }),
    sentAt: Date.UTC(2026, 10, 5, 9, 2, 0),
  };
}

const left = { message: { id: "m-9", text: "the Gateway's words" } };

beforeEach(() => {
  // No browser storage in this test: deleting and keeping the saved copy are no-ops here.
  vi.stubGlobal("indexedDB", undefined);
});

afterEach(() => vi.unstubAllGlobals());

describe("sendFactoryRecording", () => {
  it("sendFactoryRecording_GatewayMissingTheChunk_UploadsItThenLeavesTheMessageAndAcknowledges", async () => {
    let completes = 0;
    const calls = mockGateway((url, method) => {
      if (url.endsWith("/gateway/factory/zeta-works/inbox/voice") && method === "POST") return json(200, { upload_id: ID });
      if (url.includes(`/inbox/voice/${ID}/complete`)) return ++completes === 1 ? json(409, { missing: [0] }) : json(201, left);
      if (url.includes(`/dictation/${ID}/chunk/0`) && method === "PUT") return json(200, { ok: true, index: 0 });
      if (url.includes(`/dictation/${ID}/ack`)) return json(200, { ok: true, retired: true });
      return json(404, { error: `unexpected ${method} ${url}` });
    });

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("left");
    expect(outcome.kind === "left" && outcome.message?.id).toBe("m-9");
    const register = calls[0];
    expect(register.headers["Idempotency-Key"]).toBe(ID);
    const chunk = calls.find((c) => c.method === "PUT")!;
    expect(chunk.headers["X-Chunk-Sha256"]).toMatch(/^[0-9a-f]{64}$/);
    // The complete carries the note and the Send time - and no words.
    const complete = JSON.parse(String(calls.find((c) => c.url.includes("/complete"))!.body)) as Record<string, unknown>;
    expect(complete).toMatchObject({ totalChunks: 1, note: "bad", sentAtUtc: "2026-11-05T09:02:00.000Z" });
    expect(complete).not.toHaveProperty("text");
    expect(complete).not.toHaveProperty("rawText");
    expect(calls.some((c) => c.url.includes(`/dictation/${ID}/ack`))).toBe(true);
  });

  it("sendFactoryRecording_AlreadyLeft_IsLeftWithoutUploadingAgain", async () => {
    const calls = mockGateway((url) =>
      url.includes("/ack") ? json(200, { ok: true }) : json(200, { upload_id: ID, terminal: true, delivered: true }),
    );

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("left");
    expect(calls.some((c) => c.method === "PUT" || c.url.includes("/complete"))).toBe(false);
  });

  it("sendFactoryRecording_AbandonedByTheGateway_IsNothingInTheGatewaysWordsNotLeft", async () => {
    const line = "zz-the-gateway-dropped-it-zz";
    const calls = mockGateway((url) =>
      url.includes("/ack") ? json(200, { ok: true }) : json(200, { upload_id: ID, terminal: true, delivered: false, line }),
    );

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("nothing");
    expect(outcome.kind === "nothing" && outcome.error.serverReason).toBe(line);
    expect(calls.some((c) => c.method === "PUT" || c.url.includes("/complete"))).toBe(false);
  });

  it("sendFactoryRecording_HeldPastTheGatewaysSweep_IsReopenedAndLeft", async () => {
    // The Gateway keeps a held factory recording past its 24-hour sweep, so the retry the next day is answered with the
    // upload id, not a final answer, and the saved copy is left.
    mockGateway((url) => {
      if (url.endsWith("/inbox/voice")) return json(200, { upload_id: ID });
      if (url.includes("/complete")) return json(201, left);
      return json(200, { ok: true });
    });

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("left");
  });

  it("sendFactoryRecording_TranscriptionFailsForNow_IsHeldAndNotAcknowledged", async () => {
    const calls = mockGateway((url) =>
      url.includes("/complete")
        ? json(503, { error: "The recording could not be transcribed just now. It is kept, and sending it will retry by itself.", retryable: true })
        : json(200, { upload_id: ID }),
    );

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("held");
    expect(calls.some((c) => c.url.includes("/ack"))).toBe(false);
  });

  it("sendFactoryRecording_NoConnection_IsHeld", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new TypeError("Failed to fetch"))));

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("held");
  });

  it("sendFactoryRecording_ArchivedFactory_IsRefusedInTheGatewaysWords", async () => {
    mockGateway(() =>
      json(409, { error: "'zeta-works' is archived, so it takes no messages. Restore it from the Factories screen first.", code: "factory_archived" }),
    );

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("refused");
    expect(outcome.kind === "refused" && outcome.error.serverReason).toBe(
      "'zeta-works' is archived, so it takes no messages. Restore it from the Factories screen first.",
    );
  });

  it("sendFactoryRecording_NothingWasHeard_IsFinalAndAcknowledged", async () => {
    const calls = mockGateway((url) => {
      if (url.includes("/complete")) return json(422, { error: "Nothing was heard in the recording, so nothing was left in the factory inbox.", code: "message_empty" });
      if (url.includes("/ack")) return json(200, { ok: true });
      return json(200, { upload_id: ID });
    });

    const outcome = await sendFactoryRecording(recording());

    expect(outcome.kind).toBe("nothing");
    expect(calls.some((c) => c.url.includes("/ack"))).toBe(true);
  });
});

describe("discardFactoryRecording", () => {
  it("discardFactoryRecording_GatewayAgrees_TellsTheGatewayToStopHoldingIt", async () => {
    const calls = mockGateway(() => json(200, { ok: true, upload_id: ID, abandoned: true }));

    await discardFactoryRecording(ID);

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].url).toContain(`/dictation/${ID}/abandon`);
  });

  it("discardFactoryRecording_GatewayCannotBeTold_ThrowsSoTheRecordingIsKept", async () => {
    mockGateway(() => json(409, { error: "The Gateway cannot read what it recorded (gateway).", code: "recording_record_unreadable" }));

    await expect(discardFactoryRecording(ID)).rejects.toMatchObject({ serverReason: "The Gateway cannot read what it recorded (gateway)." });
  });
});
