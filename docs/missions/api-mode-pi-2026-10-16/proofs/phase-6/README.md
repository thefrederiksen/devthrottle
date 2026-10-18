# Phase 6: the end-to-end proof on Pi

Mission #4070, section 7, items 2 and 3. The script is `scripts/prove-api-mode-pi.ps1` (PowerShell 7.4 or later,
Linux and Windows). Each run writes one folder here: `summary.txt` (pass or fail per step), `wire.jsonl` (every
request to the Gateway and every `cc-devthrottle` call, with its answer), `director-pi.log` (the Director's own
lines about Pi's wire) and `spend.txt` (Pi's tokens for the run, priced as `spend.md` prices them). Paths, the
user's name and the token are scrubbed.

## Runs

| Folder | Machine | What it is | Result |
|---|---|---|---|
| `linux-dry-run-20261018-121310` | devlinux, Pi 1.2.0 | Dry run, before phases 3 and 4 (#4186, #4200) were merged. Tree: origin/main at 880a68323 plus the switch (#4208). | Every step and every failure case passed; 0.10 dollars |

The recorded run for the QA report is still to come on Linux, once #4186 and #4200 are on main, and on Windows
(SOREN_NORTH), which the Delivery Lead arranges. The dry run used the script one revision before the committed
one: since then step 4a also waits for the terminal's Pi process and asserts exactly one, and the script deletes
its scratch folder at the end.

## Which Gateway

The flow must reach a Gateway that has the API-mode code and the switch on, and the hosted Gateway only gets it at
the mission's single deploy, after this proof. So the script builds and starts the repository's own self-host
console Gateway (`src/CcDirector.Gateway`, the "dev console host" its entry point describes) from the tree it runs
in, on its own port (7995 by default) and a throwaway storage root (`CC_DIRECTOR_ROOT`). It checks first that the
tree has `ConnectionModes.ApiModeOffered` on, and stops if not. I looked for an existing local-Gateway script:
`scripts/phase2-gateway-proof.ps1` starts one the same way but is Windows-only and stops after the health check,
so this script follows its pattern rather than calling it.

The slot-5 test Director runs on the same root. Its config (`<root>/instances/default/config/config.json`, the
instance home, not the shared root) names the test Gateway, that Gateway's own machine token, stream mode on, and
one Pi agent entry. `cc-devthrottle` and the Gateway routes use the same machine token, and no session id, so the
run acts as the owner of the test Gateway.

## How the flow is driven

`cc-devthrottle`, copied from this tree under its installed package name, spawns (`session spawn --connection
api`), prompts (`session prompt`) and stops sessions. `cc-devthrottle` has no command for receipts, the live items,
the wheel, Stop or typing into the terminal, so those go to the Gateway's routes: `GET /sessions/{id}/receipts`,
`GET /sessions/{id}/live/items`, `POST /sessions/{id}/wheel/take`, `POST /sessions/{id}/wheel/hand-back`,
`POST /sessions/{id}/turn/stop`, and the terminal socket `/sessions/{id}/stream` that the Cockpit's Terminal tab
types through.

## The failure cases

| Case | Where it is proven |
|---|---|
| Take the wheel twice: the second is refused | This script (409, `wheel_already_held`) and `ApiModeWheelTests.TakeWheelAsync_Twice_TheSecondIsRefusedAndNothingChanges` |
| Hand back while the terminal is mid-turn: refused, never two holders | This script (409 with the reason; the holder stays the user; one Pi process) and `ApiModeWheelTests.HandBackWheelAsync_WhileTheTerminalIsMidTurn_IsRefusedWithTheReason`. If Pi finishes before the hand back arrives, the script reports the case as not provoked, a FAIL, never a pass |
| Pi's executable missing: spawn fails with a stated reason | This script (the Pi entry pointed at a path that does not exist; no model call) and `ApiModeSessionTests.Start_ApiModeBackendWithAMissingExecutable_FailsNamingIt` |
| Pi exits mid-turn: failed with the reason, the prompt not marked delivered | Unit tests only: `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason`, `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween`, `PiFixtureIngestTests.Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered`, `PiFixtureIngestTests.Accept_TheExitBeforeAcknowledgementRecording_TheReceiptIsRefusedByTheDirector`. A real run would have to kill Pi from outside |
| Pi refuses a prompt (`success:false`): the receipt shows the reason | Unit tests only: `PiFixtureIngestTests.Accept_TheRefusalRecording_TheSecondPromptIsRefusedByPiInItsOwnWords`, `ApiModeEventIngestTests.Accept_RefusedByTheAgent_TheReceiptCarriesPisOwnWords`. The Director queues instead of sending while Pi is busy, so a real run cannot make Pi refuse |

## Found along the way

- `cc-devthrottle session prompt` answers a prompt that API mode queued because the user has the wheel with "Held:
  the prompt ... may already be in - the Director has not said yet ... Do not send it again." The receipt says
  "Queued, 1st in line: you have the wheel". The command's words are not wrong in effect, but they do not say what
  the Gateway already knows.
- The `cc-devthrottle` installed on devlinux is older than main and has no `--connection`, which is why the script
  runs the tool from the tree under test.
