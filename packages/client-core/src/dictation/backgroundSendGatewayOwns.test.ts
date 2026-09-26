// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Voice delivery phase 5 (#3398): once the Gateway has answered a complete - or a "Send anyway" - with 202, the
// GATEWAY drives that delivery to its end itself, and the client only reads what it ruled, through
// GET /dictation/{id}/outcome. Found by the phase 4 real-path check: the only retry lived in a browser tab, the
// tab was hidden, the browser froze its timers, and a recording sent to a live agent sat undelivered for 7
// minutes 44 seconds and was then shown back as too old.
//
// jsdom, not node, because the triggers under test are the browser's own: a page becoming visible and the
// connection returning. The API client and the durable store are mocked; the status store is real, so the
// published states are checked exactly as the strip reads them.

vi.mock("../api/client", () => ({
  uploadDictationToSession: vi.fn(),
  abandonDictation: vi.fn(),
  sendPrompt: vi.fn(),
  readDictationOutcome: vi.fn(),
}));
vi.mock("./pendingStore", () => ({
  savePending: vi.fn(),
  deletePending: vi.fn(),
  getPending: vi.fn(),
  listPending: vi.fn(),
}));

import {
  readDictationOutcome,
  sendPrompt,
  uploadDictationToSession,
  type DictationOutcomeRead,
  type DictationSubmitResult,
} from "../api/client";
import {
  backgroundTranscribeAndSend,
  dismissDictationStatus,
  resumePendingDictations,
  retryPendingDictation,
  sendDroppedDictationAnyway,
  type CapturedUtterance,
} from "./backgroundSend";
import { deletePending, getPending, listPending, savePending, type PendingDictation } from "./pendingStore";
import { allDictationStatuses, clearDictationStatus } from "./status";

const SENT_AT = Date.parse("2026-09-25T09:05:12.345Z");
const captured: CapturedUtterance = { sentAt: SENT_AT, blob: new Blob(["x"]), recordedMs: 1000, prefixText: "" };

const DELIVERING_202: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  delivering: true,
  directorState: "waiting-for-director",
};
const NETWORK_HELD: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  error: "No connection - your recording is saved and will keep trying.",
};
const BAD_GATEWAY_HELD: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  error: "The transcription service is temporarily unavailable - your recording is saved and will keep trying.",
};
const SUBMITTED: DictationSubmitResult = { terminal: true, submitted: true, movedOn: false, transcript: "hi" };

const STILL: DictationOutcomeRead = { kind: "delivering", directorState: "no-answer" };
const resolved = (result: Partial<DictationSubmitResult>): DictationOutcomeRead => ({
  kind: "resolved",
  result: { terminal: true, submitted: false, movedOn: false, transcript: "", ...result },
});
const DELIVERED = resolved({ submitted: true, transcript: "the words" });
const TOO_OLD = resolved({ movedOn: true, movedOnReason: "too-old", offerSendAnyway: true, transcript: "the words I said" });
const UNCONFIRMED = resolved({ movedOn: true, movedOnReason: "unconfirmed", offerSendAnyway: false, transcript: "the words I said" });
// The Gateway's F4 ruling (phase 5): a session that really ended is resolved with NO "Send anyway" - there
// is no session left to send anything to. The words are handed back with the ended-session label.
const SESSION_EXITED = resolved({ movedOn: true, movedOnReason: "session-exited", offerSendAnyway: false, transcript: "the words I said" });
const SESSION_EXITED_NO_WORDS = resolved({ movedOn: true, movedOnReason: "session-exited", offerSendAnyway: false, transcript: "" });

// Far past every cadence the old client used: its fast retries capped at 15 seconds, its slow one at 5 minutes.
const FAR_PAST_THE_OLD_CADENCE_MS = 20 * 60 * 1000;

function statusFor(uploadId: string) {
  return allDictationStatuses().find((s) => s.uploadId === uploadId);
}

function record(id: string, extra: Partial<PendingDictation> = {}): PendingDictation {
  return {
    id,
    sessionId: "sid",
    blob: new Blob(["x"]),
    recordedMs: 1000,
    before: "",
    after: "",
    prefix: "",
    createdAt: Date.now(),
    sentAt: SENT_AT,
    ...extra,
  };
}

function shownBack(id: string, extra: Partial<PendingDictation> = {}): PendingDictation {
  return record(id, {
    staleDropped: true,
    droppedTranscript: "the words I said",
    droppedReason: "too-old",
    droppedOfferSendAnyway: true,
    ...extra,
  });
}

// The durable store the driver sees: whatever it last saved, per id, so a read after a save sees the save.
let disk: Map<string, PendingDictation>;

function setHidden(hidden: boolean): void {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
}

// Every trigger the browser can raise: the page comes back into view, and the connection returns.
async function fireEveryBrowserTrigger(): Promise<void> {
  setHidden(false);
  document.dispatchEvent(new Event("visibilitychange"));
  window.dispatchEvent(new Event("online"));
  await vi.advanceTimersByTimeAsync(0);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers();
  setHidden(false);
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
  disk = new Map();
  vi.mocked(savePending).mockImplementation(async (rec) => {
    disk.set(rec.id, rec);
  });
  vi.mocked(deletePending).mockImplementation(async (id) => {
    disk.delete(id);
  });
  vi.mocked(getPending).mockImplementation(async (id) => disk.get(id) ?? null);
  vi.mocked(listPending).mockImplementation(async () => [...disk.values()]);
  vi.mocked(sendPrompt).mockResolvedValue({ delivering: false });
  vi.mocked(readDictationOutcome).mockResolvedValue(STILL);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("after the Gateway acknowledged the complete (202), the client never drives the delivery again", () => {
  it("timers far past the old cadence, the page becoming visible and the connection returning: no complete, no prompt - only reads", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);

    await backgroundTranscribeAndSend("sid", captured);
    const id = vi.mocked(savePending).mock.calls[0][0].id;

    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    await fireEveryBrowserTrigger();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);

    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(readDictationOutcome).toHaveBeenCalled();
    expect(vi.mocked(readDictationOutcome).mock.calls.every((c) => c[0] === id)).toBe(true);
    expect(statusFor(id)?.delivering).toBe(true);
  });

  it("marks the record on disk as held by the Gateway, so a reload knows", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);

    await backgroundTranscribeAndSend("sid", captured);

    const id = vi.mocked(savePending).mock.calls[0][0].id;
    expect(disk.get(id)?.heldByGateway).toBe(true);
    expect(deletePending).not.toHaveBeenCalled();
  });

  it("reads on a modest timer while the page is visible, and not at all while it is hidden", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);
    await backgroundTranscribeAndSend("sid", captured);

    await vi.advanceTimersByTimeAsync(10_000);
    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(readDictationOutcome).toHaveBeenCalledTimes(2);

    setHidden(true);
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    // The one timer already set fires, finds the page hidden, and reads nothing - and sets no further timer.
    expect(readDictationOutcome).toHaveBeenCalledTimes(2);
    // The page coming back into view reads at once.
    await fireEveryBrowserTrigger();
    expect(readDictationOutcome).toHaveBeenCalledTimes(3);
  });
});

describe("a Send anyway answered 202 is never pressed again", () => {
  it("timers far past the old cadence, the page becoming visible and the connection returning: one prompt in total", async () => {
    disk.set("id-anyway", shownBack("id-anyway"));
    vi.mocked(sendPrompt).mockResolvedValue({ delivering: true, directorState: "no-answer" });

    await sendDroppedDictationAnyway("id-anyway");
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    await fireEveryBrowserTrigger();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);

    expect(sendPrompt).toHaveBeenCalledTimes(1);
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(readDictationOutcome).toHaveBeenCalled();
    expect(disk.get("id-anyway")?.sendingAnyway).toBe(true); // the mark stays on disk
    expect(statusFor("id-anyway")?.delivering).toBe(true);
    expect(statusFor("id-anyway")?.offerSendAnyway).toBeUndefined();
  });

  it("a second press of the button on a marked record reads instead of pressing", async () => {
    disk.set("id-anyway-twice", shownBack("id-anyway-twice", { sendingAnyway: true }));

    await sendDroppedDictationAnyway("id-anyway-twice");

    expect(sendPrompt).not.toHaveBeenCalled();
    expect(readDictationOutcome).toHaveBeenCalledWith("id-anyway-twice");
  });
});

describe("rendering what /outcome says", () => {
  async function heldThenRead(read: DictationOutcomeRead): Promise<string> {
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);
    await backgroundTranscribeAndSend("sid", captured);
    const id = vi.mocked(savePending).mock.calls[0][0].id;
    vi.mocked(readDictationOutcome).mockResolvedValue(read);
    await vi.advanceTimersByTimeAsync(10_000);
    return id;
  }

  it("202: Still delivering, calm, with nothing that sends a second copy", async () => {
    const id = await heldThenRead(STILL);

    const status = statusFor(id);
    expect(status?.phase).toBe("held");
    expect(status?.delivering).toBe(true);
    expect(status?.error).toContain("Still delivering");
    expect(status?.recoverableText).toBeUndefined();
    expect(status?.offerSendAnyway).toBeUndefined();
  });

  it("200 delivered: the copy is deleted and the send shows done", async () => {
    const id = await heldThenRead(DELIVERED);

    expect(deletePending).toHaveBeenCalledWith(id);
    expect(disk.has(id)).toBe(false);
    expect(statusFor(id)?.phase).toBe("done");
    // Resolved: no further reads.
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
  });

  it("200 too old: the words and Send anyway, with the age wording, the copy kept", async () => {
    const id = await heldThenRead(TOO_OLD);

    const status = statusFor(id);
    expect(status?.phase).toBe("dropped");
    expect(status?.recoverableText).toBe("the words I said");
    expect(status?.offerSendAnyway).toBe(true);
    expect(status?.error).toContain("more than 5 minutes old");
    const onDisk = disk.get(id);
    expect(onDisk?.staleDropped).toBe(true);
    expect(onDisk?.heldByGateway).toBeUndefined();
    expect(onDisk?.droppedReason).toBe("too-old");
  });

  it("200 unconfirmed: the words, the label and Dismiss - no Send anyway, and nothing more is sent or read", async () => {
    const id = await heldThenRead(UNCONFIRMED);

    const status = statusFor(id);
    expect(status?.phase).toBe("dropped");
    expect(status?.error).toBe("We could not confirm this arrived. Here is what you said.");
    expect(status?.recoverableText).toBe("the words I said");
    expect(status?.offerSendAnyway).toBe(false);
    expect(status?.retryable).toBe(false);
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    await fireEveryBrowserTrigger();
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
  });

  it("200 session ended: the words, the ended-session label and Dismiss only - and it is final", async () => {
    const id = await heldThenRead(SESSION_EXITED);

    const status = statusFor(id);
    expect(status?.phase).toBe("dropped");
    expect(status?.error).toBe("The session has ended, so this recording was not sent. Here is what you said.");
    expect(status?.recoverableText).toBe("the words I said");
    expect(status?.offerSendAnyway).toBe(false); // the button comes only from the Gateway's offer
    expect(status?.retryable).toBe(false); // Dismiss is the only action
    const onDisk = disk.get(id);
    expect(onDisk?.staleDropped).toBe(true);
    expect(onDisk?.droppedReason).toBe("session-exited");
    expect(onDisk?.droppedOfferSendAnyway).toBe(false);
    // Final: no further complete, prompt or read for this record, ever.
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    await fireEveryBrowserTrigger();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
  });

  it("200 session ended with no words: the ended-session label that points at the saved recording, Dismiss only", async () => {
    const id = await heldThenRead(SESSION_EXITED_NO_WORDS);

    const status = statusFor(id);
    expect(status?.phase).toBe("dropped");
    expect(status?.error).toBe("The session has ended, so this recording was not sent. Your recording is saved on your device.");
    expect(status?.offerSendAnyway).toBe(false);
    expect(status?.retryable).toBe(false); // no words, and the Gateway offered no fresh send either
    expect(disk.has(id)).toBe(true); // the audio is kept until the owner dismisses it
  });

  it("a session-ended record survives a reload and drives nothing on it", async () => {
    disk.set("id-ended", shownBack("id-ended", { droppedReason: "session-exited", droppedOfferSendAnyway: false }));

    await resumePendingDictations();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    await fireEveryBrowserTrigger();

    const status = statusFor("id-ended");
    expect(status?.phase).toBe("dropped");
    expect(status?.error).toBe("The session has ended, so this recording was not sent. Here is what you said.");
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(readDictationOutcome).not.toHaveBeenCalled();
  });

  it("Dismiss on a session-ended record deletes the copy", async () => {
    const id = await heldThenRead(SESSION_EXITED);

    await dismissDictationStatus(id);

    expect(disk.has(id)).toBe(false);
    expect(deletePending).toHaveBeenCalledWith(id);
    expect(statusFor(id)).toBeUndefined();
  });

  it("404 for a record the client marked as held: an error naming the upload id, the copy kept, nothing re-driven", async () => {
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});
    const id = await heldThenRead({ kind: "not-found" });

    const status = statusFor(id);
    expect(status?.phase).toBe("failed");
    expect(status?.error).toContain(id);
    expect(disk.get(id)?.heldByGateway).toBe(true);
    expect(deletePending).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(errors.mock.calls.some((c) => String(c[0]).includes(id))).toBe(true);
    errors.mockRestore();
  });

  it("a read that does not happen keeps Still delivering and reads again later - it never re-drives", async () => {
    const warns = vi.spyOn(console, "warn").mockImplementation(() => {});
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);
    await backgroundTranscribeAndSend("sid", captured);
    const id = vi.mocked(savePending).mock.calls[0][0].id;
    vi.mocked(readDictationOutcome).mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await vi.advanceTimersByTimeAsync(10_000);
    expect(statusFor(id)?.delivering).toBe(true);
    vi.mocked(readDictationOutcome).mockResolvedValue(DELIVERED);
    await vi.advanceTimersByTimeAsync(10_000);

    expect(statusFor(id)?.phase).toBe("done");
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    warns.mockRestore();
  });

  it("a Send anyway read back delivered ends in done; read back too old shows the record's own words again", async () => {
    disk.set("id-a", shownBack("id-a", { sendingAnyway: true }));
    disk.set("id-b", shownBack("id-b", { sendingAnyway: true, before: "typed", droppedTranscript: "spoken" }));
    vi.mocked(readDictationOutcome).mockImplementation(async (id) =>
      id === "id-a" ? DELIVERED : resolved({ movedOn: true, movedOnReason: "too-old", offerSendAnyway: true, transcript: "typed spoken" }),
    );

    await resumePendingDictations();

    expect(statusFor("id-a")?.phase).toBe("done");
    expect(disk.has("id-a")).toBe(false);
    // The words shown are what the press sent - typed text and spoken words, once each.
    expect(statusFor("id-b")?.recoverableText).toBe("typed spoken");
    expect(statusFor("id-b")?.offerSendAnyway).toBe(true);
    expect(disk.get("id-b")?.sendingAnyway).toBeUndefined();
    expect(sendPrompt).not.toHaveBeenCalled();
  });
});

describe("before the Gateway owns it, the old retry is unchanged", () => {
  it("a network error on the complete is retried as today", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValueOnce(NETWORK_HELD).mockResolvedValueOnce(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);
    const id = vi.mocked(savePending).mock.calls[0][0].id;
    await vi.advanceTimersByTimeAsync(3_000);

    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    expect(vi.mocked(uploadDictationToSession).mock.calls[1][0].uploadId).toBe(id);
    expect(vi.mocked(uploadDictationToSession).mock.calls[1][0].resumed).toBe(true);
    expect(statusFor(id)?.phase).toBe("done");
    expect(readDictationOutcome).not.toHaveBeenCalled();
  });

  it("a 502 on the complete (the Gateway did not take it over) is retried as today", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValueOnce(BAD_GATEWAY_HELD).mockResolvedValueOnce(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);
    await vi.advanceTimersByTimeAsync(3_000);

    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    expect(readDictationOutcome).not.toHaveBeenCalled();
  });
});

describe("a reload", () => {
  it("with a record marked held: reads /outcome and sends nothing", async () => {
    disk.set("id-held", record("id-held", { heldByGateway: true }));

    await resumePendingDictations();

    expect(readDictationOutcome).toHaveBeenCalledWith("id-held");
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(statusFor("id-held")?.delivering).toBe(true);
  });

  it("with an old client's sendingAnyway record: reads /outcome and presses nothing", async () => {
    disk.set("id-old-anyway", shownBack("id-old-anyway", { sendingAnyway: true }));

    await resumePendingDictations();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);

    expect(readDictationOutcome).toHaveBeenCalledWith("id-old-anyway");
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });

  it("with an old client's unmarked record the Gateway already owns: read first, found held, never completed again", async () => {
    disk.set("id-old-held", record("id-old-held"));

    await resumePendingDictations();
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);

    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(disk.get("id-old-held")?.heldByGateway).toBe(true);
    expect(statusFor("id-old-held")?.delivering).toBe(true);
  });

  it("with an unmarked record the Gateway does not own (404): uploads it as before", async () => {
    disk.set("id-new", record("id-new"));
    vi.mocked(readDictationOutcome).mockResolvedValue({ kind: "not-found" });
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await resumePendingDictations();

    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    expect(statusFor("id-new")?.phase).toBe("done");
  });
});

describe("what the Gateway handed back (voice delivery phase 5, review round)", () => {
  // The Delivery Lead's ruling on the review's finding 1: a Gateway-driven attempt that hits out of credits,
  // a permanent transcription failure, or an incomplete upload must never end in a 404 "lost track" dead
  // end. The outcome read answers the same body the complete path gives, the client clears its "held by the
  // Gateway" mark, shows the state it already has for that answer, and nothing is deleted - Dismiss is never
  // the only action.
  async function ownedThenRead(read: DictationOutcomeRead): Promise<string> {
    vi.mocked(uploadDictationToSession).mockResolvedValue(DELIVERING_202);
    await backgroundTranscribeAndSend("sid", captured);
    const id = vi.mocked(savePending).mock.calls[0][0].id;
    vi.mocked(readDictationOutcome).mockResolvedValue(read);
    await vi.advanceTimersByTimeAsync(10_000);
    return id;
  }

  it("out of credits: the held strip with the Gateway's copy, Retry, and a later complete delivers once", async () => {
    const id = await ownedThenRead({ kind: "out-of-credits", message: "Out of transcription credits - your recording is saved and will send when credits are added." });

    const status = statusFor(id);
    expect(status?.phase).toBe("held");
    expect(status?.error).toContain("Out of transcription credits");
    expect(status?.retryable).toBe(true); // Retry is offered
    expect(status?.delivering).toBeUndefined(); // the Gateway no longer owns it
    expect(disk.get(id)?.heldByGateway).toBeUndefined(); // the mark is cleared on disk
    expect(deletePending).not.toHaveBeenCalled(); // nothing is destroyed

    // The owner's Retry (and the throttled auto-retry) is a new complete - it re-enters and delivers once.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    await retryPendingDictation(id);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    expect(statusFor(id)?.phase).toBe("done");
  });

  it("a permanent failure: parked with Retry, and an explicit Retry re-drives it", async () => {
    const id = await ownedThenRead({ kind: "permanent", reason: "unsupported-format" });

    const status = statusFor(id);
    expect(status?.phase).toBe("parked");
    expect(status?.error).toContain("format we can't transcribe");
    expect(status?.retryable).toBe(true); // Retry is offered, not only Dismiss
    expect(disk.get(id)?.heldByGateway).toBeUndefined();
    expect(disk.get(id)?.parkedReason).toBe("unsupported-format");
    expect(deletePending).not.toHaveBeenCalled();

    // Parked: no automatic trigger re-drives it.
    await vi.advanceTimersByTimeAsync(FAR_PAST_THE_OLD_CADENCE_MS);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
    // The owner's explicit Retry re-enters and delivers once.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    await retryPendingDictation(id);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    expect(statusFor(id)?.phase).toBe("done");
  });

  it("an incomplete upload: the client resumes its own chunk upload and delivers", async () => {
    const id = await ownedThenRead({ kind: "incomplete", missing: [0] });

    expect(disk.get(id)?.heldByGateway).toBeUndefined(); // the mark is cleared: the client drives again
    expect(deletePending).not.toHaveBeenCalled();
    expect(statusFor(id)?.delivering).toBeUndefined();

    // The ordinary driver resumes the upload on its own cadence (a fresh register, the missing chunks, a
    // complete that takes the delivery over again) and it delivers once.
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);
    await vi.advanceTimersByTimeAsync(3_000);
    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    expect(statusFor(id)?.phase).toBe("done");
  });
});

describe("Check now", () => {
  it("reads /outcome once, at once, and sends nothing", async () => {
    disk.set("id-check", record("id-check", { heldByGateway: true }));

    await retryPendingDictation("id-check");

    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
    expect(readDictationOutcome).toHaveBeenCalledWith("id-check");
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();
  });

  it("on a Send anyway still delivering: reads once, and presses nothing", async () => {
    disk.set("id-check-anyway", shownBack("id-check-anyway", { sendingAnyway: true }));

    await retryPendingDictation("id-check-anyway");

    expect(readDictationOutcome).toHaveBeenCalledTimes(1);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });
});
