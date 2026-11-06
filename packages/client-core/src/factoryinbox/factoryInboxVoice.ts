// A SPOKEN FACTORY INBOX MESSAGE (Factory Inbox mission, phase 4): the owner's recording, sent through the same durable
// upload a session's dictation uses (the Gateway's /dictation chunk and acknowledgement legs, and its own register and
// complete for a factory inbox - src/CcDirector.Gateway/Api/FactoryInboxVoiceEndpoints.cs).
//
// DURABLE. The recording is saved in this browser the moment Send is pressed, under its own id, and that id is the
// upload's id and Idempotency-Key on the Gateway. It is deleted only when the Gateway gives a final answer - the
// message was left, or there was nothing in it to leave. Anything else - no connection, the transcription failing for
// now, credits - keeps it, and it is sent again from the saved copy: the next attempt uploads only the chunks the
// Gateway says it is still missing, and a complete the Gateway already answered answers the same again and leaves
// nothing twice.
//
// THE WORDS ARE THE GATEWAY'S (the verbatim rule). This file sends audio and never words: the Gateway transcribes it,
// applies only the account's listed corrections and keeps the raw words beside them.
import {
  authHeaders,
  extForMime,
  GatewayError,
  gatewayFetch,
  MAX_UPLOAD_CHUNK_BYTES,
  sha256Hex,
} from "../api/client";
import { planUploadChunks } from "../api/chunking";
import type { FactoryInboxMessage, FactoryInboxNote } from "./factoryInboxClient";

/** One recording waiting to be left in a factory inbox. */
export interface FactoryRecording {
  /** The recording's own id: also its upload id and Idempotency-Key on the Gateway. */
  id: string;
  factory: string;
  note: FactoryInboxNote;
  blob: Blob;
  /** Epoch milliseconds the owner pressed Send. */
  sentAt: number;
  /** The Gateway's refusal, kept with the recording so it is shown again after a reload and not sent again on its own. */
  refusal?: string;
}

/** What one attempt to leave a recording came to. */
export type FactoryVoiceOutcome =
  /** It is in the inbox (now, or by an earlier attempt). The saved copy is gone. */
  | { kind: "left"; message: FactoryInboxMessage | null }
  /** The Gateway's final answer is that nothing was left (nothing was heard). The saved copy is gone. */
  | { kind: "nothing"; error: GatewayError }
  /** Not yet: kept, and tried again. `error` is why, for the screen. */
  | { kind: "held"; error: unknown }
  /** Refused, and trying again unchanged cannot help (an archived factory, a recording that cannot be transcribed).
   *  Kept until the owner discards it. */
  | { kind: "refused"; error: GatewayError };

const DB_NAME = "dt-factory-inbox-voice";
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
    req.onerror = () => reject(req.error ?? new Error("the browser's storage could not be opened"));
  });
}

function tx<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = run(t.objectStore(STORE));
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error ?? new Error("the browser's storage refused the request"));
        t.oncomplete = () => db.close();
      }),
  );
}

/** Save a recording the moment Send is pressed. Throws when this browser has no durable storage: a recording that
 *  cannot be kept is not sent on a promise it cannot keep. */
export async function saveFactoryRecording(rec: FactoryRecording): Promise<void> {
  if (!hasIndexedDb()) throw new Error("this browser cannot keep the recording, so it was not sent");
  await tx("readwrite", (s) => s.put(rec));
}

/** The recordings still waiting for a factory, oldest first. */
export async function listFactoryRecordings(factory: string): Promise<FactoryRecording[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<FactoryRecording[]>("readonly", (s) => s.getAll() as IDBRequest<FactoryRecording[]>);
  return all.filter((r) => r.factory === factory).sort((a, b) => a.sentAt - b.sentAt);
}

/** Forget a recording: after a final answer, or when the owner discards a refused one. */
export async function deleteFactoryRecording(id: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.delete(id));
}

/**
 * The owner discards a recording that was refused. The Gateway is told first, so it stops holding it and the factory's
 * thread stops showing it as held; only then is the saved copy forgotten. A Gateway that cannot be told throws, and the
 * recording is kept, so the device and the thread never disagree about it.
 */
export async function discardFactoryRecording(id: string): Promise<void> {
  const res = await gatewayFetch(`/dictation/${encodeURIComponent(id)}/abandon`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) throw await GatewayError.from(res, "discard the recording");
  await deleteFactoryRecording(id);
}

// The refusal is kept with the recording, so it is shown again after a reload rather than sent again on its own.
async function markRefused(rec: FactoryRecording, refusal: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.put({ ...rec, refusal }));
}

function voicePath(factory: string, suffix = ""): string {
  return `/gateway/factory/${encodeURIComponent(factory)}/inbox/voice${suffix}`;
}

// The acknowledgement retires the Gateway's record of a recording that has its final answer. Best kept: a lost
// acknowledgement leaves the record, and the next register of the same id answers the same final answer again.
async function acknowledge(id: string): Promise<void> {
  try {
    await gatewayFetch(`/dictation/${encodeURIComponent(id)}/ack`, { method: "POST", headers: { ...authHeaders() } });
  } catch {
    /* the Gateway keeps the final answer until a later acknowledgement retires it */
  }
}

async function finish(rec: FactoryRecording, outcome: FactoryVoiceOutcome): Promise<FactoryVoiceOutcome> {
  await deleteFactoryRecording(rec.id);
  await acknowledge(rec.id);
  return outcome;
}

async function classify(rec: FactoryRecording, res: Response, what: string): Promise<FactoryVoiceOutcome> {
  const error = await GatewayError.from(res, what);
  // Nothing was heard, or the recording was empty: final. Nothing is left, and saying it again changes nothing.
  if (error.code === "message_empty" || error.code === "empty_recording") return finish(rec, { kind: "nothing", error });
  if (error.retryable || res.status === 402) return { kind: "held", error };
  await markRefused(rec, error.message);
  return { kind: "refused", error };
}

/** A final answer from the Gateway about a recording it already dealt with: left only when it says delivered. */
function finishTerminal(rec: FactoryRecording, answer: { delivered?: boolean; line?: string }): Promise<FactoryVoiceOutcome> {
  if (answer.delivered === true) return finish(rec, { kind: "left", message: null });
  const line = answer.line ?? "This recording was already dealt with; nothing was left from it.";
  return finish(rec, { kind: "nothing", error: new GatewayError(200, line, { reason: line, retryable: false }) });
}

/**
 * One attempt to leave a saved recording in its factory's inbox. Safe to repeat with the same recording: the Gateway
 * knows it by its id.
 */
export async function sendFactoryRecording(rec: FactoryRecording, signal?: AbortSignal): Promise<FactoryVoiceOutcome> {
  let reg: Response;
  try {
    reg = await gatewayFetch(voicePath(rec.factory), {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json", "Idempotency-Key": rec.id, ...authHeaders() },
      body: "{}",
      signal,
    });
  } catch (err) {
    return { kind: "held", error: err };
  }
  if (!reg.ok) return classify(rec, reg, "send the recording");
  const registered = (await reg.json()) as { upload_id?: string; terminal?: boolean; delivered?: boolean; line?: string };
  // A final answer on the register leg is read exactly as on the complete leg: only a recording the Gateway says was
  // delivered was left. One it abandoned (the stale sweep after a long hold) left nothing, and saying "left" would
  // delete the owner's only copy while telling them it arrived.
  if (registered.terminal === true) return finishTerminal(rec, registered);
  const uploadId = registered.upload_id ?? rec.id;

  let bytes: Uint8Array;
  try {
    bytes = new Uint8Array(await rec.blob.arrayBuffer());
  } catch (err) {
    return { kind: "held", error: err };
  }
  const ranges = planUploadChunks(bytes.length, MAX_UPLOAD_CHUNK_BYTES);
  if (ranges.length === 0) {
    const error = new GatewayError(422, "The recording is empty, so nothing was left in the factory inbox. Record it again.", {
      reason: "The recording is empty, so nothing was left in the factory inbox. Record it again.",
      code: "empty_recording",
      retryable: false,
    });
    return finish(rec, { kind: "nothing", error });
  }
  const completeBody = JSON.stringify({
    totalChunks: ranges.length,
    mime: rec.blob.type || "audio/webm",
    ext: extForMime(rec.blob.type),
    note: rec.note,
    sentAtUtc: new Date(rec.sentAt).toISOString(),
  });

  // Complete first: the Gateway answers which chunks it still needs, so a resumed attempt uploads only those.
  for (let round = 0; round < ranges.length + 2; round++) {
    let comp: Response;
    try {
      comp = await gatewayFetch(voicePath(rec.factory, `/${encodeURIComponent(uploadId)}/complete`), {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
        body: completeBody,
        signal,
      });
    } catch (err) {
      return { kind: "held", error: err };
    }
    if (comp.status === 201) {
      const body = (await comp.json()) as { message: FactoryInboxMessage };
      return finish(rec, { kind: "left", message: body.message });
    }
    if (comp.status === 200) {
      return finishTerminal(rec, (await comp.json()) as { delivered?: boolean; line?: string });
    }
    if (comp.status === 409) {
      const text = await comp.clone().text();
      let missing: number[] | null = null;
      try {
        const parsed = JSON.parse(text) as { missing?: unknown };
        if (Array.isArray(parsed.missing)) missing = parsed.missing.filter((i): i is number => typeof i === "number");
      } catch {
        missing = null;
      }
      if (missing === null || missing.length === 0) return classify(rec, comp, "send the recording");
      for (const i of missing) {
        const r = ranges[i];
        if (r === undefined) return { kind: "held", error: new GatewayError(409, `The Gateway asked for part ${i} of a recording with ${ranges.length} parts.`) };
        const part = bytes.subarray(r.start, r.end);
        let put: Response;
        try {
          put = await gatewayFetch(`/dictation/${encodeURIComponent(uploadId)}/chunk/${r.index}`, {
            method: "PUT",
            headers: { "Content-Type": "application/octet-stream", "X-Chunk-Sha256": await sha256Hex(part), ...authHeaders() },
            body: part,
            signal,
          });
        } catch (err) {
          return { kind: "held", error: err };
        }
        if (!put.ok) return { kind: "held", error: await GatewayError.from(put, "upload the recording") };
      }
      continue;
    }
    return classify(rec, comp, "send the recording");
  }
  return { kind: "held", error: new GatewayError(409, "The Gateway kept asking for parts of the recording; it will be sent again.") };
}
