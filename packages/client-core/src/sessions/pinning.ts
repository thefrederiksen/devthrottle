// Pinning (the Fleet Manager mission, step 8). The Gateway decides which rows are pinned and in what order, and every
// word a pinned row wears (SessionDto.pin); this only lifts those rows out of the top level so a shell can draw them
// first. The rows that are not pinned keep the order the shell already gives them - nothing about them changes.
import type { SessionDto, SessionPin } from "../api/client";

export interface PinnedSplit {
  /** The pinned top-level rows, in the Gateway's rank order. */
  pinned: SessionDto[];
  /** Every other top-level row, in the order it was given. */
  rest: SessionDto[];
  /** The first pinned row's words, for the heading over the rest and the hand-over link; null when nothing is pinned. */
  pin: SessionPin | null;
}

/** Split the tree's roots into the pinned rows and the rest. Pass roots, never the whole roster: a pinned session is
 *  a top-level row with its team under it. */
export function splitPinned(roots: SessionDto[]): PinnedSplit {
  const pinned = roots
    .filter((s) => s.pin != null)
    .map((s, i) => ({ s, i }))
    .sort((a, b) => (a.s.pin!.rank - b.s.pin!.rank) || (a.i - b.i))
    .map((x) => x.s);
  const rest = roots.filter((s) => s.pin == null);
  return { pinned, rest, pin: pinned.length > 0 ? pinned[0].pin! : null };
}
