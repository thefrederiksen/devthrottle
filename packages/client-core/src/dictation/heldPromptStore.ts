// Durable local store for typed prompts the Gateway is still delivering (voice delivery phase 5, contract
// section 7, T6).
//
// A typed send answered 202 "still delivering" means the Gateway holds the prompt and is asking the Director
// what became of it. The client must never send those words again, but it must keep reading the ruling - after
// a reload too - and, when the Gateway says the words did NOT arrive, hand them back to the owner. So the
// delivery id, the session id and the text are written here the moment the 202 arrives, and removed only when
// the Gateway rules the prompt delivered, or the owner acts on a prompt shown back (Send anyway or Dismiss).
//
// The same pattern as the dictation pending store (pendingStore.ts), in its own small database: a typed
// prompt is not a recording, carries no audio, and is keyed by the Gateway's delivery id rather than a
// client-made upload id. A separate database also means no version upgrade of the recordings database, which
// every open tab holds.

export interface HeldPrompt {
  /** The id the Gateway minted for this prompt (contract section 7, T1). The key, and what the outcome is read by. */
  deliveryId: string;
  sessionId: string;
  /** The text exactly as it was sent - what is shown back, and what "Send anyway" sends again. */
  text: string;
  /** Epoch milliseconds the owner pressed Send. */
  sentAt: number;
  /** The id of the account that sent it. This database is one per origin, shared by every account on the
   *  browser, and the outcome read authenticates as whichever account is active - so a held prompt is read
   *  only while its own account is active. */
  accountId?: string;
  /** Set once the Gateway ruled the prompt NOT delivered, or could not confirm it arrived: the record is kept so
   *  the words survive a reload, and it is never read again - the ruling is final. */
  shownBack?: boolean;
  /** The Gateway's reason for a shown-back prompt ("not-delivered" or "unconfirmed"), verbatim. */
  shownBackReason?: string;
  /** The Gateway's decision whether to offer "Send anyway" for a shown-back prompt, verbatim. */
  offerSendAnyway?: boolean;
}

const DB_NAME = "dt-typed-prompts";
const STORE = "held";
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
      if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: "deliveryId" });
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

/** Write (or replace) a held typed prompt. Rejects when durable storage is unusable. */
export async function saveHeldPrompt(rec: HeldPrompt): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable typed prompt store unavailable");
  await tx("readwrite", (s) => s.put(rec));
}

/** Every held typed prompt on this device (oldest first). Empty when the store is unavailable. */
export async function listHeldPrompts(): Promise<HeldPrompt[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<HeldPrompt[]>("readonly", (s) => s.getAll() as IDBRequest<HeldPrompt[]>);
  return all.sort((a, b) => a.sentAt - b.sentAt);
}

/** One held typed prompt by delivery id, or null when it is gone. */
export async function getHeldPrompt(deliveryId: string): Promise<HeldPrompt | null> {
  if (!hasIndexedDb()) return null;
  const rec = await tx<HeldPrompt | undefined>("readonly", (s) => s.get(deliveryId) as IDBRequest<HeldPrompt | undefined>);
  return rec ?? null;
}

/** Remove a held typed prompt: delivered, or the owner acted on it. Idempotent. */
export async function deleteHeldPrompt(deliveryId: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.delete(deliveryId));
}
