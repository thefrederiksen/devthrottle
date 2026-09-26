import { readPromptOutcome, sendPrompt, type DictationOutcomeRead } from "../api/client";
import type { SpokenSpan } from "./composerProvenance";
import { activeAccount, listAccounts } from "../auth/accountStore";
import { deleteHeldPrompt, getHeldPrompt, listHeldPrompts, saveHeldPrompt, type HeldPrompt } from "./heldPromptStore";
import { clearDictationStatus, publishDictationStatus } from "./status";

// A typed prompt the Gateway has not finished delivering (voice delivery phase 5, contract section 7, T6).
//
// Why: in the phase 4 real-path check (case 2f) a typed prompt was answered "delivering" while the starved
// Director had in the end refused it and typed nothing. The not-delivered outcome was only in the Director's
// log, and a typed prompt carried no delivery id, so nobody could ask. Now the Gateway mints a delivery id for
// every prompt, and when it cannot yet say whether the words arrived it answers 202 and asks the Director
// itself. The owner's surfaces only READ what it rules - they never send the words again on their own.
//
// The flow:
// - A typed send answered 200 is delivered, exactly as before. A 502 (or a network failure) throws, exactly as
//   before, and the surface restores the words to the box.
// - A 202 `{ delivering, directorState, deliveryId }`: the delivery id, session id and text are written to the
//   device (heldPromptStore), and the words are shown held in the SAME status strip the recordings use, "Still
//   delivering" with "Check now".
// - The outcome is read (GET /sessions/{sid}/prompts/{deliveryId}/outcome, through the one outcome reader the
//   recordings use) on the same wake-ups as a held recording: app load, the page becoming visible, the
//   connection returning, a modest timer while visible, and "Check now". Nothing on any of those sends.
// - Delivered: the copy is removed and the strip shows "Sent". Not delivered: the words come back with "Send
//   anyway" (a FRESH typed send, which the Gateway gives a fresh id) and Dismiss. Could not confirm: the words
//   come back with Dismiss only, because they may already be in.

// How often an owned delivery is read while the page is visible - the recordings' own interval. A hidden page
// does not read on this timer at all; it reads when it becomes visible again.
const OUTCOME_READ_INTERVAL_MS = 10_000;

const STILL_DELIVERING_MESSAGE =
  "Still delivering - checking that your message reached the session. It will not be sent twice.";
const NOT_DELIVERED_MESSAGE = "This message was not delivered. Here is what you wrote - send it?";
const UNCONFIRMED_MESSAGE = "We could not confirm this message arrived. Here is what you wrote.";
const SEND_ANYWAY_FAILED_MESSAGE = "Couldn't send that just now. Your words are still here - try again.";
const NO_DELIVERY_ID_MESSAGE =
  "The server said it is still delivering this message but gave no delivery id, so it cannot be followed. It was not sent again - check the session to see whether it arrived.";

function notFoundMessage(deliveryId: string): string {
  return `The server has no record of typed message ${deliveryId}, which it was still delivering. It was not sent again - check the session to see whether it arrived.`;
}

function unexpectedAnswerMessage(deliveryId: string): string {
  return `The server's answer for typed message ${deliveryId} was neither delivered nor shown back. It was not sent again - check the session to see whether it arrived.`;
}

/** What a typed send came to: delivered at once, or held by the Gateway and now shown in the status strip. */
export type TypedSendOutcome = "delivered" | "held";

export interface TypedSendOptions {
  /** Passed straight through to sendPrompt: the id of the one transcript this whole turn is (ruling R10). */
  spokenDeliveryId?: string;
  /** Passed straight through to sendPrompt: which characters of the text were spoken. */
  spokenSpans?: readonly SpokenSpan[];
}

// Upload-free state: which held prompts are being read right now, and the read timers.
const _inFlight = new Set<string>();
const _timers = new Map<string, ReturnType<typeof setTimeout>>();
// Held prompts this device could not write to its store (the failure is logged): kept here so this page still
// reads them on the timer and on "Check now". A reload cannot find them - that is what the log line says.
const _unkept = new Map<string, HeldPrompt>();
let _listenersInstalled = false;

/**
 * Send a typed prompt from one of the owner's surfaces (the Cockpit composer, the phone's controls).
 * Throws exactly as sendPrompt does on a 502 or a network failure, so the caller keeps its behaviour there.
 * On a 202 the words are held on the device and in the status strip, and the outcome is read from then on -
 * the caller must not report the send as delivered, and must not restore the words to the box.
 */
export async function sendTypedPrompt(
  sessionId: string,
  text: string,
  options: TypedSendOptions = {},
): Promise<TypedSendOutcome> {
  const answer = await sendPrompt(sessionId, text, true, undefined, options.spokenDeliveryId, options.spokenSpans);
  if (!answer.delivering) return "delivered";
  ensureListeners();
  if (answer.deliveryId === undefined) {
    // A 202 with nothing to read it by. The words may be in, so they are not handed back for a second send.
    console.error(`[typedPromptDelivery] the Gateway answered 202 for a typed prompt in ${sessionId} with no deliveryId`);
    publishDictationStatus({
      sessionId,
      uploadId: `typed-${Date.now()}`,
      phase: "failed",
      retryable: false,
      typed: true,
      error: NO_DELIVERY_ID_MESSAGE,
    });
    return "held";
  }
  const rec: HeldPrompt = {
    deliveryId: answer.deliveryId,
    sessionId,
    text,
    sentAt: Date.now(),
    accountId: activeAccount()?.id,
  };
  try {
    await saveHeldPrompt(rec);
  } catch (err) {
    // The device could not keep it. This page still reads the outcome; only a reload loses track of it.
    console.error(`[typedPromptDelivery] could not keep held typed message ${rec.deliveryId} on this device: ${errText(err)}`);
    _unkept.set(rec.deliveryId, rec);
  }
  publishDelivering(rec);
  scheduleOutcomeRead(rec);
  return "held";
}

/** App load: every held typed prompt of the active account is read (or, if already shown back, shown again).
 *  Nothing is sent. Called from resumePendingDictations, the one load entry point both shells already use. */
export async function resumeHeldPrompts(): Promise<void> {
  ensureListeners();
  let all: HeldPrompt[];
  try {
    all = await listHeldPrompts();
  } catch {
    return; // no durable store; nothing to resume
  }
  await Promise.all(
    all.filter(isOurs).map((rec) => {
      if (rec.shownBack) {
        publishShownBack(rec);
        return Promise.resolve();
      }
      return readHeld(rec);
    }),
  );
}

/** "Check now" on a held typed prompt: read the ruling at once. Never sends. */
export async function checkTypedPromptNow(deliveryId: string): Promise<void> {
  let rec: HeldPrompt | null;
  try {
    rec = await findHeld(deliveryId);
  } catch {
    return;
  }
  if (rec === null) {
    clearDictationStatus(deliveryId);
    return;
  }
  if (rec.shownBack) {
    publishShownBack(rec);
    return;
  }
  await readHeld(rec);
}

/**
 * "Send anyway" on a typed prompt the Gateway ruled not delivered: a FRESH typed send of the same words, which
 * the Gateway gives a fresh delivery id - the old id is never sent again. Only when the Gateway offered it.
 * Guarded so two taps, or two mounted strips, make one send.
 */
export async function sendTypedPromptAnyway(deliveryId: string): Promise<void> {
  if (_inFlight.has(deliveryId)) return;
  _inFlight.add(deliveryId);
  try {
    let rec: HeldPrompt | null;
    try {
      rec = await findHeld(deliveryId);
    } catch {
      return;
    }
    if (rec === null) {
      clearDictationStatus(deliveryId);
      return;
    }
    if (rec.shownBack !== true || rec.offerSendAnyway !== true) {
      console.error(`[typedPromptDelivery] Send anyway refused for typed message ${deliveryId}: the Gateway did not offer it`);
      if (rec.shownBack) publishShownBack(rec);
      return;
    }
    let outcome: TypedSendOutcome;
    try {
      outcome = await sendTypedPrompt(rec.sessionId, rec.text);
    } catch {
      // Keep the record and the words on screen; the owner decides again.
      publishShownBack(rec, SEND_ANYWAY_FAILED_MESSAGE);
      return;
    }
    await forgetHeld(rec.deliveryId);
    if (outcome === "delivered") {
      publishDictationStatus({ sessionId: rec.sessionId, uploadId: rec.deliveryId, phase: "done", typed: true });
    } else {
      // The fresh send is held under its own new id and shows its own strip.
      clearDictationStatus(rec.deliveryId);
    }
  } finally {
    _inFlight.delete(deliveryId);
  }
}

/** Dismiss a typed prompt shown back: the owner has read the words and does not want them. */
export async function dismissTypedPrompt(deliveryId: string): Promise<void> {
  clearScheduled(deliveryId);
  clearDictationStatus(deliveryId);
  try {
    await forgetHeld(deliveryId);
  } catch {
    /* the strip is already gone; a leftover record is shown again on the next load */
  }
}

// ---- internals -------------------------------------------------------------------------------------

async function findHeld(deliveryId: string): Promise<HeldPrompt | null> {
  return (await getHeldPrompt(deliveryId)) ?? _unkept.get(deliveryId) ?? null;
}

async function keepHeld(rec: HeldPrompt): Promise<void> {
  if (_unkept.has(rec.deliveryId)) {
    _unkept.set(rec.deliveryId, rec);
    return;
  }
  try {
    await saveHeldPrompt(rec);
  } catch (err) {
    console.error(`[typedPromptDelivery] could not keep typed message ${rec.deliveryId} on this device: ${errText(err)}`);
    _unkept.set(rec.deliveryId, rec);
  }
}

async function forgetHeld(deliveryId: string): Promise<void> {
  _unkept.delete(deliveryId);
  await deleteHeldPrompt(deliveryId);
}

// A held prompt belongs to the account that sent it. One with no stamp is read only while this browser holds a
// single account, where it has no other owner - the same rule as a recording.
function isOurs(rec: HeldPrompt): boolean {
  if (rec.accountId) return rec.accountId === activeAccount()?.id;
  return listAccounts().length <= 1;
}

// Read the Gateway's ruling for one held prompt and render it. One read at a time per delivery id.
async function readHeld(rec: HeldPrompt): Promise<void> {
  if (_inFlight.has(rec.deliveryId)) return;
  _inFlight.add(rec.deliveryId);
  clearScheduled(rec.deliveryId);
  try {
    let read: DictationOutcomeRead;
    try {
      read = await readPromptOutcome(rec.sessionId, rec.deliveryId);
    } catch (err) {
      // The read did not happen. The Gateway is still asking the Director; read again later.
      console.warn(`[typedPromptDelivery] could not read the outcome of typed message ${rec.deliveryId}: ${errText(err)}`);
      publishDelivering(rec);
      scheduleOutcomeRead(rec);
      return;
    }
    await applyOutcome(rec, read);
  } finally {
    _inFlight.delete(rec.deliveryId);
  }
}

async function applyOutcome(rec: HeldPrompt, read: DictationOutcomeRead): Promise<void> {
  if (read.kind === "delivering") {
    publishDelivering(rec);
    scheduleOutcomeRead(rec);
    return;
  }
  if (read.kind === "not-found") {
    // Never a reason to send again: the words may already be in. The copy is kept on the device.
    console.error(`[typedPromptDelivery] outcome read for typed message ${rec.deliveryId} answered 404, but the Gateway was holding it`);
    publishFailed(rec, notFoundMessage(rec.deliveryId));
    return;
  }
  const result = read.result;
  if (result.submitted) {
    await forgetHeld(rec.deliveryId);
    publishDictationStatus({ sessionId: rec.sessionId, uploadId: rec.deliveryId, phase: "done", typed: true });
    return;
  }
  if (result.movedOn) {
    const shown: HeldPrompt = {
      ...rec,
      shownBack: true,
      shownBackReason: result.movedOnReason,
      offerSendAnyway: result.offerSendAnyway,
    };
    await keepHeld(shown);
    publishShownBack(shown);
    return;
  }
  console.error(`[typedPromptDelivery] outcome for typed message ${rec.deliveryId} was neither delivered nor shown back`);
  publishFailed(rec, unexpectedAnswerMessage(rec.deliveryId));
}

function scheduleOutcomeRead(rec: HeldPrompt): void {
  clearScheduled(rec.deliveryId);
  const t = setTimeout(() => {
    _timers.delete(rec.deliveryId);
    if (typeof document !== "undefined" && document.hidden) return;
    void readHeldById(rec.deliveryId);
  }, OUTCOME_READ_INTERVAL_MS);
  _timers.set(rec.deliveryId, t);
}

async function readHeldById(deliveryId: string): Promise<void> {
  let rec: HeldPrompt | null;
  try {
    rec = await findHeld(deliveryId);
  } catch {
    return;
  }
  // Gone (delivered, dismissed) or shown back since the timer was set: nothing to read.
  if (rec === null) return;
  if (rec.shownBack) return;
  await readHeld(rec);
}

// The page became visible or the connection returned: read every held prompt now.
async function kickHeldPrompts(): Promise<void> {
  let all: HeldPrompt[];
  try {
    all = await listHeldPrompts();
  } catch {
    return;
  }
  for (const rec of [...all, ..._unkept.values()]) {
    if (rec.shownBack || !isOurs(rec)) continue;
    void readHeld(rec);
  }
}

function ensureListeners(): void {
  if (_listenersInstalled) return;
  if (typeof window === "undefined") return;
  _listenersInstalled = true;
  window.addEventListener("online", () => void kickHeldPrompts());
  if (typeof document !== "undefined") {
    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) void kickHeldPrompts();
    });
  }
}

function clearScheduled(id: string): void {
  const t = _timers.get(id);
  if (t !== undefined) {
    clearTimeout(t);
    _timers.delete(id);
  }
}

function publishDelivering(rec: HeldPrompt): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.deliveryId,
    phase: "held",
    retryable: true,
    delivering: true,
    typed: true,
    error: STILL_DELIVERING_MESSAGE,
  });
}

// The words come back. "Send anyway" only when the Gateway offered it; "could not confirm" gets Dismiss only.
function publishShownBack(rec: HeldPrompt, message?: string): void {
  const offer = rec.offerSendAnyway === true;
  const unconfirmed = rec.shownBackReason === "unconfirmed" || !offer;
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.deliveryId,
    phase: "dropped",
    retryable: false,
    typed: true,
    offerSendAnyway: offer,
    recoverableText: rec.text,
    error: message ?? (unconfirmed ? UNCONFIRMED_MESSAGE : NOT_DELIVERED_MESSAGE),
  });
}

function publishFailed(rec: HeldPrompt, message: string): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.deliveryId,
    phase: "failed",
    retryable: false,
    typed: true,
    error: message,
  });
}

function errText(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
