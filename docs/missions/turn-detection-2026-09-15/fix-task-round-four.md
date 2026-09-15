# Fix task - the four sentence-level findings from round four

You are a fresh Manager. Round four of the inspection returned **AGREE** - the verdict stands and
nothing here reopens it. Four findings remain, all sentence-level. **This is the last piece of work
before phase one merges**, so it is small and it must be exact.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-detection`) on branch
`mission/turn-detection-phase-one`. Commit and push as you go.

## Read first

1. `docs/reviews/turn-detection-phase-one-inspection-round-four.md` - the section "Findings". That is
   your mandate, and the inspection states each finding's file and line.
2. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings. Do not reopen them.

## Why four comment fixes are worth a seat at all

This mission's standard, from its own brief: *an unproven claim in a comment is worse than no
comment, because the next reader spends their scepticism somewhere else.* Three of these four are
false sentences sitting next to code that contradicts them. They are exactly the defect the standard
names, and they are cheap.

## The four

1. **A false "exactly".** `ContentTurnRuleTests.cs:979-987` says the holder "opens exactly as the
   log's append does". It does not - mode and access match, the share mode does not. The inspection
   confirmed the test's discriminating power is unaffected, so **fix the sentence, not the test.**

2. **A justification false on four branches.** `TerminalStateDetector.cs:405-406` justifies the
   disposition stamp by saying it sits "beside work that already arms a timer on the same path". On
   the suppressed, brand-new, already-active-with-no-body-change, and settled-nothing-armed branches
   that is untrue - the stamp is the only added work and nothing is armed. The COST CLAIM survives
   and should stay; the stated reason for it does not. Replace the reason with one that is true on
   every branch.

3. **A helper that does not follow the discipline its own comment points at.**
   `TurnDetectionShadowRetentionTests.cs:349-353`: `ReadRows` cross-references
   `ContentTurnRuleTests.ShadowRows` for not denying the writer, but reads with
   `Split('\n', RemoveEmptyEntries)`, which COUNTS a non-newline-terminated last line - the very
   half-written row the other helper refuses to count. No committed test is exposed today, which is
   why this is not a defect; a future poll through it against a live writer would inherit a hazard
   the comment claims it does not have. **Make the helper actually do what its comment says**, and
   if a test would now be exposed, say so.

4. **A count slip in the report.** `build-report.md:787` says "the other nineteen in
   `TurnDetectionShadowRetentionTests` stayed green"; the class runs 19 in total, 2 of them new, so
   the others are 17. Arithmetic, not a proof claim - correct it.

## What you must NOT do

- Do not change behaviour to make a sentence true. Findings 1 and 2 are sentences that must match
  code that is already correct. Only finding 3 touches code, and only a test helper.
- Do not widen scope. Anything beyond these four is not covered.
- Do not open a pull request and do not merge. The Architect lands it.

## How you are judged

`.\scripts\test-local.ps1` green, plus the two focused classes
(`ContentTurnRuleTests`, `TurnDetectionShadowRetentionTests`) run explicitly since they live in a
parked suite. Redirect every run to a file and read the exit code from `dotnet` - do NOT pipe
through `tail` or `head`, which buffers the log away and replaces the exit code with the pipe's.

For finding 3, the helper change needs a test that would have caught the hazard - a half-written
last line must not be counted - watched failing before the fix.

Do NOT run `-Parked` in full: the Gateway suite's lock is unwinnable on this machine today
(issue #2862).

Plain ASCII everywhere. No mention of any assistant, model or vendor anywhere.

## Reporting

Append a short section to `docs/missions/turn-detection-2026-09-15/build-report.md` - per finding,
what you changed and how you know. Report to the Architect
`b564b0b3-148d-402d-a5ba-ac45414fb437` ONCE, in a single line.
