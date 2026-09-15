# Manager task - phase one, work items one to four

You are the Manager for phase one of the turn detection mission.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-detection`) on branch
`mission/turn-detection-phase-one`. Never the shared checkout, never `git checkout -b` in it,
never touch `main`.

## Read these first, in this order

1. `cc-devthrottle workflow instructions mission --version 17` - your conduct.
2. `docs/missions/turn-detection-2026-09-15/handoff.md` in this worktree - the settled
   rulings. Do not reopen them.
3. The brief:
   `D:\ReposFred\devthrottle_internal\docs\missions\BRIEF-2026-09-15-turn-detection-phase-one.md`
4. The plan:
   `D:\ReposFred\devthrottle_internal\docs\design\trustworthy-state-switching\implementation-plan.html`,
   section "Phase 1".
5. The independent review:
   `D:\ReposFred\devthrottle_internal\docs\design\trustworthy-state-switching\review-2026-09-15.md`.
   Read this properly. It is the most useful document in the folder and it tells you where the
   ground is soft.

## The work

GitHub issues #2854, #2855, #2856 and #2857 on `thefrederiksen/devthrottle`, in that order.
One commit per issue. Each issue's "Done when" satisfied before you move on to the next.

**The row rule is already pinned by the scoring harness** at
`D:\ReposFred\devthrottle_internal\docs\design\trustworthy-state-switching\harness\chrome.py`,
function `newr`. Reproduce it faithfully in C#:

- the key of a row is its lower-case letters only, after stripping every non-alphanumeric
  character and then every digit;
- a row needs at least three letters or digits to count at all;
- a row carrying one of the agent's chrome markers never counts;
- a row whose key is already present in the settled screen never counts;
- a row whose key is a near-duplicate of a settled row at eighty percent similarity never
  counts, compared only against settled rows of comparable length - the band is the key length
  plus or minus `max(8, length / 2)`.

**The second candidate** is a threshold on a sequence-aligned estimate of how much text
changed. Build it beside the row rule behind one interface. Do not assume either wins; the
choice is made on live bytes later, not by you.

## How you are judged

`.\scripts\test-local.ps1` must be green before you report. If your change touches the Gateway,
run it with `-Parked` as well.

Prove each behaviour change can fail: revert it, watch the test go red with the reported
symptom, watch the controls stay green, then restore. A test that has never been watched
failing is decoration.

## What you must not do

- Do not open a pull request and do not merge anything. The Architect lands the work.
- Do not bring phase two forward, and do not change the ten-second silence rule.
- Do not turn the new rule on by default. The switch ships off.
- No Unicode, no emoji, no arrows. Plain ASCII everywhere - use `->` and `-`.
- No mention of any assistant, model or vendor in any commit message, code comment, issue
  comment or document.

## Reporting

Write `docs/missions/turn-detection-2026-09-15/build-report.md` saying what you built, what you
proved and how, and what you did NOT prove - named honestly. Then report to the Architect
ONCE, in a single line, when all four are committed and pushed.
