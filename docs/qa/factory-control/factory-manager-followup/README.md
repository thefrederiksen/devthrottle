# Factory Manager follow-up - proof

Developer brief 9: review notes 1, 2, 5 and 7 on merged pull request 3931. The design document beside the
original work is updated to say what is now true: `docs/qa/factory-control/factory-manager/DESIGN.md`.

Everything below was run on 13 October 2026 at commit `410b6a092`, which sits directly on origin/main at
`7fd15f0e8`, except where a line says otherwise.

## What changed

1. **Only the owner's own phone or browser may set the Factory Manager mark.** `PUT /gateway/factory-manager` with
   a session id answers 403 to a session key, a Director's key and the machine token. Any caller of the account
   may still read the mark and clear it. The talk that starts a Factory Manager still binds it.
   `cc-devthrottle factory manager set` run by a session prints the Gateway's refusal; `show` with no mark no
   longer points a session at `set`.
2. **A session that was the Factory Manager and is refused on resolve is told why.** The account remembers the
   sessions that held the mark before the current one (the newest twenty, in the account setting
   `factory_manager_earlier_session_ids`, written in the same save that moves or clears the mark). The refusal
   begins "This session is no longer the Factory Manager: the mark has moved to session (id) ..." or "... the mark
   was cleared ...", and then says the usual sentence about the run's factory.
3. **The card's Computer and Folder labels come from the Gateway**, and the folder field has no placeholder.
4. **`factory name-check` refuses an answer with no count of factories checked or no rule**, with or without
   `--json`, instead of printing `factories-checked: 0` and an empty rule.

## Each fix has a test that fails without it

Each fix was switched off in the production code, the tests were run, and the production code was restored. This
was done on the commit before the rebase (same production and test files).

| Fix | Switched off by | Tests that went red |
| --- | --- | --- |
| 1 | the owner check in `PutBindingAsync` made unreachable | `Binding_SetByACredentialThatIsNotTheOwnersBrowserOrPhone_Is403_AndTheMarkStaysWhereItWas` (session key, Director key, machine token), `Binding_ASessionMarkingItself_Is403_InASentenceThatSaysWhoSetsItAndWhatASessionMayStillDo` |
| 1 (command line) | `factory_manager_ops.py` and `cli.py` put back as they were | `test_show_WithNoMark_SaysNoneAndThatTheOwnerSetsIt_NeverPointingASessionAtSet` |
| 2 (route) | the "was the Factory Manager" branch in `Resolve` made unreachable | `Resolve_BySessionThatWasTheFactoryManager_IsRefused_AndToldTheMarkHasMovedAndToWhom`, `Resolve_BySessionThatWasTheFactoryManager_WhenTheMarkWasCleared_IsToldItWasCleared` |
| 2 (record) | the save no longer remembers the session that lost the mark | four `FactoryManagerMarkTests` (moves, cleared, marked again, more moves than are kept) |
| 3 (Gateway) | the card sends no labels | `Card_NotStartedAndNoPlacement_AsksForTheComputerAndFolder_ListingRunningComputers` |
| 3 (Cockpit) | `FactoryManagerCard.tsx` put back as it was | `labels the computer and the folder with the Gateway's words, and shows no path of its own in the folder field` |
| 4 | `factory_manager_ops.py` put back as it was | `test_nameCheck_AnAnswerWithNoFactoryCount_FailsRatherThanPrintingZero`, `test_nameCheck_AnAnswerWithNoRule_FailsRatherThanPrintingAnEmptyRule`, `test_nameCheck_Json_AnAnswerMissingItsCountOrRule_IsRefusedToo` |

The Gateway run with fixes 1, 2 and 3 switched off: 11 failed, 819 passed, and the 11 are the tests named above.
The command line run: 4 failed, 13 passed. The card run: 1 failed, 4 passed.

`test_set_RefusedByTheGateway_PrintsItsSentence_AndReportsNoMark` did NOT go red: printing a Gateway refusal
already worked. It pins that behaviour; it is not a proof of fix 1.

## Test runs, one suite at a time

| Suite | Command | Result |
| --- | --- | --- |
| The whole Gateway unit test project | `dotnet test src\CcDirector.Gateway.UnitTests --filter "Gate!=Quarantine"` | 11,131 passed, 0 failed |
| The default gate | `.\scripts\test-local.ps1` | green at `410b6a092`, tree clean, 10 suites, 4,562 tests |
| The whole command line suite | `python -m pytest tests` in `tools/cc-devthrottle` | 4,321 passed, 3 skipped |
| Cockpit factory tests | `npx vitest run src/factory` in `apps/cockpit` | 139 passed (8 files) |
| Cockpit type check | `npx tsc --noEmit` in `apps/cockpit` | clean |
| client-core factory tests | `npx vitest run src/factory` in `packages/client-core` | 28 passed (2 files) |
| client-core type check | `npx tsc --noEmit` in `packages/client-core` | clean |

Two things to know about those runs:

- The command line suite was run in a scratch Python environment with click 8.5.0 and typer 0.27.3. This machine's
  default Python has click 8.1.8, below the 8.2.1 the tool requires, and under it 349 tests of
  `test_axi_step_6c_help_and_errors.py` fail for that reason alone.
- The Cockpit and client-core runs were made before the rebase onto `7fd15f0e8`. That rebase changed no file under
  `apps` or `packages`, so the files tested are the files in this commit.

## Not proven

- Nothing was looked at in a browser, and no Gateway or Director was run. The card's labels and the missing
  placeholder are proven by the component test and the Gateway test, not by a screenshot.
- The three parked suites other than the Gateway unit tests (`Gateway.Tests`, `Core.Tests`) were not run; nothing
  in them is touched by name, and the release seat runs `-Parked`.
- The mobile tests were not run; the mobile app does not show this card.
- The database tests (`scripts\test-database.ps1`) were not run. The new account setting is one more key in the
  existing settings table and needs no migration.

## Review notes left alone

- Note 3: the seat's problem prompt names the factory's folder and not its computer.
- Note 4: the name check does not look at a seat's id for a person's name.
- Note 6: just after a start the card reads "not running" until the Director first reports the new session.

## A question this leaves open

After fix 1, `cc-devthrottle factory manager set` can only ever be refused when a session runs it, and no screen
sends `PUT /gateway/factory-manager` with a session id. The owner moves the mark only by starting a talk. The
command is kept, with help text that says so, because the brief names it as restricted rather than removed.
Removing it, or giving the owner a way to mark an existing session from the Factories screen, is a decision for
the lead.
