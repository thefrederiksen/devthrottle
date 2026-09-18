# Worker report - phase 3b, the apps and the tool

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-apps`, cut from `mission/dev-reports-p3b`.
Nothing here was merged anywhere; the Manager merges the branch in.

## What I changed

### B - one report, one scrollbar, one frame

`packages/client-core/src/devreports/devReports.css`

Every box the app wraps round a report now CLIPS instead of scrolling: `.dev-report-viewer`,
`.dev-report-viewer-main` and `.dev-report-frame-box` each carry `overflow: hidden` beside the
`min-height: 0` they already had. The report page inside the frame scrolls itself, so that is the only
scroll region the report has. The conversation is not the report - it is a list of its own and keeps its
own scroll in its own panel, on both surfaces.

`apps/cockpit/src/styles.css` - the open-report frame is clipped the same way (`.reports-tab-body`), and
the rail gained a heading, "Conversation", so the one conversation left on the screen says what it is now
that the hosted page draws none. The Send under it is the only Send anywhere on the screen.

`apps/mobile/src/styles.css` - the phone's `.terminal-screen > .dev-report-viewer` is clipped too.
`.terminal-screen` was already fixed and clipped, so the screen behind the report cannot scroll either.
The back link gets a 34px touch target on the phone.

Rule 8 held: the card shapes stay in `client-core`; each shell holds only its own frame.

### C - landing inside the report

**Cockpit.** `apps/cockpit/src/sessions/SessionDetail.tsx` reads the main tab and the open report from the
ADDRESS instead of component state. `?tab=reports&report=<id>` selects the Reports tab and opens that report
on arrival. Switching tab replaces the address (and drops `report`, which only means something on the
Reports tab); opening and closing a report pushes, so the browser's own Back closes a report. Terminal is
the default and is left out of the address, so an ordinary session link is still `/session/{sid}`.
`ReportsTab.tsx` no longer remembers which report is open - it is handed it.

**Phone.** `/session/:sessionId/reports/:reportId` already existed. The two report routes moved into
`apps/mobile/src/pages/reportRoutes.tsx` as one exported array that the app's route table spreads in, so a
test can mount the array the app mounts rather than one it wrote for itself.

The signed-out start was BROKEN and is fixed: the phone's auth gate sent an unenrolled phone to a bare
`/signin`, throwing the requested address away, so an owner who opened a printed report address signed in
and arrived at the roster. It now carries the route in `next=` exactly as the Cockpit's gate does - the
shared `SignIn` and `DeviceCallback` already carry it from there to the landing (issue #1088), so nothing
else had to change. The gate moved to `apps/mobile/src/components/RequireDeviceKey.tsx` because in
`main.tsx` it could not be mounted in a test: importing that module starts the whole app.

**The tool.** `cc-dev-reports` prints ONE address the owner can click - `<gateway>/r/<report id>`, built
from the Gateway base URL the tool already resolves, with the report id whole. It replaces the path
`/dev-reports/<id>` and the line saying the owner "reads it in the Reports view (arrives in phase 3)". The
`--json` shape is unchanged: `ownerRoute` carries the same address. `README.md` updated.

### D - the way back, named for a human

`DevReportSummary` gained two optional strings the Gateway composes, `sessionLabel` and `backLabel`. The
shared viewer renders both VERBATIM: the back link's text is `backLabel` whole, and the title area carries
`sessionLabel`. A record without them - an older Gateway - shows no back link and no session line at all,
because the alternative is inventing one. Going back is the shell's: the Cockpit navigates to
`/session/{sid}` and the phone to that session's default view. The viewer composes nothing and no
identifier appears in any visible text, tooltip or label.

## The tests, and the revert evidence

Every test below was reverted by changing the exact production line named, watched fail with the message
quoted, then restored. The restores are `git checkout --` of the file; the suites were re-run green
afterwards (client-core 1279, cockpit 359, mobile 82, tool 28).

**1. The back link's words are the Gateway's** - `DevReportViews.test.tsx`, "shows the Gateway's backLabel
and sessionLabel verbatim, and goes back to that session".
Reverted `DevReportViewer.tsx`: the link body `{backLabel}` became `back to {sessionLabel}`, so the viewer
composes its own sentence.
Red, 2 tests: `AssertionError: expected 'back to 121 devthrottle ~ tool not wo...' to be '<< return to
session 121 (Gateway wor...' // Object.is equality`.

**2. No words from the Gateway means no link** - same file, "shows NO back link and no session identifier
when the Gateway sent no words for one".
Reverted `DevReportViewer.tsx`: the guard `{backLabel && onBackToSession && (` became
`{onBackToSession && (`, with a session-id sentence as the fallback text.
Red: `AssertionError: expected <button type="button" ...></button> to be null`.

**3. No identifier anywhere the owner can see** - same file, "puts no session id and no report id in
anything the owner can see".
Reverted `DevReportViewer.tsx`: added `title={snapshot.detail.report.id}` to the title span - a tooltip, not
even visible text.
Red: `AssertionError: expected '<< return to session 121 (Gateway wor...' not to contain
'9c2d40fe-1188-4a63-b0e1-viewer-report'`.

**4. The Cockpit lands in the report from the address** - `reportsDeepLink.test.tsx`, all four tests.
Reverted `SessionDetail.tsx`: reading the tab from the address went back to `useState<MainTab>("terminal")`.
Red, 4 tests: `AssertionError: expected 'false' to be 'true'` on the Reports tab's `aria-selected`, and
`TestingLibraryElementError: Unable to find an element by: [data-testid="reports-back"]`.

**4b. Closing a report clears it from the address** - same file, "takes the report back out of the address
when it is closed".
Reverted `SessionDetail.tsx`: dropped the line that deletes `report` from the search parameters in
`closeReport`.
Red: `expected '/session/7d2f9c10-...?tab=reports&report=b41e77a2-...' to be
'/session/7d2f9c10-...?tab=reports'`.

**5. The phone lands in the report from cold** - `reportDeepLink.test.tsx`, "opens that report from a cold
navigation, with no list on the way".
Reverted `reportRoutes.tsx`: removed the `/session/:sessionId/reports/:reportId` entry from the array the
app mounts.
Red: `TestingLibraryElementError: Unable to find an element by: [data-testid="fake-report-viewer"]`.

**5b. Signed out, the address survives sign in** - `requireDeviceKey.test.tsx`, "carries the requested
report address into Sign in".
Reverted `RequireDeviceKey.tsx`: back to navigating to a bare `/signin`.
Red: `AssertionError: expected '/signin' to be
'/signin?next=%2Fsession%2F7d2f9c10-...%2Freports%2Fb41e77a2-...'`.

**6. The tool prints the whole Gateway address** - `test_cli.py`, four tests.
Reverted `reports_ops.py`: `owner_route` back to the bare path `/dev-reports/<report id>`.
Red, `5 failed, 23 passed`, including
`AssertionError: assert '/dev-reports/7d3c...' == 'http://gateway.invalid/r/7d3c...'` and the same failure
on the `--json` `ownerRoute`.

**7. One scroll region round the report** - `DevReportViews.test.tsx`, "clips every box around the report,
so the frame is the only thing that scrolls".
Reverted `devReports.css`: `.dev-report-frame-box` `overflow: hidden` became `overflow-y: auto`.
Red: `.dev-report-frame-box must clip, not scroll: expected '.dev-report-frame-box {...' to contain
'overflow: hidden'`.

Proofs 1, 2 and 3 use strings that are deliberately odd and are NOT one composed from the other
(`<< return to session 121 (Gateway words, odd #4)` beside
`121 devthrottle ~ tool not working on linux #odd`), so a viewer that built its own back sentence out of the
session label fails the first assertion rather than passing by accident. Proofs 4 and 5 drive the REAL
`SessionDetail` and the REAL exported route array, so putting either piece of state back where it was goes
red.

## What ran

- `python -m pytest tools/cc-dev-reports` - 28 passed
- `npm --prefix packages/client-core run test` - 110 files, 1279 passed
- `npm --prefix apps/cockpit run test` - 42 files, 359 passed
- `npm --prefix apps/mobile run test` - 15 files, 82 passed
- `npm run typecheck` (all four workspaces) - clean

## What I did NOT prove

- **The scrollbar count on a real screen.** Proof 7 checks that the rule is DECLARED in the stylesheet -
  jsdom has no layout, so no test here can say what a browser paints. The handoff asks for browser
  screenshots at 390x844 and 1400x900; I did not take them and they are still owed.
- **Anything that needs the Gateway's half.** `sessionLabel`, `backLabel` and `GET /r/{id}` are the Gateway
  Worker's. I built to the settled shape and my tests supply those strings themselves. Nobody has yet seen
  a real record carry them, or followed a real `/r/{id}` to either app.
- **The end-to-end loop** - publish, open the printed address, note a cell, Send, the agent replies - was
  not run. It needs a local Gateway with a real session and the other two Workers' changes.
- **The hosted page's half.** My layout assumes the page draws no queued list, sent list, replies list and
  no Send. That is the page Worker's change and I did not see it.
- **The 401 re-gate.** A device key revoked MID-session still goes to a bare `/mobile/signin` through
  `mobileSignInRedirect`; only the cold signed-out start is fixed. That path is shared client-core and
  outside my files.
- One client-core run printed `2 errors` (a five-second test timeout) while all 1279 tests passed; it did
  not reproduce on two further runs and is in no file this branch touches.
- `npm run lint` reports 3 pre-existing errors (missing `@typescript-eslint/no-implied-eval` and
  `react-hooks/exhaustive-deps` rule definitions in the eslint config) in three files this branch does not
  touch.
