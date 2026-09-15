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
