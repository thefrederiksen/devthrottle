# Alerts, Phase 5 - QA run of the redesigned Alerts screen

Run on 25 October 2026, on the branch `alerts/phase5-phone-screen`, by the Phase 5 Developer. It replaces the Phase 4
run's screenshots: the bottom sheet, the opened-alert screen and the old tray they showed no longer exist.

## What changed, in one paragraph

On the phone, the bell opens **Alerts as a full, opaque screen of its own** (`/mobile/alerts`): an app bar with a back
arrow and the title, no shade, no sheet, no drag handle. **Needs you** is a list of cards, each with the plain question as
its heading, one sentence of who asked and why (the reason appears once), "Expires" when there is an expiry, the kind's
buttons on the card, and the machine diagnostics behind a collapsed "Details". One tap answers; the restart still asks
twice, on the card. An answered card leaves at once and a line says what happened, in the Gateway's status word.
**Earlier** is collapsed below, as "Earlier (n)" with "Clear all" in its own header, and opens to compact rows (title,
status, time, X) that open in place. The Cockpit keeps its popover under the bell, with the same content. The titles and
summaries are rewritten on the Gateway as owner sentences: "Release Manager (121) wants to message Release Manager (125)".

## How it was run

- **A local Gateway, never the hosted one.** `tools/host` starts the real `GatewayHost` in-process on `127.0.0.1:7993`,
  self-host mode, with a throwaway data folder and SQLite. It seeds alerts straight into the Gateway's own stores
  (restart requests, secret transfers, message requests, and four sessions on one Director), and serves the Cockpit and
  the phone app built from this branch. Every name in a screenshot is a seeded fixture.
- **Two devices, as in real use.** The Cockpit signs in with a browser device and the phone app with a phone device.
  Phase 4 used one browser device for both; the Gateway decides where an answer was given from the device's type and
  refuses a secret transfer answered "on the phone" from a browser's key, so answering from the phone's cards needs the
  phone's own device.
- **The browser** is the Director's account-less "Agent browser" profile, driven with browser-harness. Phone app at
  390 x 844 (device scale 2) and 360 x 780 for the overflow fixture; Cockpit at 1440 x 900. `tools/qa.py` runs each
  scenario as ONE foreground command: it starts the host, drives the steps, and stops the host.
- **Every step's printed result is in `run-log.txt`.** It is one pass over every scenario, on one build, after the
  review's fixes. Every screenshot was taken. One line in it is not a pass of the app's: after the "twelve" scenario's
  screenshots, the QA host's own shutdown did not finish inside the 30 seconds `qa.py` gives it (it waits for the
  browser's open connections), so that scenario ends in a traceback; no QA host process was left running afterwards.
  Earlier attempts at this pass stopped part-way for reasons outside the app - the browser's screenshot call hanging, and
  the "Agent browser" being stopped while the run was using it - and were thrown away whole rather than patched.
- **What needs an answer is measured on what the screen shows.** The phone's Alerts screen has no bell (it IS Alerts),
  so on the phone each answer prints the number of cards before and after (`cards 3 after: cards 2`), and the step back
  to the session list prints the bell's badge there. The Cockpit prints its bell's badge. (Phase 5 review, finding 3.)
- The `?` in front of the Cockpit's tab title in the log is browser-harness's own tab marker, not the app's title.

## Screenshots, named for the state they prove

The phone app is `phone-*`, the Cockpit is `desktop-*` (and `cockpit-390-*` at phone width). 51 in all.

| State | Phone app (390 x 844) | Cockpit (1440 x 900) |
|---|---|---|
| Bell at zero; Alerts opened: "Nothing needs you." | `phone-bell-zero`, `phone-alerts-zero` | `desktop-bell-zero`, `desktop-alerts-zero` |
| Bell at 1; Alerts opened: one card | `phone-bell-one`, `phone-alerts-one` | `desktop-bell-one`, `desktop-alerts-one` |
| Bell at 12 ("9+"); twelve cards | `phone-bell-twelve`, `phone-alerts-twelve` | `desktop-bell-twelve`, `desktop-alerts-twelve` |
| The session list with no cards in it (counted: 0) | `phone-session-list-no-cards` | `desktop-session-list-no-cards` |
| Three need you, Earlier collapsed ("Earlier (4)") | `phone-alerts-three-need-you-earlier-collapsed`, `...-scrolled` | `desktop-alerts-three-need-you-earlier-collapsed` |
| Earlier expanded: compact rows, Clear all in its header | `phone-alerts-earlier-expanded` | `desktop-alerts-earlier-expanded` |
| An Earlier row opened in place (the running restart's progress line) | `phone-alerts-earlier-row-opened` | `desktop-alerts-earlier-row-opened` |
| A card's Details opened (the restart's diagnostics) | `phone-card-restart-details` | `desktop-card-restart-details` |
| Secret transfer approved from its card, and its delivery failed: the card leaves, the line says so in red | `phone-answer-secret-transfer-approved-then-failed` | `desktop-answer-secret-transfer-approved-then-failed` |
| Message request approved from its card (the line's edge in green: it happened) | `phone-answer-message-request-allowed` | `desktop-answer-message-request-allowed` |
| Restart accepted from its card, step 1: the confirmation on the card | `phone-answer-restart-step-1-confirm` | `desktop-answer-restart-step-1-confirm` |
| Restart, step 2: the Gateway's answer, out of Needs you | `phone-answer-restart-step-2-answered` | `desktop-answer-restart-step-2-answered` |
| Nothing needs you, Earlier still beneath it | `phone-alerts-nothing-needs-you-earlier-below` | `desktop-alerts-nothing-needs-you-earlier-below` |
| Clear one | `phone-cleared-one` | `desktop-cleared-one` |
| Clear refused on an alert that needs an answer (the sentence indented under its own row) | `phone-clear-refused-needs-an-answer` | `desktop-clear-refused-needs-an-answer` |
| Clear all (Earlier gone; the alert that needs an answer stays as a card) | `phone-cleared-all` | `desktop-cleared-all` |
| Back returns to the session list, with focus on the bell | `phone-back-to-session-list` | - |
| Gateway unreachable: the failure line, the last-known list kept | `phone-gateway-unreachable` | `desktop-gateway-unreachable` |
| Overflow fixture, cards | `phone-360-overflow-cards` (360 px) | `desktop-280-popover-overflow-cards` (280 px popover) |
| Overflow fixture, Details opened | `phone-360-overflow-restart-details`, `...-scrolled`, `phone-360-overflow-secret-details` | `desktop-280-popover-overflow-restart-details` |
| The Cockpit at phone width | - | `cockpit-390-bell`, `cockpit-390-tray` |
| The bell on a phone screen other than the roster (Settings, count 3) | `phone-bell-on-settings` | - |

**The phone's Alerts is a screen, not a sheet.** The run printed, on opening it: `address: /mobile/alerts dialog: False
shade: False` - the address changed, and there is no dialog and no backdrop on the page.

**Nothing overflows.** For every Alerts screen and popover the run measured every element inside it: none passes the
window's edge and none has content wider than its box (`"bad":[]`, 13 times in `run-log.txt`, none with an entry), with
the content's own width printed beside it (328 px on the 360 px phone, 280 px in the Cockpit popover) so an empty result
is never an empty screen. The fixture is the mission's: a 46-character launcher version
(`2.31.0-preview.20261024.214130+sha.3f9c0a1b7e2`), a 300-character reason with no break point, a long machine name, a
long session name and a long secret entry name - now shown IN FULL on the card, not ellipsised.

## Notes for the reader

- **Outcomes carry the Gateway's tone.** Each source stamps a `tone` beside the status word - good (it happened:
  Delivered, Restarted, Allowed), bad (it did not, and the owner should know: Failed, Stopped, a restart nobody can
  account for, a request whose session ended) or neutral (waiting, under way, or ended by a choice). The answered line
  and the Earlier rows only colour what they are told: red for bad, green for good. (Phase 5 review, observation 3.)
- **The approved secret transfer reads "Failed".** The approval itself went through and was recorded as given on the
  phone; the Gateway then could not deliver it, and said so. Read back from the Gateway after the same click:
  `"status":"Failed", "where":"phone", "outcome":"QA-DESK is not connected with a Director that can send it now. Nothing
  was moved."` This local Gateway has no Director to send from. The line under Needs you shows the Gateway's word as it
  stands at the next read, which here is already Failed.
- **The accepted restart reads "Stopped".** As in Phase 4: with no Director or launcher attached, the Gateway's own
  re-check on accept refuses and closes the request. It leaves Needs you at once, and the line says "Stopped". The
  "Accepted" state is shown by the seeded running restart in Earlier ("Restarting the Director on linux-rig").
- **How "clear refused" was produced** is unchanged from Phase 4: the list read is held in the browser for that one step,
  the allowed message request is put back to waiting on the Gateway, and its X is pressed on the row still shown in
  Earlier. The Gateway answers 409 and the row shows its sentence verbatim. When the read is released, that request comes
  back as a card in Needs you (`*-cleared-all`).
- **Names.** "Release Manager (121)" is the session "Release - Manager", number 121; "Release Manager (125)" is "Release -
  Release Manager". The restart request now stores its requester with the session's number ("Rig - Developer (142)"),
  so it reads "Rig Developer (142)" like the other two kinds; a session with no number keeps its short id there.

## The roster guard (goal 1)

Unchanged by this phase and still passing (`packages/client-core/src/alerts/rosterGuard.test.ts`, 3 of 3).
`guard-before.txt` and `guard-after.txt` are Phase 4's record of it failing against the old shells and passing after.

## Section 7 commands

All run in this worktree on the final code.

| Command | Result |
|---|---|
| `npm ci` | completed |
| `npm run typecheck` | no errors |
| `npm test --workspace @devthrottle/client-core` | 187 files, 2,383 tests passed |
| `npm test --workspace @devthrottle/mobile` | 39 files, 200 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 132 files, 1,429 tests passed |
| `npm run lint` | 5 errors, exactly the five known ones already on main (`FactoryRecordsTab.tsx`, `fixtures.ts`, `sw.test.ts`, `Recorder.tsx`, `VoiceMode.tsx`); no other |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Alert"` | 116 passed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Alert"` | 13 passed |
| `dotnet test src/CcDirector.Gateway.DatabaseTests --filter "FullyQualifiedName~Alert"` | plain `dotnet test` reports it SKIPPED (by design); through `scripts/test-database.ps1 -Filter "FullyQualifiedName~Alert"` against a throwaway PostgreSQL: 1 executed, 1 passed, 0 skipped |

Also run, because this branch changes the push notifications' address to the new Alerts screen
(`/mobile/alerts?alert={id}`) and the name a restart request stores for its asker:
`--filter "FullyQualifiedName~Alert|FullyQualifiedName~PushNotifier|FullyQualifiedName~DirectorRestartRequestService|FullyQualifiedName~DirectorDrainRestartStep"`,
181 passed.
