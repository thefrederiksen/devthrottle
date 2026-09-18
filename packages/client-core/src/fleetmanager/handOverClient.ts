// Hand over (the Fleet Manager mission, step 8): the owner changes who owns a running session - to the Fleet Manager,
// or back to the owner. The Gateway decides whether it may happen and writes every sentence, the success and each
// refusal; this file only carries the request and the words.
import { authHeaders, GatewayError, type SessionDto } from "../api/client";

export interface HandOverResult {
  sessionId: string;
  to: string;
  ownerSessionId?: string | null;
  previousOwnerSessionId?: string | null;
  /** The Gateway's sentence saying what changed, shown as sent. */
  sentence: string;
  session?: SessionDto | null;
}

/** POST /gateway/fleet-manager/hand-over. A refusal throws a GatewayError whose message is the Gateway's sentence. */
export async function handOverSession(sessionId: string, to: string): Promise<HandOverResult> {
  const res = await fetch("/gateway/fleet-manager/hand-over", {
    method: "POST",
    headers: { Accept: "application/json", "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ session: sessionId, to }),
  });
  if (!res.ok) throw await GatewayError.from(res, "hand the session over");
  return (await res.json()) as HandOverResult;
}

/** How one hand over ended, in the Gateway's words either way. */
export type HandOverOutcome = { ok: true; sentence: string } | { ok: false; error: string };

/** Hand one session over and say how it went. Never throws: a refusal or a lost request is an outcome to show. */
export async function runHandOver(
  sessionId: string,
  to: string,
  send: (sessionId: string, to: string) => Promise<HandOverResult> = handOverSession,
): Promise<HandOverOutcome> {
  try {
    const result = await send(sessionId, to);
    return { ok: true, sentence: result.sentence };
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }
}
