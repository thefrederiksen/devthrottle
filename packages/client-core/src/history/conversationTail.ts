import type { SessionHistoryDto } from "./types";

// The conversation TAIL (traffic optimization, phase 2). The Chat screen used to re-download the whole
// conversation every time a turn arrived - up to a megabyte compressed for a long session. Now it sends back the
// cursor the Gateway handed it, and the Gateway answers with only the messages after what the client holds -
// but ONLY when it can prove the client holds exactly the start of what it would send now. Otherwise it answers
// in full. See CcDirector.Gateway/History/ConversationTail.cs for the proof.
//
// This file is the client half: keep the conversation, apply a tail to it, and hand the renderer the SAME
// SessionHistoryDto a full read would have produced. The renderer never knows a tail happened.
//
// THE WINDOW (issue #4372). A Chat may hold only the END of the conversation: the last few turns, from message
// `windowFrom` on, with older turns fetched when the reader scrolls back (applyOlderPage). The cursor still names
// the whole conversation up to the end of what is held - it proves what the Gateway sent, not what was kept - so
// tails apply to a window exactly as to the whole. With no window, windowFrom is 0 and nothing here changes.

/** The answer to a request that carried a cursor: a SessionHistoryDto whose `messages` may be only the tail. */
export interface SessionHistoryPage extends SessionHistoryDto {
  /** How many messages the client already holds and keeps. 0 means this answer IS the whole conversation.
   *  Absent from an older Gateway that does not know the cursor - that answer is always the whole one. */
  tailFrom?: number;
  /** The cursor for the whole conversation after this answer: what to send next. Absent from an older Gateway. */
  cursor?: string;
  /** On a whole answer cut to a window: the index of its first message in the whole conversation. Absent when the
   *  window was not asked for, or from a Gateway that does not know it - the answer then starts at message 0. */
  windowFrom?: number;
  /** On an OLDER page: the index of its first message. Its messages run up to the `before` the request named. */
  olderFrom?: number;
}

/** What the client holds for one session: the end of the conversation from `windowFrom`, and the cursor that
 *  names the whole conversation up to the end of it. */
export interface HeldConversation {
  sessionId: string;
  /** `messages` is the held window - the whole conversation when windowFrom is 0. */
  history: SessionHistoryDto;
  /** "" when the Gateway handed no cursor (an older Gateway) - the next request then asks for the whole one. */
  cursor: string;
  /** The index in the whole conversation of the first held message; 0 when the start is held. */
  windowFrom: number;
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
  const { tailFrom, cursor, windowFrom, ...envelope } = page;
  const from = tailFrom ?? 0;
  let messages = page.messages ?? [];
  let start = 0;
  if (from !== 0) {
    // A tail is only meaningful on top of EXACTLY the copy the cursor named.
    if (
      held === null ||
      held.sessionId !== sessionId ||
      held.cursor !== sentCursor ||
      sentCursor === "" ||
      !Number.isInteger(from) ||
      from < 0 ||
      held.windowFrom + held.history.messages.length !== from
    ) {
      return null;
    }
    messages = held.history.messages.concat(messages);
    start = held.windowFrom;
  } else if (windowFrom !== undefined) {
    if (!Number.isInteger(windowFrom) || windowFrom < 0) return null;
    start = windowFrom;
  }
  return {
    sessionId,
    // Every other field comes from THIS answer - the Gateway folds them over the whole conversation, so the stale
    // notice, the empty text, the history state and the status are the full answer's, tail or not.
    history: { ...envelope, messages },
    cursor: cursor ?? "",
    windowFrom: start,
  };
}

/**
 * Apply an OLDER page (issue #4372) - the turns before the held window - fetched for the turns before message
 * `before`. Returns:
 *  - the held conversation with the page put in front of it, when the page is exactly the run of messages that
 *    ends where the held window starts;
 *  - the answer applied as a fresh conversation, when the Gateway answered a whole one instead (it does that when
 *    the cursor no longer proves what is held - a /clear, a new transcript);
 *  - null when it fits neither, which means: drop what is held and read again.
 *
 * The held cursor is NOT compared: a tail may have landed while the page was in flight, moving the cursor on while
 * leaving the window's start where it was. The caller makes sure `held` is still built on the same whole read the
 * page was asked against (useSessionChat counts its whole reads), and the Gateway only served the page on a cursor
 * that proved it.
 */
export function applyOlderPage(
  held: HeldConversation,
  sessionId: string,
  before: number,
  page: SessionHistoryPage,
): HeldConversation | null {
  if (page.olderFrom === undefined) return applyHistoryPage(null, sessionId, "", page);
  const older = page.messages ?? [];
  if (
    held.sessionId !== sessionId ||
    held.windowFrom !== before ||
    !Number.isInteger(page.olderFrom) ||
    page.olderFrom < 0 ||
    page.olderFrom + older.length !== before
  ) {
    return null;
  }
  return {
    ...held,
    history: { ...held.history, messages: older.concat(held.history.messages) },
    windowFrom: page.olderFrom,
  };
}
