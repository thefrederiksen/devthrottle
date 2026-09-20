# Proof - Smart Director Restart, phase 6: retire the hand-run

Written by the Developer seat opened by the Delivery Lead for phase 6, on 20 September 2026.

- Branch: `smart-restart/p6-retire`, cut from `origin/main` = `8fcda423b`.
- Worktree: `D:/ReposFred/devthrottle-smart-restart-p6`.
- Nothing under `src/`, `tools/`, `apps/`, `packages/` or `scripts/` was changed. This phase is a skill,
  a documentation page and this proof.

Everything below that says "on main" was read with `git show origin/main:<path>` or
`git grep ... origin/main` after `git fetch origin`, never from the working tree.

---

## 1. The headline the Delivery Lead should read first

**The feature is merged and is in no release.** The newest tag is `v2.8.1`;
`git tag --contains 217b79f63` and `git tag --contains 1ed421f7d` are both empty, so neither the way
up engine nor the phase 2 swap is in a released build. `cc-devthrottle director list --fields
id,name,machine,version,state` at 14:09 UTC on 20 September 2026 reported every live Director on 2.8.1
or 2.7.0. **No Director in the fleet can do a smart restart today.**

**And the way up has no caller.** The phase 3 engine is on main
(`src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs`, merged as `217b79f63`), but
`git grep -n "CreateDirectorWayUp" origin/main -- src` returns exactly one hit: the factory's own
definition at `ControlApiHost.cs:292`. No file under `src/CcDirector.Avalonia` mentions `WayUp` at all.
There is no "A restart is available" screen, no restart history window and no start-up check. So the
sentence in my mandate - "the sessions are offered back on the next start" - is not true of main, and
neither the skill nor the page says it. Bringing the sessions back is by hand, with
`cc-devthrottle director restore`, and both documents say so plainly.

The command line door of phase 4 (`cc-devthrottle director smart-restart`,
`cc-devthrottle director restart-history`) does not exist either: there is no
`proof-phase-4-command-line.md` in this folder, and `git grep -n "smart-restart\|restart-history"
origin/main -- tools/cc-devthrottle` returns nothing but test fixture ids. The skill says so, because
an agent told to use a command that does not exist will go looking for a way round.

---

## 2. What the skill now says, and what was cut

Skill `director-restart`, on the Gateway. It went from 829 lines to 160.

**What it says now**, in order: the Director does this itself, File, Smart Restart; the hand-run is
retired and must not be improvised back; which Directors have the feature and how to check; what the
feature does (two doors, the three choices, the time allowed, the two stages, the progress screen and
its two buttons, where the handovers and the record live); then six things a person still does by
hand - bring the sessions back, another machine's Director, Codex and Pi, sessions held by a question
box, watch the first real run, and anything needing judgment; then six traps that outlived the
hand-run.

**What was cut, and why:**

| Cut | Why |
|---|---|
| Rule zero (drive the drain from a session that is NOT on the target) | There is no driving session any more. The Director shuts itself down. |
| Step 0, the pre-flight (`machine restart-capability` before draining) | The product asks this itself: `ISmartShutdown.CheckAsync` answers `CanRestart` with a reason before the confirm button goes live (`ISmartShutdown.cs`, `SmartShutdownAvailability`). The command is kept in the skill, but only for the remote case where there is still no feature. |
| Steps 1 and 2 (take the roster, message the chain) | The drain reads its own sessions and asks them over the Director's own prompt path. And a session may message only the session that started it, so this step could not be run at all - the retired version recorded that gap itself, dated 17 September 2026. |
| Step 3, the drain message and its three load-bearing sentences | `DrainMessages` is the product's text now, and one of the three sentences ("you will not be killed") was deliberately removed by phase 1: the feature does end sessions at the limit. |
| Step 4, `index.json` and its whole schema, the secret sweep, the integrity check | The record is a workspace on the Gateway, written by the engine; `HandoverSecretSweep` is in the product. Nobody hand-writes an index. |
| Steps 5 and 6 (close one at a time, the stop condition, never force, recover a half-drained Director) | The engine closes sessions as they hand over, and **never-force is gone**: at the limit every session still present is ended. Keeping the old rule would have taught the opposite of what ships. |
| Step 7, the restart curl and `confirmProtected` | The Director asks its own launcher. |
| Step 8's spawn mechanics, seed-file text and verification recipe | The restore writes its own seed files. What is kept is the one command, its exit codes and the `--force-seat` warning. |
| Step 9, the report shape, and "What this version does not know yet" | The record of two hand runs in September 2026. It is history and it stays in the mission records, not in an instruction. |

**What was kept from the retired version** (and is labelled in this proof as coming from it): the six
traps in the last section. Their evidence is the two real runs of 6 September 2026 recorded in
`docs/missions/restart-a-director-2026-09-06/` and in the retired skill body itself. They are kept
because each is a machine-level fact the new feature does not change - a restarted Director gets a new
identifier, a launcher may update itself, background jobs and scratchpad files do not survive,
`session done` is a flag, never hand-start `cc-launcher`, never start a Director from an agent's own
process (the last is also `CLAUDE.md` critical rule 0b).

## 3. What was published, and when

| When (UTC) | What |
|---|---|
| 2026-09-20 14:10:51 | draft v4 pushed (`skill push`), note: "Retire the hand-run: the Director does this itself (File, Smart Restart); keep only what a person still does by hand" |
| 2026-09-20 14:11 | the stored draft pulled into a second directory and diffed against the source - body and metadata identical |
| 2026-09-20 14:12:03 | **v4 published** |
| 2026-09-20 14:19:07 | draft v5 pushed, note: "Correction: with no sessions the File menu door does not restart an empty Director" |
| 2026-09-20 14:19:12 | **v5 published**, and fetched back with `skill get` to confirm the corrected sentence is what the fleet now reads |

v5 is the live version. The correction is in section 5 below, under the claim it fixes.

The published body and metadata are committed beside this proof, so a Reviewer can read exactly what
went out without the Gateway: `attachments/phase-6/director-restart-skill-v5.md` and
`director-restart-skill-v5.json`.

Two things the push refused on the way, both fixed rather than worked round: a summary over 200
characters, and more than 12 triggers.

## 4. The documentation page, and how it is reached

The product's public documentation is markdown in this repository under `docs/public/`, served raw
from GitHub with no build step, and navigated by `docs/public/index.json`. It is not in another
repository.

- **The page:** `docs/public/features/10-smart-restart.md` - what Smart Restart is and why a restart is
  worth doing, the two doors, what the owner is asked (the three choices), the time allowed and the two
  stages, the progress screen and its two buttons, what survives afterwards (the handovers on disk and
  the record on the Gateway) and the fact that the sessions do not come back by themselves in this
  version, then what it does not do.
- **Reached three ways**, so no route to it is missing:
  1. `docs/public/index.json` - a new page in the "Features" category, after Skills.
  2. `docs/public/features/01-overview.md` - two rows in the feature matrix pointing at the page.
  3. `docs/features/feature-inventory.yaml` - a new `smart-restart` category whose `page` is the new
     file, and two features (`smart-restart-dialog`, `shutdown-progress-screen`) naming the source files
     that implement them. This is what the Docs Drift check and `/document-features` read.

It is written for a user: no session ids, no commands, no repository paths except the folder the
handovers land in, and no mission or phase numbers.

## 5. Every claim, and what proves it

### The skill and the page, on what the feature does

| Claim | Source (all on `origin/main` = `8fcda423b`) |
|---|---|
| File, Smart Restart is a File menu item | `src/CcDirector.Avalonia/MainWindow.axaml.cs:4606` - `file.Menu.Items.Add(Item("Smart Restart", () => { _ = SmartShutdown.OpenFromFileMenuAsync(); }))` |
| Closing the main window leads to the same dialog, on every platform | `MainWindow.axaml.cs`, `OnClosing` calls `SmartShutdown.HandleWindowClosing(e.CloseReason)` and cancels the close when it returns true; `SmartShutdownCoordinator.HandleWindowClosing` opens the same `RunDoorAsync` with door `WindowClose` |
| With no sessions the window close just closes, and File, Smart Restart does nothing and says so | `SmartShutdownCoordinator.cs` - `HandleWindowClosing`: "no sessions running, carry on without asking"; `OpenFromFileMenuAsync`: `_showMessage("Smart Restart: there are no sessions to shut down, so nothing was done.")`. **This is the correction published as v5**: v4 said "the Director just closes or restarts", which is wrong for the File menu door. The same sentence was wrong on the page and was fixed before it was committed. `phase-2-proof.md` flags the same behaviour as a decision deliberately left alone |
| The dialog shows the number of sessions and working against waiting | `SmartShutdownViewModel.cs` - `SessionCountText` ("9 sessions are running" / "1 session is running"), `WorkingWaitingText` ("N working, N waiting") |
| Smart shutdown is the default and the one Enter takes; the titles differ by door | `SmartShutdownViewModel.Title` (`"Smart Restart"` from the File menu, `"Smart shutdown"` from the window close); `SmartShutdownDialogTests` asserts the default button and the Enter key (phase 2 proof, the dialog's 25 tests) |
| The time allowed is 5, 10, 15, 30 or 60 minutes, default 10 | `SmartShutdownViewModel.TimeOptions` and `DefaultMinutes = 10`; `SmartShutdownTimes.Allowed` and `SmartShutdownTimes.Default` refuse anything else on the value itself |
| The explanation: handovers, the time, what happens after it, starting the sessions again | `SmartShutdownViewModel.ExplanationText`, quoted almost word for word on the page |
| Ignore-all writes the record first, then ends everything, and from the File menu does not restart | `ISmartShutdown.ShutDownIgnoringAllAsync` documentation; `SmartShutdownCoordinator.RunDoorAsync`, the `IgnoreAllSessions` case - the File menu branch shows "The Director was not restarted: ..." |
| A refusal is shown in the engine's words, and the other choices still work | `SmartShutdownViewModel.ApplyAvailability` / `RefusalHeading`; `RequireReason` throws rather than inventing words |
| The Gateway unreachable refuses a smart shutdown because the record must live off the machine | `SmartShutdownAvailability.CanSmartShutdown` documentation in `ISmartShutdown.cs` |
| The progress screen replaces the session view and shows rows, a count, the time left and two buttons | `SmartShutdownCoordinator.RunDoorAsync` (`_showInPlaceOfSessionView`); `ISmartShutdownRun` and `SmartShutdownSnapshot` (`CountLabel`, `LimitUtc`, `CanShutDownNow`, `CanCancel`) |
| The row states and the count are the engine's own words | `SmartShutdownWords.StateLabel`, `PhaseLabel`, `CountLabel` ("4 of 9 shut down") |
| Sessions under a lead are drawn beneath it | `SmartShutdownSessionProgress.OwnerSessionId`; phase 2 proof, the progress screen's tests |
| At two thirds, sessions still mid-turn are interrupted and asked to hand over now | `DirectorDrain.cs:565` (`interruptAt = StartedUtc + TwoThirdsOf(...)`), `:613-616` and `InterruptAndAskAgainAsync` at `:1577` |
| At the limit every session still present is ended and recorded with its conversation id | `SmartShutdownSessionState.EndedAtLimit` ("Ended by the engine at the limit, conversation id recorded"); phase 1 proof, task 3 |
| Never-force is replaced | The retired skill's own "Version one NEVER FORCES" against `SmartShutdownPhase.EndingAtLimit` ("every session still present is ended") and phase 1 proof, task 1: "the promise that a session will not be killed is gone" |
| Shut down now jumps to the limit; Cancel and keep working stops the closing, tells the open sessions, and brings the closed ones back against the same Director | `ISmartShutdownRun.ShutDownNow` / `CancelAndKeepWorking`; `SmartShutdownSessionState.KeptRunning` and `BroughtBack`; phase 1 proof, task 4 |
| File, Smart Restart restarts through the launcher once empty; the window close closes and stays closed | `SmartShutdownCoordinator.OnRunFinished` - `Emptied` closes the application, `RestartAccepted` leaves the launcher to stop the process; `SmartShutdownPurpose` (Close = "leave it closed", Restart = "through its launcher"); mission 10.4 |
| An operating system shutdown writes the record and asks nothing | `ISmartShutdown.RecordAndLetEndAsync` ("no dialog and no progress screen ... it ends nothing itself"); `SmartShutdownCoordinator.RecordForOperatingSystemShutdown`, which waits at most five seconds and lets the close carry on |
| The handovers are one file per session under the data root | `DrainPaths` - `<data root>/vault/handovers/director-restart/<timestamp>-<mark>-<director name>/<short id> - <name>.md`; the data root is `%LOCALAPPDATA%\cc-director` on Windows and `~/.local/share/cc-director` otherwise (`CcStorage.ResolveDefaultBase`, `CcStorage.VaultHandovers`) |
| The record is a workspace on the Gateway, id `restart-<yyyyMMdd-HHmm>-<director slug>` | `DrainPaths.WorkspaceIdFor`. Confirmed live: `GET /gateway/workspaces` with this session's key returned `restart-20260919-2040-devthrottle-1` |

### The skill, on what a person still does by hand

| Claim | Source |
|---|---|
| The way up engine is merged and nothing calls it | `git grep -n "CreateDirectorWayUp" origin/main -- src` - one hit, the definition; no `WayUp` in `src/CcDirector.Avalonia` |
| `cc-devthrottle director restore` exists, and its exit codes are 0, 1, 3 | `cc-devthrottle director restore --help`, run today; `tools/cc-devthrottle/src/cli.py:1668` |
| A seat whose start may have landed needs `--force-seat`, after looking | the same help text |
| A session key may list the workspaces | run today: `GET /gateway/workspaces` with `CC_GATEWAY_SESSION_KEY` returned the record above. The routes are documented at `src/CcDirector.Gateway/Api/WorkspaceEndpoints.cs:15-28` |
| A restarted Director has a new identifier | the retired skill, from the 6 September run; and `cc-devthrottle director restore --help`: "after a restart, the NEW one" |
| There is no remote smart restart and no command line door | mission 5.4; `git grep "smart-restart" origin/main -- tools/cc-devthrottle` returns no command |
| A session key may not deliver a restart, but may ask the owner for one | `src/CcDirector.Gateway/Util/SessionKeyGuard.cs:241` names "the admission guard on POST .../director/restart"; `:628` `IsRestartRequestCreate` allows `POST /machines/{machine}/director/restart-requests`. The commands are `cc-devthrottle machine restart-capability`, `machine restart-request`, `machine restart-request-status`, all present in `cc-devthrottle machine --help` today |
| Codex has no conversation resume | `src/CcDirector.Core/Drivers/CodexDriver.cs:79` - "ignoring resume=... (Codex does not support Director-preassigned resume in this integration)"; mission 5.4 |
| Pi has no safe interrupt, is asked again anyway, and is ended at the limit with no handover | `src/CcDirector.Core/Drivers/PiDriver.cs:19-20` and `:77-79` ("pi has no safe hard interrupt"); `DirectorDrain.cs:1573-1575` - "An agent with no safe interrupt (Pi) refuses by design. It is still sent the short message, its row is not called interrupted because it was not, and the limit ends it like any other session still present" |
| A Pi session stopped with Escape wrote its handover in under seven seconds | `proof-phase-3-rig.md`, finding 1: 6.8 seconds, measured on the isolated rig |
| The "Answer these first?" section will not appear on a real Director | `src/CcDirector.Core/Sessions/PendingInteraction.cs:53-56` - "its only source was the Claude Code hook path, which has been removed ... this is currently never populated"; `phase-2-proof.md`, "What could not be reached" |
| Nothing has been run against a real Director, Gateway or launcher | `phase-1-proof.md` ("Nothing in phase 1 was ever run against a real Gateway, a real Director or a real launcher") and `phase-2-proof.md` ("nothing here ran against a real Director or the real engine"). Phase 5 is the QA phase in mission section 6 and has no proof file in this folder |
| A Gateway that does not know a drain state refuses to store it | `src/CcDirector.Gateway/Workspaces/WorkspaceValidation.cs:505` - `drainState must be one of: ...`; `phase-1-proof.md` says the new marks need a Gateway carrying them |
| A session that appears after the record is written is ended with no trace of it in the record | `phase-1-proof.md`, "What the phase could NOT reach"; `ISmartShutdown.ShutDownIgnoringAllAsync` / `IgnoreAllResult.Detail` |
| No model is involved and a handover is accepted on size and its closing block | mission 5.4 ("Any model" is out of scope); `DirectorDrain.MinimumHandoverBytes = 500` and `DrainReportBlock` |
| The retired hand-run could not be run in any case | the retired skill's own step 2, "KNOWN GAP (17 September 2026, the Message Load mission)" |

### Which Directors have it

| Claim | Source |
|---|---|
| Merged on main, in no release | `git tag --sort=-creatordate` (newest `v2.8.1`); `git tag --contains 217b79f63` and `git tag --contains 1ed421f7d` both empty |
| Every live Director is on 2.8.1 or 2.7.0 | `cc-devthrottle director list --fields id,name,machine,version,state`, run today |
| Older builds carry "Drain this Director for restart..." and its window threw on opening | `git grep -n "Drain this Director" v2.8.1 -- src/CcDirector.Avalonia/MainWindow.axaml.cs` - line 4513; mission 5.5 for the defect (issue 3168), closed by pull request 3217 |

### Sentences that came out because they could not be sourced

- "the sessions are offered back on the next start" - from my own mandate. There is no caller for the
  way up engine (section 1).
- Anything about a restart history window, a start-up offer, or `cc-devthrottle director smart-restart`.
- The "A restart is available" wording of `WayUpWords` - it is real code, but no person can see it, so
  neither document quotes it.
- Any screenshot. The phase 2 pictures are of headless renders and I have not seen the real window, so
  the page has no image and does not describe pixels.

## 6. The checks I ran

All in the foreground, in this worktree, on `smart-restart/p6-retire` with the changes in place.

| Check | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` (the mission's own check, section 7) | **606 passed, 0 failed, 606 total** - the same count the Delivery Lead read on `origin/main` at `c2bbf7d36`, which is what a documentation-only branch should give |
| `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~RetiredMessagingWords"` - the sweep that reads every file under `docs/public/`, so it covers the new page | **5 passed, 0 failed** |
| `.\scripts\check-inventory-drift.ps1` - what the Docs Drift workflow runs on a pull request touching `docs/public/features/**` | **OK**, exit 0; 33 source paths and 7 pages checked (the new page is one of them) |
| The same script against a COPY of the inventory with the new page's path made wrong | **FAIL**, exit 1, naming `docs/public/features/10-smart-restart-MISSING.md`. This is the proof the check actually reads my page rather than passing over it; the real inventory was not touched, the mutation was made in a scratch copy and passed with `-InventoryPath` |
| The stored skill draft pulled back and diffed against what I wrote | body and metadata identical, before publishing |
| `cc-devthrottle skill get director-restart` after publishing v5 | the corrected sentence is in the live body |
| ASCII sweep of every file I wrote (`grep -P "[^\x00-\x7F]"`) | no hits (the byte-order mark on line 1 of `feature-inventory.yaml` is pre-existing and was not added by me) |
| Attribution sweep of every file I wrote - the agent and vendor names, "Co-authored-by", "Generated with" | no hits: nothing I wrote is signed |

The default local gate (`scripts/test-local.ps1`) was not run: this branch changes no code, and the two
suites that do read what I changed were run directly and named above.

## 7. What I could NOT reach

- **I have never seen the feature run.** Every sentence in the skill and on the page about what the
  owner sees comes from the code that draws it and from the phase 1 and phase 2 proofs. I did not build
  the application, did not open the window, and took no screenshot. Nobody has run it against a real
  Director (section 5), so the page describes what the code does, not what anyone has watched it do.
- **The deployed Gateway's version is unknown to me.** A session key is refused on `GET /version` and
  `GET /health` ("session_key_out_of_scope"), so I could not confirm that the hosted Gateway carries the
  new drain-state marks. Both documents say the Gateway has to carry them without claiming that it does.
- **The page has no screenshots.** The other feature pages carry PNGs captured by
  `/document-features` on an interactive desktop; that pipeline needs the running application and a
  foreground window. The inventory entries carry `screenshot: ""`, which the inventory's own rules
  define as "none yet", and the drift check passes with it. Somebody should run `/document-features`
  once the feature is in a build.
- **I did not verify that `cc-devthrottle director restore` actually brings back a record written by
  the smart shutdown.** It has never been done - the engine's own restore path is untested end to end
  (phase 1 proof), and running it would mean shutting down a live Director, which is not mine to do. The
  skill gives the command as the way, which is what the mission design says it is, not as something I
  watched work.
- **The `/document-features` pipeline itself was not run**, only the drift half of it, which is the half
  that works without a desktop.
- **The parked suites, the web tests and the Python tests were not run.** Nothing here touches them.

## 8. What happens to this if the way up ships

One section of the skill goes, and only one: "1. Bring the sessions back. Nothing offers them yet."
When something calls `IDirectorWayUp`, that section becomes a sentence saying the Director offers the
sessions on the next start, and the page's "Afterwards" section changes with it. Nothing else in either
document depends on it.
