// YOUR INBOX (Factory Inbox mission, step C): the typed client the phone and the Cockpit read, mark and reply through.
// These types mirror the Gateway's answers in src/CcDirector.Gateway/Api/YourInboxEndpoints.cs and
// src/CcDirector.Gateway/Inbox/YourInboxFold.cs.
//
// CRITICAL RULE 7 - THE CLIENT IS DUMB. Who sent a message, its state, its tone, its links, the buttons it offers, where
// a reply goes, the filters and their counts, the empty sentence, the unread count, and the choices offered when a reply
// cannot go where it was meant to - every one arrives finished from the Gateway. A screen prints them and decides
// nothing.
import { authHeaders, GatewayError, gatewayFetch } from "../api/client";
import type { FactoryInboxTone } from "../factoryinbox/factoryInboxClient";

export interface YourInboxFrom {
  /** "session", "factory" or "gateway" (the Gateway's own morning report). */
  kind: string;
  /** Who it is from, for example "#412 Release - Developer" or "Booking - Boss". */
  label: string;
}

/** One way back from a message to where it came from. */
export interface YourInboxLink {
  /** "session", "report" or "factory": which of the shell's addresses the target is opened at. */
  kind: string;
  label: string;
  /** The session id, the report id, or the factory. */
  target: string;
}

/** One thing the owner can do to a message: the state it sets, and the button's words. */
export interface YourInboxAction {
  state: string;
  label: string;
}

export interface YourInboxReplyTo {
  target: string;
  /** Where a reply goes, in words, for example "Reply into the Factory inbox of Booking". */
  label: string;
}

export interface YourInboxReply {
  id: string;
  text: string;
  routedTo: string;
  /** Where it went, in words. */
  label: string;
  sentAtUtc: string;
}

/** One thing an agent did to a message: the act, the Gateway's finished line, and when. */
export interface YourInboxAgentAction {
  act: string;
  /** The Gateway's line, e.g. 'Archived by an agent: session 114 "Factory Inbox - Delivery Lead", 2026-11-07 14:02 UTC'. */
  line: string;
  atUtc: string;
}

export interface YourInboxMessage {
  id: string;
  subject: string;
  body: string;
  state: string;
  stateLabel: string;
  tone: FactoryInboxTone;
  unread: boolean;
  from: YourInboxFrom;
  links: YourInboxLink[];
  actions: YourInboxAction[];
  /** Where a reply goes; null when nobody reads a reply (a message from the Gateway itself), and then no Reply is offered. */
  replyTo: YourInboxReplyTo | null;
  sentAtUtc: string;
  readAtUtc: string | null;
  archivedAtUtc: string | null;
  /** Given only when one message is read on its own. */
  replies: YourInboxReply[] | null;
  /** What agents did to the message, oldest first, each with its finished line (the owner's decision of 7 November 2026). */
  agentActions: YourInboxAgentAction[];
  /** The newest agent act's line, for a list row; null when no agent has touched the message. */
  agentLine: string | null;
}

/** One filter on the screen: the view it shows, its words and how many it holds. */
export interface YourInboxFilter {
  view: string;
  label: string;
  count: number;
}

export interface YourInboxList {
  view: string;
  count: number;
  unread: number;
  unreadLabel: string;
  filters: YourInboxFilter[];
  /** What this view says when it holds nothing. */
  empty: string;
  /** Newest first. */
  messages: YourInboxMessage[];
}

export interface YourInboxCount {
  unread: number;
  /** "3 unread", or empty when there is nothing unread. */
  label: string;
}

/** An offer made when a reply cannot go where it was meant to. */
export interface YourInboxReplyOffer {
  /** "new-session" or "factory". */
  kind: string;
  label: string;
  directorId: string | null;
  repoPath: string | null;
  prompt: string | null;
  factory: string | null;
}

/** What a reply came to: it went (with the Gateway's words for where), or it did not and these are the choices. */
export type YourInboxReplyOutcome =
  | { kind: "sent"; reply: YourInboxReply; message: YourInboxMessage }
  | { kind: "offers"; sentence: string; offers: YourInboxReplyOffer[] };

const PREFIX = "/gateway/inbox";

// A 2XX is not proof the Gateway understood the request: a Gateway from before this mission answers an unknown path with
// the app's own web page. Every answer asserts it got JSON.
function assertJson(res: Response): void {
  const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  if (type !== "application/json")
    throw new GatewayError(
      502,
      "This Gateway does not serve Your inbox - it answered with a web page instead of data. Update or redeploy the Gateway.",
    );
}

async function readJson<T>(res: Response, what: string): Promise<T> {
  if (!res.ok) throw await GatewayError.from(res, what);
  assertJson(res);
  return (await res.json()) as T;
}

async function get<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const res = await gatewayFetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() }, signal });
  return readJson<T>(res, what);
}

async function post(path: string, body: unknown, signal?: AbortSignal): Promise<Response> {
  return gatewayFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(body),
    signal,
  });
}

function messagePath(id: string, suffix = ""): string {
  return `${PREFIX}/${encodeURIComponent(id)}${suffix}`;
}

/** One view of Your inbox ("unread", "inbox" or "archived" - the views the Gateway's filters name), newest first. */
export function getYourInbox(view: string, signal?: AbortSignal): Promise<YourInboxList> {
  return get<YourInboxList>(`${PREFIX}?view=${encodeURIComponent(view)}`, "read your inbox", signal);
}

/** The unread count for the Inbox entry. */
export function getYourInboxCount(signal?: AbortSignal): Promise<YourInboxCount> {
  return get<YourInboxCount>(`${PREFIX}/count`, "read how many messages are unread", signal);
}

/** One message, with the replies sent from it. */
export async function getYourInboxMessage(id: string, signal?: AbortSignal): Promise<YourInboxMessage> {
  const body = await get<{ message: YourInboxMessage }>(messagePath(id), "read the message", signal);
  return body.message;
}

/** Set a message's state - one of the states its own actions name. */
export async function setYourInboxState(id: string, state: string, signal?: AbortSignal): Promise<YourInboxMessage> {
  const res = await post(messagePath(id, "/state"), { state }, signal);
  const body = await readJson<{ message: YourInboxMessage }>(res, "change the message");
  return body.message;
}

/**
 * Reply to a message. `to` is omitted for where the message came from, or the kind of the offer the owner took ("factory").
 * A session that has ended is answered 409 with the Gateway's sentence and its offers: that is not a failure but a
 * choice for the owner, so it is returned, not thrown. Every other refusal throws with the Gateway's words.
 */
export async function replyYourInbox(id: string, text: string, to?: string, signal?: AbortSignal): Promise<YourInboxReplyOutcome> {
  const res = await post(messagePath(id, "/reply"), to === undefined ? { text } : { text, to }, signal);
  if (res.status === 409) {
    const type = (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
    if (type === "application/json") {
      const body = (await res.clone().json()) as { error?: string; offers?: Partial<YourInboxReplyOffer>[] };
      if (Array.isArray(body.offers)) {
        return {
          kind: "offers",
          sentence: body.error ?? "",
          offers: body.offers.map((o) => ({
            kind: o.kind ?? "",
            label: o.label ?? "",
            directorId: o.directorId ?? null,
            repoPath: o.repoPath ?? null,
            prompt: o.prompt ?? null,
            factory: o.factory ?? null,
          })),
        };
      }
    }
  }
  const body = await readJson<{ reply: YourInboxReply; message: YourInboxMessage }>(res, "send the reply");
  return { kind: "sent", reply: body.reply, message: body.message };
}
