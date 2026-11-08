# Issue 4744 - updating the Director left the cc-* tools behind

## What happened, from this machine's own logs

SOREN_NORTH, 7 November 2026 (`%LOCALAPPDATA%\cc-director\instances\default\logs\director\`):

- The installed Director ran as version 2.33.0 from 3 November until 14:05 on 7 November. Its update pass kept
  reporting `Release v2.35.0 is already staged`, and its tools refresh kept reporting `Python tools bundle up to date
  and healthy (2.35.0)`, even after v2.37.0 was released at 06:20 that morning.
- So between 06:20 and 14:44 the machine's tools were 2.35.0. That copy has `cc-devthrottle factory`, but not
  `factory inbox` (it answers `Error: No such command 'inbox'.`). The hosted Gateway, deployed from main, still
  started a factory Secretary there. That Secretary was told to run `cc-devthrottle factory inbox new` and could do
  nothing.
- At 14:19 the launcher swapped in Director 2.35.0, which then saw 2.37.0. Its tools refresh was held twice because
  the tools were in use (`pyenv cannot be replaced (Access ... denied)`), and the 2.37.0 tools landed at 14:44.

So two things were wrong:
- The tools follow their own hourly refresh, separately from the Director update, and nothing ties a Director to the
  tools its seats are told to use.
- A seat was started whatever the tools could do.

## What changed

### 1. A Director update brings the tools to its version first (the root cause)

The launcher already installs a staged Director update only when the Director is idle. Now, before it swaps the
Director in, it brings the cc-* tools to that same version, using that release's own bundle and never "the latest".
The launcher change is in `DirectorUpdateOwner.RunOnceCoreAsync`, and the engine change is
`ToolUpdater.BringPythonToolsToDirectorAsync` plus `ReleaseSource.FetchReleaseAsync`.

- Tools already at or past the Director's version are left alone, and nothing is downloaded.
- When the tools cannot be brought up, the update is held with a new decision, `HeldBecauseToolsNotReady`. This
  covers tools in use, a release with no bundle for the platform, a failed install, and a download server that does
  not answer. Nothing is stopped or replaced, the staged record stays, and the next pass asks again. The reason is
  recorded and shown in two places:
  - the Director's own update panel (`UpdateStatusFold`, "UPDATE WAITING - waiting for the cc-* tools ...");
  - the Gateway's machine view (`DirectorUpdateViewFold`).
- A Director therefore never runs ahead of its tools. Holding is the honest answer. Installing a Director beside
  tools that lack its commands is not.

### 2. A seat whose instructions use a command the tools lack is not started

- `NewSessionRequest.RequiredToolCommands` lists the `cc-devthrottle` commands the new session's instructions use.
- The factory wake sends `FactoryInboxSecretary.SecretaryToolCommands` on every Secretary start, and
  `BossToolCommands` on every Boss start. This covers defined and undefined factories alike.
- A test holds both lists to the instruction text, so neither can drift from it.
- The Director asks its OWN installed `cc-devthrottle` for each command (`ToolCommandCheck`, running
  `cc-devthrottle <words> --help` concurrently) before it creates anything.
- A missing command is refused as a 409, with one sentence naming the command and the tool's own words. The tool
  being absent, not answering in time, or failing to start are refusals too. None of them is read as "probably fine".
- The check is AWAITED inside the create (`SessionCommandExecutor.CreateAsync`). The Director's command channel is
  handed back while the tool answers, which keeps the rule of #4755, merged just before this change: a start never
  holds the command channel. A test of this was written and shown red against a blocking wait, but it was removed:
  it needed a real delay, which the gate forbids, and without one a regression would hang the run instead of failing
  it. The await is in the code for the review to read.
- The wake records the refusal as the factory's waiting reason ("the Secretary did not start: ..."), and it is tried
  again.

### 3. The earlier draft is removed

- `FactoryInboxSeatCommandChecker` ran `cc-devthrottle --help` on the Gateway's own host. That is the hosted cloud
  server, not the machine the seat starts on. It also cached its first answer forever and looked for the word
  "factory", which the old tools also had.
- The `ToolReconciler.HasFactoryCommand` check counted any exception or a timeout as "no drift". That was a
  fallback. Both are gone: `ToolReconciler.cs` and `FactoryInboxWake.cs` are back to main apart from the one
  stamping line.

## Evidence

Each new test was shown red with the fix disabled by a one-line mutation, then green with the fix restored from the
commit. The mutations were made on a committed tree and reverted with `git checkout`.

| Test | Mutation | Red result |
|---|---|---|
| `SeatToolCommandsOnTheDirectorTests.Create_InstructionsUseACommandTheToolsLack_...` | the Director drops the required commands before its check | Failed: status Ok, not Conflict (a session was created) |
| `FactoryInboxWakeTests.TheSecretarysStart_NamesEveryCommand...`, `TheBossesStart_NamesEveryCommand...` | the wake stamping line commented out | 2 failed |
| `ToolCommandCheckTests.WhyTheToolsRefuseAsync_ToolsLackTheFactoryInbox_...` | an exit code of 2 counted as present | Failed |
| `ToolsForDirectorTests.BringPythonToolsToDirectorAsync_ToolsBehind_ReadsThatReleasesOwnFolder_...` | the latest release fetched instead of the Director's own | Failed |
| `UnreadableRegistrationIsNotAnEmptyInstanceTests.The_update_owner_HOLDS_an_idle_Directors_update_...` | the hold disabled | Test host crashed: the pass went on to stop the "Director", which in this rig is the test process itself (the file's own comments describe this). Not a clean assertion failure, but it shows the update proceeding |

The real tool was also checked on this machine:
- `cc-devthrottle factory inbox --help` exits 0;
- `cc-devthrottle factory nosuch --help` and `cc-devthrottle nosuchgroup --help` exit 2 with `Error: No such command ...`.

Gate (`scripts/test-local.ps1 -Parked`): see the counts in the pull request and the handback. On this machine C: had
3.9 GB free against the 15 GB the gate requires, so TEMP was pointed at a scratch folder on D: for the run. Nothing
was deleted to make room.

A first full run, on the commit before #4755 was merged, was red on two tests:
- `CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot`. This was mine: a log line in a catch did not
  use the house form. Fixed.
- `AtomicFileReplaceTests.WriteAllText_LeavesNothingBesideTheDestination`. It failed with "Access is denied" after
  126 rename attempts in 10 seconds, on code this change does not touch. It passes alone, with TEMP on C: and on D:.
  Under the repository's rule, a test that fails on correct code is removed on main. That is not done in this pull
  request.

## What is NOT proven

- No real Director update was run end to end. The launcher's tools step is proven with an injected tools answer and
  the engine method with a fake download server. No real swap was run beside a real bundle install.
- An older Director, one released before this change, ignores `RequiredToolCommands` and starts the seat as it does
  today. It was not gated on a capability, because that would stop every Secretary on every machine until this
  release reached it. Fix 1 is what keeps a NEW Director's tools current.
- The check runs only for starts that name their commands, which today means the factory inbox Secretary and Boss.
  Other seats whose briefs mention `cc-devthrottle` commands are not checked.
- A defined factory's Boss also receives its own published brief, which may name further `cc-devthrottle` commands.
  Only the wake's bundle work and ring line are declared and checked.
- The engine method's real install, held and failed-install paths are not tested here (see review finding 3).
- The 2.33.0 Director's view that 2.35.0 was the latest release, until it was replaced, was not investigated. That
  code is no longer what ships.

## The separate review, and the answers

The reviewer was a separate session, `cc-devthrottle session review`, on a detached worktree of the head. Codex refused
the model it was given, so the next seat reviewed. It found no blockers, three findings to fix, and seven minor or
proof notes.

1. **A hung or wrong download was an unrecorded failure** (to fix). Fixed. The launcher now records the hold when the
   fetch fails for any of these reasons: a refused connection, a hang past the HTTP timeout (`TaskCanceledException`),
   or a manifest for another release (`InvalidOperationException`). The reason is shown on the panel, and the next pass
   asks again. New test: `The_update_owner_RECORDS_a_tools_download_that_timed_out_as_the_reason_it_holds`, red with
   the catch narrowed back to HTTP errors only.
2. **The check's 30-second timeout equalled the Gateway's 30-second wait** (to fix). Fixed. The timeout is now 15
   seconds, and the first refusal answers at once while the slower questions are withdrawn (finding 6). New test:
   `WhyTheToolsRefuseAsync_ARefusalArrives_IsAnsweredAtOnce_AndTheSlowerQuestionsAreWithdrawn`. It was not shown red:
   the mutation that would undo it makes the run wait on the hung question forever, so it would hang rather than fail.
3. **Missing tests** (to fix). Added:
   - the update panel's new branch (`AnUpdateHeldBecauseTheToolsAreNotReady_SaysWhy_AndOffersNoInstall`, red with the
     branch disabled);
   - the Gateway sentence (`Sentence_HeldBecauseToolsNotReady_...`);
   - the owner's catch (finding 1);
   - the health and unreadable-version answers below.

   NOT added: the engine method's real install, held and failed-install paths. They run the real bundle installer
   (`PythonToolsInstaller.InstallAsync`), which these tests do not fake. They reuse `RefreshPythonToolsAsync`, which has
   its own tests.
4. **"Already at the version" ignored an unhealthy install** (minor). Fixed. Tools at the version whose environment is
   missing console scripts are not ready, and the update waits for the refresh or Fix it to repair them. New test, red
   with the condition disabled.
5. **An unreadable recorded version threw on every pass** (minor). Fixed. It is not ready, and the answer names the
   value and the file. New test.
6. Answered with finding 2.
7. **"Does not have it" stated a cause that was not observed** (minor). Fixed. The sentence now says the tool refused
   the command, quotes the tool's own words, and explains the older-tools case only for "No such command".
8. **A defined factory's Boss brief can name other commands** (minor). True and not covered. Only the wake's own
   bundle work and ring line are declared and checked against the text. Added to "What is NOT proven".
9. **The Director refusal test would start a shell if it regressed** (minor). Kept on purpose. It starts nothing when
   correct, and a harmless shell (which it kills) is the visible failure.
10. **The proof held no gate counts** (proof). The counts are below.

## Gate counts

Full run, `scripts/test-local.ps1 -Parked` with TEMP on D:, commit 539ea6dbb. That is the change before the review
fixes and before the removal of one test that needed a real delay:

| Suite | Result |
|---|---|
| CcDirector.Core.UnitTests | 3658 of 3659 passed (the red was my delay test, since removed) |
| CcDirector.Core.Tests | 4643 passed, 18 skipped |
| CcDirector.Gateway.UnitTests | 17494 passed |
| CcDirector.Gateway.Tests (four shards) | 1835 of 1836 passed, 3 skipped (the red was `DurableDictationDedupeTests`, not touched by this change, which passes alone) |
| CcDirector.Avalonia.Tests | 1272 passed |
| cc-director-setup-engine.Tests | 1027 passed, 6 skipped |
| CcDirector.Launcher.Tests | 235 passed |
| Others (Engine, HostedAgent, Terminal.Avalonia, Reclaim, DeliveryQualification, RuleHook, setup, setup-cli) | all passed |

The final head was gated again after the review fixes; the handback carries those counts.
