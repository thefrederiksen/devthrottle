// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Voice delivery phase 5, contract section 7, T6: a typed prompt the Gateway answered 202 "still delivering" is
// HELD - on the device and in the status strip - and only READ from then on, through
// GET /sessions/{sid}/prompts/{deliveryId}/outcome. Found by the phase 4 real-path check, case 2f: a typed prompt
// was answered "delivering" while the starved Director had in the end refused it and typed nothing, and nobody
// could ask.
//
// jsdom, because the wake-ups under test are the browser's own: a page becoming visible and the connection
// returning. The API client and the durable store are mocked (the store as an in-memory disk); the status store
// is real, so the published states are checked exactly as the strip reads them.

vi.mock("../api/client", () => ({
  sendPrompt: vi.fn(),
  readPromptOutcome: vi.fn(),
  // What backgroundSend.ts imports, for the load-wiring test below.
  readDictationOutcome: vi.fn(),
  uploadDictationToSession: vi.fn(),
  abandonDictation: vi.fn(),
}));
vi.mock("./heldPromptStore", () => ({
  saveHeldPrompt: vi.fn(),
  listHeldPrompts: vi.fn(),
  getHeldPrompt: vi.fn(),
  deleteHeldPrompt: vi.fn(),
}));
vi.mock("./pendingStore", () => ({
  savePending: vi.fn(),
  deletePending: vi.fn(),
  getPending: vi.fn(async () => null),
  listPending: vi.fn(async () => []),
}));

import { readPromptOutcome, sendPrompt, type DictationOutcomeRead, type DictationSubmitResult } from "../api/client";
import { resumePendingDictations } from "./backgroundSend";
import { deleteHeldPrompt, getHeldPrompt, listHeldPrompts, saveHeldPrompt, type HeldPrompt } from "./heldPromptStore";
import { allDictationStatuses, clearDictationStatus } from "./status";
import {
  checkTypedPromptNow,
  dismissTypedPrompt,
  resumeHeldPrompts,
  sendTypedPrompt,
  sendTypedPromptAnyway,
} from "./typedPromptDelivery";

const SID = "sid-typed";
const TEXT = "please run the tests";
const DELIVERY_ID = "0f0e0d0c0b0a09080706050403020100";

const HELD_202 = { delivering: true, directorState: "no-answer", deliveryId: DELIVERY_ID };
const DELIVERED_200 = { delivering: false, deliveryId: "aaaa0000aaaa0000aaaa0000aaaa0000" };

const STILL: DictationOutcomeRead = { kind: "delivering", directorState: "no-answer" };
const resolved = (result: Partial<DictationSubmitResult>): DictationOutcomeRead => ({
  kind: "resolved",
  result: { terminal: true, submitted: false, movedOn: false, transcript: "", ...result },
});
const DELIVERED = resolved({ submitted: true, transcript: TEXT });
const NOT_DELIVERED = resolved({ movedOn: true, movedOnReason: "not-delivered", offerSendAnyway: true, transcript: TEXT });
const UNCONFIRMED = resolved({ movedOn: true, movedOnReason: "unconfirmed", offerSendAnyway: false, transcript: TEXT });

// Far past any cadence: the read interval is 10 seconds, the old dictation retry capped at 5 minutes.
const FAR_PAST_ANY_CADENCE_MS = 20 * 60 * 1000;

// The shape of the Gateway error sendPrompt throws on a 502 (the real class lives in the mocked module).
class BadGateway extends Error {
  status = 502;
}

let disk: Map<string, HeldPrompt>;

function statusFor(id: string) {
  return allDictationStatuses().find((s) => s.uploadId === id);
}

function held(extra: Partial<HeldPrompt> = {}): HeldPrompt {
  return { deliveryId: DELIVERY_ID, sessionId: SID, text: TEXT, sentAt: Date.now(), ...extra };
}

function setHidden(hidden: boolean): void {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
}

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
  vi.mocked(saveHeldPrompt).mockImplementation(async (rec) => {
    disk.set(rec.deliveryId, rec);
  });
  vi.mocked(deleteHeldPrompt).mockImplementation(async (id) => {
    disk.delete(id);
  });
  vi.mocked(getHeldPrompt).mockImplementation(async (id) => disk.get(id) ?? null);
  vi.mocked(listHeldPrompts).mockImplementation(async () => [...disk.values()]);
});

afterEach(async () => {
  // Resolve anything still held so a timer from this test cannot read into the next one.
  for (const id of [...disk.keys()]) await dismissTypedPrompt(id);
  vi.clearAllTimers();
  vi.useRealTimers();
});

describe("a typed send answered 202 is held and never sent again", () => {
  it("timers far past any cadence, a visibility change and a connection return make exactly one prompt call, then only outcome reads", async () => {
    vi.mocked(sendPrompt).mockResolvedValueOnce(HELD_202);
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);

    const outcome = await sendTypedPrompt(SID, TEXT);

    expect(outcome).toBe("held");
    expect(disk.get(DELIVERY_ID)).toMatchObject({ deliveryId: DELIVERY_ID, sessionId: SID, text: TEXT });
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "held", delivering: true, typed: true, sessionId: SID });

    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    await fireEveryBrowserTrigger();
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);

    expect(sendPrompt).toHaveBeenCalledTimes(1);
    expect(vi.mocked(readPromptOutcome).mock.calls.length).toBeGreaterThan(10);
    for (const call of vi.mocked(readPromptOutcome).mock.calls) expect(call).toEqual([SID, DELIVERY_ID]);
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "held", delivering: true });
  });

  it("a hidden page reads nothing on the timer, and reads at once when it becomes visible", async () => {
    vi.mocked(sendPrompt).mockResolvedValueOnce(HELD_202);
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);
    await sendTypedPrompt(SID, TEXT);

    setHidden(true);
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    expect(readPromptOutcome).not.toHaveBeenCalled();

    setHidden(false);
    document.dispatchEvent(new Event("visibilitychange"));
    await vi.advanceTimersByTimeAsync(0);
    expect(readPromptOutcome).toHaveBeenCalledTimes(1);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });

  it("Check now reads at once and sends nothing", async () => {
    vi.mocked(sendPrompt).mockResolvedValueOnce(HELD_202);
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);
    await sendTypedPrompt(SID, TEXT);

    await checkTypedPromptNow(DELIVERY_ID);

    expect(readPromptOutcome).toHaveBeenCalledWith(SID, DELIVERY_ID);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });
});

describe("each outcome of a held typed prompt", () => {
  async function holdOne(): Promise<void> {
    vi.mocked(sendPrompt).mockResolvedValueOnce(HELD_202);
    await sendTypedPrompt(SID, TEXT);
  }

  it("still delivering keeps the strip on Still delivering and reads again", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);

    await checkTypedPromptNow(DELIVERY_ID);

    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "held", delivering: true, typed: true, retryable: true });
    expect(disk.has(DELIVERY_ID)).toBe(true);
  });

  it("delivered removes the held copy and shows done", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValueOnce(DELIVERED);

    await checkTypedPromptNow(DELIVERY_ID);

    expect(disk.has(DELIVERY_ID)).toBe(false);
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "done", typed: true });
    // Resolved: no further reads, however long the page stays open.
    vi.mocked(readPromptOutcome).mockClear();
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    await fireEveryBrowserTrigger();
    expect(readPromptOutcome).not.toHaveBeenCalled();
  });

  it("not delivered shows the words back with the label and offers Send anyway", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValueOnce(NOT_DELIVERED);

    await checkTypedPromptNow(DELIVERY_ID);

    expect(statusFor(DELIVERY_ID)).toMatchObject({
      phase: "dropped",
      typed: true,
      offerSendAnyway: true,
      recoverableText: TEXT,
      error: "This message was not delivered. Here is what you wrote - send it?",
    });
    // Kept on the device so a reload still shows it, and never read again: the ruling is final.
    expect(disk.get(DELIVERY_ID)).toMatchObject({ shownBack: true, shownBackReason: "not-delivered", offerSendAnyway: true });
    vi.mocked(readPromptOutcome).mockClear();
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    await fireEveryBrowserTrigger();
    expect(readPromptOutcome).not.toHaveBeenCalled();
    expect(sendPrompt).toHaveBeenCalledTimes(1);
  });

  it("could not confirm shows the words back with its label and does NOT offer Send anyway", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValueOnce(UNCONFIRMED);

    await checkTypedPromptNow(DELIVERY_ID);

    expect(statusFor(DELIVERY_ID)).toMatchObject({
      phase: "dropped",
      typed: true,
      offerSendAnyway: false,
      recoverableText: TEXT,
      error: "We could not confirm this message arrived. Here is what you wrote.",
    });
  });

  it("Send anyway on an unconfirmed prompt sends nothing", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValueOnce(UNCONFIRMED);
    await checkTypedPromptNow(DELIVERY_ID);
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});

    await sendTypedPromptAnyway(DELIVERY_ID);

    expect(sendPrompt).toHaveBeenCalledTimes(1);
    expect(disk.has(DELIVERY_ID)).toBe(true);
    errors.mockRestore();
  });

  it("a 404 is an error naming the delivery id, and nothing is sent again", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockResolvedValueOnce({ kind: "not-found" });
    const errors = vi.spyOn(console, "error").mockImplementation(() => {});

    await checkTypedPromptNow(DELIVERY_ID);

    const status = statusFor(DELIVERY_ID);
    expect(status).toMatchObject({ phase: "failed", typed: true });
    expect(status?.error).toContain(DELIVERY_ID);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
    expect(errors).toHaveBeenCalled();
    errors.mockRestore();
  });

  it("a read that did not happen keeps Still delivering and reads again later", async () => {
    await holdOne();
    vi.mocked(readPromptOutcome).mockRejectedValueOnce(new Error("offline"));
    const warns = vi.spyOn(console, "warn").mockImplementation(() => {});

    await checkTypedPromptNow(DELIVERY_ID);

    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "held", delivering: true });
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(readPromptOutcome).toHaveBeenCalledTimes(2);
    expect(sendPrompt).toHaveBeenCalledTimes(1);
    warns.mockRestore();
  });
});

describe("Send anyway on a typed prompt shown back not delivered", () => {
  it("makes exactly one FRESH typed send that carries no old id, and retires the old record", async () => {
    disk.set(DELIVERY_ID, held({ shownBack: true, shownBackReason: "not-delivered", offerSendAnyway: true }));
    vi.mocked(sendPrompt).mockResolvedValueOnce(DELIVERED_200);

    await Promise.all([sendTypedPromptAnyway(DELIVERY_ID), sendTypedPromptAnyway(DELIVERY_ID)]);

    expect(sendPrompt).toHaveBeenCalledTimes(1);
    const call = vi.mocked(sendPrompt).mock.calls[0];
    expect(call[0]).toBe(SID);
    expect(call[1]).toBe(TEXT);
    expect(call[2]).toBe(true);
    // No recording claim, no spoken id: the Gateway mints a fresh delivery id for it.
    expect(call[6]).toBeUndefined();
    expect(JSON.stringify(call)).not.toContain(DELIVERY_ID);
    expect(disk.has(DELIVERY_ID)).toBe(false);
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "done" });
  });

  it("a fresh send that is itself held is held under its NEW id, and the old strip goes", async () => {
    disk.set(DELIVERY_ID, held({ shownBack: true, shownBackReason: "not-delivered", offerSendAnyway: true }));
    const NEW_ID = "1111222233334444aaaabbbbccccdddd";
    vi.mocked(sendPrompt).mockResolvedValueOnce({ delivering: true, directorState: "no-answer", deliveryId: NEW_ID });
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);

    await sendTypedPromptAnyway(DELIVERY_ID);

    expect(disk.has(DELIVERY_ID)).toBe(false);
    expect(statusFor(DELIVERY_ID)).toBeUndefined();
    expect(disk.get(NEW_ID)).toMatchObject({ text: TEXT, sessionId: SID });
    expect(statusFor(NEW_ID)).toMatchObject({ phase: "held", delivering: true, typed: true });
  });

  it("a fresh send that fails keeps the words on screen and on the device", async () => {
    disk.set(DELIVERY_ID, held({ shownBack: true, shownBackReason: "not-delivered", offerSendAnyway: true }));
    vi.mocked(sendPrompt).mockRejectedValueOnce(new BadGateway("bad gateway"));

    await sendTypedPromptAnyway(DELIVERY_ID);

    expect(disk.has(DELIVERY_ID)).toBe(true);
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "dropped", recoverableText: TEXT, offerSendAnyway: true });
  });
});

describe("a reload with a held typed prompt", () => {
  it("reads the outcome and sends nothing", async () => {
    disk.set(DELIVERY_ID, held());
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);

    await resumeHeldPrompts();

    expect(readPromptOutcome).toHaveBeenCalledWith(SID, DELIVERY_ID);
    expect(sendPrompt).not.toHaveBeenCalled();
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "held", delivering: true, typed: true });
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    await fireEveryBrowserTrigger();
    expect(sendPrompt).not.toHaveBeenCalled();
  });

  it("is reached through resumePendingDictations, the load entry point both shells call", async () => {
    disk.set(DELIVERY_ID, held());
    vi.mocked(readPromptOutcome).mockResolvedValue(STILL);

    await resumePendingDictations();

    expect(readPromptOutcome).toHaveBeenCalledWith(SID, DELIVERY_ID);
    expect(sendPrompt).not.toHaveBeenCalled();
  });

  it("a prompt already shown back is shown again without a read", async () => {
    disk.set(DELIVERY_ID, held({ shownBack: true, shownBackReason: "unconfirmed", offerSendAnyway: false }));

    await resumeHeldPrompts();

    expect(readPromptOutcome).not.toHaveBeenCalled();
    expect(statusFor(DELIVERY_ID)).toMatchObject({ phase: "dropped", offerSendAnyway: false, recoverableText: TEXT });
  });
});

describe("a 200 and a 502 on a typed send behave exactly as before", () => {
  it("200 is delivered: nothing kept, nothing shown, nothing read", async () => {
    vi.mocked(sendPrompt).mockResolvedValueOnce(DELIVERED_200);

    const outcome = await sendTypedPrompt(SID, TEXT, { spokenSpans: [] });

    expect(outcome).toBe("delivered");
    expect(saveHeldPrompt).not.toHaveBeenCalled();
    expect(allDictationStatuses()).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(FAR_PAST_ANY_CADENCE_MS);
    expect(readPromptOutcome).not.toHaveBeenCalled();
  });

  it("502 throws the Gateway's error to the caller: nothing kept, nothing shown", async () => {
    vi.mocked(sendPrompt).mockRejectedValueOnce(new BadGateway("The session did not take that."));

    await expect(sendTypedPrompt(SID, TEXT)).rejects.toMatchObject({ status: 502 });

    expect(saveHeldPrompt).not.toHaveBeenCalled();
    expect(allDictationStatuses()).toHaveLength(0);
  });

  it("the spoken id and spans are passed straight through to the one prompt call", async () => {
    vi.mocked(sendPrompt).mockResolvedValueOnce(DELIVERED_200);
    const spans = [{ start: 0, length: 6, transcriptId: "utt-1" }];

    await sendTypedPrompt(SID, TEXT, { spokenDeliveryId: "utt-1", spokenSpans: spans });

    expect(sendPrompt).toHaveBeenCalledWith(SID, TEXT, true, undefined, "utt-1", spans);
  });
});
