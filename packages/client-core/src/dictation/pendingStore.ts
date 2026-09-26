// Durable local store for recorded dictation audio (issue #1006, strengthened for #1182).
//
// The instant Send is pressed, the raw recorded audio is written here (IndexedDB, which holds Blobs
// on disk) BEFORE any network work. So a page refresh, a tab crash, a phone reboot, or a dropped
// connection can no longer lose a just-recorded utterance: the audio survives, and the background
// driver re-drives the upload+submit on the next load and whenever connectivity returns.
//
// The on-device copy is the single source of truth. A record is deleted ONLY when the server confirms
// it owns the turn (submitted), after the owner dismisses a recording that was shown back not sent, or
// after an explicit user abandon (a later Task). Undelivered audio is NEVER aged out automatically - there is deliberately no time-based
// prune here (issue #1182): a recording the user could not send yet is kept until it is delivered.
//
// IndexedDB (not localStorage) because the audio is binary and can be minutes long; localStorage is
// string-only and tiny. Absence of IndexedDB (rare, e.g. some private modes) is capability-detected -
// the caller tells the user durable storage is unavailable rather than silently dropping the clip.

export interface PendingDictation {
  /** Client-generated GUID; doubles as the server upload id and Idempotency-Key. */
  id: string;
  sessionId: string;
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.). */
  blob: Blob;
  recordedMs: number;
  /** Capture-health (issue #863): the decoded audio duration and source blob size, measured once at
   *  Send time by decoding the clip. The fire-and-forget Send path never transcodes on-device, so these
   *  are stashed here and forwarded with the upload for the Gateway to persist the audio-loss deficit
   *  (recording wall-clock vs decoded audio duration). Absent when the on-device decode failed. */
  decodedSeconds?: number;
  sourceBytes?: number;
  /** A material capture-loss caution measured at Send time (issue #863): some of what was said was dropped
   *  during recording, so the transcript may be missing words. Stored durably so it survives a resume and is
   *  shown with the delivered `done` status (the Send path's equivalent of the dialog's dropped-audio
   *  warning, so a Send that dropped audio is never silent). Absent on a clean capture. */
  captureWarning?: string;
  /** The capture-health surface tag for this clip ("cockpit-send" / "mobile-send"), stamped when the
   *  clip was recorded and stored durably so a resume after a reload still files the measurement under
   *  the shell that actually recorded it. Absent on records written before this field existed - the
   *  Gateway then falls back to the tag that path has always used. */
  surface?: string;
  /** The id of the account that RECORDED this clip (devthrottle_internal #1509). This database is one
   *  per ORIGIN, so two accounts on one browser share it, while the upload authenticates as whichever
   *  account is active when it finally runs - so a clip recorded with no connection on one account would
   *  otherwise be driven with the other account's credential after a switch. Absent on records written
   *  before this field existed; those are driven only while this browser holds a single account, where
   *  there is no other account they could have belonged to. */
  accountId?: string;
  /** Typed text before the caret (Terminal Speak compose); empty for the voice case. */
  before: string;
  /** Typed text after the caret; empty for the voice case. */
  after: string;
  /** Earlier paused dictation segments already turned to text, joined ahead of this clip. */
  prefix: string;
  /** Epoch milliseconds the clip was saved here, which is AFTER the Send press (the audio is decoded
   *  first). Drives the retry cadence (hard for the first hour, then throttled) - NOT a prune deadline:
   *  undelivered audio is never aged out (issue #1182). It is not the Send time; `sentAt` is. */
  createdAt: number;
  /** Epoch milliseconds the owner pressed Send (stamped by DictationDialog before it stops the
   *  recorder). Every complete call carries it as `sentAtUtc`, and every retry of this upload id carries
   *  the SAME value: the Gateway measures the 5-minute age rule from it (voice delivery, #3398). A record
   *  saved before this field existed is given one when it is read - see migratePendingRecord. */
  sentAt: number;
  /** Set when the clip is PARKED after a genuinely permanent, non-retryable failure (issue #1184): it
   *  carries the allow-listed reason ("audio-too-large" / "unsupported-format"). A parked record keeps its
   *  audio but is EXCLUDED from every automatic retry trigger (app load, online, foreground, the cadence
   *  timer) - the forever-loop stops. It is cleared only by an explicit user Retry, which moves the record
   *  back to active. Absent for a normal, still-auto-retrying clip. */
  parkedReason?: string;
  /** Set when the Gateway did NOT send this clip and handed its words back (issue #1590; voice delivery,
   *  #3398): too old, session ended, or could not confirm it arrived - see `droppedReason`. The name is
   *  older than that meaning and is kept because records on disk carry it. Like `parkedReason` it EXCLUDES
   *  the record from every automatic retry trigger, and for a stronger reason: the Gateway's answer for this
   *  upload id is permanent (issue #1183), so re-driving it could only return the same answer. The record is
   *  kept so it stays visible across a reload (nothing about an unsent dictation may be silent) and so the
   *  words can be offered back. It leaves only by an explicit user action - Send anyway, Retry, or Dismiss.
   *  Absent for a normal clip. */
  staleDropped?: boolean;
  /** The words the server heard and handed back when it did not send the clip (issue #1590), stored durably so
   *  "Send anyway" still works after a reload. Empty on the rare drop before transcription, where the audio
   *  is what gets retried instead. Only meaningful alongside `staleDropped`. */
  droppedTranscript?: string;
  /** Why the Gateway did not send the clip, stored durably beside `staleDropped` so the right words
   *  survive a reload: "too-old" (more than 5 minutes from Send), "session-exited", or "unconfirmed" (the
   *  Director gave no answer for more than 5 minutes, so nobody can say whether the words arrived). Absent
   *  when the Gateway gave no reason (a tombstone written before the reason existed). */
  droppedReason?: string;
  /** The Gateway's decision whether to offer "Send anyway" for this dropped clip (phase 2, change 1),
   *  stored durably beside `staleDropped` so a reload shows the same buttons. False for "unconfirmed": the
   *  words may already be in, so the owner gets them back with Dismiss only. Always set on a dropped
   *  record; one saved before the field existed is given one when it is read - see migratePendingRecord. */
  droppedOfferSendAnyway?: boolean;
  /** Set while "Send anyway" on a dropped clip is waiting on a 202 "still delivering" answer (voice
   *  delivery, #3398): the words may already be in, so the same "Send anyway" - with the same delivery
   *  claim, which the Director refuses to type twice - is pressed again automatically on the ordinary
   *  cadence, including after a reload, until a 200 ends it or a failure shows the words back. Only
   *  meaningful alongside `staleDropped`. */
  sendingAnyway?: boolean;
  /** Set when the user ABANDONED this clip (issue #1181, Task 5). The record is kept ONLY to carry the
   *  abandon through to the Gateway: while set, the retry loop no longer uploads it - it calls
   *  /dictation/{id}/abandon instead, and deletes the on-device copy once the Gateway confirms (retrying
   *  silently if the Gateway is unreachable, so the session never wedges locked). Absent for a normal clip. */
  abandoning?: boolean;
}

const DB_NAME = "dt-dictation";
const STORE = "pending";
const DB_VERSION = 1;

function hasIndexedDb(): boolean {
  try {
    return typeof indexedDB !== "undefined";
  } catch {
    return false;
  }
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: "id" });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error("indexedDB open failed"));
  });
}

function tx<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = run(t.objectStore(STORE));
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error ?? new Error("indexedDB request failed"));
        t.oncomplete = () => db.close();
      }),
  );
}

/** True where durable storage is available (a microphone-capable PWA normally is). */
export function pendingStoreAvailable(): boolean {
  return hasIndexedDb();
}

/** Persist a recorded dictation the instant Send is pressed. Rejects only if IndexedDB is unusable. */
export async function savePending(rec: PendingDictation): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable dictation store unavailable");
  await tx("readwrite", (s) => s.put(rec));
}

// A record as it may sit on disk: one saved before `sentAt` existed has none.
type StoredPendingDictation = Omit<PendingDictation, "sentAt"> & { sentAt?: number };

/** The one-time migration of a record read from disk (voice delivery, #3398). `migrated` says the record
 *  changed and must be written back, so this happens once per record.
 *
 *  - A record saved before `sentAt` existed has no Send time, and nothing but its createdAt recorded when it
 *    was sent: createdAt was stamped when the clip was saved, a second or so after the Send press (after the
 *    audio decode), so it is the closest recorded moment and becomes the Send time.
 *  - A dropped record saved before `droppedOfferSendAnyway` existed was dropped for a reason the Gateway
 *    offers "Send anyway" for (too old, session exited, or no reason): "unconfirmed" did not exist when it
 *    was written, and the old screen showed it with "Send anyway". It keeps that. */
export function migratePendingRecord(stored: StoredPendingDictation): { rec: PendingDictation; migrated: boolean } {
  let rec = stored;
  let migrated = false;
  if (typeof rec.sentAt !== "number") {
    rec = { ...rec, sentAt: rec.createdAt };
    migrated = true;
  }
  if (rec.staleDropped && typeof rec.droppedOfferSendAnyway !== "boolean") {
    rec = { ...rec, droppedOfferSendAnyway: true };
    migrated = true;
  }
  return { rec: rec as PendingDictation, migrated };
}

// Migrate a record read from disk, writing it back when it changed, so the send path always finds a Send time.
async function readMigrated(stored: StoredPendingDictation): Promise<PendingDictation> {
  const { rec, migrated } = migratePendingRecord(stored);
  if (migrated) await savePending(rec);
  return rec;
}

/** Every pending dictation still on disk (oldest first). Empty when the store is unavailable. */
export async function listPending(): Promise<PendingDictation[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<StoredPendingDictation[]>("readonly", (s) => s.getAll() as IDBRequest<StoredPendingDictation[]>);
  const migrated = await Promise.all(all.map(readMigrated));
  return migrated.sort((a, b) => a.createdAt - b.createdAt);
}

/** One pending record by id, or null when it is gone (already sent, pruned, or no durable store).
 *  Used by the Retry action on a failed dictation to re-drive that exact clip. */
export async function getPending(id: string): Promise<PendingDictation | null> {
  if (!hasIndexedDb()) return null;
  const rec = await tx<StoredPendingDictation | undefined>(
    "readonly",
    (s) => s.get(id) as IDBRequest<StoredPendingDictation | undefined>,
  );
  return rec === undefined ? null : readMigrated(rec);
}

/** Remove a record once the server has confirmed the turn (submitted), or the owner dismisses a clip shown
 *  back not sent, or explicitly abandons it. There is deliberately no time-based prune: undelivered audio is kept until it
 *  is delivered or abandoned (issue #1182). */
export async function deletePending(id: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.delete(id));
}
