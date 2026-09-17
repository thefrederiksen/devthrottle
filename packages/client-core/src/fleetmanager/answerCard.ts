// A card button "sends your answer as if you had said it" (the Fleet Manager mission, step 6). The owner's words do
// two things, with the SAME words, byte for byte:
//
//   1. answer the record    POST /gateway/fleet-manager/outcomes/{id}/answer
//   2. tell the Fleet Manager    POST /sessions/{fleet manager}/prompt   (queues behind a turn in progress)
//
// THE ORDER IS ANSWER FIRST. The record is the durable truth: the Fleet Manager's digest, the badge and "Waiting on
// you" all read it, and an answer is final. Answering first means a record that cannot take the answer (already
// answered - someone got there first) stops the whole thing before the Fleet Manager is told something the record
// does not say.
//
// NO SILENT HALF-SUCCESS. Each outcome is reported:
//   - the answer fails: nothing was sent to the Fleet Manager, the card keeps its buttons, and the Gateway's
//     sentence is shown;
//   - the answer succeeds but the prompt fails: the record IS answered (it cannot be taken back), the Fleet Manager
//     was NOT told, and the page says exactly that, with the Gateway's sentence and a way to send the words again.
import { sendPrompt } from "../api/client";
import { answerFleetOutcome } from "./pageClient";

export type CardAnswerResult =
  | { kind: "sent" }
  | { kind: "answer-failed"; error: string }
  | { kind: "prompt-failed"; error: string };

export interface CardAnswerDeps {
  answer: (outcomeId: string, words: string) => Promise<void>;
  prompt: (sessionId: string, words: string) => Promise<void>;
}

const defaultDeps: CardAnswerDeps = {
  answer: answerFleetOutcome,
  prompt: (sessionId, words) => sendPrompt(sessionId, words, true),
};

function messageOf(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** Answer one record with the owner's words, then send the same words to the Fleet Manager session. */
export async function answerCard(
  outcomeId: string,
  fleetManagerSessionId: string | null | undefined,
  words: string,
  deps: CardAnswerDeps = defaultDeps,
): Promise<CardAnswerResult> {
  if (words.trim().length === 0) throw new Error("answerCard: the words are empty; a card never sends nothing");

  try {
    await deps.answer(outcomeId, words);
  } catch (err) {
    return { kind: "answer-failed", error: messageOf(err) };
  }

  if (!fleetManagerSessionId) {
    return {
      kind: "prompt-failed",
      error: "This account has no Fleet Manager, so there was no session to tell.",
    };
  }
  try {
    await deps.prompt(fleetManagerSessionId, words);
  } catch (err) {
    return { kind: "prompt-failed", error: messageOf(err) };
  }
  return { kind: "sent" };
}

/** Send the words to the Fleet Manager again, after the record was answered but the prompt did not arrive. */
export async function retellFleetManager(
  fleetManagerSessionId: string | null | undefined,
  words: string,
  deps: CardAnswerDeps = defaultDeps,
): Promise<CardAnswerResult> {
  if (!fleetManagerSessionId) {
    return { kind: "prompt-failed", error: "This account has no Fleet Manager, so there was no session to tell." };
  }
  try {
    await deps.prompt(fleetManagerSessionId, words);
  } catch (err) {
    return { kind: "prompt-failed", error: messageOf(err) };
  }
  return { kind: "sent" };
}
