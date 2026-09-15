# Turn detection, phase one - the running note

Mission `d2bb87ec`. Branch `mission/turn-detection-phase-one`, worktree
`D:\ReposFred\devthrottle-turn-detection`, cut from `origin/main` at `90dd38ad5`.

This is the note a fresh Manager is rebuilt from. It is short on purpose. Everything the
Architect knows is either here, in the brief, or in the design - never only in a conversation.

## Where the rest lives

- The brief: `devthrottle_internal/docs/missions/BRIEF-2026-09-15-turn-detection-phase-one.md`
- The design and its evidence: `devthrottle_internal/docs/design/trustworthy-state-switching/`
  (`trustworthy-state-switching.html` section five is the measurement,
  `implementation-plan.html` is the plan, `review-2026-09-15.md` is the independent review,
  `harness/` is the scoring code)
- How the mission is conducted: `cc-devthrottle workflow instructions mission`
- The issues: parent #2853, children #2854 through #2858

## The goal, in one sentence

A session should turn blue because the conversation gained something, not because a byte
arrived.

## Rulings that are already settled - do not reopen these

- Phase one is the terminal rule only. The ten-second silence rule is untouched, so red
  still lands mid-work. That needs each agent's own turn-end event and is phase two.
- The rule ships behind a switch that is OFF. Turning it on is the owner's decision and he
  wants the shadow numbers first.
- There are two candidate rules, not one, and neither is assumed to win. Both go behind one
  interface and the choice is made on live bytes.
- A miss must not be able to lose a turn. Any later byte re-arms the check, and bytes inside
  the settling window push it out rather than being dropped.
- The corpus never enters the product repository - it is public and those screens are real
  work with client names and paths in them. Only hand-redacted screen pairs cross over.
- The corpus is a regression gate, not the proof. Its labels come from the old detector's own
  timing and its body split is a guess. The proof is the shadow run on live bytes.
- Owner's decision, 15 September: the live shadow happens by cutting a release once the code
  lands, so his installed Director picks it up on its next auto-update. The corpus numbers are
  reported first and the live comparison follows.

## Where the work is up to

Filled in as the phase runs. See the phase log below.

## Phase log

- 15 September: worktree cut, five issues filed as children of #2853, the owner answered the
  one question that was his to answer (how the live shadow happens).

## Ruling: the eighty percent similarity is difflib's ratio, not a longest common subsequence

The near-duplicate filter's 0.80 threshold comes from Python's
`difflib.SequenceMatcher.ratio`, which is not a longest-common-subsequence ratio. It finds the
longest matching block, then recurses on what is left of it and what is right of it, and
answers twice the matched length over the total length. The greedy block choice can lose
matches an optimal subsequence would find.

Measured on twenty thousand random short pairs on 15 September: the two disagree on
twenty-eight percent of them, and the worst disagreement was 0.15 against 0.67 - far wider than
the threshold being tested. A C# implementation built on a longest common subsequence would
look right, pass its own tests, and quietly score differently from the published measurement,
which would make the whole candidate comparison in work item five meaningless.

So the C# must implement difflib's algorithm. Python's `autojunk` heuristic only engages on
sequences of two hundred elements or more; whatever the C# does above that has to be written
down rather than left to chance.

## Ruling: the pinned corpus is a NEW set, and the published numbers are re-taken against it

The published measurement counted four thousand three hundred and twenty-eight pairs. An
independent rerun during review counted four thousand three hundred and twenty-five. A third
rerun, by the Architect on 15 September from the same scripts, produced one thousand three
hundred and thirty-seven repaint-labelled pairs and two hundred and thirty-nine long
unexplained ones, where the published figures were nine hundred and sixty-eight plus three
hundred and sixty-three, and two hundred and thirty-one. The directory the corpus is built from
keeps growing and the scripts carry only a lower date bound, so every rerun scores a different
set. That is not a discrepancy to reconcile; it is the defect the manifest exists to remove.

**The published set is not recoverable and no attempt is made to recover it.** Reconstructing
the exact four thousand three hundred and twenty-eight would be fitting the corpus to a number
rather than pinning a corpus. The manifest is built fresh, with an explicit upper time bound as
well as a lower one, and every number in the phase one report is re-taken against it. The older
figures stay in the design document as history and are never quoted as current.

**What the manifest records, per wake:** the session id, the agent, the wake time, how long blue
lasted, whether a submission explained it, the class label, and both screen files by relative
path AND by content hash. The hashes are what make it a gate: a screen file that changes under
the manifest is caught rather than silently rescored.

## Ruling: the shadow verdicts go to a local append-only log, not to the activity ledger

The activity ledger's rows live on the hosted Gateway and a session key is refused there - that
is question five on #2853. A verdict written only to the ledger is a number nobody on the
machine that produced it can read, which makes the whole comparison unanswerable.

So phase one adds `TurnDetectionShadowLog`, built in exactly the shape of
`src/CcDirector.Core/Wingman/StateChangeLog.cs`: append-only lines of JSON, one file per
session, the path resolved through `CcStorage` so `CC_DIRECTOR_ROOT` redirects it under test, a
single process-wide lock, and failures logged and swallowed so the log can never affect the
session it observes. It carries its own switch.

One row per check, never per byte. Each row carries the time, the agent, what the row candidate
decided and the first new row it saw, what the size candidate decided and its magnitude, what
the old byte rule would have done, and both screen hashes so the pair can be matched back to a
saved turn-review capture.

**The rule switch defaults OFF and the shadow log defaults ON.** That pairing is the point: the
owner's Director produces the comparison numbers while behaving exactly as it does today.
