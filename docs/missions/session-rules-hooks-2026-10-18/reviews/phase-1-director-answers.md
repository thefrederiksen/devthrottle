# Answers - Session Rules part 2, phase 1, Developer C (the Director side)

Written 2026-10-18 by the Developer seat "Session Rules - Developer - phase 1 Director rules file and
forwarding", answering `phase-1-director-review.md` and the Tech Lead's calls at its end. Each fix was
written test first; the new tests were run and seen failing before the code changed.

## Finding 1 - exit 128 read as "no repo", cached for the session's life: ACCEPTED

Fixed to the Tech Lead's call (the reviewer's second option), in `SessionRepoIdentity.ResolveAsync`:
- Exit 2 ("No such remote 'origin'") is the empty identity.
- Exit 128 is the empty identity ONLY when git's error says "not a git repository" AND no `.git` entry
  (folder or file) exists in the folder or any parent. A plain folder still gets a rules file.
- Every other 128 - an orphaned worktree's `.git` file, a `.git` above the folder, "dubious ownership" -
  is a failure: no file, logged with git's own error text, asked again on the next refresh.
- `SessionRulesMaintainer` never caches the empty identity; a real repo is still cached.

Tests: `ResolveAsync_Exit128InAPlainFolderWithNoGitAnywhereAbove_ReturnsTheEmptyIdentity`,
`ResolveAsync_Exit128InAWorktreeWhoseParentIsGone_Fails`, `ResolveAsync_Exit128BelowAFolderThatHoldsAGitEntry_Fails`,
`ResolveAsync_Exit128ForAnythingButNotARepository_Fails`,
`RefreshAllAsync_CheckoutGainsAnOriginLater_PicksItUpOnTheNextRefresh`.

## Finding 2 - decisions lost at Director stop, leftover directories never cleaned: ACCEPTED

Fixed to the Tech Lead's call:
- **Final sweep at shutdown.** `HookDecisionForwarder.DrainAsync` ignores any wait a failed post set and
  waits for a running sweep. `ControlApiHost.StopAsync` calls it before the Gateway client is stopped
  and disposed; the sessions are already killed by then (the desktop app kills them before it stops the
  host). It is time-boxed at ONE second, not more: the desktop app gives the whole host stop two
  seconds (`App.axaml.cs`, `stopTask.Wait(TimeSpan.FromSeconds(2))`) and the Gateway farewell follows.
  What the drain cannot send is not lost - see the next point.
- **A local record of minted pairs.** At launch, `SessionHookFiles.StampRuleHookEnvironment` writes an
  empty file `<pointer drop box>/decision-records/<id>.<token>` BEFORE it creates the decision directory.
  It is removed with the directory once that directory is drained.
- **At start** (`HookDecisionForwarder.Start`), for every directory no session on the roster owns: empty
  - deleted, with its record; holding decisions and vouched for by a record - adopted, forwarded by the
  first sweep, then removed with its record; holding decisions and vouched for by nothing - left and
  logged once.

Tests: `DrainAsync_AtShutdownDuringAWait_StillTriesOnce`,
`Start_LeftoverTheRecordVouchesFor_IsForwardedAndThenRemovedWithItsRecord`,
`Start_LeftoverNoRecordVouchesFor_IsLeftAndNeverForwarded`, `Start_EmptyLeftover_IsDeleted` (with and
without a record), `Start_SessionOnTheRoster_KeepsItsEmptyDirectory`,
`SweepAsync_SessionRemovedAndDrained_RemovesItsRecord`,
`StampRuleHookEnvironment_ClaudeSession_RecordsTheIdAndTokenItMinted`.

Two limits, stated rather than hidden:
- The record is as strong as the token and no stronger: it keeps out a writer that knows only a session
  id, not a same-user process that reads the Director's own folders. That is the isolation level the
  pointer drop already states.
- The start-up clean-up deletes an EMPTY directory whose session is not on the roster. A session whose
  environment was stamped but which was not yet on the roster at the moment the forwarder starts would
  lose its empty directory (the hook then refuses to run for it). The forwarder starts first in
  `StartSessionStateServices`, before any session is launched or restored, so I found no path that
  reaches this; it is not covered by a test.

## Finding 3 - a stray refusal id could make up the count: ACCEPTED

Fixed as the reviewer says: only refusals for ids in the batch count, and a refusal for an id not sent
means the answer does not account for the batch, so nothing unaccepted is deleted.
Test: `SweepAsync_RefusalNamesADecisionThatWasNotSent_KeepsTheUnaccountedOnesForRetry`.

## Finding 4 - the evaluation log outlives its session: ACCEPTED

Fixed to the Tech Lead's call: `SessionRulesFile.DeleteFor` deletes `<id>.eval.log` with `<id>.json`
when the session is removed. The path is `SessionHookFiles.EvaluationLogPathFor`, the hook's own rule
(`Path.ChangeExtension(rulesFile, ".eval.log")`, `EvaluationLog.PathFor` on the hook branch). The live
check must read the log before it closes the session.
Tests: `Start_SessionRemoved_DeletesItsEvaluationLogWithItsRulesFile`, `EvaluationLogPathFor_Session_IsBesideItsRulesFile`.

## Unpinned constants: ACCEPTED

Test: `SweepAsync_DecisionLargerThanTheCeiling_IsDeletedAndNeverPosted`. It passed on first run - the code
already refused an oversized file; the test pins it. `SweepInterval` and `MaxBackoff` stay referenced
symbolically by the tests; the numbers in `docs/BackgroundWork.md` are not test-backed.

## The note on the misleading "not on this Director's roster" line

Not changed. The line is logged once per directory, and the race the reviewer describes (a sweep between
the environment stamp and the roster add) is harmless: the directory is read again on the next sweep.

## Runs (Linux, Debug, `DOTNET_USE_POLLING_FILE_WATCHER=1`)

- `src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook|FullyQualifiedName~SessionPointerDrop|FullyQualifiedName~CcDirector.Core.Tests.Rules"`: 114 passed, 0 failed.
- `src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"`: 494 passed, 0 failed.
- `src/CcDirector.Core.UnitTests` whole: 2469 passed, 5 failed, 23 skipped. The 5 are the same
  Windows-path tests as before (`CcWorktreesPoolSlotsTests`, `WorktreePoolSettingsTests`,
  `RepositorySourceIndexTests`), in code this branch does not touch; not checked against main.
- Not run: a live Director, Windows, the full local gate.
