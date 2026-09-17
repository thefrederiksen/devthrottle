// Every stop the Wingman judged for one session, as the Wingman tab shows it (the Wingman inspector, phase 3).
//
// THE CLIENT IS DUMB. Every string and flag below is folded once on the Gateway by WingmanStopsFold: what an outcome
// means, which filter group a stop is in, the colour the row wore at the time, why an answer was refused, and whether
// the carrying-on clock ran out. The tab formats the UTC instants into local time and lays out the rest verbatim.
//
// Hand-written rather than generated, like verdictAnswer.ts: the committed schema.ts predates the route. The shapes
// mirror src/CcDirector.Gateway.Contracts/WingmanStopsDto.cs field for field.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** One filter chip, in the Gateway's display order. */
export interface WingmanStopGroup {
  key: string;
  label: string;
  count: number;
}

/** The one line across the top of the selected stop. */
export interface WingmanStopStrip {
  label: string;
  replyText: string;
  outcomeText: string;
}

/** One session fact the judge was given. */
export interface WingmanStopFact {
  name: string;
  value: string;
}

/** Block one: what the judge was shown. */
export interface WingmanStopSaw {
  kept: boolean;
  notKeptText?: string | null;
  screenRows: string[];
  sourceHeading?: string | null;
  sourceText?: string | null;
  recentTurns?: string | null;
  facts: WingmanStopFact[];
}

/** Block two: what the judge was asked. */
export interface WingmanStopAsked {
  prompt?: string | null;
  cut: boolean;
  cutText?: string | null;
  notAskedText?: string | null;
}

/** Block three: what the judge answered. */
export interface WingmanStopAnswered {
  rawReply?: string | null;
  cut: boolean;
  cutText?: string | null;
  replySeconds?: number | null;
  noAnswerText?: string | null;
}

/** The carrying-on clock of a "continues-alone" stop, or the expiry that ended one. Instants are UTC. */
export interface WingmanStopClock {
  setToRunOutAtUtc?: string | null;
  ranOutAtUtc?: string | null;
  text: string;
  ranOutTraceId?: string | null;
}

/** Block four: what the product did with the answer. */
export interface WingmanStopDid {
  decision: string;
  accepted: boolean;
  reason?: string | null;
  verdictLabel?: string | null;
  verdictSummary?: string | null;
  causeText?: string | null;
  replacedText?: string | null;
  replacedTraceId?: string | null;
  clock?: WingmanStopClock | null;
}

/** One stop the Wingman judged, or stood down from. Instants are UTC. */
export interface WingmanStop {
  traceId: string;
  observedAtUtc: string;
  recordedAtUtc: string;
  trigger: string;
  triggerText: string;
  outcome: string;
  outcomeText: string;
  group?: string | null;
  rowRecorded: boolean;
  rowColour?: string | null;
  rowColourHex?: string | null;
  rowLabel: string;
  verdictWord?: string | null;
  confidence?: string | null;
  verdictText: string;
  strip: WingmanStopStrip;
  saw: WingmanStopSaw;
  asked: WingmanStopAsked;
  answered: WingmanStopAnswered;
  did: WingmanStopDid;
}

/** The answer to GET /sessions/{sid}/wingman-stops. */
export interface WingmanStopsResponse {
  sessionId: string;
  stops: WingmanStop[];
  groups: WingmanStopGroup[];
}

/**
 * GET /sessions/{sid}/wingman-stops - every stop the Wingman judged for this session, newest first, with the filter
 * chips and their counts.
 *
 * Resolves with the Gateway's answer as sent. Throws a GatewayError when the read was refused (a session key, a
 * session outside this account, a Gateway with no record), carrying the Gateway's own sentence, so a failure is never
 * shown as "nothing judged".
 */
export async function readWingmanStops(sessionId: string, signal?: AbortSignal): Promise<WingmanStopsResponse> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/wingman-stops`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "read the Wingman's stops for this session");
  }
  return (await res.json()) as WingmanStopsResponse;
}
