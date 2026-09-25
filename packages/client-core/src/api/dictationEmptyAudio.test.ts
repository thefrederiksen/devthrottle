import { afterEach, describe, expect, it, vi } from "vitest";
import { uploadDictationToSession, type DictationUploadArgs } from "./client";

// The empty-audio guard in uploadDictationToSession.
//
// WHAT WENT WRONG. planUploadChunks documents that "a zero-length payload yields no ranges (the caller
// guards empty audio upstream)", and this caller did not guard. A clip whose on-device copy read back as
// zero bytes therefore planned no chunks, uploaded nothing, and sent the server totalChunks:0 - which the
// complete endpoint correctly refuses with a 400. A 400 is not an allow-listed permanent reason, so the
// outcome fell through to the retryable/held arm and the driver re-drove the clip forever. On the phone
// that reads as "Saved - still sending" on a recording that can never be sent; on the Gateway it leaves a
// staging directory holding a delivery record and no audio, which is how it was found.
//
// The contract these tests hold: empty or unreadable audio is decided ON THE DEVICE, is PERMANENT (the
// clip parks and the auto-loop stops), and never reaches the network as a malformed completion.

const UPLOAD_ID = "33333333-3333-3333-3333-333333333333";

function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
  } as unknown as Response;
}

function argsWithAudio(audio: Blob): DictationUploadArgs {
  return {
    sessionId: "11111111-1111-1111-1111-111111111111",
    uploadId: UPLOAD_ID,
    audio,
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

// A server that behaves exactly as the real one does for a healthy clip: register succeeds, the FIRST
// complete reports the chunk still missing (nothing is staged yet), the chunk is accepted, and the second
// complete delivers. Every call is recorded so a test can prove the guard stopped before any of it.
function mockFetch(): Call[] {
  const calls: Call[] = [];
  let completes = 0;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: unknown, init?: { method?: string }) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      calls.push({ url, method });
      if (url.includes("/dictation/upload")) return fakeResponse(200, { upload_id: UPLOAD_ID });
      if (url.includes("/complete")) {
        completes += 1;
        if (completes === 1) return fakeResponse(409, { missing: [0] });
        return fakeResponse(200, { submitted: true, transcript: "hello" });
      }
      if (url.includes("/chunk/")) return fakeResponse(200, { ok: true });
      if (url.includes("/ack")) return fakeResponse(200, { ok: true });
      throw new Error("unexpected fetch: " + url);
    }),
  );
  return calls;
}

const completed = (calls: Call[]): boolean => calls.some((c) => c.url.includes("/complete"));
const uploadedAChunk = (calls: Call[]): boolean => calls.some((c) => c.url.includes("/chunk/"));

describe("the empty-audio guard (a recording with no bytes must never hold forever)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("parks a ZERO-BYTE recording instead of holding it, and never completes with totalChunks:0", async () => {
    const calls = mockFetch();

    const result = await uploadDictationToSession(argsWithAudio(new Blob([], { type: "audio/webm" })));

    // Permanent, so the driver PARKS: the audio is kept, the auto-loop stops, and the user is told.
    expect(result.permanent).toBe(true);
    expect(result.permanentReason).toBe("empty-recording");
    // Not held. This is the exact confusion that produced the forever-spinner.
    expect(result.terminal).toBe(false);
    expect(result.submitted).toBe(false);
    // The malformed completion never leaves the device.
    expect(completed(calls)).toBe(false);
    expect(uploadedAChunk(calls)).toBe(false);
  });

  it("KEEPS RETRYING a recording whose copy could not be read - a failed read is not an empty recording", async () => {
    // A read that throws says nothing about how long the recording is. A good clip can fail to read under
    // browser storage or memory pressure and read fine next time, so parking it would strand a recording
    // that automatic delivery would have recovered - and would tell the user it was empty, which is false.
    // Only a read that SUCCEEDS and returns zero bytes is permanent.
    const unreadable = {
      type: "audio/webm",
      arrayBuffer: async () => {
        throw new Error("NotReadableError: the requested file could not be read");
      },
    } as unknown as Blob;
    const calls = mockFetch();

    const result = await uploadDictationToSession(argsWithAudio(unreadable));

    expect(result.permanent).toBeUndefined();
    expect(result.terminal).toBe(false);
    // And it says what actually happened, rather than claiming the recording was empty or gone.
    expect(result.error).toBe(
      "Couldn't read the saved recording just now - it is still on your device and delivery will keep trying.",
    );
    expect(completed(calls)).toBe(false);
    expect(uploadedAChunk(calls)).toBe(false);
  });

  it("leaves a NORMAL recording completely alone - it uploads and completes as before", async () => {
    // The guard must be exact. Run it against known-good audio so a future widening that swallowed real
    // recordings would fail here rather than in someone's session.
    const calls = mockFetch();

    const result = await uploadDictationToSession(argsWithAudio(new Blob(["some real audio bytes"], { type: "audio/webm" })));

    expect(result.permanent).toBeUndefined();
    expect(result.terminal).toBe(true);
    expect(result.submitted).toBe(true);
    expect(uploadedAChunk(calls)).toBe(true);
    expect(completed(calls)).toBe(true);
  });
});
