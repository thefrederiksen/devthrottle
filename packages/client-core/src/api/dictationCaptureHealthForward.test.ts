import { afterEach, describe, expect, it, vi } from "vitest";
import { uploadDictationToSession, type DictationUploadArgs } from "./client";

// Capture-health forwarding (issue #863): the durable Terminal/Chat Send path measures the clip once at
// Send time and must FORWARD those numbers on the /dictation/{id}/complete call, so the Gateway can
// persist this path's audio-loss deficit into the same log every other surface writes. This drives
// uploadDictationToSession against a mocked fetch and asserts the complete body carries the measurements
// (and that a send WITHOUT them omits them rather than sending nulls-as-zero).

const UPLOAD_ID = "33333333-3333-3333-3333-333333333333";

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
    audio: new Blob(["hello world"], { type: "audio/webm" }),
    before: "",
    after: "",
    prefix: "",
    sentAtUtc: "2026-09-25T09:05:12.345Z",
    resumed: false,
  };
}

interface CompleteBody {
  clientRecordedMs?: number;
  clientDecodedSeconds?: number;
  clientSourceBytes?: number;
}

// Drive a full happy send (register -> first complete reports the chunk missing -> upload it -> second
// complete submits) and return the LAST /complete request body so the test can inspect what was sent.
function driveAndCaptureCompleteBody(): { completeBodies: CompleteBody[] } {
  const completeBodies: CompleteBody[] = [];
  let completeCalls = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string; body?: unknown }) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID, terminal: false });
      if (url.includes(`/dictation/${UPLOAD_ID}/complete`) && method === "POST") {
        completeBodies.push(JSON.parse(String(init?.body ?? "{}")) as CompleteBody);
        completeCalls += 1;
        // First complete: nothing uploaded yet, so report chunk 0 missing. Second complete: submitted.
        return completeCalls === 1
          ? fakeResponse(409, { missing: [0] })
          : fakeResponse(200, { submitted: true });
      }
      if (url.includes(`/dictation/${UPLOAD_ID}/chunk/`) && method === "PUT") return fakeResponse(200, { ok: true });
      if (url.includes("/ack")) return fakeResponse(200, { ok: true });
      throw new Error("unexpected fetch: " + method + " " + url);
    }),
  );
  return { completeBodies };
}

describe("dictation Send forwards capture-health on complete (issue #863)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("forwards recordedMs, decodedSeconds and sourceBytes in the complete body", async () => {
    const { completeBodies } = driveAndCaptureCompleteBody();

    await uploadDictationToSession({
      ...baseArgs(),
      clientRecordedMs: 120_000,
      clientDecodedSeconds: 108.5,
      clientSourceBytes: 1_500_000,
    });

    expect(completeBodies.length).toBeGreaterThan(0);
    const body = completeBodies[completeBodies.length - 1];
    expect(body.clientRecordedMs).toBe(120_000);
    expect(body.clientDecodedSeconds).toBe(108.5);
    expect(body.clientSourceBytes).toBe(1_500_000);
  });

  it("omits the fields (does not send zeros) when the client did not measure", async () => {
    const { completeBodies } = driveAndCaptureCompleteBody();

    await uploadDictationToSession(baseArgs());

    const body = completeBodies[completeBodies.length - 1];
    expect(body.clientRecordedMs).toBeUndefined();
    expect(body.clientDecodedSeconds).toBeUndefined();
    expect(body.clientSourceBytes).toBeUndefined();
  });
});
