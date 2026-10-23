# Phase 6: the end-to-end proof on Pi

Mission #4070, section 7, items 2 and 3. The script is `scripts/prove-api-mode-pi.ps1` (PowerShell 7.4 or later,
Linux and Windows). Each run writes one folder here: `summary.txt` (pass or fail per step), `wire.jsonl` (every
request to the Gateway and every `cc-devthrottle` call, with its answer), `director-pi.log` (the Director's own
lines about Pi's wire) and `spend.txt` (Pi's tokens for the run, priced as `spend.md` prices them). Paths, the
user's name and the token are scrubbed.

## Runs

| Folder | Machine | What it is | Result |
|---|---|---|---|
| `linux-dry-run-20261018-122023` | devlinux, Pi 1.2.0 | Dry run, before phases 3 and 4 (#4186, #4200) were merged. Tree: origin/main at 880a68323 plus the switch (#4208) and this script as committed (the summary names the commit before the home-folder fix, which was in the working tree when it ran). | Every step and every failure case passed, and the user's `~/.local/bin` was unchanged; 0.08 dollars |

**This dry run is superseded by the recorded run.** It is kept as a record of the rig working, not as the evidence
for the QA report: its `Tree:` line names commit `c2b463f5c`, but it ran with the next commit's home-folder fix in the
working tree, and the script's last teardown check (that `~/.local/bin/cc-devthrottle` points at the user's own
install) postdates it. The recorded run, from a committed script on a clean tree, is the evidence. It is still to come
on Linux, once #4200, #4209 and phase 3 part B are on main, and on Windows (SOREN_NORTH), which the Delivery Lead
arranges. Two earlier dry runs passed every step too; their evidence is
not kept (one of them is the run that changed the user's tool links, below). All of them are in `spend.md`.

## The QA run, and the report

The QA report (section 7 in full) is built from one recorded run per platform:

- **Linux, on devlinux:** from a clean worktree of the ref the Delivery Lead names,
  `pwsh -NoProfile -File scripts/prove-api-mode-pi.ps1`. No `-SkipBuild`: the Gateway is built with the Cockpit and the
  phone app (`-p:BuildCockpit=true -p:BuildMobile=true`) and the Director is built fresh.
- **Windows, on SOREN_NORTH:** a runner seat follows `windows-runner-brief.md` in this folder.
- **The report:** `pwsh -File scripts/build-api-mode-qa-report.ps1 -Runs <linux folder>,<windows folder> -Out
  docs/missions/api-mode-pi-2026-10-16/proofs/phase-6/qa-report.html`. It writes one self-contained HTML file in the
  dev report shape (checked with the Gateway's own `DevReportShapeCheck`), with every screenshot embedded, the checks
  per platform with their evidence, the failure cases proven by unit tests, the findings from `qa-findings.txt`, and an
  evidence section saying what is NOT proven. A platform with no run is reported as not proven, and the status is
  `agent-working` until both platforms have a run with every check passing.

**The screens** (section 7, item 4) are taken by `scripts/prove-api-mode-pi-screens.mjs`, which the proof script calls
at the moment each screen exists. They are the real Cockpit (1440 by 900) and phone app (390 by 844) that the test
Gateway serves, driven by headless Chrome through `playwright-core` (installed into the rig; it downloads no browser),
signed in with the test Gateway's own token. Each is a picture and the page's accessibility tree, and each is checked
for the words its state shows before it is taken:

| Screen | Taken when | Must show |
|---|---|---|
| `new-session-api-mode` | after the session starts; nothing is submitted | the Connection row, with API mode pressed |
| `chat-tool-card` | after the bash tool's turn | `TOOL-OK` |
| `chat-midturn` | while the long prompt's turn runs (the prompt is sent once both pages are open, and Stop waits for the picture) | `Working - turn` |
| `chat-after-stop` | after Stop | `stopped` |
| `terminal-before-take` | before taking the wheel | `Take the wheel` |
| `terminal-after-take` | after taking the wheel | `Hand back` |

On Windows the desktop Director's New Session dialog and Terminal tab (before and after taking the wheel) are dumped
through UI Automation, the API `scripts/ui-drive.ps1` uses, and photographed. On Linux there is no accessibility reader
for the desktop, so those are not taken and the summary says so. The desktop has no API-mode Chat screen at all.

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

Pi exiting mid-turn is held to **the Delivery Lead's ruling of 16 October 2026**, recorded in `event-model.md`
(section 2, after rule 5), which replaced section 7's wording "the prompt is not marked delivered". Pi acknowledges a
prompt 0.1 to 0.4 seconds after it is written, long before a turn can be cut short, so: Delivered means Pi acknowledged
the prompt and stays true; the turn ends failed with the reason (the exit code and Pi's error lines); the receipt shows
that failed turn beside Delivered, so it never reads as a plain success. Only a prompt Pi never acknowledged is
Refused, by the Director, with the reason "Pi exited before acknowledging". The tests below assert exactly that.

| Case | Where it is proven |
|---|---|
| Take the wheel twice: the second is refused | This script (409, `wheel_already_held`) and `ApiModeWheelTests.TakeWheelAsync_Twice_TheSecondIsRefusedAndNothingChanges` |
| Hand back while the terminal is mid-turn: refused, never two holders | This script (409 with the reason; the holder stays the user; one Pi process) and `ApiModeWheelTests.HandBackWheelAsync_WhileTheTerminalIsMidTurn_IsRefusedWithTheReason`. If Pi finishes before the hand back arrives, the script reports the case as not provoked, a FAIL, never a pass |
| Pi's executable missing: spawn fails with a stated reason | This script (the Pi entry pointed at a path that does not exist; no model call) and `ApiModeSessionTests.Start_ApiModeBackendWithAMissingExecutable_FailsNamingIt` |
| Pi exits mid-turn, after acknowledging: the session shows failed with the reason, and the receipt stays Delivered with the failed turn beside it | Unit tests only: `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason`, `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween`, `PiDialectReplayTests.Replay_Exit_TurnFailsWithTheExitCodeAndThePromptStaysDelivered`, `PiFixtureIngestTests.Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered`. A real run would have to kill Pi from outside |
| Pi exits before acknowledging: the receipt is Refused by the Director, "Pi exited before acknowledging", with no turn | Unit tests only: `PiDialectReplayTests.Replay_ExitBeforeAcknowledgement_RefusesThePromptAsTheDirector`, `PiFixtureIngestTests.Accept_TheExitBeforeAcknowledgementRecording_TheReceiptIsRefusedByTheDirector` |
| Pi refuses a prompt (`success:false`): the receipt shows the reason | Unit tests only: `PiFixtureIngestTests.Accept_TheRefusalRecording_TheSecondPromptIsRefusedByPiInItsOwnWords`, `ApiModeEventIngestTests.Accept_RefusedByTheAgent_TheReceiptCarriesPisOwnWords`. The Director queues instead of sending while Pi is busy, so a real run cannot make Pi refuse |

## Found along the way

- **A test Director on a scratch root still writes into the user's home folder.** At start-up a Director whose
  root has no cc-* tools installs them into the root and then links all twelve into `~/.local/bin`
  (`PythonToolsInstaller`, through the start-up self-heal in `MainWindow`), whatever `CC_DIRECTOR_ROOT` says. The
  first scripted dry run did this and then deleted its root, so `cc-devthrottle` and eleven other commands on
  devlinux pointed at a deleted folder. I put the twelve links back to the user's own install
  (`~/.local/share/cc-director/pyenv/bin`), checked the skill folders (unchanged: their markers name the hosted
  Gateway and their files predate the run), and changed the script: on Linux and macOS the test Director gets a
  HOME of its own inside the rig, Pi is pointed back at the user's agent folder with `PI_CODING_AGENT_DIR`, and the
  run ends by checking that every link in `~/.local/bin` is as it was. On Windows the same repair writes only
  under the root. The product behaviour itself is not changed here.
- Because the test Director has its own HOME, Pi in these runs does not see the user's global skill folders, so its
  first request is smaller (about 4,700 tokens against 5,900). The preamble and skills loading in API mode was
  proven in phase 1 (`preamble-proof.md`); this proof is about delivery, the live view and the wheel.

- `cc-devthrottle session prompt` answers a prompt that API mode queued because the user has the wheel with "Held:
  the prompt ... may already be in - the Director has not said yet ... Do not send it again." The receipt says
  "Queued, 1st in line: you have the wheel". The command's words are not wrong in effect, but they do not say what
  the Gateway already knows.
- The `cc-devthrottle` installed on devlinux is older than main and has no `--connection`, which is why the script
  runs the tool from the tree under test.
