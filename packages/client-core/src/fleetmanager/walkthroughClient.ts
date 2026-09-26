// "Take me through them" - the Fleet Manager's walkthrough (the Fleet Manager mission, step 7).
//
// Everything here is folded on the Gateway (CLAUDE.md rule 7): the round, its order, every heading and sentence,
// both picks on the answer buttons, and whether snooze and close are offered. This file only carries it. A refusal
// throws a GatewayError carrying the Gateway's own sentence, which the page shows as it is.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";
import type { FleetOutcomeCard } from "./pageClient";

export interface FleetWalkthroughReading {
  heading: string;
  available: boolean;
  /** Why there is no current reading, or why the one shown may be out of date. */
  note?: string | null;
  label?: string | null;
  summary?: string | null;
  evidenceLead?: string | null;
  /** The session's own words, copied exactly. */
  evidence?: string | null;
  riskLine?: string | null;
}

export interface FleetWalkthroughAdvice {
  heading: string;
  text?: string | null;
  emptyText?: string | null;
}

export interface FleetWalkthroughScreen {
  heading: string;
  /** True when the client should read the session's last lines from the terminal buffer route. */
  offered: boolean;
  lines: number;
  note?: string | null;
  loadingText: string;
}

export interface FleetWalkthroughOption {
  /** What the answer route takes. */
  index: number;
  label: string;
  note?: string | null;
  sessionPick: boolean;
  fleetManagerPick: boolean;
  /** The words beside the button, as the Gateway wrote them. */
  markText?: string | null;
}

export interface FleetWalkthroughAnswer {
  verdictId: string;
  question?: string | null;
  multiple: boolean;
  parkedReply: boolean;
  options: FleetWalkthroughOption[];
  pickNote?: string | null;
  sendChosenLabel: string;
  parkedReplyLabel: string;
  sendingText: string;
  recordFailedLead: string;
}

export interface FleetWalkthroughSnooze {
  offered: boolean;
  label: string;
  minutes: number;
  note?: string | null;
}

export interface FleetWalkthroughOpen {
  offered: boolean;
  label: string;
}

export interface FleetWalkthroughClose {
  offered: boolean;
  label: string;
  refusedText?: string | null;
  confirmTitle: string;
  confirmMessage: string;
  confirmLabel: string;
  busyLabel: string;
}

export interface FleetWalkthroughItem {
  id: string;
  position: number;
  positionLabel: string;
  kind: "ready" | "finding" | "decision";
  title: string;
  sessionName?: string | null;
  meta: string;
  waitLabel?: string | null;
  stepLine: string;
  done: boolean;
  sessionId?: string | null;
  reading: FleetWalkthroughReading;
  advice: FleetWalkthroughAdvice;
  screen: FleetWalkthroughScreen;
  /** "session": the Wingman's options, to the session. "fleet-manager": the record's card. "none": settled. */
  answerMode: "session" | "fleet-manager" | "none";
  answer?: FleetWalkthroughAnswer | null;
  card?: FleetOutcomeCard | null;
  snooze: FleetWalkthroughSnooze;
  skipLabel: string;
  open: FleetWalkthroughOpen;
  close: FleetWalkthroughClose;
}

export interface FleetManagerWalkthrough {
  generatedAtUtc: string;
  fleetManagerSessionId?: string | null;
  title: string;
  intro: string;
  backLabel: string;
  roundTitle: string;
  /** Sent back on every read, so the round stays the one the owner started. */
  roundIds: string[];
  items: FleetWalkthroughItem[];
  notInRound?: string | null;
  openCount: number;
  endTitle: string;
  endText: string;
  againLabel?: string | null;
  newRoundLabel?: string | null;
  emptyText?: string | null;
}

const PREFIX = "/gateway/fleet-manager/walkthrough";

function jsonHeaders(): HeadersInit {
  return { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() };
}

/** GET /gateway/fleet-manager/walkthrough - the round, folded. Pass the round's ids to keep the same round; pass
 *  nothing to start a new one. */
export async function getWalkthrough(roundIds?: readonly string[] | null, signal?: AbortSignal): Promise<FleetManagerWalkthrough> {
  const query = roundIds ? `?round=${roundIds.map(encodeURIComponent).join(",")}` : "";
  const res = await gatewayFetch(`${PREFIX}${query}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "read the walkthrough");
  return (await res.json()) as FleetManagerWalkthrough;
}

/** POST .../walkthrough/{id}/answered - record, as the owner's answer, the options the session has just taken. The
 *  Gateway records the options its answer route stored for that verdict, never these; the positions sent here are
 *  only compared with the stored ones, and a difference is refused. It also refuses unless the answer route marked
 *  that verdict answered, and when the verdict is not the stop the record is waiting on. */
export async function recordWalkthroughAnswer(recordId: string, verdictId: string, optionIndexes: readonly number[]): Promise<void> {
  const res = await gatewayFetch(`${PREFIX}/${encodeURIComponent(recordId)}/answered`, {
    method: "POST",
    headers: jsonHeaders(),
    body: JSON.stringify({ verdictId, optionIndexes: [...optionIndexes] }),
  });
  if (!res.ok) throw await GatewayError.from(res, "record your answer for the Fleet Manager");
}

/** POST .../walkthrough/{id}/snoozed - note on the record that its session was snoozed. */
export async function recordWalkthroughSnooze(recordId: string): Promise<void> {
  const res = await gatewayFetch(`${PREFIX}/${encodeURIComponent(recordId)}/snoozed`, {
    method: "POST",
    headers: jsonHeaders(),
    body: "{}",
  });
  if (!res.ok) throw await GatewayError.from(res, "record the snooze for the Fleet Manager");
}

/** What the close route did: the stop's own headline, and the Gateway's sentence when the record was not updated. */
export interface WalkthroughCloseResult {
  headline: string;
  recordError: string | null;
}

/** POST .../walkthrough/{id}/close - the Gateway decides again, stops the session through the one stop handler, then
 *  records it. A refusal (409 close_refused) throws with the Gateway's sentence and nothing was stopped. */
export async function closeWalkthroughSession(recordId: string): Promise<WalkthroughCloseResult> {
  const res = await gatewayFetch(`${PREFIX}/${encodeURIComponent(recordId)}/close`, {
    method: "POST",
    headers: jsonHeaders(),
    body: "{}",
  });
  if (!res.ok) throw await GatewayError.from(res, "close that session");
  const body = (await res.json()) as { stop?: { headline?: string } | null; recordError?: string | null };
  return { headline: body.stop?.headline ?? "", recordError: body.recordError ?? null };
}

/** GET /sessions/{sid}/buffer?lines=N - the session's last lines, from the existing terminal buffer route. */
export async function readSessionLines(sessionId: string, lines: number, signal?: AbortSignal): Promise<string> {
  const res = await gatewayFetch(`/sessions/${encodeURIComponent(sessionId)}/buffer?lines=${lines}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "read the session's screen");
  const body = (await res.json()) as { text?: string | null };
  return body.text ?? "";
}
