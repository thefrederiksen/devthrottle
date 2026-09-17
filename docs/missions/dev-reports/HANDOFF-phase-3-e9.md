# Handoff - phase 3, last defect and the pull request (Architect, 2026-09-17)

The Reports view is built and merged into `mission/dev-reports-p3`. The proof run on the merged build
(`mission/dev-reports-p3-proof`, 5d084cc61, worktree `../devthrottle-dev-reports-p3-proof`) passed 33 of 34:
frame isolation on both apps with every guard removal red, and the whole phone flow end to end. Read its README
(`packages/client-core/browser-tests/dev-report-viewer-proof/README.md`), section E9.

## E9 - a note from a second browser is lost while reading as delivered. FIX IT in two places.

1. **The note script** makes every item id globally unique - random, from `crypto.getRandomValues` (it works in the
   sandboxed frame) - never a per-page counter like `n1`. Update CONTRACT.md to match.
2. **The Gateway** refuses an item whose id it already holds with DIFFERENT content, with a clear reason, instead of
   treating it as the already-delivered one. The tray then shows it refused and still queued. Same id with the SAME
   content stays the idempotent resend it is today.

Revert each fix and watch its test go red, then restore. Rerun the proof (E9 must pass, the rest stay green) on a
local Gateway you start yourself; tear it down cleanly as the proof README describes.

## Then

- Merge `mission/dev-reports-p3-proof` into `mission/dev-reports-p3` (evidence and README).
- `.\scripts\test-local.ps1` (a desktop speak-dialog timing test flaked once under load - rerun it alone if it
  fails again, and say so), the client-core, cockpit and mobile web tests, and the dev-report Gateway tests:
  `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"`.
- Open ONE pull request from `mission/dev-reports-p3` to main, not merged, no tool or assistant names in it.
- Write `PHASE-3-REPORT.md`, including exactly what the owner should try on the phone and in the Cockpit.
- Commit, push, and send the Architect (session 184d1571) one line. If `message send` fails, the pushed report is
  enough - the Architect reads the branch.

Speed matters; the owner is waiting to see it. Read terminals, never wait on messages.
