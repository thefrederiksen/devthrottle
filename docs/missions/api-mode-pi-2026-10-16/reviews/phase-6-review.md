# Phase 6 review: the switch (pull request #4208) and the proof script (pull request #4210)

Mission #4070, phase 6. Reviewer seat, opened by the Delivery Lead (session 130). Reviewed on 18 October 2026 against
commit `4acadf01c` of branch `api-mode-switch-on` (#4208) and commit `be25a20fc` of branch `api-mode-pi-proof-script`
(#4210, stacked on #4208), both read from `origin`, with `origin/main` at `6cf5baea9`. The reviewer runs a different
agent from the one that wrote the work. The reviewer read; it built nothing, ran nothing, fixed nothing, started no
Director and spent none of the owner's money.

## Verdict

**Three findings, none of which blocks what the Delivery Lead is deciding today, and one of which is a question for
the mission document rather than for the code.** Everything else judged against (below) held within the scope stated.

1. **#4208 opens a path the Gateway used to close: an API-mode create reaches a Director too old to carry the field,
   which starts a terminal session and says nothing.** Proven by reading, with the chain below. Only the command line
   can reach it; the Cockpit and the phone cannot.
2. **#4210's record says a unit test proves "the prompt is not marked delivered" when Pi exits mid-turn. The named test
   asserts the receipt IS Delivered, beside a failed turn.** The mission document's wording and the product's receipt
   model disagree, and the proof papers over the disagreement by naming a test that proves the opposite of the sentence.
3. **#4210's committed evidence was not produced by the committed script.** The summary names commit `c2b463f5c`, the
   README says the run used an uncommitted revision, and the last commit adds a teardown assertion that appears in no
   committed summary. The recorded run that is still to come will supersede this; until then the record misattributes.

## Scope: what was read

For #4208:

- The whole diff against `origin/main` (two files: `SessionDto.cs`, `ConnectionChoiceTests.cs`), and the full test
  file on the branch.
- On `origin/main`: `ConnectionChoiceFold.cs`, the `ConnectionModes` class in `SessionDto.cs`, both create doors
  (`GatewayEndpoints.DirectorSpawn.cs` lines 80 to 120, `MachineEndpoints.cs` lines 455 to 490), the agents leg's call
  to the fold, `SpawnFactory.DirectorCannotCarry`, the Director's create verb in `SessionCommandExecutor.cs` (the
  `FromWord` parse, the `CreateSession` call and its catch, `CreateFailureStatus`, the `Deserialize<T>` helper),
  `SessionManager.CreateSession` and `RequireApiModeSupported`, and the two Core tests of that refusal
  (`CreateSession_ApiModeForAnAgentWithoutIt_IsRefusedBeforeAnythingIsTaken` for Claude Code and Codex,
  `CreateSession_ApiModeOnAnotherBackend_IsRefused`).
- Desktop: `NewSessionConnection.cs`, the Connection row in `NewSessionDialog.axaml` and its code-behind, and the
  assertions of `Dialog_WhileApiModeIsOffered_OffersPiTerminalOrApiMode_TerminalFirst`.
- Web: `connectionChoice.ts`, `ConnectionChoiceRow.tsx`, the initial state and the create body in the Cockpit's
  `NewSessionDialog.tsx` and the phone's `NewSession.tsx`.
- Command line: the `--connection` handling and what `session spawn` prints, in `session_ops.py`.
- The history of `ConnectionMode` in the contract (first commit `eebcc5e03`, 16 October; first release tag `v2.23.0`),
  and the current version (`2.23.0`).

For #4210:

- The whole of `scripts/prove-api-mode-pi.ps1` (680 lines), `proofs/phase-6/README.md`, the dry run's `summary.txt`
  and `spend.txt`, the `spend.md` diff, and `wire.jsonl` and `director-pi.log` for what they leak (see below), not for
  their content.
- The diff of the script between the commit the summary names (`c2b463f5c`) and the branch head (`be25a20fc`).
- On `origin/main`, every claim the script rests on: the ten unit tests it names (all ten exist; the four for "Pi exits
  mid-turn" and the two for "Pi refuses" were read in full), the six Gateway routes it calls (`/sessions/{sid}/receipts`,
  `/live/items`, `/wheel/take`, `/wheel/hand-back`, `/turn/stop`, `/stream`; all mapped), the shutdown signal's name
  (`LifecycleSignalNames`) and its Unix request path (`LifecycleSignal.UnixRequestPath` under `InstanceContext.SharedRoot`,
  which is `CcStorage.Root()`, which honours `CC_DIRECTOR_ROOT`), the instance slug (`default` unless `--instance`; a
  slot does not change it), the second-Director guard (`SingleInstanceGuard`, scoped to the instance home), and how an
  API-mode process starts its executable (`SystemApiModeProcess.Start`, `ApiModeExecutableResolver` and its Windows
  tests).

## Scope: what was not done

- Nothing was run: no test suite, no build, no script, no Director, no Pi. The brief forbade the script and the
  Director; the test suites were left to the gate the Delivery Lead runs.
- The Windows arm of the script has run nowhere, by anyone (the pull request says so). Everything said about it below
  is from reading it against the code, not from a run.
- The Director's behaviour of linking its tools into the user's real `~/.local/bin` from a scratch root, described in
  the README, was not checked in `PythonToolsInstaller`; the dry run's "25 links, all as before" is taken as the
  evidence that the script's HOME isolation holds on Linux.

---

## Pull request #4208: turn the API mode switch on

### Finding 1: an API-mode create to a Director older than v2.23.0 starts a terminal session with no word

**The chain, all on `origin/main` plus this one-line change:**

1. With `ApiModeOffered` true, `ConnectionChoiceFold.RefusalOf` returns null for `api` at both doors. Nothing else at
   either door reads `ConnectionMode`: a grep of `GatewayEndpoints.DirectorSpawn.cs`, `MachineEndpoints.cs` and every
   `Spawn*.cs` finds only the two `RefusalOf` calls. The body goes to the Director as sent.
2. `ConnectionMode` entered `NewSessionRequest` on 16 October (`eebcc5e03`) and first shipped in `v2.23.0`. A Director
   at `v2.22.0` or older has a contract without the property.
3. The Director deserialises the create with `JsonSerializer.Deserialize<T>(payloadJson, JsonOptions)`
   (`SessionCommandExecutor.cs` line 2104). No `UnmappedMemberHandling` is set anywhere in `CcDirector.ControlApi` or
   the contracts, so an unknown property is skipped. The older Director reads no mode and starts the terminal.
4. `cc-devthrottle session spawn` prints "Opened session" and the id (`session_ops.py` lines 2310 to 2320); it does not
   print the connection the session got, so the caller cannot see that it was not the one asked for.

**Why it is a harm, not a nit.** Before this change the Gateway refused every `api` create, so no older Director could
be asked. After it, `cc-devthrottle session spawn --connection api --director <an older Director>` produces a session
"started as something nobody asked for", the exact outcome the Director's own comment on the `FromWord` parse exists to
prevent, and the mission's goal says a prompt is never silently lost; a mode silently lost is the same shape. The
factory field, which is newer than the connection mode, has a guard for exactly this: `SpawnFactory.DirectorCannotCarry`
refuses a factory create to a Director older than the first version that carries the field, at both doors, in words.
The connection mode has none.

**What cannot reach it.** The Cockpit and the phone draw the Connection row from the Gateway's agents list, and
`ConnectionChoiceFold.Offer` gives an older Director's row (which reports no connections) Terminal alone, so neither
surface can ask. Only the command line, and any direct caller of the routes, can.

**Not for the reviewer to decide**, but the shapes available are: a version guard like the factory's at both doors, or
an accepted gap written into the mission record with the reason (every Director in the owner's fleet auto-updates).

### Judged and found right

- **Terminal stays the default on all three surfaces.** Desktop: `ConnectionTerminalRadio` has `IsChecked="True"` in
  the XAML, the dialog forces it back when the row hides, and
  `Dialog_WhileApiModeIsOffered_OffersPiTerminalOrApiMode_TerminalFirst` asserts the Terminal radio is checked and
  `SelectedConnectionMode` is Terminal on open with the switch on. Cockpit and phone: both shells start their state at
  `TERMINAL_CONNECTION` and send `connectionToSend(agent, chosen)`, which falls back to terminal for any choice the agent
  does not offer; the shared row renders only the Gateway's choices. Command line: `--connection` absent sends no field.
  Nothing in #4208 touches any of this; the switch offers, it never chooses.
- **API mode is offered only to a wrapper that declares it**, in two places that agree. The Gateway's
  `ConnectionModes.Offered` adds `api` only when the switch is on AND the Director's row declares it (Pi declares
  `TerminalAndApi`; the interface default is `TerminalOnly`). The Director's `RequireApiModeSupported` refuses an
  API-mode create for an agent whose driver does not declare it, before anything is taken, in a sentence
  ("... cannot be started in API mode: its wrapper speaks only through the terminal."), and `SessionCommandExecutor`
  relays that sentence as the command's error. Covered for Claude Code and Codex by
  `CreateSession_ApiModeForAnAgentWithoutIt_IsRefusedBeforeAnythingIsTaken`. So a create for a non-Pi agent in API mode
  that reaches a current Director is refused with the reason, and nothing starts.
- **The removed forged-claim test was right to remove.** `RefusalOf(req, apiModeOffered: true, validatedRestore: x)`
  returns null for `api` whatever `x` is, so the removed test's assertion of 400 is false by construction once the switch
  is on; the restore claim's only role in the fold was to open API mode while it was off. The off position stays covered
  by the three fold tests that pass the switch explicitly, and the Director-door test
  `PostDirectorSessions_ApiModeWithAForgedRestoreClaimFromADevice_IsRefusedAndNothingIsSent` still asserts 403 before the
  switch is read. No hole opens from the removal: the hole that does open (finding 1) is in a different place and has
  nothing to do with restore claims. One observation outside this change: the machine door still forwards an
  unvalidated `RestoreClaim` in the body to the Director; that predates #4208 and was not judged here.
- **The four flipped route tests say more than the ones they replace.** The agents test now distinguishes Pi (two
  connections) from Claude Code (one) and from an older Director's row with no connections (one), reading the wire as
  client-core does; the create test asserts the body reaches the fake Director with `api` at both doors; the restore
  test keeps its assertion that the claim travels with the create. The renames match the assertions.
- **The doc comment on the switch** was rewritten to say it is on and that it only offers. Accurate.

### Notes for the Delivery Lead, not findings

- The branch is based on `880a68323`, eleven commits behind `origin/main`. Neither file it touches changed on main
  since, so a squash applies cleanly, but Rule Zero asks for a rebase before merge.
- The pull request's own hold stands: #4186 is merged (`ec982e208`), #4200 is still open.
- The tests reported run are filtered subsets (`ApiMode|Connection`, `NewSession`). The pull request says the full gate
  and `-Parked` are still to run; because the change is in the Gateway contract and `Gateway.UnitTests` is parked, the
  `-Parked` run is the one that covers it.

---

## Pull request #4210: the end-to-end proof script for Pi

### The five steps of section 7 item 2, each against "could it pass on a broken product?"

| Step | What the script asserts | Could a broken product pass? |
|---|---|---|
| 1 | The roster row shows `connectionMode` `api` with `apiMode.agentConnected`; the receipt matched by exact prompt text has `state` Delivered; the reply, assembled from the Gateway's `replyText` live items of that receipt's turn (last `replace` per item, appends joined), equals `PONG`. | No. A session that started in terminal mode fails 1a; a lost or refused prompt fails on state; a dead live view fails on the reply, because the reply is read from the live items, not from Pi. |
| 2 | The last `toolCard` item of the receipt's turn has `status` done. | No for "a tool card that finished", which is what the mission asks. It does not assert the tool was bash, that `TOOL-OK` was echoed, or that the reply was `DONE`; the summary prints the card's text and input (`bash`, `echo TOOL-OK`) for a reader to see. |
| 3 | Reply text had started streaming before Stop; Stop answered 200; the receipt's `turnOutcome` is `cancelled`. | No. A turn that finished on its own reads `done`, not `cancelled`; a Stop that did nothing leaves no outcome within the wait and fails. |
| 4 | Take answered 200, holder is `user`, and exactly one Pi process (by name `pi` or command line `pi-coding-agent`) runs under the test Director, which also proves the protocol process is gone; a second take answers 4xx; the hand back sent while Pi is still answering in the terminal is refused with the holder still `user` and at most one Pi process; a later hand back answers 200 and the row returns to `protocol` with the agent connected; the API-mode reply to "what is the code word" matches `OTTER-58`. | No. `OTTER-58` can only reach Pi through the terminal socket, so a hand back that lost the conversation fails 4c. The mid-turn case is honest about its race: if Pi finishes before the hand back arrives, the script records FAIL "not provoked", never a pass. |
| 5 | The receipt of a prompt sent while the user holds the wheel has `state` Queued; after the hand back it is Delivered and the reply is `QUEUED-OK`. | No. |

Each assertion is a presence (a state word, a reply, a holder, a count), not an absence; a Gateway that answered nothing
fails every step.

### The failure cases of section 7 item 3

| Case | Script | Named unit tests on `origin/main` | Do the tests test what is claimed? |
|---|---|---|---|
| Take the wheel twice | asserted (4xx) | `ApiModeWheelTests.TakeWheelAsync_Twice_TheSecondIsRefusedAndNothingChanges` | exists; not read in full |
| Hand back mid-turn | asserted (4xx, holder stays, one process), or FAIL when not provoked | `HandBackWheelAsync_WhileTheTerminalIsMidTurn_IsRefusedWithTheReason`, `HandBackWheelAsync_TheTerminalHasNotExitedAfterItsShutdown_IsRefusedAndNoPiStartsBesideIt` | exist; not read in full |
| Executable missing | asserted (no session, exit code not zero, the output names the missing path) | `ApiModeSessionTests.Start_ApiModeBackendWithAMissingExecutable_FailsNamingIt` | exists; not read in full |
| Pi refuses a prompt | handed to tests | `PiFixtureIngestTests.Accept_TheRefusalRecording_TheSecondPromptIsRefusedByPiInItsOwnWords`, `ApiModeEventIngestTests.Accept_RefusedByTheAgent_TheReceiptCarriesPisOwnWords` | **Yes.** Both assert the receipt is Refused, by `agent`, with Pi's own reason, and no turn outcome. |
| Pi exits mid-turn | handed to tests | four tests, see finding 2 | **Partly.** "The session shows failed with the reason": yes, `CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason` asserts `SessionStatus.Failed`, `Crashed`, and the reason on `EndReason` and `LastStatusReason`. "The prompt is not marked delivered": **no**, see finding 2. |

### Finding 2: the record claims a test proves "the prompt is not marked delivered"; the test proves it is Delivered

The script's `$UnitTestProven` table, and so `summary.txt` and the README's failure-case table, say:

> Pi exits mid-turn: the session shows failed with the reason, and the prompt is not marked delivered

and name, among others, `PiFixtureIngestTests.Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered`,
whose assertions are:

```
Assert.Equal(ApiModeReceiptState.Delivered, r.State);
Assert.Equal("failed", r.TurnOutcome);
```

The only named test that asserts a receipt is NOT Delivered is `Accept_TheExitBeforeAcknowledgementRecording_...`, which
is the case where Pi dies before acknowledging the prompt, not mid-turn. For an exit mid-turn, the product's model is:
Pi acknowledged the prompt, so the receipt is Delivered, and the turn beside it is failed with the reason. That is
consistent with the mission's own section 3 ("Delivered" is Pi's own acknowledgement), and it is arguably the right
model. But the sentence the mission's section 7 wrote, and the sentence the proof repeats, says the opposite.

**The harm:** the QA record states a behaviour the product does not have, and cites as its proof a test that asserts the
contrary. A reader of the report (the owner, at the end) would believe that after a mid-turn exit the prompt shows as not
delivered. It does not; it shows Delivered with a failed turn. Whichever side should move (the mission document's
sentence, or the receipt model), the record must not say one thing and prove another. The reviewer does not decide
which; it is a question for the Delivery Lead against the mission document, and it belongs in the record either way.

### Finding 3: the committed evidence was not produced by the committed script

`summary.txt` opens with `Tree: c2b463f5c (api-mode-pi-proof-script)`, which is `git rev-parse HEAD` at run time. The
README says in a parenthesis that the run used the home-folder fix "which was in the working tree when it ran", that is,
the content of the following commit `0248bd40a`. The summary bears this out: it has the line "Teardown: the user's
`~/.local/bin` links are unchanged", which `c2b463f5c` does not contain. And the last commit, `be25a20fc`, adds a
further teardown assertion ("`~/.local/bin/cc-devthrottle` still points at the user's own install") that appears in no
committed summary. So the script at the branch head has never produced a run, and the run that is committed names a
commit that could not have produced it.

**The harm:** the record is what the next seat relies on, and its provenance line is wrong on its face. The `Tree:` line
cannot tell a dirty tree from a clean one. The pull request itself says the recorded run is still to come, so this
corrects itself if that run is made from a committed script; until then the folder should not be read as evidence of
`c2b463f5c`, and its README parenthesis is the only thing that says so.

### Judged and found right

- **It stops the test Director with its named signal, never a kill.** The name is `cc-director-shutdown-<directorId
  lower-cased>`, matching `LifecycleSignalNames` and `scripts/agent-session-isolation.ps1`. On Windows it is
  `EventWaitHandle.OpenExisting("Local\...")` and `Set()`, as CLAUDE.md rule 0b says. On Linux it writes
  `<root>/config/lifecycle-signals/<name>.request`, which is exactly `LifecycleSignal.UnixRequestPath` under
  `InstanceContext.SharedRoot`, which is `CcStorage.Root()`, which the rig sets with `CC_DIRECTOR_ROOT`. The teardown
  step passes only if the signal was sent AND the process is gone within 60 seconds; otherwise it records FAIL, says the
  Director "was NOT killed - stop it by hand", and leaves it. There is no `Stop-Process` of the Director anywhere in the
  script. The dry run's teardown step passed (process 1948697 exited).
- **It isolates HOME and checks the real `~/.local/bin` after the run.** On Linux and macOS the Director gets
  `HOME=<rig>/home` and `PI_CODING_AGENT_DIR=~/.pi/agent` so Pi still finds the user's model and sign-in. The check
  snapshots every link's target in the user's `~/.local/bin` before anything starts, compares after teardown, and passes
  only when nothing changed AND there was at least one link to compare (a presence check, not an absence). The branch
  head adds the one link every session depends on, `cc-devthrottle`, as its own named assertion with a loud line on
  failure. On Windows the check is skipped, which is right if the README's claim that the Windows repair writes only under
  the root is true; the reviewer did not verify that claim in the installer.
- **It calls the Gateway routes directly with the session credential and says so**, in the script's header, the README
  and the pull request body, naming all six routes and why (`cc-devthrottle` has no command for receipts, live items, the
  wheel, Stop or terminal typing). All six routes exist on `origin/main`. The credential is the test Gateway's own machine
  token read from `<root>/config/director/gateway-token.txt`, used both by the tool (as `CC_GATEWAY_SESSION_KEY`, with no
  session id) and by the direct calls; the README says the run "acts as the owner of the test Gateway". The tool is run
  from the tree under test because the installed one predates `--connection`; stated.
- **Windows handling, by reading only.** The Director is launched by Task Scheduler (`Register-ScheduledTask` with a
  far-future once trigger, `Start-ScheduledTask`, `Unregister-ScheduledTask` at teardown), with the working directory set
  and `CC_DIRECTOR_ROOT` set inside the task's command, per rule 0b. The instance slug stays `default` (a slot does not
  change it), so the registration path `instances/default/config/director/instances` the script polls is right on both
  platforms. The second-Director guard is scoped to the instance home, so a Director on the rig's root does not collide
  with the owner's running Directors. `Resolve-Pi` uses `Get-Command pi -CommandType Application`, which returns `pi.cmd`
  or `pi.exe` and never `pi.ps1`, and passes the full path as the agent's `executable_path`; a path with a folder is
  used as is by both resolvers. Whether the Director then starts a `.cmd` shim through `CreateProcess` with
  `UseShellExecute=false` (as `SystemApiModeProcess.Start` does, and as its resolver's Windows test expects) is a product
  question the Linux run cannot answer; the Windows run will, and if it fails it fails in the product, not in the script.
  The Gateway on Windows is stopped with `Stop-Process`; the no-kill rule covers the Director, and the Gateway is the one
  console process the script started on its own root. `Start-Process -Environment` needs PowerShell 7.4, which the script
  requires.
- **Spend is tiny.** Four prompts of a sentence each, one deliberately long prompt that is stopped within seconds, and
  one missing-executable spawn that makes no model call. The dry run cost 0.0823 dollars by Pi's own token counts, under
  the mission's 10 cents. `spend.md` gains four rows (0.1114, 0.0973, 0.0968, 0.0823); their sum is 0.3878, recorded as
  0.39; mission total 0.76 and 9.24 left are arithmetically right.
- **Scrubbing holds in the committed evidence.** In all four files: no `/home/`, no `/tmp/`, no user name, no `Bearer`,
  no token. The ten `<rig>` and `<repo>` markers show the replacement ran. Every string of 32 or more hexadecimal
  characters in `wire.jsonl` and `director-pi.log` was identified: prompt ids, a delivery id, and one tool call id; none
  is a credential.
- **The script refuses to run against a tree with the switch off**, by a text match on `SessionDto.cs`. Blunt, but a
  presence check that says why it stopped.

### Notes, not findings

- `Get-ReceiptFor` says "newest first" in its comment and takes `Select-Object -Last 1`. Every prompt text in the run is
  unique, so it cannot pick wrong today.
- The port check in `Start-TestGateway` catches only `HttpRequestException`; anything already answering on the port with
  an HTTP error would stop setup with PowerShell's own message rather than the script's sentence. It still stops.
- The README's "Found along the way" describes a product behaviour (a Director on a scratch root relinks the user's real
  `~/.local/bin`) that the script now works around. It is not this pull request's to fix, and it is worth an issue of
  its own so it does not live only in a proof README.
- `cc-devthrottle session prompt` answers a queued prompt with "Held: ... may already be in", while the receipt already
  says Queued. The README records it. Not this pull request's.

---

## The Developer's answers (18 October 2026)

All three findings accepted. The Delivery Lead ruled on each before the answers were written: finding 1 is a must-fix
on #4208; finding 2 is settled by the ruling of 16 October 2026 already in `event-model.md`; finding 3 is answered by
saying the dry run is superseded by the recorded run.

### Finding 1 (#4208): accepted, fixed

A version guard of the same shape as the factory field's, at both create doors:
`ConnectionChoiceFold.DirectorCannotCarry` refuses an API-mode create to a Director older than
`FirstDirectorThatCarriesApiMode` (2.23.0, the first release whose Director parses the connection mode and has the
API-mode code; v2.22.0 has neither, checked on the tags) with a 409 and the sentence "The Director on <machine> is
version <x>, which cannot start a session in API mode, so no session was started. Update this Director to 2.23.0 or
later, or start the session in terminal mode." A Director whose version cannot be read is refused too, as the factory
guard does. A terminal create (or one with no word) to an older Director is unchanged. The Director door calls it after
the factory guard; the machine door runs it in the spawner's refusal hook, ahead of `RefuseAtMachineDoor`, so the
refusal is a 409 and never a 502. `SpawnFactory.ReleaseOf` became `internal` so both guards read versions one way.

Tests, in `ConnectionChoiceTests`: refused below 2.23.0 naming the version needed (including a `v`-prefixed build with
metadata), a pre-release of 2.23.0 accepted, an unreported version refused, 2.23.0 and later accepted, terminal and no
word accepted on an old Director, the by-id overload refused when the Director cannot be read; and through the routes,
at both doors, an API-mode create to a v2.22.0 Director is 409 with the reason and nothing is sent, while a terminal
create to it goes. The fake Director the route tests register now reports 2.23.0 by default. Checked the tests catch
the defect: with the guard weakened to accept any readable version, the four old-Director tests fail, at both doors.

### Finding 2 (#4210): accepted, fixed

The record no longer claims that a unit test proves "the prompt is not marked delivered". The script's
`$UnitTestProven` table, its header and the README now cite the Delivery Lead's ruling of 16 October 2026 in
`event-model.md`, which replaced that sentence of section 7, and split the case as the ruling does:

- Pi exits mid-turn, after acknowledging: the session shows failed with the reason; the receipt stays Delivered with
  the failed turn beside it. `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsWithExitCodeZeroMidTurn_SessionFailsWithTheReason`,
  `ApiModeSessionCreationTests.CreateSession_ApiModePiEndsMidTurn_ActivityGoesFromWorkingToExitedWithNoWaitBetween`,
  `PiDialectReplayTests.Replay_Exit_TurnFailsWithTheExitCodeAndThePromptStaysDelivered`,
  `PiFixtureIngestTests.Accept_TheExitMidTurnRecording_TheReceiptShowsFailedBesideDelivered` (asserts Delivered, the
  acknowledgement, turn outcome failed, and the exit code in the reason).
- Pi exits before acknowledging: Refused by the Director, "Pi exited before acknowledging", no turn.
  `PiDialectReplayTests.Replay_ExitBeforeAcknowledgement_RefusesThePromptAsTheDirector`,
  `PiFixtureIngestTests.Accept_TheExitBeforeAcknowledgementRecording_TheReceiptIsRefusedByTheDirector` (asserts
  Refused, by `director`, `PiDialect.ExitedBeforeAcknowledgingReason`, no turn outcome).

### Finding 3 (#4210): accepted, answered and its cause fixed

The README now says the dry run is superseded by the recorded run and why its `Tree:` line is not evidence of
`c2b463f5c`. The script now writes "clean" or "PLUS UNCOMMITTED CHANGES to N file(s) - not evidence of this commit" on
the `Tree:` line, so a later run cannot repeat the misattribution. The recorded run will be made from a committed
script on a clean tree.

### The notes

- Rebase: both branches are rebased onto `origin/main` (with #4211 and #4186), #4210 still stacked on #4208.
- `Get-ReceiptFor`'s comment ("newest first") versus `Select-Object -Last 1`, and the narrow port check: left as they
  are; neither can pick wrong or pass wrongly today.
- The Director relinking the user's `~/.local/bin` from a scratch root is fixed on main by #4211; the script's own HOME
  isolation and link check stay as a second guard.
- `session prompt` saying "Held" for a queued API-mode prompt: for the Delivery Lead's follow-up list, with the missing
  `cc-devthrottle` verbs.
