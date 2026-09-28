// A factory's memory (Factory Memory mission, phase 3b): the typed client the Cockpit's Memory tab reads and
// writes. These types mirror src/CcDirector.Gateway.Contracts/FactoryMemoryDtos.cs.
//
// A PERSON NAMES THE FACTORY. A session's calls are always about its own factory and it never names one; a
// person is in no factory, so every call here carries ?factory=<id>. The Gateway reads which kind of caller
// this is from the device key, never from anything this client says.
//
// A STALE WRITE IS NOT AN ERROR TO SWALLOW. The Gateway answers 409 with the note as it stands now under
// "current", so the person can merge rather than overwrite what someone else wrote. That answer is thrown as a
// FactoryMemoryRefusal carrying `current`, so the tab can show it.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";

export interface FactoryMemoryNote {
  factory: string;
  name: string;
  /** The version this is. A write sends it back as the version last read. */
  version: number;
  /** The text, or null when this version is a delete. */
  text: string | null;
  deleted: boolean;
  /** "session" or "person". */
  authorKind: string;
  /** The writing session's id, or the person's identifier. */
  authorId: string | null;
  writtenAtUtc: string;
}

export interface FactoryMemoryList {
  factory: string;
  notes: FactoryMemoryNote[];
  bytes: number;
  maxBytes: number;
  maxNotes: number;
}

export interface FactoryMemoryHistory {
  factory: string;
  name: string;
  /** Every kept version, newest first. */
  versions: FactoryMemoryNote[];
}

/**
 * The Gateway refused a write, delete or restore. `outcome` is the store's own word for why (for example
 * "Stale" when the note changed since it was read), and `current` is the note as it stands now when the
 * Gateway sent it - the thing a person needs in order to merge.
 */
export class FactoryMemoryRefusal extends GatewayError {
  readonly outcome: string | null;
  readonly current: FactoryMemoryNote | null;

  constructor(status: number, message: string, outcome: string | null, current: FactoryMemoryNote | null) {
    // The sentence is the server's reason, so gatewayErrorMessage shows it rather than a line invented from
    // the status code. A refusal of this kind is about what the memory holds, so retrying unchanged cannot help.
    super(status, message, { reason: message, code: outcome ?? undefined, retryable: false });
    this.name = "FactoryMemoryRefusal";
    this.outcome = outcome;
    this.current = current;
  }
}

const PREFIX = "/factory-memory/notes";

function notePath(factory: string, name?: string, suffix?: string): string {
  const base = name === undefined ? PREFIX : `${PREFIX}/${encodeURIComponent(name)}${suffix ?? ""}`;
  return `${base}?factory=${encodeURIComponent(factory)}`;
}

interface RefusalBody {
  error?: string;
  detail?: string;
  outcome?: string;
  current?: FactoryMemoryNote | null;
}

async function refusalFrom(res: Response, label: string): Promise<FactoryMemoryRefusal> {
  let body: RefusalBody = {};
  let raw = "";
  try {
    raw = await res.text();
    if (raw.length > 0) body = JSON.parse(raw) as RefusalBody;
  } catch {
    /* not JSON - the raw text, or the status code, is what there is to say */
  }
  const reason = body.error ?? (raw.length > 0 ? raw : `${res.status}`);
  const message = body.detail !== undefined ? `${reason}. ${body.detail}` : reason;
  return new FactoryMemoryRefusal(res.status, `${label} failed: ${message}.`, body.outcome ?? null, body.current ?? null);
}

// A 2XX is not proof the Gateway understood the request: a Gateway from before this mission answers an unknown
// path with the app's own HTML shell. Every answer asserts it got JSON.
async function send<T>(method: string, path: string, label: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(path, {
    method,
    headers: {
      Accept: "application/json",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      ...authHeaders(),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await refusalFrom(res, label);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not serve factory memory - it answered with a web page instead of data. Upgrade or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

/** The factory's notes as they stand. Deleted notes are not listed. */
export function listFactoryMemory(factory: string, signal?: AbortSignal): Promise<FactoryMemoryList> {
  return send<FactoryMemoryList>("GET", notePath(factory), "Read the factory's memory", undefined, signal);
}

/** One note, including a deleted one (it answers with the delete on it). */
export function getFactoryMemoryNote(factory: string, name: string, signal?: AbortSignal): Promise<FactoryMemoryNote> {
  return send<FactoryMemoryNote>("GET", notePath(factory, name), `Read the note '${name}'`, undefined, signal);
}

/** Every kept version of one note, newest first. */
export function getFactoryMemoryHistory(
  factory: string,
  name: string,
  signal?: AbortSignal,
): Promise<FactoryMemoryHistory> {
  return send<FactoryMemoryHistory>("GET", notePath(factory, name, "/history"), `Read the history of '${name}'`, undefined, signal);
}

/** Write a note, stating the version last read. A stale write throws a FactoryMemoryRefusal with `current`. */
export function setFactoryMemoryNote(
  factory: string,
  name: string,
  text: string,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<FactoryMemoryNote> {
  return send<FactoryMemoryNote>("PUT", notePath(factory, name), `Save the note '${name}'`, { text, expectedVersion }, signal);
}

/** Delete a note, stating the version last read. The delete is itself a version and a person can restore it. */
export function deleteFactoryMemoryNote(
  factory: string,
  name: string,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<FactoryMemoryNote> {
  return send<FactoryMemoryNote>("DELETE", notePath(factory, name), `Delete the note '${name}'`, { expectedVersion }, signal);
}

/**
 * Put an old version's text back as a new version, written by this person. Only a person may do this.
 *
 * `expectedVersion` is the CURRENT version the person was looking at when they chose to restore, and sending it is
 * the whole point: an agent can write a new version between the owner opening a note's history and pressing the
 * button, and without it the restore would bury that agent's lesson with nobody seeing (phases 3 and 4 review,
 * finding 4 - the Gateway learned to refuse a stale restore and this client was not telling it which version it
 * had seen, which made that refusal unreachable from the Cockpit).
 */
export function restoreFactoryMemoryNote(
  factory: string,
  name: string,
  version: number,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<FactoryMemoryNote> {
  return send<FactoryMemoryNote>(
    "POST",
    notePath(factory, name, "/restore"),
    `Restore version ${version} of '${name}'`,
    { version, expectedVersion },
    signal,
  );
}
