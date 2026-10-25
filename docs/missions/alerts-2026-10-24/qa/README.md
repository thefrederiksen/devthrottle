# Alerts, Phase 4 - QA run (section 7 of the mission)

Run on 25 October 2026, on the branch `alerts/phase4-mount`, by the Phase 4 Developer.

## How it was run

- **A local Gateway, never the hosted one.** `tools/host` starts the real `GatewayHost` in-process on
  `127.0.0.1:7993`, self-host mode, with a throwaway data folder and SQLite - the way the Gateway tests start
  it. It seeds alerts straight into the Gateway's own stores (restart requests, secret transfers, message
  requests, and four sessions on one Director so the requests have real sessions), registers one browser
  device, and serves the Cockpit and the phone app built from this branch. No production database, no hosted
  Gateway, no real account. Every name in a screenshot is a seeded fixture.
- **The browser** is the Director's account-less `agent-browser` profile, driven with browser-harness.
  Phone app at 390 x 844 (device scale 2), Cockpit at 1440 x 900. `tools/qa.py` runs each scenario as ONE
  foreground command: it starts the host, drives the steps, and stops the host.
- **Every step's printed result is in `run-log.txt`** (40 screenshots: 39 from the same final run, and
  `phone-bell-on-settings` from the review follow-up run appended at its end).
- The `?` in front of the tab titles in the log is browser-harness's own tab marker, not the app's title.

## Screenshots, named for the state they prove

The phone app is `phone-*`, the Cockpit is `desktop-*` (and `cockpit-390-*` at phone width).

| State | Phone app | Cockpit |
|---|---|---|
| Bell at zero (no badge; tab title `DevThrottle Cockpit`) | `phone-bell-zero` | `desktop-bell-zero` |
| Bell at 1 (`(1) DevThrottle Cockpit`) | `phone-bell-one` | `desktop-bell-one` |
| Bell at 12, shown "9+" (`(12) DevThrottle Cockpit`) | `phone-bell-twelve` | `desktop-bell-twelve`, `desktop-bell-twelve-collapsed-rail` |
| The session list with NO cards in it (counted: 0) | `phone-session-list-no-cards` | `desktop-session-list-no-cards` |
| Tray: Needs you, then Earlier | `phone-tray-needs-you-and-earlier` | `desktop-tray-needs-you-and-earlier` |
| A restart request opened (Details expanded) | `phone-opened-restart` | `desktop-opened-restart` |
| A secret transfer opened | `phone-opened-secret-transfer` | `desktop-opened-secret-transfer` |
| A message request opened | `phone-opened-message-request` | `desktop-opened-message-request` |
| Accept a restart, step 1: the confirmation | `phone-accept-restart-step-1-confirm` | `desktop-accept-restart-step-1-confirm` |
| Accept, step 2: the Gateway's answer | `phone-accept-restart-step-2-answered` | `desktop-accept-restart-step-2-answered` |
| Accept, step 3: out of the count (3 to 2), into Earlier | `phone-accept-restart-step-3-moved-to-earlier` | `desktop-accept-restart-step-3-moved-to-earlier` |
| Clear one | `phone-cleared-one` | `desktop-cleared-one` |
| Clear refused on an alert that needs an answer | `phone-clear-refused-needs-an-answer` | `desktop-clear-refused-needs-an-answer` |
| Clear all (Earlier gone, "Clear all" no longer offered) | `phone-cleared-all` | `desktop-cleared-all` |
| Gateway unreachable: the failure line, the last-known list kept | `phone-gateway-unreachable` | `desktop-gateway-unreachable` |
| Overflow fixture, list | `phone-360-overflow-list` (360 px) | `desktop-280-popover-overflow-list` (280 px popover) |
| Overflow fixture, opened with Details | `phone-360-overflow-restart-details`, `...-scrolled`, `phone-360-overflow-secret-details` | `desktop-280-popover-overflow-restart-details` |
| The Cockpit at phone width | - | `cockpit-390-bell`, `cockpit-390-tray` |
| The bell on a phone screen other than the roster (Settings, count 3) | `phone-bell-on-settings` | - |

**Nothing overflows.** For every overflow shot the run measured every element inside the tray: none passes
the window's edge and none has content wider than its box (`"bad":[]` in `run-log.txt`), with the tray's own
width printed beside it (344 px on the 360 px phone, 280 px in the Cockpit popover) so an empty result is never
an empty tray. The fixture is the mission's: a 46-character launcher version
(`2.31.0-preview.20261024.214130+sha.3f9c0a1b7e2`), a 300-character reason with no break point, a long
machine name, a long session name, and a long secret entry name.

## What the run found, and what was fixed in this pull request

1. **The Cockpit tray ran off the right edge in a narrow window.** At 390 px the bell sits at x = 141 and the
   374 px tray was drawn from there to x = 515. Fixed in `RailAlertsBell`: once drawn, the tray is measured
   and moved left just enough to stay 8 px inside the window (`cockpit-390-tray`: 8 to 382). Test:
   `OpeningTheTray_InANarrowWindow_MovesItLeftToStayInside`.
2. **A refused restart accept left the alert looking open.** On accept the Gateway checks the machine again
   and, when it cannot be restarted, closes the request with its own sentence; the opened alert kept saying
   WAITING and offering "Yes, restart it" until the next 5-second read. Fixed in `RestartRequestActions`:
   a refusal reads the list again at once and leaves the confirmation (`*-accept-restart-step-2-answered`
   now shows STOPPED). Test: `a refused accept reads the list again at once and leaves the confirmation`.

## Notes for the reader

- **The accept ends in "Stopped", not "Accepted - waiting for the Director".** A local Gateway has no
  Director or launcher attached, so the Gateway's own re-check on accept refuses and closes the request; that
  is the Gateway's true answer here, and the shot shows it leaving Needs you at once. The "Accepted" row the
  mission describes is shown by the seeded running restart in Earlier ("Restarting the Director on
  linux-rig", status ACCEPTED). An accept that a real Director takes is for the hosted run after deploy.
- **How "clear refused" was produced.** The tray only offers an X on Earlier rows, so the refusal can only
  happen when an alert comes back to needing an answer between two reads. The run makes that happen on
  purpose: it holds the tray's list read in the browser for that one step (the clear itself goes through),
  puts the allowed message request back to waiting on the Gateway, and presses its X. The Gateway answers 409
  and the tray shows its sentence verbatim.
- **The refusal sentence stays under that row** while it is in Needs you (`*-gateway-unreachable`), where it is
  still true. After the review it goes once a read lists that alert as answered or it leaves the list (review
  observation 1, with tests in `Alerts.test.tsx`).
- **Observation 5 of the Phase 3 review**: with the tray open, a click on the bell lands on the backdrop and
  closes the tray. That is expected and was not counted as a defect.

## The roster guard (goal 1), both ways

`packages/client-core/src/alerts/rosterGuard.test.ts` fails if a request panel or card is mounted in, or one
of the three old modules imported by, `apps/mobile/src/pages/Home.tsx` or
`apps/cockpit/src/sessions/SessionRoster.tsx`. Its own instrument test proves it sees every shape it exists to
catch, and that it leaves the per-session badges alone.

- `guard-before.txt` - run against the two files as they are on origin/main `6b0decd48`: **FAILS**, listing
  10 hits (three panels and three imports in Home, two panels and two imports in SessionRoster).
- `guard-after.txt` - run on this branch: **PASSES**, 3 of 3.

## Section 7 commands

All run in this worktree on the final code, and run again after the review follow-up (the numbers below are
that second run).

| Command | Result |
|---|---|
| `npm ci` | completed |
| `npm run typecheck` | no errors |
| `npm test --workspace @devthrottle/client-core` | 187 files, 2,374 tests passed |
| `npm test --workspace @devthrottle/mobile` | 39 files, 198 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 132 files, 1,429 tests passed |
| `npm run lint` | 5 errors, exactly the five known ones already on main (`FactoryRecordsTab.tsx`, `fixtures.ts`, `sw.test.ts`, `Recorder.tsx`, `VoiceMode.tsx`); no other |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert"` | 94 passed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Alert"` | 13 passed |
| `dotnet test src/CcDirector.Gateway.DatabaseTests --filter "FullyQualifiedName~Alert"` | plain `dotnet test` reports it SKIPPED (by design, it needs a database); run through `scripts\test-database.ps1 -Filter "FullyQualifiedName~Alert"` against a throwaway PostgreSQL: 1 executed, 1 passed, 0 skipped |

Also run, because this branch changes the two push payloads' `url`: the push notifier test classes
(`dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert|FullyQualifiedName~PushNotifier"`):
115 passed.
