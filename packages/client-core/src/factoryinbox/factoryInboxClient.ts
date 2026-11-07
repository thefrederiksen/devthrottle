// THE FACTORY INBOX (Factory Inbox mission, phase 4): the typed client the phone and the Cockpit read and send through.
// These types mirror the Gateway's answers in src/CcDirector.Gateway/Api/FactoryInboxEndpoints.cs and
// src/CcDirector.Gateway/Factory/Inbox/FactoryInboxFold.cs.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Every word on these screens - a state, a badge, a trail line, a row's line, the
// summary's sentence - and every tone a screen colours by, arrives finished from the Gateway. A screen prints them and
// decides nothing; a new state or decision is one edit on the Gateway and none here.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";

/** The one word a screen maps to a colour. */
export type FactoryInboxTone = "ok" | "amber" | "red" | "grey";

export interface FactoryInboxTrailStep {
  /** The step's closed word: sent, received, with-boss, merged, decided. */
  step: string;
  /** The step as it is shown, for example "Received by the Secretary". */
  label: string;
  /** When the step happened; null when it has no time of its own. */
  atUtc: string | null;
}

export interface FactoryInboxDecision {
  kind: string;
  label: string;
  why: string | null;
  sessionId: string | null;
  todoId: string | null;
  decidedBySessionId: string;
  decidedAtUtc: string;
}

export interface FactoryInboxMessage {
  id: string;
  factory: string;
  text: string;
  rawText: string | null;
  note: string;
  /** "Good", "Bad", or empty when the owner gave no note. */
  noteLabel: string;
  source: string;
  /** "Typed" or "Spoken". */
  sourceLabel: string;
  surface: string;
  sentAtUtc: string;
  state: string;
  /** What the message's state is shown as: "Sent", "With the Boss", "Delegated", or why it waits. */
  stateLabel: string;
  tone: FactoryInboxTone;
  mergedInto: string | null;
  links: { kind: string; ref: string }[];
  bundleId: string | null;
  decision: FactoryInboxDecision | null;
  trail: FactoryInboxTrailStep[];
}

export interface FactoryInboxRow {
  /** The newest message's text, or that there are none. */
  line: string;
  /** How many still wait for a decision, in words; null when there are no messages. */
  waitingLine: string | null;
  badge: string;
  tone: FactoryInboxTone;
  /** Why the newest message waits, in the Gateway's words; null when it does not wait. */
  reasonLine: string | null;
}

export interface FactoryInboxListEntry {
  factory: string;
  title: string;
  row: FactoryInboxRow;
  waiting: number;
  lastMessage: FactoryInboxMessage | null;
}

export interface FactoryInboxList {
  count: number;
  factories: FactoryInboxListEntry[];
}

export interface FactoryInboxThread {
  factory: string;
  title: string;
  count: number;
  /** Newest first. */
  messages: FactoryInboxMessage[];
  /** Every spoken message the Gateway still holds for this factory (recorded, not left yet), oldest first. */
  held: FactoryInboxHeld[];
}

/** A spoken message held on the Gateway: the line, the time's label and the tone are the Gateway's, printed as they are. */
export interface FactoryInboxHeld {
  uploadId: string;
  line: string;
  atLabel: string;
  atUtc: string;
  tone: string;
}

export interface FactoryInboxSummary {
  factory: string;
  sinceUtc: string;
  untilUtc: string;
  count: number;
  /** The one sentence: what came in and what was decided. */
  line: string;
  byDecision: { kind: string; label: string; count: number }[];
  items: FactoryInboxMessage[];
}

/** The owner's Good or Bad note on a message; "none" when there is none. */
export type FactoryInboxNote = "none" | "good" | "bad";

const PREFIX = "/gateway/factory";

function factoryPath(factory: string, suffix: string): string {
  return `${PREFIX}/${encodeURIComponent(factory)}${suffix}`;
}

// A 2XX is not proof the Gateway understood the request: a Gateway from before this mission answers an unknown path with
// the app's own web page. Every answer asserts it got JSON.
async function readJson<T>(res: Response, what: string): Promise<T> {
  if (!res.ok) throw await GatewayError.from(res, what);
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not serve the factory inbox - it answered with a web page instead of data. Update or redeploy the Gateway.",
    );
  return (await res.json()) as T;
}

async function get<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  return readJson<T>(res, what);
}

/** Every factory with a Boss: its row's words, and its newest message. */
export function getFactoryInboxList(signal?: AbortSignal): Promise<FactoryInboxList> {
  return get<FactoryInboxList>(`${PREFIX}/inbox`, "read the factory inbox", signal);
}

/** One factory's thread, newest first. */
export function getFactoryInboxThread(factory: string, signal?: AbortSignal): Promise<FactoryInboxThread> {
  return get<FactoryInboxThread>(factoryPath(factory, "/inbox"), "read the factory's messages", signal);
}

/** What came in since yesterday at this time, and the decision on each (the Gateway's default window). */
export function getFactoryInboxSummary(factory: string, signal?: AbortSignal): Promise<FactoryInboxSummary> {
  return get<FactoryInboxSummary>(factoryPath(factory, "/inbox/summary"), "read the factory's summary", signal);
}

/** Leave a typed message in a factory's inbox. A refusal (archived factory, no Boss) throws with the Gateway's words. */
export async function sendFactoryInboxMessage(
  factory: string,
  text: string,
  note: FactoryInboxNote,
  signal?: AbortSignal,
): Promise<FactoryInboxMessage> {
  const res = await gatewayFetch(factoryPath(factory, "/inbox"), {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ text, note, source: "typed" }),
    signal,
  });
  const body = await readJson<{ message: FactoryInboxMessage }>(res, "leave the message");
  return body.message;
}
