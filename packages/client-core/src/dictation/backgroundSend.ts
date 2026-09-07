import { abandonDictation, sendPrompt, uploadDictationToSession, type RecordRefusalKind } from "../api/client";
import { captureLossWarning, logCaptureHealth } from "./captureHealth";
import { deletePending, getPending, listPending, savePending, type PendingDictation } from "./pendingStore";
import { clearDictationStatus, publishDictationStatus } from "./status";
import { blobToWav16kMono } from "./wav";
import { reportDictationQuality } from "./qualityReport";
import { activeAccount, listAccounts } from "../auth/accountStore";

// The durable Send pipeline + background retry driver for the mobile Speak dialog (issue #1006,
// strengthened for #1182). The instant the user hits Send the dialog hands the recorded audio here and
// closes; we persist the raw audio locally (IndexedDB) BEFORE any network work, then drive delivery to
// the Gateway. The Gateway assembles, transcribes, and INJECTS the turn into the session itself, so once
// the audio is up a dead tab or a dropped connection can no longer lose it.
//
// The on-device copy is the single source of truth. A recorded dictation is NEVER lost on a bad
// connection and is NEVER aged out: it stays in the durable queue until the server confirms it owns the
// turn (submitted, or deliberately dropped as stale), or the user explicitly abandons it (a later Task).
// Delivery keeps retrying automatically - hard for the first hour, then throttled to slow background
// attempts, forever - and resumes the instant connectivity returns (the browser `online` event and app
// foreground) and on every app load. Every attempt is idempotent by the upload id (which is also the
// server Idempotency-Key), so a retry or a resume can never inject the same dictation twice (this is the
// direction issue #1181 reverses from #1135's over-correction: dedupe by upload id, never by dropping the
// queue).
//
// It deliberately lives OUTSIDE the DictationDialog component: the dialog unmounts (and disposes its
// recorder) the moment Send is pressed, so the work must not be tied to the dialog's lifecycle.

// ---- retry cadence ---------------------------------------------------------------------------------
// Try hard for the first hour after the clip was recorded, then throttle to a slow background attempt so
// a long outage does not hammer a dead connection - but never stop, and never discard the audio.
const HARD_WINDOW_MS = 60 * 60 * 1000; // "hard" retries for the first hour since the clip was recorded
const HARD_MIN_DELAY_MS = 2_000; // first hard retry after two seconds
const HARD_MAX_DELAY_MS = 15_000; // hard exponential backoff caps at fifteen seconds
const THROTTLED_DELAY_MS = 5 * 60 * 1000; // after the hard hour (or out of credits): one slow attempt every five minutes

// The honest, plain-English held lines. A held dictation is saved and still being delivered - the copy
// never says "was not transcribed", because it is held, not lost (criterion 8).
const WAITING_FOR_CONNECTION_MESSAGE =
  "Waiting for a connection - your recording is saved and will send automatically.";
const RETRYING_MESSAGE = "Saved - still trying to send your recording...";
const THROTTLED_MESSAGE =
  "Saved - still trying to send your recording in the background. It stays saved until it is delivered.";
const NO_DURABLE_STORE_MESSAGE =
  "This browser can't save recordings for reliable delivery, so this dictation couldn't be sent. Record it again in a normal browser tab.";

// The parked (permanent-failure) messages (issue #1184). A parked clip is saved-and-retryable, never a
// permanent loss: the copy says the audio is safe on the device and the user can retry it. There is no
// auto-loop; only an explicit Retry re-drives it (it will succeed once the server transcode-and-split fix
// lands). The size wording is the exact text agreed in the issue.
const PARKED_TOO_LARGE_MESSAGE =
  "This recording is too long to transcribe right now; it is saved on your device and you can retry it.";
const PARKED_UNSUPPORTED_FORMAT_MESSAGE =
  "This recording is in a format we can't transcribe right now; it is saved on your device and you can retry it.";
// The empty-recording line, for a durable copy that was read SUCCESSFULLY and held zero bytes. It says the
// one thing the old forever-spinner never did: this clip has no audio behind it, so waiting will not
// deliver it. Retry is still on the strip because that is parked's contract and the copy is still on the
// device - but a copy that really is empty will simply park again, which is honest, and far better than an
// amber "still sending" that was never true. A copy that could not be READ is NOT this: that stays held and
// keeps retrying by itself (DICTATION_HELD_UNREADABLE_MESSAGE), because a failed read says nothing about
// how long the recording is and parking it would strand audio that is still there.
const PARKED_EMPTY_RECORDING_MESSAGE =
  "This recording came back empty on this device, so there was nothing to send. Nothing is in flight - you can retry it, or record it again.";

// The plain saved-and-retryable line for a parked clip, chosen by the allow-listed reason on the record.
function parkMessage(reason: string | undefined): string {
  if (reason === "unsupported-format") return PARKED_UNSUPPORTED_FORMAT_MESSAGE;
  if (reason === "empty-recording") return PARKED_EMPTY_RECORDING_MESSAGE;
  return PARKED_TOO_LARGE_MESSAGE;
}

// The dropped-as-stale lines (issue #1590). Plain English, and honest about what happened: the session moved
// on, so the words were NOT delivered. Never soft-pedalled into sounding like a success, and never silent.
const DROPPED_WITH_TRANSCRIPT_MESSAGE =
  "The session moved on before this recording arrived, so it wasn't sent. Here is what you said - send it?";
const DROPPED_NO_TRANSCRIPT_MESSAGE =
  "The session moved on before this recording arrived, so it wasn't sent. Your recording is saved on your device and you can try again.";
const UNHEARD_MESSAGE = "Nothing was heard in that recording, so nothing was sent.";
// The empty-CAPTURE line, for a clip that arrived from the recorder with no bytes in it at all. Deliberately
// not the unheard sentence above: that one means the server listened and heard no speech, which is a fact
// about the words. This one means the microphone handed us nothing, which is a fact about the device - so it
// points at the microphone, because a user whose recordings keep coming back empty needs to know where to
// look rather than being told twice that they were silent. It says NOTHING WAS SENT in as many words,
// because the failure it replaces spent an evening claiming the opposite.
const EMPTY_CAPTURE_MESSAGE =
  "Recording failed - it captured no audio, so nothing was sent and nothing is being retried. Check the microphone is working and record it again.";
const SEND_ANYWAY_FAILED_MESSAGE =
  "Couldn't send that just now. Your words are still here - try again.";

// The full message a dropped dictation would have delivered (issue #1590), composed EXACTLY as the Gateway's
// complete path composes it before injecting: the typed text the caret split the dictation around (before /
// after), any earlier paused segments already turned to text (prefix), and the words the server heard -
// space-joined, skipping empties.
//
// It must match the server's rule (GatewayDictationEndpoint.RunCompleteCoreAsync) because this IS the
// recovery of that same turn: sending the transcript alone would silently throw away the typed text the user
// composed around it, which is the very "your words vanished" defect this whole item exists to end - just
// smaller and harder to notice. Every part is already on the durable record, so nothing extra is stored.
//
// The common voice case is the transcript alone, and this returns exactly that.
function composeDroppedMessage(rec: PendingDictation): string {
  return [rec.before, rec.prefix, rec.droppedTranscript ?? "", rec.after]
    .filter((p) => (p ?? "").trim().length > 0)
    .map((p) => p.trim())
    .join(" ")
    .trim();
}

/** The audio buffer + context the dialog hands up when Send is pressed. */
export interface CapturedUtterance {
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.). */
  blob: Blob;
  /** Wall-clock milliseconds the segment was capturing (capture-health, issue #863). */
  recordedMs: number;
  /** Earlier Pause/Resume dictation segments, already turned to text, joined ahead of this final
   *  segment. Empty in the common "just talk and Send" case. */
  prefixText: string;
  /** The microphone's name, for per-device quality reporting. Optional: a caller that does not know
   *  it still delivers its words, it just cannot say which microphone recorded them. */
  deviceLabel?: string;
  /** The microphone's stable identifier - the value quality measurements are grouped by. Optional
   *  for the same reason as the label. */
  deviceId?: string;
  /** Which shell recorded it ("cockpit" / "mobile"), so the capture-health measurement is filed under
   *  the surface the user was actually in. Optional: an unlabelled caller still delivers every word,
   *  it just lands under the generic browser tag. */
  surface?: string;
}

/** Callbacks so the host can react to the rare hard failure (durable storage unavailable). A normal send
 *  is durable and its progress shows on the status strip and the roster, so success and held states need
 *  no host callback - the status store is the single source of truth. */
export interface BackgroundSendHooks {
  onError?: (message: string) => void;
  /** Called only when the clip could NOT be saved durably (so nothing is queued), so the host can restore
   *  any typed compose text it cleared at dialog-close time. It is NOT called for a held/retrying send:
   *  the typed text is part of the durable record and is delivered with the dictation. */
  onFailed?: () => void;
  /** Typed text the caret split the dictation around (Terminal Speak's Insert-then-Enter). The voice
   *  case omits this and the transcript is submitted alone. */
  composeParts?: { before: string; after: string };
  /** The session's TotalBufferBytes at record time, for the Gateway's "session moved on" guard when a
   *  clip is resumed later. A promise is the PRESS-TIME snapshot (issue #2478): the Speak press starts
   *  the roster read and hands the promise here, so a quick Send cannot outrun it and record "unknown"
   *  for a session whose position was knowable. Durability never waits on it - the clip is persisted
   *  immediately with the baseline unknown, then the pending record is enriched before the first
   *  upload once this ORIGINAL promise resolves; the pipeline never starts a later roster read of its
   *  own, because a post-recording reading would mask the very movement the guard detects. Omit when
   *  genuinely unknown; unknown is persisted as unknown and the wire request omits the field (the
   *  guard is then skipped for safety) - it is NEVER collapsed into zero, which is a real reading (a
   *  terminal that had produced nothing yet). */
  baselineBufferBytes?: number | Promise<number | undefined>;
}

// ---- driver state ----------------------------------------------------------------------------------
// The in-flight guard: upload ids currently being driven. All four triggers (a fresh Send, app load, the
// browser `online` event, app foreground, and the explicit "Upload now") funnel through driveRecord,
// which no-ops if the clip is already being driven - so two triggers can never run two concurrent drivers
// for the same clip and cannot double-inject it. The server single-flights complete per upload id too,
// but we do not rely on that alone (Manager lock-in for #1182).
const _inFlight = new Set<string>();
// Scheduled next-attempt timers, keyed by upload id, so a new trigger can cancel a waiting timer and
// drive immediately.
const _timers = new Map<string, ReturnType<typeof setTimeout>>();
let _listenersInstalled = false;

// Persist the recorded audio durably the instant Send is pressed, then drive the first delivery attempt.
// If durable storage is genuinely unavailable we tell the user clearly rather than doing a silent
// one-shot send (a one-shot on a bad connection is exactly the loss this feature exists to prevent).
export async function backgroundTranscribeAndSend(
  sessionId: string,
  captured: CapturedUtterance,
  hooks: BackgroundSendHooks = {},
): Promise<void> {
  ensureRetryListeners();

  // THE EMPTY-CAPTURE GATE - the root cause, refused at the door.
  //
  // DictationRecorder builds its clip with `new Blob(this.chunks, ...)`, and when the recorder delivered no
  // chunks - the microphone produced nothing, or Send arrived before the first chunk did - that Blob is ZERO
  // BYTES. Nothing between there and here checked, and all four Send surfaces (the phone's controls, the
  // Cockpit composer, Voice mode, the Voice tab) hand their capture straight to this function. So an empty
  // recording was written to the durable queue like any other, and then driven forever: it can never produce
  // a chunk, so the Gateway can never complete it, so the clip retried for as long as the app was open while
  // the phone said "Saved - still sending". It also registered a staging directory on the Gateway for every
  // attempt - a delivery record with no audio behind it, which is the residue this was found through.
  //
  // Refusing it HERE rather than in each caller is the point: one gate covers all four surfaces, and it is
  // the last place before the durable write, so an unsendable clip never enters the queue at all. Nothing is
  // queued, so there is nothing to retry, nothing to cancel, and no staging on the Gateway.
  //
  // IT FAILS LOUDLY AND AT ONCE. The phase is "failed", not "unheard": "unheard" is the quiet grey cousin
  // that means the server listened and heard no speech, and it renders role="status". This did not reach a
  // server and is not a fact about the words - the device handed us nothing - so it renders the red
  // role="alert" strip that never clears itself, and fires the host's onError as well, exactly like the
  // other case where a clip cannot be queued at all (durable storage missing). The user gets one clear,
  // immediate, DIFFERENT error instead of an amber "Saved - still sending" that was never true.
  //
  // Immediately means immediately: before the decode, before the durable write, before any network work.
  // Transcription is server-side, so a clip with no bytes has nothing to transcribe and nothing to upload;
  // there is no outcome worth waiting for and no reason to make the user watch a spinner discover it.
  // onFailed still runs, so any typed text the dialog cleared comes straight back.
  //
  // The client's own empty-audio guard (uploadDictationToSession) stays as the backstop below this one: it
  // covers the different case of a clip that was queued whole and whose on-device copy later reads back
  // empty or unreadable. This gate stops the queue being polluted; that one stops a polluted queue looping.
  if (captured.blob.size === 0) {
    publishDictationStatus({
      sessionId,
      uploadId: crypto.randomUUID(),
      phase: "failed",
      retryable: false,
      error: EMPTY_CAPTURE_MESSAGE,
    });
    hooks.onError?.(EMPTY_CAPTURE_MESSAGE);
    hooks.onFailed?.();
    return;
  }

  // Decode the clip ONCE here - the screen has already closed, so this is off the critical path - to do
  // two things the Send path previously skipped:
  //   1. Upload the decoded 16 kHz WAV instead of the raw recording. The WAV carries the trailing-silence
  //      run-out (added in blobToWav16kMono) that keeps the last word from being clipped by the model, and
  //      it is what the Gateway's Local Whisper mode can read directly. Every other finish path already
  //      sends this WAV; the Send path now matches, so all surfaces transcribe the same padded WAV.
  //   2. Measure capture-health (issue #863): the recorded-vs-decoded deficit, logged on this surface and
  //      forwarded on the upload so the Gateway persists it into the same dictation session log.
  // Diagnostics AND the pad are best-effort: a decode failure is logged loudly but NEVER blocks delivery -
  // we fall back to uploading the raw recording so the user's words are the guarantee, the pad is not.
  let decodedSeconds: number | undefined;
  let sourceBytes: number | undefined;
  let uploadBlob = captured.blob;
  let captureWarning: string | undefined;
  // The Send path's tag for this surface. "mobile" yields "mobile-send" - byte-identical to the tag
  // this path has always written - so the existing capture-health history stays comparable while the
  // Cockpit finally gets a tag of its own instead of being counted as a phone.
  const sendSurface = `${captured.surface ?? "browser"}-send`;
  try {
    const transcoded = await blobToWav16kMono(captured.blob);
    decodedSeconds = transcoded.decodedSeconds;
    sourceBytes = transcoded.sourceBytes;
    uploadBlob = transcoded.wav;
    const health = {
      recordedMs: captured.recordedMs,
      decodedSeconds: transcoded.decodedSeconds,
      sourceBytes: transcoded.sourceBytes,
    };
    logCaptureHealth(sendSurface, health);
    // Material capture loss must not ship SILENTLY on Send the way it currently did (the Insert/Pause paths
    // already warn and park). We cannot park a fire-and-forget Send - the screen is gone and the words the
    // mic DID capture should still be delivered - so instead the deficit rides along as a caution shown with
    // the delivered `done` status, and stored durably so a resumed send still carries it.
    captureWarning = captureLossWarning(health) ?? undefined;
    // Measure the microphone in the background. Inside the try because it needs the decode, and
    // deliberately AFTER captureWarning so a measurement problem can never cost the user the
    // dropped-audio warning, which is about their words rather than about our analytics.
    reportDictationQuality(
      transcoded.nativeSamples,
      transcoded.nativeSampleRate,
      { label: captured.deviceLabel ?? "", deviceId: captured.deviceId ?? "" },
      "dictation-send",
    );
  } catch (err) {
    console.warn(
      `[backgroundSend] decode failed; uploading the raw recording unpadded (delivery is unaffected): ${err instanceof Error ? err.message : String(err)}`,
    );
  }

  // The moved-on baseline is the PRESS-TIME snapshot or nothing (issue #2478). A plain number (the
  // Voice screen) rides the first durable write below; a promise is awaited only AFTER the clip is
  // safely on disk - the durable-send contract (persist before any network work) outranks the guard,
  // so a slow or timed-out roster read must never leave the clip memory-only.
  const pressTimeBaseline = hooks.baselineBufferBytes;

  let rec: PendingDictation = {
    id: crypto.randomUUID(),
    sessionId,
    blob: uploadBlob,
    recordedMs: captured.recordedMs,
    decodedSeconds,
    sourceBytes,
    captureWarning,
    surface: sendSurface,
    // WHICH ACCOUNT RECORDED THIS (devthrottle_internal #1509). Stamped at record time, because the
    // upload happens later and authenticates as whoever is active THEN - see resumePendingDictations.
    accountId: activeAccount()?.id,
    before: hooks.composeParts?.before ?? "",
    after: hooks.composeParts?.after ?? "",
    prefix: captured.prefixText ?? "",
    baselineBufferBytes: typeof pressTimeBaseline === "number" ? pressTimeBaseline : undefined,
    createdAt: Date.now(),
  };

  // Show the very first step (before any network work) so the status strip appears the instant Send is
  // pressed and the screen is never quiet.
  publishDictationStatus({ sessionId, uploadId: rec.id, phase: "saving" });

  // RESERVE the upload id from the kick machinery for the whole save-and-enrich window. The first
  // durable write makes the record VISIBLE to every automatic trigger (app load, the online event,
  // foreground), and a kick landing before enrichment would list the record and drive it with the
  // baseline still unknown - uploading unguarded and racing both the enrichment write and this flow's
  // own first drive. The reservation is the same in-flight set every drive honors, so a kick no-ops
  // on exactly this id until enrichment has finished and THIS flow drives it. It lives only in
  // memory, deliberately: a crash or reload drops it with the process, and the resume path then
  // drives the durable unknown record - unguarded, which is exactly what the durable copy honestly
  // knows.
  _inFlight.add(rec.id);
  try {
    try {
      await savePending(rec);
    } catch {
      // Durable storage genuinely unavailable (rare, e.g. a private-mode tab with IndexedDB disabled): the
      // clip cannot be queued, so say so loudly and restore the typed text. We do NOT silently one-shot it.
      publishDictationStatus({
        sessionId,
        uploadId: rec.id,
        phase: "failed",
        retryable: false,
        error: NO_DURABLE_STORE_MESSAGE,
      });
      hooks.onError?.(NO_DURABLE_STORE_MESSAGE);
      hooks.onFailed?.();
      return;
    }

    // The clip is durable. Now - and only now - wait for the press-time baseline snapshot and enrich
    // the pending record with it before the first upload. When the ORIGINAL press-time promise cannot
    // answer, unknown is FINAL: never a fresh roster read here, because a reading taken now can include
    // bytes the session produced during or after the recording, over-stating the baseline and masking
    // exactly the movement the guard exists to detect. The wait is bounded (the shared snapshot rides
    // the roster read's own timeout and never rejects); the defensive catch is for a foreign caller's
    // promise only, because a guard input must never cost the user's words.
    if (pressTimeBaseline !== undefined && typeof pressTimeBaseline !== "number") {
      let bytes: number | undefined;
      try {
        bytes = await pressTimeBaseline;
      } catch {
        bytes = undefined;
      }
      if (bytes !== undefined) {
        rec = { ...rec, baselineBufferBytes: bytes };
        try {
          await savePending(rec);
        } catch {
          // The durable copy keeps unknown (a resume would go unguarded); this attempt still carries
          // the press-time reading in memory. Never a reason to hold the words.
        }
      }
    }
  } finally {
    // Release, and hand off to the drive below with NO await in between: driveRecord re-takes the
    // in-flight entry synchronously on entry, so no kick can slip into the gap.
    _inFlight.delete(rec.id);
  }

  // Drive the first delivery attempt now. resumed:false so an immediate send injects without the
  // moved-on guard; any failure becomes a held-and-retrying state the driver owns. The caller does
  // not await this (it fired and moved on), but awaiting the first attempt here keeps the returned
  // promise honest about when that attempt settled.
  await driveRecord(rec, { resumed: false, attempt: 0 });
}

// Re-drive every recorded-but-unsent dictation on app load (issue #1006/#1182): a clip whose upload was
// interrupted by a refresh, a crash, or a dropped connection is resumed from the durable copy. Idempotent
// by upload id, so a clip that actually landed before the tab died is de-duplicated by the Gateway rather
// than double-submitted. This also installs the connectivity listeners so a resume happens the moment the
// network returns, not only on the next load.
export async function resumePendingDictations(): Promise<void> {
  ensureRetryListeners();
  let all: PendingDictation[];
  try {
    all = await listPending();
  } catch {
    return; // no durable store; nothing to resume
  }
  // A parked clip (permanent failure, issue #1184) and a stale-DROPPED clip (issue #1590) are NOT auto-driven:
  // re-publish their status so the strip and roster still show them after a reopen, but never re-drive them. A
  // dropped clip especially - its upload id carries a permanent moved-on tombstone, so re-driving it could only
  // be dropped again, and re-publishing is what keeps a lost dictation visible instead of vanishing on reload.
  // A CLIP BELONGS TO THE ACCOUNT THAT RECORDED IT (devthrottle_internal #1509). This store is one
  // IndexedDB database per origin, shared by every account on the browser, and this resume runs on load
  // authenticating as whichever account is active NOW. Driving another account's clip would send the
  // person's own voice - and the text it becomes - into the wrong tenant.
  //
  // A clip that is not ours is LEFT WHERE IT IS, neither driven nor deleted: it goes when its account is
  // active again, which is what the durable store promises. A clip with no stamp predates this field and
  // is driven only while a single account is enrolled, where there is no other owner it could have.
  const mine = activeAccount()?.id;
  const single = listAccounts().length <= 1;
  const ours = all.filter((rec) => (rec.accountId ? rec.accountId === mine : single));

  await Promise.all(
    ours.map((rec) => {
      if (rec.staleDropped) {
        publishDropped(rec);
        return Promise.resolve();
      }
      if (rec.parkedReason) {
        publishParked(rec, rec.parkedReason);
        return Promise.resolve();
      }
      return driveRecord(rec, { resumed: true, attempt: 0 });
    }),
  );
}

// The explicit "Upload now" control on the status strip: kick a waiting or throttled clip back to
// full-speed delivery immediately (resetting the backoff to the hard cadence). If the durable record is
// gone (already delivered, or abandoned) the stale status is cleared so a dead strip cannot linger.
export async function retryPendingDictation(uploadId: string): Promise<void> {
  let rec: PendingDictation | null;
  try {
    rec = await getPending(uploadId);
  } catch {
    return;
  }
  if (rec === null) {
    clearDictationStatus(uploadId);
    return;
  }
  // A stale-DROPPED clip (issue #1590) cannot be re-driven under its own id - the server holds a permanent
  // moved-on tombstone for it, so this exact upload id can only ever be dropped again. "Retry this clip"
  // genuinely means "send the recording as a new dictation", so hand over to the fresh-id path rather than
  // re-driving into a guaranteed re-drop (or, worse, quietly doing nothing).
  if (rec.staleDropped) {
    await retryDroppedDictation(uploadId);
    return;
  }
  // If it was PARKED after a permanent failure (issue #1184), this explicit Retry moves it back to active:
  // clear the parked reason on the durable record first, so the auto-triggers stop skipping it and this
  // deliberate drive re-enters the normal flow. Harmless (a no-op) for a non-parked held clip.
  if (rec.parkedReason) {
    const reactivated: PendingDictation = { ...rec, parkedReason: undefined };
    try {
      await savePending(reactivated);
    } catch {
      // Could not clear the flag durably: still drive from the in-memory reactivated record below.
    }
    rec = reactivated;
  }
  clearScheduled(uploadId);
  await driveRecord(rec, { resumed: true, attempt: 0 });
}

// The user explicitly ABANDONS a dictation (issue #1181, Task 5). The strip clears IMMEDIATELY so cancel
// feels instant, and the record is marked `abandoning` and driven: the loop tells the Gateway to abandon
// (discarding the staged audio and clearing the session lock), then drops the on-device copy. If the
// Gateway cannot be reached the record is kept and the abandon is retried silently, so the session can
// never wedge locked - the cancel always reaches the durable marker eventually. A no-op for an id already
// gone (delivered, or abandoned from another surface).
export async function abandonPendingDictation(uploadId: string): Promise<void> {
  clearScheduled(uploadId);
  clearDictationStatus(uploadId); // instant: the user asked to cancel, so the strip goes away now
  let rec: PendingDictation | null;
  try {
    rec = await getPending(uploadId);
  } catch {
    return;
  }
  if (rec === null) return; // already delivered or abandoned; nothing on device to drive
  const abandoning: PendingDictation = { ...rec, abandoning: true };
  try {
    await savePending(abandoning); // durable, so a reload keeps abandoning it rather than resuming upload
  } catch {
    // Could not persist the flag: still drive the in-memory abandoning record below.
  }
  await driveRecord(abandoning, { resumed: true, attempt: 0 });
}

// "Send anyway" on a dropped dictation (issue #1590): the session moved on and the server threw the words
// away, but it told us what they were. Send them as a NORMAL prompt - a fresh turn, deliberately NOT a
// re-drive of the dictation upload id, which by design (#1183) can only ever return the same drop again.
//
// GUARDED by the same in-flight set the delivery driver uses. This send has NO server-side idempotency behind
// it: it is an ordinary prompt, so the durable upload id that de-duplicates a dictation (#1183) protects
// nothing here. Two rapid taps - or two mounted strips for one session, each with its own button state -
// would both read the record before either deleted it, and the user would get their words twice. The button's
// disabled state is a courtesy; THIS is the guarantee.
//
// The words are read from the DURABLE record, not the in-memory status, so this still works after a reload,
// and they are composed exactly as the delivery path composes them - the typed text goes with them.
// The record is deleted only AFTER the send is confirmed: on failure nothing is discarded, and the status
// stays sticky with the words still in it, so a bad moment cannot lose them.
export async function sendDroppedDictationAnyway(uploadId: string): Promise<void> {
  if (_inFlight.has(uploadId)) return; // already sending this exact clip
  _inFlight.add(uploadId);
  try {
    let rec: PendingDictation | null;
    try {
      rec = await getPending(uploadId);
    } catch {
      return;
    }
    if (rec === null) {
      clearDictationStatus(uploadId); // already dealt with; do not leave a dead strip behind
      return;
    }
    const text = composeDroppedMessage(rec);
    if (text.length === 0) return; // nothing to send; this clip's action is Retry, not Send anyway

    try {
      await sendPrompt(rec.sessionId, text, true);
    } catch {
      // Keep the record AND the sticky status - the words are still on the device and still on screen.
      publishDictationStatus({
        sessionId: rec.sessionId,
        uploadId: rec.id,
        phase: "dropped",
        retryable: false,
        recoverableText: text,
        error: SEND_ANYWAY_FAILED_MESSAGE,
      });
      return;
    }
    // Confirmed sent: only now is the durable copy safe to drop.
    await deletePending(rec.id);
    publishDictationStatus({ sessionId: rec.sessionId, uploadId: rec.id, phase: "done" });
  } finally {
    _inFlight.delete(uploadId);
  }
}

// Retry a dropped dictation whose words we never got (the rare drop before transcription, issue #1590).
// The audio is still on the device, but its upload id is tombstoned moved-on for good (#1183), so it is
// re-driven under a FRESH upload id - a genuinely new dictation carrying the same recording. The baseline
// is cleared to UNKNOWN (the field is omitted on the wire, so the server skips the guard): the
// recorded-at baseline describes a terminal that has long since moved on, and re-sending it would simply
// invite the same drop. The user asked for this send now, deliberately.
// Guarded on the OLD id by the same in-flight set: each tap mints a NEW upload id, so without this two rapid
// taps would stage two fresh clips and inject the same recording twice - and being different ids, nothing
// downstream would de-duplicate them.
export async function retryDroppedDictation(uploadId: string): Promise<void> {
  if (_inFlight.has(uploadId)) return; // already retrying this exact clip
  _inFlight.add(uploadId);
  let fresh: PendingDictation;
  try {
    let rec: PendingDictation | null;
    try {
      rec = await getPending(uploadId);
    } catch {
      return;
    }
    if (rec === null) {
      clearDictationStatus(uploadId);
      return;
    }

    fresh = {
      ...rec,
      id: crypto.randomUUID(),
      staleDropped: undefined,
      droppedTranscript: undefined,
      baselineBufferBytes: undefined,
      createdAt: Date.now(),
    };
    try {
      await savePending(fresh);
    } catch {
      return; // could not stage the fresh copy; leave the dropped record and its sticky status exactly as they are
    }
    // The old id is finished with only once the fresh copy is safely on disk, so a failure here can never
    // leave the user with neither.
    await deletePending(rec.id);
    clearDictationStatus(rec.id);
  } finally {
    _inFlight.delete(uploadId);
  }
  // Outside the old id's guard: this drives the FRESH id, which takes its own in-flight entry. Holding both
  // would be harmless but pointless - the old id no longer exists by this point.
  await driveRecord(fresh, { resumed: false, attempt: 0 });
}

// Dismiss a dropped / unheard dictation (issue #1590): the user has read it and does not want the words.
// This is the ONLY thing that discards a dropped clip without sending it, and it is always a deliberate act -
// nothing here ever fires on its own.
export async function dismissDictationStatus(uploadId: string): Promise<void> {
  clearScheduled(uploadId);
  clearDictationStatus(uploadId);
  try {
    await deletePending(uploadId);
  } catch {
    /* the status is already gone from screen; a leftover record is re-published only if it is re-driven */
  }
}

// ---- internals -------------------------------------------------------------------------------------

interface DriveOptions {
  /** True for any retry/resume (applies the server's moved-on guard); false only for the first immediate send. */
  resumed: boolean;
  /** Backoff step for scheduling the NEXT attempt. Reset to 0 by a fresh send, a connectivity kick, and Upload now. */
  attempt: number;
}

// Drive one durable clip through a single delivery attempt, then either delete it (the server owns the
// turn) or keep it and schedule the next attempt. Guarded so concurrent triggers cannot double-drive one
// clip.
async function driveRecord(rec: PendingDictation, opts: DriveOptions): Promise<void> {
  if (_inFlight.has(rec.id)) return; // another trigger is already driving this clip
  _inFlight.add(rec.id);
  clearScheduled(rec.id); // we are driving now; cancel any waiting timer

  try {
    if (rec.abandoning) {
      // The user cancelled this clip (issue #1181, Task 5): do NOT upload it. Tell the Gateway to abandon
      // the durable upload; on confirmation drop the on-device copy, otherwise retry silently (no strip -
      // the cancel already cleared it) so the session's lock is always released eventually.
      if (isOffline() || !(await abandonDictation(rec.id))) {
        scheduleNext(rec, opts.attempt, false);
        return;
      }
      await deletePending(rec.id);
      clearDictationStatus(rec.id);
      return;
    }

    if (isOffline()) {
      // No point calling out with no network: show waiting-for-connection and lean on the `online`
      // listener, with a slow fallback timer so we still recover even if that event is missed.
      publishHeld(rec, WAITING_FOR_CONNECTION_MESSAGE);
      scheduleNext(rec, opts.attempt, false);
      return;
    }

    const outcome = await uploadDictationToSession({
      sessionId: rec.sessionId,
      uploadId: rec.id,
      audio: rec.blob,
      before: rec.before,
      after: rec.after,
      prefix: rec.prefix,
      baselineBufferBytes: rec.baselineBufferBytes,
      resumed: opts.resumed,
      // Capture-health (issue #863): forward the Send-time measurement so the Gateway persists the
      // audio-loss deficit for this path. Absent when the on-device decode failed.
      clientRecordedMs: rec.recordedMs,
      clientDecodedSeconds: rec.decodedSeconds,
      clientSourceBytes: rec.sourceBytes,
      // Read from the durable record, not recomputed, so a clip resumed after a reload is still filed
      // under the surface that actually recorded it.
      clientSurface: rec.surface,
    });

    if (outcome.terminal) {
      // The server owns the turn: a fresh delivery, a server that DEDUPED a delivery it had already made (a
      // cached-delivered outcome from the durable record, issue #1183 - treated identically to a fresh
      // success), a deliberately dropped stale clip, an empty clip, or an ABANDONED upload id. The client
      // already acknowledged the outcome to the Gateway (in uploadDictationToSession).
      //
      // These are NOT one arm (issue #1590). Every terminal-not-submitted outcome used to fall into a single
      // deletePending + clearDictationStatus - audio gone, banner gone, no trace, and the user was never told
      // that the words they spoke had been thrown away. "It worked and then nothing happened." Only ONE of
      // these outcomes is genuinely nothing to say: an abandon, which the user did on purpose.
      if (outcome.submitted) {
        await deletePending(rec.id);
        // Delivered. If the capture dropped audio, the words went in but the transcript may be missing some,
        // so ride a non-blocking caution on the done status (it will not auto-clear) rather than a silent "Sent".
        publishDictationStatus({ sessionId: rec.sessionId, uploadId: rec.id, phase: "done", warning: rec.captureWarning });
        return;
      }

      if (outcome.abandoned) {
        // The user gave this clip up themselves. They already know; saying it again would be noise. This is
        // the ONLY silent terminal arm, and it stays silent deliberately (the issue rules it out of scope).
        await deletePending(rec.id);
        clearDictationStatus(rec.id);
        return;
      }

      if (outcome.movedOn) {
        // The session moved on while the clip was in flight, so the server dropped the user's words. Re-driving
        // this upload id is useless BY DESIGN - the drop wrote a permanent moved-on tombstone (#1183), so every
        // future complete returns the same drop. The recovery is therefore a fresh turn, not a retry.
        //
        // Keep the record durably (marked stale-dropped, so no automatic trigger ever re-drives it) rather than
        // deleting it: the words must survive a reload, or "Send anyway" would quietly stop working the moment
        // the user backgrounds the app - which is the same silent loss in a new costume.
        const transcript = (outcome.transcript ?? "").trim();
        const dropped: PendingDictation = { ...rec, staleDropped: true, droppedTranscript: transcript };
        try {
          await savePending(dropped);
        } catch {
          // The durable store hiccuped. The in-memory status below is still published, so this drive stays
          // loud; it just may not survive a reload. Never fall through to a silent clear.
        }
        publishDropped(dropped);
        return;
      }

      // Nothing was heard: the clip reached the server, which found no speech and no typed text in it, so
      // there was no turn to make. Nothing was lost and there is nothing to retry - but it is still an answer,
      // and a Send that ends in silence is the defect. The audio is of no further use, so it goes.
      await deletePending(rec.id);
      publishUnheard(rec);
      return;
    }

    if (outcome.permanent) {
      // Genuinely permanent, non-retryable failure (issue #1184): PARK the clip. Keep the audio, stop the
      // auto-loop (cancel any timer and do NOT scheduleNext), and persist the parked reason on the record so
      // every automatic trigger skips it - including across a close and reopen. It waits for an explicit
      // user Retry; nothing here re-drives it. The reason is one of the allow-listed permanent reasons.
      const reason = outcome.permanentReason ?? "audio-too-large";
      try {
        await savePending({ ...rec, parkedReason: reason });
      } catch {
        // Persisting the parked flag failed (durable store hiccup): the in-memory return below still stops
        // THIS drive; a later trigger may re-attempt, which simply re-parks. We never re-drive in a tight loop.
      }
      publishParked(rec, reason);
      return;
    }

    // Held: keep the audio and keep trying. Publish the honest held reason and schedule the next attempt.
    publishHeld(rec, heldMessage(rec, outcome.error, outcome.recordRefusal));
    scheduleNext(rec, opts.attempt, Boolean(outcome.outOfCredits), outcome.recordRefusal);
  } catch {
    // uploadDictationToSession returns a held result rather than throwing, so this is a defensive net for
    // an unexpected fault: keep the audio and keep trying - never drop it.
    publishHeld(rec, heldMessage(rec, undefined));
    scheduleNext(rec, opts.attempt, false);
  } finally {
    _inFlight.delete(rec.id);
  }
}

// Re-read a record by id (it may already be delivered and gone) and drive it. Used by the scheduled
// retry timers.
async function driveById(id: string, opts: DriveOptions): Promise<void> {
  let rec: PendingDictation | null;
  try {
    rec = await getPending(id);
  } catch {
    return;
  }
  if (rec === null) {
    clearScheduled(id); // delivered or abandoned; nothing left to drive
    return;
  }
  if (rec.staleDropped) {
    // Dropped as stale between scheduling and firing (issue #1590): never auto-drive it - the upload id is
    // tombstoned moved-on, so a re-drive could only be dropped again. Defensive; a dropped clip is never
    // given a timer.
    clearScheduled(id);
    publishDropped(rec);
    return;
  }
  if (rec.parkedReason) {
    // Parked between scheduling and firing (issue #1184): never auto-drive it. Defensive - a parked clip is
    // never given a timer, so this should not normally be reached.
    clearScheduled(id);
    publishParked(rec, rec.parkedReason);
    return;
  }
  await driveRecord(rec, opts);
}

// Resume every pending clip immediately at full speed - the connectivity/foreground kick and what the
// `online`/`visibilitychange` listeners call. A parked clip (permanent failure, issue #1184) and a
// stale-dropped clip (issue #1590) are skipped: neither ever auto-drives, and only an explicit user action
// moves them. Re-driving a dropped clip would be worse than pointless - its moved-on tombstone is permanent,
// so every kick would re-drop it.
async function kickAll(): Promise<void> {
  let all: PendingDictation[];
  try {
    all = await listPending();
  } catch {
    return;
  }
  for (const rec of all) {
    if (rec.parkedReason || rec.staleDropped) continue;
    void driveRecord(rec, { resumed: true, attempt: 0 });
  }
}

// Schedule the next automatic attempt for a held clip. The delay is hard (exponential from two seconds,
// capped at fifteen) for the first hour since the clip was recorded, then throttled to five minutes - and
// out of credits is always throttled (a fast retry cannot conjure credits). Never stops.
function scheduleNext(rec: PendingDictation, attempt: number, outOfCredits: boolean, recordRefusal?: RecordRefusalKind): void {
  clearScheduled(rec.id);
  const delay = nextDelayMs(rec, attempt, outOfCredits, recordRefusal);
  const t = setTimeout(() => void driveById(rec.id, { resumed: true, attempt: attempt + 1 }), delay);
  _timers.set(rec.id, t);
}

// A "needs-operator" record refusal (issue #2745) is throttled from the first attempt: the Gateway has said
// its delivery record for this upload is damaged and no retry changes that until someone looks at the file,
// so a fast loop is pure churn in both logs. It is still retried - the operator fixing the file is exactly
// what a later attempt would find - just at the slow cadence. A "retry-later" refusal (the record could not
// be read just now) stays on the ordinary cadence, because that one really may clear on the next try.
function nextDelayMs(rec: PendingDictation, attempt: number, outOfCredits: boolean, recordRefusal?: RecordRefusalKind): number {
  const age = Date.now() - rec.createdAt;
  if (outOfCredits || recordRefusal === "needs-operator" || age >= HARD_WINDOW_MS) return THROTTLED_DELAY_MS;
  return Math.min(HARD_MIN_DELAY_MS * 2 ** attempt, HARD_MAX_DELAY_MS);
}

function clearScheduled(id: string): void {
  const t = _timers.get(id);
  if (t !== undefined) {
    clearTimeout(t);
    _timers.delete(id);
  }
}

// The held status line to show: waiting-for-connection when offline, the throttled line once past the hard
// hour, otherwise the specific reason the last attempt returned (or a generic retrying line).
//
// A record refusal (issue #2745) keeps its reason on screen whatever the clip's age: "still trying in the
// background" is the wrong answer to a user whose recording cannot go through until an operator looks at a
// file on the server, and the age throttle exists for outages, not for that.
function heldMessage(rec: PendingDictation, reason: string | undefined, recordRefusal?: RecordRefusalKind): string {
  if (isOffline()) return WAITING_FOR_CONNECTION_MESSAGE;
  if (recordRefusal !== undefined && reason !== undefined) return reason;
  if (Date.now() - rec.createdAt >= HARD_WINDOW_MS) return THROTTLED_MESSAGE;
  return reason ?? RETRYING_MESSAGE;
}

function publishHeld(rec: PendingDictation, message: string): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "held",
    retryable: true,
    error: message,
  });
}

// Publish the parked (permanent-failure) status: saved-and-retryable, with an explicit Retry (retryable
// true) and no auto-loop behind it (issue #1184).
function publishParked(rec: PendingDictation, reason: string): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "parked",
    retryable: true,
    error: parkMessage(reason),
  });
}

// Publish the dropped-as-stale status (issue #1590): sticky, never auto-clearing, and carrying the words
// back when we have them. With a transcript the action is "Send anyway" (a fresh turn, so NOT retryable -
// re-driving the tombstoned upload id could only be dropped again); without one, the audio is still on the
// device, so it is retryable under a fresh upload id instead.
// The "do we have words to hand back" question is asked of the COMPOSED message, not of the transcript
// alone: a Terminal Speak clip that was dropped before transcription still has the typed text the user
// composed around it, and that text is theirs and is recoverable. Only when the whole composed message is
// empty is there genuinely nothing to offer, and the recording itself becomes the recovery.
function publishDropped(rec: PendingDictation): void {
  const words = composeDroppedMessage(rec);
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "dropped",
    retryable: words.length === 0,
    recoverableText: words,
    error: words.length > 0 ? DROPPED_WITH_TRANSCRIPT_MESSAGE : DROPPED_NO_TRANSCRIPT_MESSAGE,
  });
}

// Publish the nothing-was-heard notice (issue #1590): visible and dismissible, with nothing to retry.
function publishUnheard(rec: PendingDictation): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "unheard",
    retryable: false,
    error: UNHEARD_MESSAGE,
  });
}

function isOffline(): boolean {
  return typeof navigator !== "undefined" && navigator.onLine === false;
}

// Install the connectivity/foreground listeners once, so a held clip resumes the instant the network
// returns or the app is brought to the foreground - not only on the next full app load.
function ensureRetryListeners(): void {
  if (_listenersInstalled) return;
  if (typeof window === "undefined") return;
  _listenersInstalled = true;
  window.addEventListener("online", () => void kickAll());
  if (typeof document !== "undefined") {
    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) void kickAll();
    });
  }
}
