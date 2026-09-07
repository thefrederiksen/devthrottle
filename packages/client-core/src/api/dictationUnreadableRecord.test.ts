import { afterEach, describe, expect, it, vi } from "vitest";
import { transcriptionFailureMessage, uploadDictationToSession, type DictationUploadArgs } from "./client";

// Client half of issue #2745: the Gateway now REFUSES to re-open, deliver, or write over an upload whose
// on-disk delivery record is there but cannot be read. It answers 409 (the record was read and is not a
// delivery record, or is another account's - an operator has to look at the file) or 423 Locked (the record could
// not be read just now - a retry may read it), with a body that carries `record` (the kind) and an `error`
// sentence naming the file. Neither is a terminal outcome, so the client must keep its on-device copy, must
// NOT acknowledge (an ack retires the server's evidence), must not read the 409 as a missing-chunk answer,
// and must tell the user what is actually wrong rather than "the transcription service had a problem".

const UPLOAD_ID = "33333333-3333-3333-3333-333333333333";
const MALFORMED_ERROR =
  `the delivery record for upload ${UPLOAD_ID} is Malformed: the record has no State property (file: C:\\x\\record.json); refusing to re-open, deliver, or overwrite it`;
const UNREADABLE_ERROR =
  `the delivery record for upload ${UPLOAD_ID} is Unreadable: being used by another process (file: C:\\x\\record.json); refusing to re-open, deliver, or overwrite it`;

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
    baselineBufferBytes: 0,
    resumed: true,
  };
}

interface Call {
  url: string;
  method: string;
}

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

const ackFired = (calls: Call[]) => calls.some((c) => c.url.includes(`/dictation/${UPLOAD_ID}/ack`) && c.method === "POST");
const chunkPuts = (calls: Call[]) => calls.filter((c) => c.url.includes("/chunk/") && c.method === "PUT").length;

describe("a dictation whose server-side delivery record cannot be read (issue #2745)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("a 409 record refusal at REGISTER is held with the damaged-record message, no ack, no upload", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("/dictation/upload"))
        return fakeResponse(409, { error: MALFORMED_ERROR, upload_id: UPLOAD_ID, record: "Malformed", file: "C:\\x\\record.json" });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.submitted).toBe(false);
    expect(result.error).toContain("delivery record");
    expect(result.error).toContain("operator");
    expect(ackFired(calls)).toBe(false);
    expect(chunkPuts(calls)).toBe(0);
    expect(calls.some((c) => c.url.includes("/complete"))).toBe(false);
  });

  it("a 423 record refusal at REGISTER is held with the could-not-read message, not as a transcription outage", async () => {
    mockFetch((url) => {
      if (url.includes("/dictation/upload"))
        return fakeResponse(423, { error: UNREADABLE_ERROR, upload_id: UPLOAD_ID, record: "Unreadable", file: "C:\\x\\record.json" });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.error).toContain("could not read");
    expect(result.error).not.toContain("transcription service");
  });

  it("a 409 record refusal at COMPLETE is not read as a missing-chunk answer: held, no re-upload, no ack", async () => {
    const calls = mockFetch((url, method) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/chunk/") && method === "PUT") return fakeResponse(200, { ok: true, index: 0 });
      if (url.includes("/complete"))
        return fakeResponse(409, { error: MALFORMED_ERROR, upload_id: UPLOAD_ID, record: "Malformed", file: "C:\\x\\record.json" });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.submitted).toBe(false);
    expect(result.error).toContain("delivery record");
    // A resumed clip completes FIRST and sends only what the server reports missing (see the control below).
    // The refusal carries no missing list, so no chunk may have been sent on the strength of it.
    expect(chunkPuts(calls)).toBe(0);
    expect(calls.filter((c) => c.url.includes("/complete")).length).toBe(1);
    expect(ackFired(calls)).toBe(false);
  });

  it("a 423 record refusal at COMPLETE is held with the could-not-read message", async () => {
    const calls = mockFetch((url, method) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/chunk/") && method === "PUT") return fakeResponse(200, { ok: true, index: 0 });
      if (url.includes("/complete"))
        return fakeResponse(423, { error: UNREADABLE_ERROR, upload_id: UPLOAD_ID, record: "Unreadable", file: "C:\\x\\record.json" });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(false);
    expect(result.error).toContain("could not read");
    expect(ackFired(calls)).toBe(false);
  });

  it("a plain 409 missing-chunk answer still drives the missing chunks (control)", async () => {
    let completes = 0;
    const calls = mockFetch((url, method) => {
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/chunk/") && method === "PUT") return fakeResponse(200, { ok: true, index: 0 });
      if (url.includes("/complete")) {
        completes += 1;
        return completes === 1
          ? fakeResponse(409, { missing: [0] })
          : fakeResponse(200, { submitted: true, movedOn: false, transcript: "hello" });
      }
      if (url.includes("/ack")) return fakeResponse(200, { ok: true, retired: true });
      throw new Error("unexpected fetch: " + url);
    });

    const result = await uploadDictationToSession(baseArgs());

    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(true);
    expect(chunkPuts(calls)).toBe(1); // exactly the chunk the server reported missing
    expect(ackFired(calls)).toBe(true);
  });

  it("the message mapper names the record problem for both statuses and never the transcription service", () => {
    expect(transcriptionFailureMessage(MALFORMED_ERROR, 409, true)).toContain("operator");
    expect(transcriptionFailureMessage(UNREADABLE_ERROR, 423, true)).toContain("could not read");
    expect(transcriptionFailureMessage(UNREADABLE_ERROR, 423, true)).not.toContain("transcription service");
  });
});
