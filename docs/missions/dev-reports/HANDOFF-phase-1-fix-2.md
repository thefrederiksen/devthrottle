# Handoff - phase 1 fix, round 2: INSPECTION-phase-1-round-2.md

Read `STATE.md` (ruling 9: the shape check is guidance, not the security boundary) and the round 2 inspection.
Fix on `mission/dev-reports` (pull request #2948). Small, tight changes only - no new features, no phase 2.

**Architect ruling for this round - stop chasing CSS.** The shape check checks STRUCTURE the browser builds.
It does not judge CSS visibility. Remove the inline `display:none` rule and its contract promise; keep the
`hidden` attribute rule. Write the limit plainly in the class comment and the contract: styles (inline or
stylesheet) can hide a section and the check does not try to detect it. This closes finding 2 by removing
a promise, not by writing a CSS parser.

1. **Finding 1** - an element that is not rendered (`template`, and anything inside `noscript`/`head` if the
   parser puts it there) never counts as a section marker. Test header and detail variants.
2. **Finding 3** - non-rendered SVG text (`desc`, `title`, `metadata`) does not count as words. Test it.
3. **Finding 4** - the radio-name check is linear (count names once in a dictionary). Add a test that a
   report at the 10 MB publish limit (ruling 6) is checked in a bounded time - measure in Release on this
   machine, set the bound with generous headroom, and say the measured number in the test comment.
4. **Finding 5** - correct `PHASE-1-REPORT.md` to state exactly which fixes have a regression test and which
   (private path, attribution) were verified by inspection only.

Revert each code fix and watch its test fail, then restore. Focused suites and `.\scripts\test-local.ps1`
green. Commit and push, then ONE line to the Architect (session 184d1571).
