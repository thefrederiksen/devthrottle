// Both model calls behind every reading of one session - what was fed in, the exact prompt, and the raw answer, for
// the judge AND for the narration (the owner's ruling of 2026-09-18). STAFF ONLY: the Gateway refuses this read to
// any account that is not on the deployment's staff list, and the Cockpit draws the tab only when GET /account/status
// says the same.
//
// THIS ONE IS A DUMP, NOT A FOLD, AND ON PURPOSE. Every other Wingman read in this package carries the Gateway's own
// finished words and the client lays them out (CLAUDE.md rule 7). This carries the stored bytes: the package as it
// was serialized, the prompt as it was sent, the answer as it came back. A debug view that showed a rendering of the
// evidence would answer a different question from the one it is opened to answer. The Gateway still owns the
// headings and the sentence shown where a call was not made, and those are read verbatim from this shape.
//
// Hand-written, like wingmanStops.ts, and mirroring src/CcDirector.Gateway/Wingman/WingmanDebugFold.cs field for field.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/** One stop, both calls. Every instant is UTC. */
export interface WingmanDebugStop {
  traceId: string;
  observedAtUtc: string;
  recordedAtUtc: string;
  trigger: string;
  outcome: string;
  cause?: string | null;
  verdictId?: string | null;
  model?: string | null;
  contractVersion?: string | null;

  /** The five fields of the contract, as validation kept them. */
  state?: string | null;
  label?: string | null;
  agentRecommends?: string | null;
  narration?: string | null;
  failed: boolean;
  failureReason?: string | null;
  rowColour?: string | null;
  rowLabel?: string | null;

  /** What BOTH calls were fed: one package, serialized. Null when none was kept, and then the text says why. */
  fed?: string | null;
  fedAbsentText?: string | null;

  judgePrompt?: string | null;
  judgePromptCutText?: string | null;
  judgeRawReply?: string | null;
  judgeRawReplyCutText?: string | null;
  judgeSeconds?: number | null;
  judgeNotAskedText?: string | null;

  narrationPrompt?: string | null;
  narrationPromptCutText?: string | null;
  narrationRawReply?: string | null;
  narrationRawReplyCutText?: string | null;
  narrationSeconds?: number | null;
  narrationFailureDetail?: string | null;
  narrationNotMadeText?: string | null;
}

export interface WingmanDebugResponse {
  sessionId: string;
  judgeCallTitle: string;
  narrationCallTitle: string;
  stops: WingmanDebugStop[];
}

/**
 * GET /sessions/{sid}/wingman-debug - both calls of every stop, newest first.
 *
 * Throws a GatewayError carrying the Gateway's own sentence when the read is refused - which includes the ordinary
 * case of an account that is not staff. The view shows that sentence rather than an empty list, because "you may not
 * read this" and "there is nothing here" are different answers.
 */
export async function readWingmanDebug(sessionId: string, signal?: AbortSignal): Promise<WingmanDebugResponse> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}/wingman-debug`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw await GatewayError.from(res, "read the Wingman's debug record for this session");
  }
  return (await res.json()) as WingmanDebugResponse;
}
