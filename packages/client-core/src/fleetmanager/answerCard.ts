// A card button "sends your answer as if you had said it" (the Fleet Manager mission, step 6; the steps 5 and 6
// fixes). It is ONE Gateway call:
//
//   POST /gateway/fleet-manager/outcomes/{id}/answer
//
// The Gateway records the answer and, in the same save, queues the owner's words to the Fleet Manager as an event.
// The event reaches the Fleet Manager only while it is waiting for a prompt, is sent again until the Fleet Manager
// acknowledges it, and survives a reload, a closed browser and a restarted Fleet Manager. So the page never types
// into the Fleet Manager itself, and there is no second call that can fail after the first one succeeded.
//
// How far the answer has got is the Gateway's sentence on the answered card (answerDelivery), read on the next page
// refresh. A refused answer (already answered, not found) throws the Gateway's own sentence, and nothing was
// recorded or queued.
import { answerFleetOutcome } from "./pageClient";

export type CardAnswerResult = { kind: "recorded" } | { kind: "refused"; error: string };

export interface CardAnswerDeps {
  answer: (outcomeId: string, words: string) => Promise<void>;
}

const defaultDeps: CardAnswerDeps = { answer: answerFleetOutcome };

function messageOf(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** Answer one record with the owner's words. The Gateway passes them to the Fleet Manager. */
export async function answerCard(outcomeId: string, words: string, deps: CardAnswerDeps = defaultDeps): Promise<CardAnswerResult> {
  if (words.trim().length === 0) throw new Error("answerCard: the words are empty; a card never sends nothing");
  try {
    await deps.answer(outcomeId, words);
  } catch (err) {
    return { kind: "refused", error: messageOf(err) };
  }
  return { kind: "recorded" };
}
