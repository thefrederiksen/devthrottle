import { useMemo, useSyncExternalStore } from "react";

// The single live-status source for in-flight dictations (owner rule after #1139: a dictation Send
// must NEVER be silent). Every dictation surface reads from here - the on-screen status strip on the
// Terminal/Chat/Voice screens AND the session-list badge on the roster - so the same send shows the
// same live state whether the user stays on the session or walks back to the roster.
//
// The store is in-memory and keyed by uploadId. It intentionally survives route changes within the
// single-page PWA (leaving the Terminal for Home keeps the status), which is exactly why the roster
// can carry a dictation that started on the session screen. A full app reload clears the in-memory
// status, but the durable pendingStore record plus the background driver re-drive the send and
// re-publish status on the next load, so a held dictation is never silently dropped (issue #1182).
//
// Publishing lives in the send pipeline: uploadDictationToSession publishes the mechanical steps
// (uploading, transcribing) and the terminal outcome (done / failed), and the background driver
// publishes the very first "saving" step before any network work and the "held" step between retries.
// Consumers only read.

// saving      - writing the audio to durable on-device storage, before any network work.
// uploading   - streaming chunks to the Gateway (uploaded / total).
// transcribing - all chunks are up; the Gateway is assembling, transcribing, and injecting the turn.
// held        - kept durably and will keep retrying in the background (waiting for a connection, or
//               retrying, or throttled after the first hard hour). This is NOT a failure: the audio is
//               safe and delivery continues automatically. retryable is true so the UI offers Upload now.
//               With `delivering` set it is the "Still delivering" state (voice delivery, #3398): the
//               Gateway could not yet say whether the words reached the session, so the UI shows it calm
//               and in progress, and never offers "Send anyway" or a fresh-id "Retry" for it.
// parked      - a genuinely permanent, non-retryable failure stopped the auto-loop (issue #1184): the clip
//               is over the provider size cap or an unsupported format. The audio is KEPT and the clip is
//               saved-and-retryable, but delivery does NOT auto-retry - retryable is true so the UI offers
//               an explicit Retry, which is the only way it re-drives (it will succeed once the server
//               transcode-and-split fix lands). Distinct from held: nothing is auto-retrying.
// done        - the server confirmed it owns the turn (delivered). Brief, then auto-clears.
// failed      - a genuine, non-recoverable failure (durable storage unavailable, so the clip could not
//               be saved at all). Distinct from held: nothing is retrying.
// dropped     - the server deliberately DROPPED this clip as stale: the session moved on while it was in
//               flight, so the words were never delivered (issue #1590). Nothing is retrying and re-driving
//               the same upload id is useless by design (its moved-on tombstone is permanent, issue #1183) -
//               so this is a STICKY state that must never clear itself. It carries `transcript` when the
//               server heard something, and the UI offers "Send anyway" (a fresh turn) plus Dismiss. On the
//               rare drop before transcription the transcript is empty, retryable is true, and the audio is
//               kept for an explicit Retry under a FRESH upload id.
// unheard     - the clip was delivered to the server, which heard NOTHING in it (silence, no typed text), so
//               there was no turn to submit (issue #1590). Nothing was lost and there is nothing to retry;
//               this is a visible, dismissible notice so a Send never ends in silence.
export type DictationPhase =
  | "saving"
  | "uploading"
  | "transcribing"
  | "held"
  | "parked"
  | "done"
  | "failed"
  | "dropped"
  | "unheard";

export interface DictationStatus {
  /** The session the dictation is being sent into. */
  sessionId: string;
  /** The client-generated upload id (also the server Idempotency-Key). */
  uploadId: string;
  phase: DictationPhase;
  /** Chunks uploaded so far (uploading phase only). */
  uploaded?: number;
  /** Total chunks for this clip (uploading phase only). */
  total?: number;
  /** Human-readable reason for a held, parked, or failed phase. For held, this is the honest "kept and will
   *  keep trying" copy (never "was not transcribed"); for parked, the saved-and-retryable message; for
   *  failed, the non-recoverable reason. */
  error?: string;
  /** True when the clip is saved durably and the UI should offer a retry control: the held phase (an
   *  "Upload now" that kicks a waiting or throttled retry to full speed), the parked phase (an explicit
   *  "Retry" that re-enters the active drive after a permanent failure stopped the auto-loop), and a
   *  `dropped` clip with no transcript (an explicit Retry under a FRESH upload id). False for a genuine,
   *  non-recoverable failure that no retry can fix, and for a `dropped` clip that HAS a transcript - there
   *  the action is "Send anyway", not a retry, because re-driving that upload id can only be dropped again. */
  retryable?: boolean;
  /** The full message a `dropped` dictation would have delivered, carried so the UI can offer it back
   *  ("Send anyway", issue #1590). NOT just the transcript: it is the typed text the caret split the dictation
   *  around plus any earlier paused segments plus the words the server heard, composed exactly as the delivery
   *  path composes them. This is precisely what "Send anyway" sends, which is why the UI must show THIS and
   *  nothing else - a strip that quotes one thing and sends another is its own small lie. Empty/absent on the
   *  rare drop before transcription with no typed text, where the audio is kept for a fresh-id Retry instead. */
  recoverableText?: string;
  /** True on a `held` status whose last attempt the Gateway answered 202 "still delivering" (voice
   *  delivery, #3398): the words may already be in the session, so the UI shows "Still delivering" - calm,
   *  not an error - and offers nothing that could send a second copy. Absent on every other status. */
  delivering?: boolean;
  /** On a `dropped` status: whether the UI offers "Send anyway" (words to hand back) or "Retry" (only the
   *  recording). The Gateway's decision, passed through verbatim (phase 2, change 1): false for a recording the
   *  Gateway "could not confirm arrived", which is shown back with Dismiss only, because a second copy might
   *  double the words. The UI offers either button only when this is exactly true. */
  offerSendAnyway?: boolean;
  /** A non-blocking caution shown alongside a DELIVERED send (the `done` phase): the words were sent, but
   *  the capture-health check found a material audio-loss deficit, so the transcript may be missing words
   *  and the user should check it (issue #863, "never fail silently on mobile"). Unlike a plain `done`, a
   *  `done` carrying a warning does NOT auto-clear - the user dismisses it, so a Send that dropped audio is
   *  never silent. Absent on a clean send. */
  warning?: string;
  /** Epoch milliseconds of the last update (newest-first ordering, and the done auto-clear timer). */
  updatedAt: number;
}

type Listener = () => void;

const _byId = new Map<string, DictationStatus>();
const _listeners = new Set<Listener>();

// A frozen array snapshot recomputed only when the data changes, so useSyncExternalStore's getSnapshot
// returns a stable reference between renders (returning a fresh array every call would loop forever).
let _snapshot: DictationStatus[] = [];

function rebuildAndEmit(): void {
  _snapshot = Array.from(_byId.values());
  for (const l of _listeners) l();
}

function subscribe(listener: Listener): () => void {
  _listeners.add(listener);
  return () => {
    _listeners.delete(listener);
  };
}

/** Publish or replace the live status for one dictation (keyed by uploadId). */
export function publishDictationStatus(status: Omit<DictationStatus, "updatedAt">): void {
  _byId.set(status.uploadId, { ...status, updatedAt: Date.now() });
  rebuildAndEmit();
}

/** Drop one dictation's status - after a success has been acknowledged on screen, or the user
 *  dismisses a failure. Idempotent: clearing an unknown id is a no-op. */
export function clearDictationStatus(uploadId: string): void {
  if (_byId.delete(uploadId)) rebuildAndEmit();
}

/** Every current dictation status (in-flight, just-done, or failed-and-waiting). */
export function allDictationStatuses(): DictationStatus[] {
  return _snapshot;
}

/** React hook: the full live status list, re-rendering the caller whenever any dictation changes. */
export function useDictationStatuses(): DictationStatus[] {
  return useSyncExternalStore(subscribe, () => _snapshot, () => _snapshot);
}

/** React hook: the one status that matters for a session - an active (non-done) send if there is one,
 *  otherwise the most recent. Null when the session has no dictation activity. */
export function useDictationStatusFor(sessionId: string | undefined): DictationStatus | null {
  const all = useDictationStatuses();
  return useMemo(() => {
    if (!sessionId) return null;
    const mine = all.filter((s) => s.sessionId === sessionId).sort((a, b) => b.updatedAt - a.updatedAt);
    if (mine.length === 0) return null;
    return mine.find((s) => s.phase !== "done") ?? mine[0];
  }, [all, sessionId]);
}
