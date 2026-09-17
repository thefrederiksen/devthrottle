# Handoff - phase 3 finish (Architect, 2026-09-17)

You replace the first phase 3 Manager, which stopped receiving messages. Read `HANDOFF-phase-3.md` (the design and
the proof required) and the two Worker briefs beside it. Everything is committed and pushed:

- `mission/dev-reports-p3-viewer` (worktree `../devthrottle-dev-reports-p3-viewer`, head 4be0f5aee) - the viewer
  Worker FINISHED: the Reports tab in the Cockpit, the Reports screen, full-screen report and conversation sheet on
  the phone. Test ids: session-tab-reports, dev-report-row, dev-report-frame, dev-report-conversation,
  dev-report-send (data-sending), report-conversation-open, report-conversation-sheet.
- `mission/dev-reports-p3-proof` (worktree `../devthrottle-dev-reports-p3-proof`, head 92b237618) - the proof Worker
  finished its FIRST half (the harness, dry run); the second half (frame isolation on the real apps, end to end on a
  local Gateway at phone and desktop width) waits on the viewer.
- Phase 2 is now MERGED to main (ef411aa06) and being deployed.

## Do now, in order

1. Merge `origin/main` and `mission/dev-reports-p3-viewer` into `mission/dev-reports-p3`. Push.
2. Run the proof's second half against it (a fresh Worker, or yourself). Frame isolation proven on the real apps; the
   end-to-end run on a local Gateway with screenshots.
3. `.\scripts\test-local.ps1` plus the client-core, cockpit and mobile web tests.
4. Open the pull request to main, not merged. Write `PHASE-3-REPORT.md` including exactly what the owner should try
   on the phone and in the Cockpit.
5. ONE line to the Architect (session 184d1571). Required.

The Architect calls the independent inspection; do not seat a reviewer yourself.

**Speed matters: the owner is waiting to see this.** Do not wait for any seat's message - read its terminal with
`cc-devthrottle session buffer <id>` at least every 15 minutes. Close every Worker you start when it finishes.
