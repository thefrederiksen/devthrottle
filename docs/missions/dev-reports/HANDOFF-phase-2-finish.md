# Handoff - phase 2 finish (Architect, 2026-09-17)

You replace the previous phase 2 Manager, which ran out of context. Everything it built is pushed on
`mission/dev-reports` (28 commits ahead of origin/main at 5a34062da). Read `STATE.md`, `HANDOFF-phase-2.md`,
`BRIEF-phase-2-fix.md`, the "Fix round" section of `WORKER-phase-2-gateway.md`, and `REVIEW-phase-2-round-2.md`.
Do not re-read round 1 history beyond that. Existing worktrees `-p2-gate`, `-p2-gateway`, `-p2-review` are clean;
reuse or remove them.

## Architect rulings on the round 2 review

1. **Cross-process at-most-once (round 2 new finding 2 / round 1 Critical 1) - FIX IT in the database, not in
   memory.** Two Gateway processes share the database during a deploy swap, so the per-process lock is not enough.
   Claim items with a conditional update: move `held` to `sending` stamping a fresh claim id and time, only where the
   state is still `held`; write the final state only where the claim id still matches. The settle pass may rule a
   `sending` item orphaned ONLY when its claim is older than a stated timeout (longer than the longest prompt send),
   and it too uses the conditional update. Keep the in-process lock if it still helps, but it is no longer the
   guarantee. Test: two delivery instances over ONE database (two contexts), interleaved as the review describes -
   exactly one final write wins, the item is never both delivered and not confirmed. Revert and watch it red.
2. **Stale list (new finding 1) - FIX IT.** The owner list route settles the sessions it returns before counting
   (bounded to the sessions in the response). Test it.
3. **High 3 (roster read then prompt with WaitForIdle false) - stays ACCEPTED.** Write it into `PHASE-2-REPORT.md`
   as not proven, in plain words: in the gap between reading idle and typing, a turn can start, and the prompt then
   arrives while the agent works.
4. **Raw report key in the prompt** - escape it the same way as the other report-derived strings. Small.

No further review round from the builder side. The Architect calls the independent inspection after your pull request.

## Then finish

- `.\scripts\test-local.ps1` green; `Gateway.UnitTests` in full; `Gateway.Tests` in FULL on the final commit (the
  earlier run only got half through because of the machine-wide lock - queue for it, in the foreground, suite by
  suite if needed; say how many ran). Run gates in their own worktree, never one a Worker is editing.
- Open ONE pull request from `mission/dev-reports` to main, not merged, body with no tool or assistant names.
- Write `PHASE-2-REPORT.md`: what the owner gets, what is proven and how, what is not.
- Commit and push, then ONE line to the Architect (session 184d1571).

## Watch your seats

Some reviewer families are out of usage or not configured on this machine. If you seat a reviewer from another
agent family, confirm it works by reading `cc-devthrottle session buffer <id>`, not its state, and never wait more
than 20 minutes on a seat without reading its buffer.
