// What the walkthrough's buttons do (the Fleet Manager mission, step 7), in order, with every outcome reported.
//
// THE ORDER IS: ACT ON THE SESSION FIRST, THEN RECORD IT. This is the opposite of a card on the Fleet Manager page
// (answerCard.ts), and on purpose:
//   - A card's words go to the Fleet Manager, which can always be told again, so the durable record is answered
//     first and a refused record stops everything.
//   - Here the words go to the SESSION, through the Wingman's one answer route, which refuses often and on purpose
//     (the screen changed, a menu owns it, the computer is not connected). The record is final, so it must only ever
//     carry an answer the session actually took. The Gateway enforces the same thing from its side: it records an
//     answer only once the answer route has marked that verdict answered.
// So a refused answer shows the route's sentence verbatim and NOTHING ELSE is sent - the record stays open. An answer
// the session took whose record could not be updated says exactly that, with the Gateway's sentence.
//
// A snooze is the same: the snooze route first, then the note on the record, which the Gateway writes only when it
// sees the session snoozed. Close is one Gateway call (closeWalkthroughSession) that decides, stops and records in
// that order on the server.
import { GatewayError, holdSession } from "../api/client";
import { answerTurnVerdict } from "../sessions/verdictAnswer";
import { recordWalkthroughAnswer, recordWalkthroughSnooze } from "./walkthroughClient";

export type WalkthroughActResult =
  | { kind: "done"; sentence: string }
  | { kind: "refused"; sentence: string }
  | { kind: "record-failed"; sentence: string };

export interface WalkthroughActionDeps {
  answer: (sessionId: string, verdictId: string, indexes: readonly number[]) => Promise<{ reason: string }>;
  recordAnswer: (recordId: string, verdictId: string, indexes: readonly number[]) => Promise<void>;
  snooze: (sessionId: string, minutes: number) => Promise<void>;
  recordSnooze: (recordId: string) => Promise<void>;
}

const defaultDeps: WalkthroughActionDeps = {
  answer: (sessionId, verdictId, indexes) => answerTurnVerdict(sessionId, verdictId, indexes),
  recordAnswer: recordWalkthroughAnswer,
  snooze: async (sessionId, minutes) => {
    await holdSession(sessionId, true, minutes);
  },
  recordSnooze: recordWalkthroughSnooze,
};

/** The Gateway's own sentence when it sent one; otherwise the error's message. */
export function sentenceOf(err: unknown): string {
  if (err instanceof GatewayError && err.serverReason) return err.serverReason;
  return err instanceof Error ? err.message : String(err);
}

/** Answer the session with the chosen options, then record the same options on the item's record. */
export async function answerWalkthroughItem(
  sessionId: string,
  recordId: string,
  verdictId: string,
  indexes: readonly number[],
  deps: WalkthroughActionDeps = defaultDeps,
): Promise<WalkthroughActResult> {
  let reason: string;
  try {
    reason = (await deps.answer(sessionId, verdictId, indexes)).reason;
  } catch (err) {
    return { kind: "refused", sentence: sentenceOf(err) };
  }
  try {
    await deps.recordAnswer(recordId, verdictId, indexes);
  } catch (err) {
    return { kind: "record-failed", sentence: sentenceOf(err) };
  }
  return { kind: "done", sentence: reason };
}

/** Snooze the session, then note it on the item's record. */
export async function snoozeWalkthroughItem(
  sessionId: string,
  recordId: string,
  minutes: number,
  deps: WalkthroughActionDeps = defaultDeps,
): Promise<WalkthroughActResult> {
  try {
    await deps.snooze(sessionId, minutes);
  } catch (err) {
    return { kind: "refused", sentence: sentenceOf(err) };
  }
  try {
    await deps.recordSnooze(recordId);
  } catch (err) {
    return { kind: "record-failed", sentence: sentenceOf(err) };
  }
  return { kind: "done", sentence: "" };
}
