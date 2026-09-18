import type { RenderedBubble } from "@devthrottle/client-core/history/chatView";
import type { FleetOutcomeCard } from "@devthrottle/client-core/fleetmanager/pageClient";

// The Fleet Manager conversation (the Fleet Manager mission, step 6): the marked session's own history, with each
// outcome card placed at the time it was filed. This is LAYOUT, not ruling: which records exist, what they say and
// when they were filed all come from the Gateway; this only interleaves two time-ordered lists.
//
// A bubble the history carried no time for keeps its place after the bubble before it, so a card is never placed
// by a guess at a missing time. Cards filed after the last timed bubble come at the end.

export type ConversationItem =
  | { kind: "bubble"; key: string; bubble: RenderedBubble }
  | { kind: "card"; key: string; card: FleetOutcomeCard };

function timeOf(iso: string | undefined | null): number | null {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
}

export function mergeConversation(
  bubbles: readonly RenderedBubble[],
  cards: readonly FleetOutcomeCard[],
): ConversationItem[] {
  // The Gateway sends the cards oldest first; sort again by filed time (stable), so the interleave below never
  // depends on it.
  const queue = cards
    .map((card, i) => ({ card, i, t: timeOf(card.filedAtUtc) ?? Number.POSITIVE_INFINITY }))
    .sort((a, b) => a.t - b.t || a.i - b.i);

  const out: ConversationItem[] = [];
  let next = 0;
  bubbles.forEach((bubble, i) => {
    const t = timeOf(bubble.bubble.timestamp);
    if (t !== null) {
      while (next < queue.length && queue[next].t <= t) {
        out.push({ kind: "card", key: `card-${queue[next].card.id}`, card: queue[next].card });
        next += 1;
      }
    }
    out.push({ kind: "bubble", key: `bubble-${i}`, bubble });
  });
  for (; next < queue.length; next += 1) {
    out.push({ kind: "card", key: `card-${queue[next].card.id}`, card: queue[next].card });
  }
  return out;
}
