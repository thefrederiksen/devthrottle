import type { SessionHistoryDto } from "./types";

// The conversation TAIL (traffic optimization, phase 2). The Chat screen used to re-download the whole
// conversation every time a turn arrived - up to a megabyte compressed for a long session. Now it sends back the
// cursor the Gateway handed it, and the Gateway answers with only the messages after what the client holds -
// but ONLY when it can prove the client holds exactly the start of what it would send now. Otherwise it answers
// in full. See CcDirector.Gateway/History/ConversationTail.cs for the proof.
//
// This file is the client half: keep the whole conversation, apply a tail to it, and hand the renderer the SAME
// full SessionHistoryDto a full read would have produced. The renderer never knows a tail happened.

/** The answer to a request that carried a cursor: a SessionHistoryDto whose `messages` may be only the tail. */
export interface SessionHistoryPage extends SessionHistoryDto {
  /** How many messages the client already holds and keeps. 0 means this answer IS the whole conversation.
   *  Absent from an older Gateway that does not know the cursor - that answer is always the whole one. */
  tailFrom?: number;
  /** The cursor for the whole conversation after this answer: what to send next. Absent from an older Gateway. */
  cursor?: string;
}

/** What the client holds for one session: the full conversation it would have got from a full read, and the
 *  cursor that names it. */
export interface HeldConversation {
  sessionId: string;
  history: SessionHistoryDto;
  /** "" when the Gateway handed no cursor (an older Gateway) - the next request then asks for the whole one. */
  cursor: string;
}

/**
 * Apply one answer to what is held. `sentCursor` is the cursor the request carried; `held` is what was held when
 * it was sent. Returns the new held conversation, or null when the answer cannot be applied - a tail that does
 * not start exactly where the held copy ends, or a tail with nothing held for this session. Null means: drop
 * what is held and ask for the whole conversation. Never a gap, never a duplicate: when in doubt, full.
 */
export function applyHistoryPage(
  held: HeldConversation | null,
  sessionId: string,
  sentCursor: string,
  page: SessionHistoryPage,
): HeldConversation | null {
  const { tailFrom, cursor, ...envelope } = page;
  const from = tailFrom ?? 0;
  let messages = page.messages ?? [];
  if (from !== 0) {
    // A tail is only meaningful on top of EXACTLY the copy the cursor named.
    if (
      held === null ||
      held.sessionId !== sessionId ||
      held.cursor !== sentCursor ||
      sentCursor === "" ||
      !Number.isInteger(from) ||
      from < 0 ||
      held.history.messages.length !== from
    ) {
      return null;
    }
    messages = held.history.messages.concat(messages);
  }
  return {
    sessionId,
    // Every other field comes from THIS answer - the Gateway folds them over the whole conversation, so the stale
    // notice, the empty text, the history state and the status are the full answer's, tail or not.
    history: { ...envelope, messages },
    cursor: cursor ?? "",
  };
}
