# Review brief - phase 2, round 2 (the fix round)

You are an independent reviewer from a different agent family. You did not write this code. Be adversarial and do
not trust the Worker's report. REVIEW ONLY: change no code, commit nothing.

**Worktree:** `D:\ReposFred\devthrottle-dev-reports-p2-review`, detached at `1d5dccdf4`.
**The change under review:** `git diff 677474822 1d5dccdf4` (the fix round).
**Read:** `docs/missions/dev-reports/REVIEW-phase-2.md` (round 1 findings), `BRIEF-phase-2-fix.md` (the Manager's
rulings on each finding - High 3 is ACCEPTED as a gap by ruling, do not re-raise it unless the fix round made it
worse), and the "Fix round" section of `WORKER-phase-2-gateway.md` (the claims to test).

## Questions

1. For each round 1 finding: is it actually closed, or only closed for the case the new test builds? Name any path
   around each fix.
2. The `sending` state: can an item now be lost (never delivered and never told) or stuck in `sending`? Is "no drain
   in this process owns it" really true under the lock - including the owner's read route and the send route calling
   the settle, the timer, and the turn-end launcher concurrently? Could the settle pass mark an in-flight send "not
   confirmed" in the SAME process?
3. The settle timer: does it read across tenants correctly and only within each tenant's scope? Can one tenant's
   failure or volume starve others? Does it do unbounded work per tick?
4. `DirectorRefused`: are Session Rules and the turn verdict channel truly unchanged? Is any other caller of
   `SessionVerbClient` now behaving differently?
5. The prompt boundary: can owner or report text still escape its block or forge a line? Is the owner's text still
   byte for byte?
6. Did the fix round introduce anything new (logging holes, a fallback, a swallowed exception, a test that passes for
   the wrong reason)?

## Output

Write to `D:\ReposFred\devthrottle-dev-reports\docs\missions\dev-reports\REVIEW-phase-2-round-2.md` (the only file you
may write): per round 1 finding, CLOSED or OPEN with reasons; then new findings with severity, file and line, the
concrete scenario, and how you established it. Then ONE line to the Manager:
`cc-devthrottle message send 2ba644bd "<counts> - round 2 review written"`.
