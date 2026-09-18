# Worker brief - phase 3b, the apps and the tool: land inside the report, one frame, one way back

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-apps`, cut from `mission/dev-reports-p3b`.
Your worktree is yours alone. Commit and push as you go. NEVER merge anything to main, and never touch
`mission/dev-reports-p3b` itself - your Manager merges your branch in.

Read first:
- `docs/missions/dev-reports/HANDOFF-phase-3b.md` - what the owner saw.
- `docs/VisualStyle.md` - every UI change must comply.
- `CLAUDE.md` rule 7 (the client is dumb - it renders the Gateway's words verbatim) and rule 8
  (one page on two surfaces - the Cockpit and the phone must not drift).
- `packages/client-core/src/devreports/DevReportViewer.tsx`, `DevReportConversation.tsx`, `devReports.css`.

## THE WHY

The owner opened a report in the Cockpit and counted three scrollbars, two conversations and two Send
buttons. He also had no way back to the session the report came from, and nothing on screen named that
session in words a human would use. He is not happy.

## YOUR FILES - and only these

- `apps/cockpit/` and `apps/mobile/`
- `packages/client-core/src/devreports/DevReportViewer.tsx`, `DevReportConversation.tsx`,
  `DevReportList.tsx`, `devReportsClient.ts`, `devReports.css`, `DevReportViews.test.tsx`
- `tools/cc-dev-reports/`

Do NOT edit `dev-report-notes.js`, `devReportNotes.test.ts`, `CONTRACT.md` (the page Worker holds those) or
anything under `src/` (the Gateway Worker holds that).

## WHAT THE OTHER TWO WORKERS ARE GIVING YOU - build to this, it is settled

- The PAGE, when hosted, will no longer draw any queued list, sent list, replies list or Send button. The
  app's panel is the ONE conversation and the ONE Send. You do not need to hide anything inside the frame -
  but your layout must now assume the frame contains only the report and a small note box.
- The GATEWAY will put two finished strings on the report record, and you render them VERBATIM - no
  composing, no fallback text of your own:
  - `sessionLabel`, e.g. `121 devthrottle - tool not working on linux`
  - `backLabel`, e.g. `back to 121 devthrottle - tool not working on linux`
  A record without them (an older Gateway) shows no back link rather than an invented one.
- The GATEWAY will serve `GET /r/{reportId}` and 302 a desktop browser to
  `/session/{sessionId}?tab=reports&report={reportId}` and a phone to
  `/mobile/session/{sessionId}/reports/{reportId}`. The phone route already exists. The Cockpit route is
  yours to make land.

## THE WORK

### B (the app's half). One report, one scrollbar, one frame

The owner counted three scrollbars. The report frame scrolls itself; the app must not wrap it in a second
scrolling box, and the page behind it must not scroll as well. Fix the Cockpit's Reports tab and the phone's
report screen so that the report is framed with exactly ONE scroll region, at both widths. This is CSS and
layout in `devReports.css` and the two shells' stylesheets - keep the shared card shape shared (rule 8) and
put only true layout differences in each shell.

The conversation panel is the app's: the Cockpit's right-hand rail and the phone's bottom sheet, as today.
Check it still reads as THE one conversation now that the page has none - headings, empty states and the
Send button should make sense as the only ones on screen.

### C (the app's half). Landing inside the report

- **Cockpit:** a deep link must land straight in the open report. `/session/:sessionId` currently keeps its
  tab and its open report in component state, so a link cannot reach them. Make the Reports tab and the open
  report readable from the address: `?tab=reports&report=<id>` selects the Reports tab and opens that report
  on arrival, and opening or closing a report keeps the address honest. Do not break the other tabs.
- **Phone:** `/session/:sessionId/reports/:reportId` already exists and already opens the report. Check it
  really lands in the report from a cold navigation (no list first), and from a signed-out start after the
  sign-in round trip.
- **The tool:** `cc-dev-reports open` must print ONE address the owner can click:
  `<gateway>/r/<report id>`, built from the Gateway base URL the tool already resolves
  (`tools/cc_shared/gateway.py`). Replace today's `ownerRoute` of `/dev-reports/<id>` and the line that says
  the owner "reads it in the Reports view (arrives in phase 3)" - it has arrived. Keep the tool's AXI
  standard: `--json` keeps its shape and carries the same address, no identifier is cut short, and the
  human output stays plain ASCII. Update `tools/cc-dev-reports/README.md` and its tests.

### D (the app's half). The way back, named for a human

Every report view - the Cockpit's and the phone's - shows a link back to its session whose text is
`backLabel`, rendered verbatim, and going back navigates to that session in that app. The report's title
area shows `sessionLabel`. NO internal identifier anywhere the owner can see: no session id, no report id,
in any visible text, tooltip or label. Search your own diff for it before you call this done.

## PROOF - this is the point of the phase, the owner asked for it

Tests (vitest + testing-library, following the existing `DevReportViews.test.tsx` and the shells' own tests):

1. The back link's text is exactly the Gateway's `backLabel`, and the title area shows `sessionLabel` -
   both verbatim, for a record whose strings are unusual (so a test cannot pass by re-composing them).
2. A record with no `backLabel` shows no back link - and shows no session identifier either.
3. No visible text in a rendered report view contains the session id or the report id.
4. Cockpit: mounting at `/session/<sid>?tab=reports&report=<rid>` shows the Reports tab with that report
   open, not the list. Closing the report clears it from the address.
5. Phone: mounting at `/session/<sid>/reports/<rid>` shows that report, not the list.
6. The tool prints `<gateway>/r/<id>` in human output and in `--json`, and the address is not shortened.

**REVERT EACH ONE AND WATCH IT GO RED** with the reported symptom, then restore and watch it green. Record,
per test: the exact line reverted and the exact failure message. A test nobody has watched fail is decoration.

Run: the client-core, cockpit and mobile test commands (check each `package.json`), `npm run typecheck`, and
the tool's own tests (`tools/cc-dev-reports/tests`).

## DONE MEANS

Everything committed and pushed on `mission/dev-reports-p3b-apps`, and a file
`docs/missions/dev-reports/WORKER-phase-3b-apps.md` in your worktree holding: what you changed, the test
list, the revert evidence per test, and what you did NOT prove. Then tell your Manager in ONE line - fleet
messages truncate at the first newline.

Do not guess. If something is genuinely undecidable, ask your Manager - do not invent a product decision.
