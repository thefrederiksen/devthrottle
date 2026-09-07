import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The durable Send pipeline + background retry driver (issue #1006, strengthened for #1182, parking for
// #1184).
//
// These tests exercise the durable behavior against a mocked API client and a mocked durable store, and
// assert on the REAL status store (status.ts is not mocked) so the published phases are checked exactly as
// the UI reads them. The core guarantees under test:
//   - persist to the durable store BEFORE any network work;
//   - a delivered clip is removed from the store on the terminal submitted outcome (no accumulation);
//   - a held (non-terminal) outcome KEEPS the audio and publishes a held, retryable status - never a loss;
//   - resume-on-load re-drives every pending clip from the durable copy with resumed=true;
//   - the in-flight guard makes concurrent triggers drive a clip at most once (no double injection);
//   - durable storage being unavailable is a clear, loud failure, not a silent one-shot send;
//   - a genuinely permanent failure PARKS the clip (keeps the audio, stops the auto-loop) and only an
//     explicit Retry re-drives it, while transient failures still auto-retry (issue #1184).

vi.mock("../api/client", () => ({ uploadDictationToSession: vi.fn(), abandonDictation: vi.fn(), sendPrompt: vi.fn() }));
vi.mock("./pendingStore", () => ({
  savePending: vi.fn(),
  deletePending: vi.fn(),
  getPending: vi.fn(),
  listPending: vi.fn(),
}));

import { abandonDictation, sendPrompt, uploadDictationToSession, type DictationSubmitResult } from "../api/client";
import {
  abandonPendingDictation,
  backgroundTranscribeAndSend,
  dismissDictationStatus,
  resumePendingDictations,
  retryDroppedDictation,
  retryPendingDictation,
  sendDroppedDictationAnyway,
  type CapturedUtterance,
} from "./backgroundSend";
import { deletePending, getPending, listPending, savePending, type PendingDictation } from "./pendingStore";
import { allDictationStatuses, clearDictationStatus } from "./status";

const captured: CapturedUtterance = { blob: new Blob(["x"]), recordedMs: 1000, prefixText: "" };

const SUBMITTED: DictationSubmitResult = { terminal: true, submitted: true, movedOn: false, transcript: "hi" };
const HELD: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  error: "The transcription service is temporarily unavailable - your recording is saved and will keep trying.",
};
const PERMANENT: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  permanent: true,
  permanentReason: "audio-too-large",
};
// The four terminal-not-submitted shapes (issue #1590). Only ABANDONED is silent.
const MOVED_ON: DictationSubmitResult = {
  terminal: true,
  submitted: false,
  movedOn: true,
  transcript: "the words the user actually said",
};
const MOVED_ON_NO_TRANSCRIPT: DictationSubmitResult = {
  terminal: true,
  submitted: false,
  movedOn: true,
  transcript: "",
};
const EMPTY_CLIP: DictationSubmitResult = { terminal: true, submitted: false, movedOn: false, transcript: "" };
const ABANDONED: DictationSubmitResult = {
  terminal: true,
  submitted: false,
  movedOn: false,
  abandoned: true,
  transcript: "",
};

function statusFor(uploadId: string) {
  return allDictationStatuses().find((s) => s.uploadId === uploadId);
}

function makeRecord(id: string): PendingDictation {
  return {
    id,
    sessionId: "sid",
    blob: new Blob(["x"]),
    recordedMs: 1000,
    before: "",
    after: "",
    prefix: "",
    baselineBufferBytes: 0,
    createdAt: Date.now(),
  };
}

const flush = async () => {
  for (let i = 0; i < 6; i++) await Promise.resolve();
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
  vi.mocked(savePending).mockResolvedValue(undefined);
  vi.mocked(deletePending).mockResolvedValue(undefined);
  vi.mocked(getPending).mockResolvedValue(null);
  vi.mocked(listPending).mockResolvedValue([]);
  vi.mocked(abandonDictation).mockResolvedValue(true);
  vi.mocked(sendPrompt).mockResolvedValue(undefined);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("the empty-capture gate (an empty recording must never enter the durable queue)", () => {
  // The root cause of the forever-spinner. DictationRecorder returns `new Blob(this.chunks, ...)`, which is
  // ZERO BYTES when the recorder delivered no chunks. Nothing guarded it, so an empty clip was queued like
  // any other and then re-driven forever - it can never produce a chunk, so the Gateway can never complete
  // it, and the phone said "Saved - still sending" about a recording that did not exist. Every Send surface
  // goes through this one function, so this is the one gate that covers all of them.
  const emptyCapture: CapturedUtterance = { blob: new Blob([]), recordedMs: 1000, prefixText: "" };

  it("refuses a zero-byte capture: nothing is queued, nothing is uploaded, and the user is told", async () => {
    const onFailed = vi.fn();
    const onError = vi.fn();

    await backgroundTranscribeAndSend("sid", emptyCapture, { onFailed, onError });

    // Never queued. This is what stops the loop existing at all, rather than stopping it later.
    expect(savePending).not.toHaveBeenCalled();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    // LOUD, and a different error. "failed" renders the red role="alert" strip that never clears itself;
    // "unheard" would be the grey role="status" cousin, and "held" is the amber lie this replaces.
    const status = allDictationStatuses().find((s) => s.sessionId === "sid");
    expect(status?.phase).toBe("failed");
    expect(status?.retryable).toBe(false);
    expect(status?.error).toBe(
      "Recording failed - it captured no audio, so nothing was sent and nothing is being retried. Check the microphone is working and record it again.",
    );
    // The host's own error surface fires too, so the message is not confined to the strip.
    expect(onError).toHaveBeenCalledWith(status?.error);
    // Nothing was queued, so any typed text the dialog cleared must come back - same contract as the
    // no-durable-store path, the other case where the clip is not queued.
    expect(onFailed).toHaveBeenCalledTimes(1);
  });

  it("fails IMMEDIATELY - before the decode, the durable write, or any network work", async () => {
    // The complaint this fixes is not only that it retried, but that it took all evening to say anything.
    // There is nothing to wait for: transcription is server-side, and a clip with no bytes has nothing to
    // upload, so the verdict is available on the device at once and is delivered at once.
    await backgroundTranscribeAndSend("sid", emptyCapture);

    // The status is already published with no timers advanced and no promises pumped beyond the call.
    expect(allDictationStatuses().find((s) => s.sessionId === "sid")?.phase).toBe("failed");
    expect(savePending).not.toHaveBeenCalled();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });

  it("no Gateway staging is ever created for an empty capture, and no timer is left behind", async () => {
    await backgroundTranscribeAndSend("sid", emptyCapture);

    // The registration that left a delivery record with no audio behind it on the Gateway never happens.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    // And nothing is scheduled: the clip is finished, not waiting.
    await vi.advanceTimersByTimeAsync(10 * 60 * 1000);
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(savePending).not.toHaveBeenCalled();
  });

  it("a recording WITH audio is untouched by the gate", async () => {
    // The gate must be exact: one byte is a recording, and it takes the normal durable path.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    expect(savePending).toHaveBeenCalled();
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
  });
});

describe("backgroundTranscribeAndSend", () => {
  it("persists the audio to the durable store BEFORE any network work", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    expect(savePending).toHaveBeenCalledTimes(1);
    // savePending must run before the first upload call.
    const saveOrder = vi.mocked(savePending).mock.invocationCallOrder[0];
    const uploadOrder = vi.mocked(uploadDictationToSession).mock.invocationCallOrder[0];
    expect(saveOrder).toBeLessThan(uploadOrder);
    // The persisted record carries the recorded audio.
    expect(vi.mocked(savePending).mock.calls[0][0].blob).toBe(captured.blob);
  });

  it("saves the clip durably AT ONCE (baseline unknown), then enriches it from the press-time snapshot before upload (issue #2478)", async () => {
    // Two contracts at once, in the right order. Durability never waits on the roster: the clip is on
    // disk immediately, so a slow or timed-out roster read cannot leave it memory-only. And a quick
    // Send still cannot outrun the snapshot: the UPLOAD holds until the press-time promise resolves,
    // and the durable record is enriched with the reading before the first attempt.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    let releaseBaseline: (bytes: number | undefined) => void = () => {};
    const pending = new Promise<number | undefined>((resolve) => { releaseBaseline = resolve; });

    const send = backgroundTranscribeAndSend("sid", captured, { baselineBufferBytes: pending });
    await flush();
    // The snapshot has not answered yet - the clip is ALREADY durable (unknown baseline), and the
    // upload is holding for the press-time answer.
    expect(savePending).toHaveBeenCalledTimes(1);
    expect(vi.mocked(savePending).mock.calls[0][0].baselineBufferBytes).toBeUndefined();
    expect(uploadDictationToSession).not.toHaveBeenCalled();

    releaseBaseline(48213);
    await send;
    // The durable record was enriched with the press-time reading before the first upload carried it.
    expect(savePending).toHaveBeenCalledTimes(2);
    expect(vi.mocked(savePending).mock.calls[1][0].baselineBufferBytes).toBe(48213);
    expect(vi.mocked(savePending).mock.calls[1][0].id).toBe(vi.mocked(savePending).mock.calls[0][0].id);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBe(48213);
  });

  it("a kick landing between the first save and enrichment cannot drive the unknown record (issue #2478 round three)", async () => {
    // The first durable write makes the record visible to every automatic trigger, and the
    // enrichment window is as long as a roster read. A kick (online, foreground, app load) that
    // listed and drove the record inside that window would upload with the baseline still unknown
    // and race the enrichment write and the original drive. The upload id is reserved from the kick
    // machinery for the whole window, so the kick must no-op.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    let releaseBaseline: (bytes: number | undefined) => void = () => {};
    const pending = new Promise<number | undefined>((resolve) => { releaseBaseline = resolve; });

    const send = backgroundTranscribeAndSend("sid", captured, { baselineBufferBytes: pending });
    await flush();
    expect(savePending).toHaveBeenCalledTimes(1);
    const saved = vi.mocked(savePending).mock.calls[0][0];
    expect(saved.baselineBufferBytes).toBeUndefined();

    // The kick lands mid-window: the durable store lists the unknown record and the kick machinery
    // tries to drive it, exactly as the online/visibility listeners would.
    vi.mocked(listPending).mockResolvedValue([saved]);
    await resumePendingDictations();
    expect(uploadDictationToSession).not.toHaveBeenCalled(); // the reservation held: no unguarded upload

    releaseBaseline(48213);
    await send;
    // Exactly ONE upload happened, from the original flow: enriched with the press-time reading and
    // an immediate (not resumed) send - never the kick's resumed, unknown-baseline drive.
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBe(48213);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].resumed).toBe(false);
  });

  it("a crash or reload still resumes the durable unknown record - the reservation dies with the process", async () => {
    // The reservation lives only in memory, deliberately. After a crash mid-enrichment a fresh
    // process holds no reservation, and the resume path drives the durable unknown record from the
    // on-device copy - unguarded, which is exactly what that copy honestly knows.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    const rec: PendingDictation = { ...makeRecord("id-unknown-crash"), baselineBufferBytes: undefined };
    vi.mocked(listPending).mockResolvedValue([rec]);

    await resumePendingDictations();

    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBeUndefined();
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].resumed).toBe(true);
  });

  it("an UNKNOWN press-time baseline is FINAL - persisted as unknown, no later re-read, never a fabricated zero (issue #2478)", async () => {
    // Unknown (the press-time read could not answer) and zero (a terminal that had produced nothing
    // yet) are different facts. Collapsing unknown into zero at persist time was how the moved-on
    // guard silently stayed unarmed; and substituting a LATER roster reading would be worse - it can
    // include bytes produced after recording, masking the movement the guard detects. Unknown stays
    // absent all the way to the wire, where JSON omits the field and the server skips the guard.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured, {
      baselineBufferBytes: Promise.resolve(undefined),
    });

    // Exactly one durable write - no enrich pass, and no roster call of the pipeline's own.
    expect(savePending).toHaveBeenCalledTimes(1);
    expect(vi.mocked(savePending).mock.calls[0][0].baselineBufferBytes).toBeUndefined();
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBeUndefined();
  });

  it("passes a GENUINE zero baseline through as a real reading, distinct from unknown (issue #2478)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured, { baselineBufferBytes: Promise.resolve(0) });

    // Zero is an answer: it lands in the enriched durable record and on the upload.
    expect(vi.mocked(savePending).mock.calls.at(-1)?.[0].baselineBufferBytes).toBe(0);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBe(0);
  });

  it("a plain-number baseline (the Voice screen) rides the FIRST durable write, unchanged", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured, { baselineBufferBytes: 321 });

    expect(savePending).toHaveBeenCalledTimes(1);
    expect(vi.mocked(savePending).mock.calls[0][0].baselineBufferBytes).toBe(321);
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].baselineBufferBytes).toBe(321);
  });

  it("a foreign baseline promise that REJECTS costs the guard, never the words", async () => {
    // The shared snapshot never rejects, but the hooks contract cannot force that on every caller. A
    // rejection must not escape and strand the clip - it is already durable by then, and it still
    // delivers, unguarded.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured, {
      baselineBufferBytes: Promise.reject(new Error("roster exploded")),
    });

    expect(vi.mocked(savePending).mock.calls[0][0].baselineBufferBytes).toBeUndefined();
    // The clip still delivered: persisted durably and driven through one upload attempt.
    expect(savePending).toHaveBeenCalledTimes(1);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
  });

  it("a delivered send that dropped audio carries a capture-loss warning on done (never silent)", async () => {
    // The send succeeds, but the record was flagged with a capture-loss warning at Send time. The delivered
    // `done` status must carry that warning so the strip shows a non-clearing caution instead of a silent
    // "Sent" - the fire-and-forget Send's equivalent of the dialog's dropped-audio warning.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    const warned: PendingDictation = { ...makeRecord("warn-1"), captureWarning: "About 3 seconds of your audio was not captured, so words may be missing." };
    vi.mocked(listPending).mockResolvedValue([warned]);

    await resumePendingDictations();

    const s = statusFor("warn-1");
    expect(s?.phase).toBe("done");
    expect(s?.warning).toBe("About 3 seconds of your audio was not captured, so words may be missing.");
  });

  it("a clean delivered send has no warning on done", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    vi.mocked(listPending).mockResolvedValue([makeRecord("clean-1")]);

    await resumePendingDictations();

    const s = statusFor("clean-1");
    expect(s?.phase).toBe("done");
    expect(s?.warning).toBeUndefined();
  });

  it("removes the durable copy on a terminal submitted outcome (the queue does not accumulate)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(deletePending).toHaveBeenCalledWith(savedId);
    expect(statusFor(savedId)?.phase).toBe("done");
  });

  it("keeps the audio and publishes a held, retryable status on a non-terminal outcome - never deletes it", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(deletePending).not.toHaveBeenCalled();
    const status = statusFor(savedId);
    expect(status?.phase).toBe("held");
    expect(status?.retryable).toBe(true);
    // The held copy is honest: saved and will keep trying, never "was not transcribed".
    expect(status?.error).toContain("saved");
    expect(status?.error).not.toContain("was not transcribed");
  });

  it("passes resumed=false on the very first immediate send", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].resumed).toBe(false);
  });

  it("fails loudly (no silent one-shot) when durable storage is unavailable", async () => {
    vi.mocked(savePending).mockRejectedValue(new Error("indexedDB unavailable"));
    const onError = vi.fn();
    const onFailed = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError, onFailed });

    // No upload was attempted, and the failure is surfaced clearly.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledTimes(1);
    expect(onFailed).toHaveBeenCalledTimes(1);
    const all = allDictationStatuses();
    expect(all[0].phase).toBe("failed");
    expect(all[0].retryable).toBe(false);
  });
});

describe("resumePendingDictations", () => {
  it("re-drives every pending clip from the durable copy with resumed=true, deleting delivered ones", async () => {
    const a = makeRecord("id-a");
    const b = makeRecord("id-b");
    vi.mocked(listPending).mockResolvedValue([a, b]);
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await resumePendingDictations();

    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    for (const call of vi.mocked(uploadDictationToSession).mock.calls) {
      expect(call[0].resumed).toBe(true);
    }
    expect(deletePending).toHaveBeenCalledWith("id-a");
    expect(deletePending).toHaveBeenCalledWith("id-b");
  });

  it("keeps a clip that still cannot be delivered (held, not dropped)", async () => {
    const a = makeRecord("id-held");
    vi.mocked(listPending).mockResolvedValue([a]);
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await resumePendingDictations();

    expect(deletePending).not.toHaveBeenCalled();
    expect(statusFor("id-held")?.phase).toBe("held");
  });
});

describe("in-flight guard (idempotency: never inject twice)", () => {
  it("drives a clip at most once when two triggers fire concurrently for the same upload id", async () => {
    const rec = makeRecord("id-dup");
    vi.mocked(getPending).mockResolvedValue(rec);
    let resolveUpload!: (r: DictationSubmitResult) => void;
    vi.mocked(uploadDictationToSession).mockImplementation(
      () => new Promise<DictationSubmitResult>((res) => { resolveUpload = res; }),
    );

    // Two "Upload now" triggers race for the same clip.
    const p1 = retryPendingDictation("id-dup");
    const p2 = retryPendingDictation("id-dup");
    await flush();

    // The in-flight guard let exactly one attempt start.
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);

    resolveUpload(SUBMITTED);
    await Promise.all([p1, p2]);

    expect(deletePending).toHaveBeenCalledTimes(1);
  });
});

describe("retryPendingDictation (Upload now)", () => {
  it("clears a stale status when the durable record is already gone", async () => {
    vi.mocked(getPending).mockResolvedValue(null);
    // Seed a lingering held status for a clip that has since been delivered/abandoned.
    const { publishDictationStatus } = await import("./status");
    publishDictationStatus({ sessionId: "sid", uploadId: "gone", phase: "held", retryable: true });

    await retryPendingDictation("gone");

    expect(statusFor("gone")).toBeUndefined();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });
});

describe("parking permanently-failed clips (#1184)", () => {
  it("parks on a permanent outcome: persists the parked reason, keeps the audio, shows the saved-and-retryable message, and STOPS the auto-loop", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(PERMANENT);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    // The audio is never discarded.
    expect(deletePending).not.toHaveBeenCalled();
    // The record was persisted with the allow-listed parked reason so every auto-trigger can skip it.
    const parkedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].parkedReason);
    expect(parkedSave?.[0].parkedReason).toBe("audio-too-large");
    // The status is parked, retryable, with the exact saved-and-retryable size wording.
    const status = statusFor(savedId);
    expect(status?.phase).toBe("parked");
    expect(status?.retryable).toBe(true);
    expect(status?.error).toBe(
      "This recording is too long to transcribe right now; it is saved on your device and you can retry it.",
    );
    // The forever-loop is gone: advancing well past the hard-retry window triggers no further attempt.
    const callsBefore = vi.mocked(uploadDictationToSession).mock.calls.length;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(vi.mocked(uploadDictationToSession).mock.calls.length).toBe(callsBefore);
  });

  it("an EMPTY recording parks with its own honest sentence, not the too-long one", async () => {
    // The empty-audio guard (see dictationEmptyAudio.test.ts) returns this outcome. What the user is told
    // matters as much as the parking: the old behaviour said "Saved - still sending" forever, and the
    // default park wording would now say the clip is "too long", which is a second wrong answer. It must
    // say the recording came back empty and that nothing is in flight.
    vi.mocked(uploadDictationToSession).mockResolvedValue({
      terminal: false,
      submitted: false,
      movedOn: false,
      transcript: "",
      permanent: true,
      permanentReason: "empty-recording",
    });

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(deletePending).not.toHaveBeenCalled();
    const parkedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].parkedReason);
    expect(parkedSave?.[0].parkedReason).toBe("empty-recording");
    const status = statusFor(savedId);
    expect(status?.phase).toBe("parked");
    expect(status?.retryable).toBe(true);
    expect(status?.error).toBe(
      "This recording came back empty on this device, so there was nothing to send. Nothing is in flight - you can retry it, or record it again.",
    );
    // And the forever-loop that started all this is gone.
    const callsBefore = vi.mocked(uploadDictationToSession).mock.calls.length;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(vi.mocked(uploadDictationToSession).mock.calls.length).toBe(callsBefore);
  });

  it("does NOT auto-drive a parked clip on app load - it only republishes the parked status", async () => {
    const parked: PendingDictation = { ...makeRecord("id-parked"), parkedReason: "audio-too-large" };
    vi.mocked(listPending).mockResolvedValue([parked]);

    await resumePendingDictations();

    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(deletePending).not.toHaveBeenCalled();
    expect(statusFor("id-parked")?.phase).toBe("parked");
  });

  it("an explicit Retry reactivates a parked clip (clears parkedReason) and re-drives it from the on-device copy", async () => {
    const parked: PendingDictation = { ...makeRecord("id-retry"), parkedReason: "audio-too-large" };
    vi.mocked(getPending).mockResolvedValue(parked);
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD); // still cannot succeed yet, but it re-drove

    await retryPendingDictation("id-retry");

    // The durable record was reactivated (parked reason cleared) so the triggers stop skipping it.
    const reactivate = vi.mocked(savePending).mock.calls.find((c) => c[0].id === "id-retry");
    expect(reactivate).toBeTruthy();
    expect(reactivate?.[0].parkedReason).toBeUndefined();
    // And the explicit retry actually re-drove the clip.
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
  });

  it("a transient failure still auto-retries and is never parked (no regression)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(statusFor(savedId)?.phase).toBe("held");
    // A transient outcome never parks the record.
    const parkedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].parkedReason);
    expect(parkedSave).toBeUndefined();
  });
});

// Issue #1590: a dropped dictation must be LOUD and must give the words back.
//
// Every one of these outcomes used to land in a single arm - deletePending + clearDictationStatus. Audio
// gone, banner gone, no trace, and the user was never told their words had been thrown away. "It worked and
// then nothing happened." Only the abandon is genuinely nothing to say.
describe("a terminal outcome that did NOT submit is never silent (#1590)", () => {
  it("a moved-on drop stays VISIBLE, carries the transcript back, and never auto-clears", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(MOVED_ON);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    const status = statusFor(savedId);
    // The defect was that this was undefined: the words vanished with no banner at all.
    expect(status).toBeDefined();
    expect(status?.phase).toBe("dropped");
    // The words are handed back so the UI can offer "Send anyway".
    expect(status?.recoverableText).toBe("the words the user actually said");
    // Honest about what happened - it says the recording was NOT sent, never a success.
    expect(status?.error).toContain("moved on");
    expect(status?.error).toContain("wasn't sent");
    // "Send anyway" is a fresh turn, not a retry of a tombstoned upload id.
    expect(status?.retryable).toBe(false);

    // Sticky: no timer clears it. Advancing well past every cadence leaves it exactly where it was.
    await vi.advanceTimersByTimeAsync(10 * 60 * 1000);
    expect(statusFor(savedId)?.phase).toBe("dropped");
    // And nothing auto-re-drives it - re-driving a moved-on upload id could only be dropped again.
    expect(vi.mocked(uploadDictationToSession).mock.calls.length).toBe(1);
  });

  it("a moved-on drop persists the words durably so they survive a reload", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(MOVED_ON);

    await backgroundTranscribeAndSend("sid", captured);

    // The record is KEPT (not deleted) and marked, so no automatic trigger re-drives it...
    expect(deletePending).not.toHaveBeenCalled();
    const droppedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].staleDropped);
    expect(droppedSave?.[0].staleDropped).toBe(true);
    // ...and the words are on disk, or "Send anyway" would quietly stop working after a reload.
    expect(droppedSave?.[0].droppedTranscript).toBe("the words the user actually said");
  });

  it("re-publishes a dropped clip on app load instead of re-driving it (the words are still there)", async () => {
    const dropped: PendingDictation = {
      ...makeRecord("id-dropped"),
      staleDropped: true,
      droppedTranscript: "words from before the reload",
    };
    vi.mocked(listPending).mockResolvedValue([dropped]);

    await resumePendingDictations();

    expect(uploadDictationToSession).not.toHaveBeenCalled(); // never re-drive a tombstoned upload id
    expect(deletePending).not.toHaveBeenCalled();
    const status = statusFor("id-dropped");
    expect(status?.phase).toBe("dropped");
    expect(status?.recoverableText).toBe("words from before the reload");
  });

  it("a drop BEFORE transcription keeps the audio and offers a retry instead of words", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(MOVED_ON_NO_TRANSCRIPT);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    const status = statusFor(savedId);
    expect(status?.phase).toBe("dropped");
    expect(status?.recoverableText).toBe("");
    // No words to hand back, so the recording itself is the recovery: kept, and explicitly retryable.
    expect(status?.retryable).toBe(true);
    expect(status?.error).toContain("saved on your device");
    expect(deletePending).not.toHaveBeenCalled();
  });

  it("an empty clip says nothing was heard, visibly and dismissibly, with nothing to retry", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(EMPTY_CLIP);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    const status = statusFor(savedId);
    expect(status).toBeDefined(); // it used to be silent
    expect(status?.phase).toBe("unheard");
    expect(status?.retryable).toBe(false); // there is nothing to retry
    expect(status?.error).toContain("Nothing was heard");
    expect(deletePending).toHaveBeenCalledWith(savedId); // the audio is of no further use
  });

  // The control. The fix must not turn into "never clear anything": an abandon is the one terminal
  // not-submitted outcome the user caused on purpose, and it stays silent.
  it("an ABANDONED outcome stays silent - the user did that on purpose", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(ABANDONED);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(statusFor(savedId)).toBeUndefined();
    expect(deletePending).toHaveBeenCalledWith(savedId);
  });

  // The other control: a delivered turn is unaffected.
  it("a delivered turn still shows done and drops the audio (no regression)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(statusFor(savedId)?.phase).toBe("done");
    expect(deletePending).toHaveBeenCalledWith(savedId);
  });
});

describe("recovering a dropped dictation (#1590)", () => {
  const droppedRecord = (id: string, transcript: string): PendingDictation => ({
    ...makeRecord(id),
    staleDropped: true,
    droppedTranscript: transcript,
  });

  it("Send anyway sends the words as a NORMAL prompt - a fresh turn, not a re-drive of the dead upload id", async () => {
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-send", "send me please"));

    await sendDroppedDictationAnyway("id-send");

    expect(sendPrompt).toHaveBeenCalledWith("sid", "send me please", true);
    // Never re-drives the dictation: that upload id carries a permanent moved-on tombstone.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    // Done with: the record goes and the strip acknowledges the send.
    expect(deletePending).toHaveBeenCalledWith("id-send");
    expect(statusFor("id-send")?.phase).toBe("done");
  });

  it("Send anyway is guarded: two rapid taps submit the user's words exactly ONCE", async () => {
    // There is NO server-side idempotency behind this send - it is an ordinary prompt, so the durable upload
    // id that de-duplicates a dictation (#1183) protects nothing here. Unguarded, two taps (or two mounted
    // strips, each with its own button state) both read the record before either deletes it, and the user
    // gets their words twice.
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-race", "say this once"));
    let resolveSend!: () => void;
    vi.mocked(sendPrompt).mockImplementation(() => new Promise<void>((res) => { resolveSend = () => res(); }));

    const first = sendDroppedDictationAnyway("id-race");
    const second = sendDroppedDictationAnyway("id-race");
    await flush();

    expect(sendPrompt).toHaveBeenCalledTimes(1); // the second tap found the first still in flight

    resolveSend();
    await Promise.all([first, second]);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
    expect(deletePending).toHaveBeenCalledTimes(1);
  });

  it("Retry is guarded: two rapid taps stage and drive exactly ONE fresh clip", async () => {
    // Each tap mints a NEW upload id, so nothing downstream would de-duplicate two of them - the guard is
    // the only thing standing between an impatient double-tap and the same recording injected twice.
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-retry-race", ""));
    let resolveUpload!: (r: DictationSubmitResult) => void;
    vi.mocked(uploadDictationToSession).mockImplementation(
      () => new Promise<DictationSubmitResult>((res) => { resolveUpload = res; }),
    );

    const first = retryDroppedDictation("id-retry-race");
    const second = retryDroppedDictation("id-retry-race");
    await flush();

    const freshSaves = vi.mocked(savePending).mock.calls.filter((c) => c[0].id !== "id-retry-race");
    expect(freshSaves).toHaveLength(1);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);

    resolveUpload(SUBMITTED);
    await Promise.all([first, second]);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
  });

  it("Send anyway sends the WHOLE message - typed text included - not just the transcribed words", async () => {
    // A Terminal Speak dictation composes the transcript with the typed text the caret split it around. The
    // Gateway's delivery path joins before + prefix + transcript + after, skipping empties; the recovery of
    // that same turn must send the same message, or it silently throws the typed text away - the very
    // vanishing this item exists to end, just smaller and harder to notice.
    const composed: PendingDictation = {
      ...makeRecord("id-compose"),
      staleDropped: true,
      droppedTranscript: "the spoken words",
      before: "typed before",
      prefix: "an earlier paused segment",
      after: "typed after",
    };
    vi.mocked(getPending).mockResolvedValue(composed);

    await sendDroppedDictationAnyway("id-compose");

    expect(sendPrompt).toHaveBeenCalledWith(
      "sid",
      "typed before an earlier paused segment the spoken words typed after",
      true,
    );
  });

  it("shows the user exactly what it will send (the quote and the send are one string)", async () => {
    // The strip quotes recoverableText and Send anyway sends the composed message; if those two ever drift
    // apart the strip shows one thing and sends another.
    const composed: PendingDictation = {
      ...makeRecord("id-quote"),
      staleDropped: true,
      droppedTranscript: "spoken",
      before: "typed",
      after: "",
      prefix: "",
    };
    vi.mocked(listPending).mockResolvedValue([composed]);

    await resumePendingDictations();
    const shown = statusFor("id-quote")?.recoverableText;

    vi.mocked(getPending).mockResolvedValue(composed);
    await sendDroppedDictationAnyway("id-quote");

    expect(shown).toBe("typed spoken");
    expect(sendPrompt).toHaveBeenCalledWith("sid", shown, true);
  });

  it("a drop with typed text but no transcript still offers the typed words back", async () => {
    // "No transcript" is not the same as "nothing to recover": the typed text is the user's too.
    const typedOnly: PendingDictation = {
      ...makeRecord("id-typed-only"),
      staleDropped: true,
      droppedTranscript: "",
      before: "please run the tests",
    };
    vi.mocked(listPending).mockResolvedValue([typedOnly]);

    await resumePendingDictations();

    const status = statusFor("id-typed-only");
    expect(status?.recoverableText).toBe("please run the tests");
    expect(status?.retryable).toBe(false); // it has words, so the action is Send anyway, not Retry
  });

  it("Send anyway that fails KEEPS the words and stays sticky - a bad moment must not lose them", async () => {
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-send-fail", "precious words"));
    vi.mocked(sendPrompt).mockRejectedValue(new Error("network died"));

    await sendDroppedDictationAnyway("id-send-fail");

    expect(deletePending).not.toHaveBeenCalled();
    const status = statusFor("id-send-fail");
    expect(status?.phase).toBe("dropped");
    expect(status?.recoverableText).toBe("precious words"); // still on screen, still recoverable
    expect(status?.error).toContain("still here");
  });

  it("Retry re-drives the recording under a FRESH upload id, with the stale baseline cleared", async () => {
    const old = droppedRecord("id-old", "");
    vi.mocked(getPending).mockResolvedValue(old);
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await retryDroppedDictation("id-old");

    // A brand-new id: the old one is tombstoned moved-on and could only ever be dropped again.
    const fresh = vi.mocked(savePending).mock.calls.find((c) => c[0].id !== "id-old")?.[0];
    expect(fresh).toBeDefined();
    expect(fresh?.id).not.toBe("id-old");
    expect(fresh?.blob).toBe(old.blob); // the SAME recording, under a new id - the audio is what we are retrying
    expect(fresh?.staleDropped).toBeUndefined();
    // The record-time baseline describes a terminal that has long since moved on; re-sending it would just
    // invite the same drop. The user asked for this send now. Cleared to UNKNOWN (field omitted on the
    // wire, guard skipped) - never a fabricated zero, which is a real reading (issue #2478).
    expect(fresh?.baselineBufferBytes).toBeUndefined();
    // The fresh clip really was driven, and the old id is retired.
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].uploadId).toBe(fresh?.id);
    expect(deletePending).toHaveBeenCalledWith("id-old");
    expect(statusFor("id-old")).toBeUndefined();
  });

  it("Upload now on a dropped clip hands over to the fresh-id retry rather than re-driving a dead id", async () => {
    // Defensive wiring: whatever calls the generic retry must not silently do nothing (or re-drop).
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-generic", ""));
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await retryPendingDictation("id-generic");

    const fresh = vi.mocked(savePending).mock.calls.find((c) => c[0].id !== "id-generic")?.[0];
    expect(fresh).toBeDefined();
    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].uploadId).toBe(fresh?.id);
  });

  it("Dismiss is the ONLY thing that throws the words away, and it is always deliberate", async () => {
    vi.mocked(getPending).mockResolvedValue(droppedRecord("id-dismiss", "unwanted words"));

    await dismissDictationStatus("id-dismiss");

    expect(statusFor("id-dismiss")).toBeUndefined();
    expect(deletePending).toHaveBeenCalledWith("id-dismiss");
    expect(sendPrompt).not.toHaveBeenCalled();
  });
});

// Issue #1181, Task 5: the user explicitly abandons a stuck dictation from the phone.
describe("abandonPendingDictation", () => {
  it("tells the Gateway to abandon, drops the on-device copy, and never uploads", async () => {
    vi.mocked(getPending).mockResolvedValue(makeRecord("id-abandon"));

    await abandonPendingDictation("id-abandon");

    // Marked abandoning durably (so a reload does not resume uploading it)...
    const abandoningSave = vi.mocked(savePending).mock.calls.find((c) => c[0].id === "id-abandon");
    expect(abandoningSave?.[0].abandoning).toBe(true);
    // ...the Gateway was told to abandon the durable upload...
    expect(abandonDictation).toHaveBeenCalledWith("id-abandon");
    // ...the on-device copy was dropped on confirmation...
    expect(deletePending).toHaveBeenCalledWith("id-abandon");
    // ...and it never uploaded the clip.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(statusFor("id-abandon")).toBeUndefined();
  });

  it("when the Gateway is unreachable, keeps the record (retries) and still never uploads", async () => {
    vi.mocked(getPending).mockResolvedValue(makeRecord("id-abandon-offline"));
    vi.mocked(abandonDictation).mockResolvedValue(false); // could not reach the Gateway

    await abandonPendingDictation("id-abandon-offline");

    // The abandon was attempted, but the on-device copy is NOT dropped (or the session would wedge locked)...
    expect(abandonDictation).toHaveBeenCalledWith("id-abandon-offline");
    expect(deletePending).not.toHaveBeenCalled();
    // ...and a cancelled clip is never uploaded, even while the abandon is still being retried.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });

  it("is a no-op for an id already gone (delivered, or abandoned from another surface)", async () => {
    vi.mocked(getPending).mockResolvedValue(null);

    await abandonPendingDictation("id-missing");

    expect(abandonDictation).not.toHaveBeenCalled();
    expect(deletePending).not.toHaveBeenCalled();
  });
});

describe("a record refusal from the Gateway (issue #2745): the driver keeps the reason and slows the loop", () => {
  const OPERATOR_MESSAGE =
    "The server's delivery record for this recording is damaged and needs an operator to look at it. Your recording is saved and cannot go through until that is fixed.";
  const RETRY_LATER_MESSAGE =
    "The server could not read this recording's delivery record just now. Your recording is saved and will try again.";
  const NEEDS_OPERATOR: DictationSubmitResult = {
    terminal: false, submitted: false, movedOn: false, transcript: "", error: OPERATOR_MESSAGE, recordRefusal: "needs-operator",
  };
  const RETRY_LATER: DictationSubmitResult = {
    terminal: false, submitted: false, movedOn: false, transcript: "", error: RETRY_LATER_MESSAGE, recordRefusal: "retry-later",
  };

  it("keeps the operator-needed reason on screen for a clip older than the throttle hour", async () => {
    // Without the refusal kind, heldMessage replaces every reason with the generic "still trying in the
    // background" line once the clip is an hour old - the wrong answer to a user whose recording cannot go
    // through until someone looks at a file on the server.
    const old = { ...makeRecord("id-old-refused"), createdAt: Date.now() - 2 * 60 * 60 * 1000 };
    vi.mocked(listPending).mockResolvedValue([old]);
    vi.mocked(uploadDictationToSession).mockResolvedValue(NEEDS_OPERATOR);

    await resumePendingDictations();

    const status = statusFor("id-old-refused");
    expect(status?.phase).toBe("held");
    expect(status?.error).toBe(OPERATOR_MESSAGE);
    expect(deletePending).not.toHaveBeenCalled();
  });

  it("retries an operator-needed refusal on the slow cadence from the first attempt, not the fast loop", async () => {
    const rec = makeRecord("id-refused-slow");
    vi.mocked(getPending).mockResolvedValue(rec);
    vi.mocked(uploadDictationToSession).mockResolvedValue(NEEDS_OPERATOR);

    await retryPendingDictation("id-refused-slow");
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);

    // The fast loop would have fired again within two seconds and several more times inside a minute.
    await vi.advanceTimersByTimeAsync(60_000);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);

    // It is still retried - the operator fixing the file is exactly what a later attempt would find.
    await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
  });

  it("retries a could-not-read-just-now refusal on the ordinary fast cadence (control)", async () => {
    const rec = makeRecord("id-refused-fast");
    vi.mocked(getPending).mockResolvedValue(rec);
    vi.mocked(uploadDictationToSession).mockResolvedValue(RETRY_LATER);

    await retryPendingDictation("id-refused-fast");
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(statusFor("id-refused-fast")?.error).toBe(RETRY_LATER_MESSAGE);

    await vi.advanceTimersByTimeAsync(60_000);
    expect(vi.mocked(uploadDictationToSession).mock.calls.length).toBeGreaterThan(2);
  });
});
