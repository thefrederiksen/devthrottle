// The closed list of verdict words, for the one screen that has to OFFER them (the Wingman-on-every-turn
// mission, slice G): the "this is wrong" picker, where the owner says which word he thinks was right.
//
// WHY A LIST LIVES IN THE CLIENT AT ALL, given that the client is dumb. Everything else about a verdict is
// stamped by the Gateway and rendered verbatim - the label, the summary, the receipt, the refusal sentence - and
// the panel decides nothing about what any of it MEANS. This is the one thing the Gateway cannot stamp on a row,
// because it is not a fact about that row: it is the set of answers the owner may give, and it has to be on the
// screen before he gives one. So it is here, and it is the WORDS THEMSELVES, unprettified - no friendly
// spellings, no per-word explanations, nothing this file decides. A word the Gateway would refuse cannot be
// offered, and a word it would accept is never hidden.
//
// AND IT CANNOT DRIFT. TurnVerdictVocabularyReachesTheClientTests (C#, in the default gate) reads this file and
// fails when this list and CcDirector.Core's TurnVerdictVocabulary.AllVerdicts differ in content or in order.
// That is the same protection the labelling tool in the internal repository has, and it exists because the whole
// point of a closed word is that two readers can be compared mechanically: a vocabulary that has silently split
// in two makes every number computed across the split a comparison of two different questions.
//
// IT IS THE SEVEN, NOT THE SIX. The Wingman may only ever ANSWER with six; "not-a-turn-end" belongs to the
// detector and says the boundary fired while the session was still working. The owner may correct TO it, because
// his correction becomes a label in the graded corpus and "that was never the end of a turn" is a real reading of
// a real failure that none of the six can express.
export const VERDICT_WORDS = [
  "needed-you",
  "finished",
  "stuck-recoverable",
  "stuck-needs-person",
  "continues-alone",
  "not-a-turn-end",
  "cannot-tell",
] as const;

export type VerdictWord = (typeof VERDICT_WORDS)[number];
