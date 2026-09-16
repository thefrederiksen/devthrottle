// The turn verdict a session row carries, and the one call that answers it (the Wingman-on-every-turn mission,
// slice E).
//
// THE CLIENT IS DUMB. Every field below is stamped by the Gateway and rendered verbatim: the panel never works
// out what a verdict word means, which option is safe, or whether a screen is still current. The answer route
// decides all of that - it re-reads the screen, compares it with the one the verdict was formed on, and either
// writes the chosen options or refuses with a sentence. That sentence is what the owner sees, unedited.
//
// Hand-written rather than generated: the committed schema.ts predates these fields, exactly as it predates
// verdictState in ordering.ts.
import { authHeaders, gatewayFetch, GatewayError, type SessionDto } from "../api/client";

/** One option the Wingman read off the stop. `key` is its label; `send` never reaches the screen. */
export interface TurnVerdictOption {
  key: string;
  send: string;
  recommended: boolean;
  note: string;
}

/** The picker the options belong to, when the stop is a menu. */
export interface TurnVerdictMenu {
  question: string;
  selectionMode: "single" | "multiple" | string;
  submit: string;
}

/** The judged stop, as the Gateway stamps it on the row (the C# TurnVerdictDto). */
export interface TurnVerdict {
  verdictId: string;
  verdict: string;
  confidence: string;
  evidence: string;
  label: string;
  summary: string;
  agentRecommends?: string | null;
  answerVia: string;
  menu?: TurnVerdictMenu | null;
  options: TurnVerdictOption[];
  risk: string;
  failed?: boolean;
  /** When this verdict stopped describing the screen it was formed on, or null/absent while it still does. Only
   *  the history read ever carries it: the latest read and the roster fold never return a superseded record at
   *  all, so a verdict with this set came from the history. */
  supersededAtUtc?: string | null;
}

/** The stamped row fields the panel reads, augmented onto the generated session type. */
export type TurnVerdictRow = SessionDto & {
  verdictState?: string | null;
  turnVerdict?: TurnVerdict | null;
};

/** What the answer route did. `reason` is the Gateway's sentence for the owner, shown as it is. */
export interface TurnVerdictAnswerResult {
  accepted: boolean;
  code: string;
  reason: string;
}

/**
 * POST /sessions/{sid}/turn-verdict/answer - the owner's tap, sent as ONE request whatever the number of options,
 * so a multiple-select picker is answered in one activation under one screen lock. An empty `optionIndexes` is the
 * confirm of a parked reply; the route accepts it in that shape only.
 *
 * Resolves with the route's answer when it wrote the options. Throws a GatewayError on every refusal; the route's
 * sentence is on its `serverReason`, and the panel shows it verbatim.
 */
export async function answerTurnVerdict(
  sessionId: string,
  verdictId: string,
  optionIndexes: readonly number[],
  signal?: AbortSignal,
): Promise<TurnVerdictAnswerResult> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/turn-verdict/answer`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ verdictId, optionIndexes: [...optionIndexes] }),
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "answer that stop");
  }
  const body = (await res.json()) as Partial<TurnVerdictAnswerResult>;
  return { accepted: body.accepted === true, code: body.code ?? "", reason: body.reason ?? "" };
}

/** What the feedback route did. `reason` is the Gateway's sentence for the owner, shown as it is. */
export interface TurnVerdictFeedbackResult {
  accepted: boolean;
  code: string;
  reason: string;
}

/**
 * POST /sessions/{sid}/turn-verdict/feedback - the owner saying a verdict was WRONG, and which word he thinks was
 * right. It reaches the labelled corpus as an owner label, which outranks two reviewers agreeing.
 *
 * The verdict it names is usually SUPERSEDED by the time this is sent: answering a red row puts the session back
 * to work, and that is what supersedes it. The route accepts that on purpose - it is the ordinary case, not an
 * edge - and it is the reason the record is now kept rather than deleted.
 *
 * Resolves with the route's answer when the correction was stored. Throws a GatewayError on every refusal; the
 * route's sentence is on its `serverReason`, and the panel shows it verbatim.
 */
export async function reportTurnVerdictWrong(
  sessionId: string,
  verdictId: string,
  correctVerdict: string,
  note: string,
  signal?: AbortSignal,
): Promise<TurnVerdictFeedbackResult> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/turn-verdict/feedback`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ verdictId, correctVerdict, note }),
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "report that verdict wrong");
  }
  const body = (await res.json()) as Partial<TurnVerdictFeedbackResult>;
  return { accepted: body.accepted === true, code: body.code ?? "", reason: body.reason ?? "" };
}

/**
 * GET /sessions/{sid}/turn-verdicts?count=1 - the newest judged stop this session has, SUPERSEDED OR NOT.
 *
 * This is the read that keeps a correction reachable. Answering a red row is what puts the session back to work,
 * and working is what supersedes the verdict - so by the time the owner thinks "that was never a question", the
 * row carries no verdict any more and the latest read answers null. The history keeps it, which is what slice G
 * stopped deleting, and the panel reports it wrong from here.
 *
 * Resolves with the newest record, or null when this session has never been judged. Throws a GatewayError when
 * the read itself was refused, so a failure is never read as "nothing to show".
 */
export async function readLatestJudgedStop(
  sessionId: string,
  signal?: AbortSignal,
): Promise<TurnVerdict | null> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/turn-verdicts?count=1`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "read what the Wingman said about this session");
  }
  const body = (await res.json()) as { verdicts?: TurnVerdict[] | null };
  const rows = body.verdicts ?? [];
  return rows.length > 0 ? rows[0] : null;
}
