# Pull request 3868: answers to the review

The review is `review-3868-director.md` beside this file. All three findings are accepted and fixed, and the Tech
Lead's requirement is added. Every new test below fails with its fix taken out (8 of 8, all four fixes out at once)
and passes with it.

## P1: unrecognised command line error values can carry provider text - ACCEPTED, fixed

The command line's answers are now held to the contract's words before anything is logged or reported:

- A usage row whose `readError` is outside the contract's read errors is read as `unexpected-answer`. The value
  itself is not logged. The tool's `readErrorDetail` is dropped at the parser, so no tool text survives even
  briefly.
- A switch answer whose `reasonCode` is outside the contract's reason codes is read as `failed`,
  `exchange-failed-unknown`: the Director cannot know what the exchange did. The value is not logged.
- The two sentence writers no longer quote an unknown value either. They answer a fixed sentence, as a second
  line of defence.

Tests: `ReadUsageRows_ReadErrorOutsideTheContract_ReadsAsUnexpectedAnswerAndCarriesNoToolText`,
`ReadSwitchAnswer_ReasonOutsideTheContract_ReadsAsFailedStateUnknown` (a "switched" and a "failed" answer), and
`SwitchAsync_ToolReasonOutsideTheContract_IsNeitherReportedNorQuoted`. Each plants the marker the fake tool uses in
the unknown value and proves it reaches neither the result nor a sentence.

## P2: the Director does not refuse a spare whose sign-in is expired - ACCEPTED, fixed

Step 9 now also refuses `spare-not-usable` when the target's last known `SignInExpiresAtUtc` is at or before the
Director's clock. The reporter keeps that date from the last usage read (and through a failed read). The
coordinator reads it with `SignInExpiryFor`. A null date does not refuse on its own (contract 3.3 and the Gateway
rule beside it).

Tests: `SwitchAsync_SpareSignInExpired_RefusesSpareNotUsable` (expiry exactly now: refused, nothing exchanged) and
`SwitchAsync_SpareSignInExpiresLater_StillSwitches`.

## P2: a `switch-in-progress` result is not replayed by SwitchId - ACCEPTED, fixed

The refusal is saved to the result store under its own SwitchId, so the same id retried after the other switch
ends replays the refusal for 24 hours instead of running.

`not-key-holder` stays unstored, on purpose. The results folder is shared by every Director on the machine. If one
Director without the lock stored the refusal, the Director that holds the lock would replay it instead of running
the switch it is meant to run.

Tests: `SwitchAsync_RefusedSwitchInProgressRetriedAfterTheOtherEnds_ReplaysTheRefusal`, and
`SwitchAsync_NotTheKeyHolderThenRetriedOnTheHolder_Runs` holding the second point.

## Tech Lead requirement: license switching is off until it is turned on

One Director setting, `licenseSwitching.enabled` in config.json (`LicenseSwitchingSetting`). It is OFF unless the
file says exactly `true`; a missing file, section or key, or a value that is not a boolean, is off. It is read once
at start, so changing it takes a restart.

When it is off:

- **No report loop:** the reporter is never put on the scheduler.
- **No lock:** the key holder lock is never taken, so a Director that has the setting on can take it.
- **No tool runs:** `cc-devthrottle license` is never run.
- **Every switch fails plainly:** it is answered `failed` with "License switching is turned off on this Director,
  so nothing was changed."

No new reason code was added. The answer uses `not-key-holder`, which is literally true: an off Director never takes
the lock. It is not stored, so the same SwitchId runs on a Director that has the setting on.

Tests: `ReadFrom_AnythingButAnExplicitTrue_IsOff` (five shapes), `ReadFrom_ExplicitTrue_IsOn`,
`BuildPatch_ReadsBackAsWritten`, `Start_TurnedOff_ReadsNothingTakesNoLockAndRefusesSwitches`,
`Start_TurnedOn_SwitchRunsOnceTheLockIsHeld`, and `SwitchAsync_LicenseSwitchingTurnedOff_FailsPlainlyAndTouchesNothing`
(which also proves the refusal is not stored).

## The review's last two paragraphs

The live-swap proof script (mission section 7) is not in this pull request. It belongs to the live proof seat
(`dev-live-proof.md`), which turns this setting on against isolated folders.

## Runs

- Core.UnitTests: 1,546 passed, 0 failed.
- Avalonia.Tests: 1,044 passed, 0 failed.

# Fix round: answers

The fix-round review is `review-3868-fix-round.md` beside this file.

## P2: a replayed switch-in-progress refusal can clear the Gateway's active account - ACCEPTED, fixed

A refusal now always carries the active account as it is now (contract 3.4, "or now, on a refusal"):

- **When refusing.** The `switch-in-progress` refusal reads the active account outside the switch lock, so it is never
  empty, and stores that.
- **When replaying.** A replay of any stored refusal reads the active account again, and both accounts in the answer
  are that one, since a refusal changed nothing. This covers every refusal made before the exchange:
  `switch-in-progress`, `expired`, `active-account-changed`, `spare-not-held` and `spare-not-usable`. So a late replay
  cannot undo what an after-switch report told the Gateway since. A switch that RAN still replays its own read-back,
  as contract 3.6 says.
- **When the tool cannot list.** The account named by this Director's last successful read stands. The message says
  so in the Director's own sentence; the tool's words are not quoted.
- **When nothing has been read yet** (a restarted Director whose tool cannot list). There is no honest answer, so it
  throws: the tunnel answers Error, the Gateway logs `director-error`, and nothing is stored. The answer is never an
  empty account.

Tests, each red with the fix taken out (3 of 3) and green with it:

- `SwitchAsync_RefusedWhileAnotherRunsThenReplayedAfterIt_CarriesTheAccountActiveAtEachMoment`, the reviewer's exact
  sequence: A is active while S1 to B runs; S2 is refused and carries A; S1 finishes; S2's retry is replayed and
  carries B.
- `SwitchAsync_StoredRefusalReplayedWhenTheToolCannotList_LastReadAccountAndSaysSo`.
- `SwitchAsync_StoredRefusalReplayedByARestartedDirectorThatCannotList_ThrowsRatherThanAnEmptyAccount`.
- `SwitchAsync_StoredSwitchReplayed_KeepsItsOwnReadBack` guards the other side: a switch that ran is not re-read.

## One case left for the Tech Lead

Two answers still carry an empty `ActiveAccountEmailAfter`, because the Director may not read the key store for them:

- **`not-key-holder`:** only the key holder touches the store (contract 3.3 step 3).
- **License switching turned off:** an off Director runs no tool at all.

Neither is stored, so neither is ever replayed. Both are still a refusal with an empty account in its first answer.
The Tech Lead's ruling: both stay empty, with no Director error. An empty `ActiveAccountEmailAfter` means "not
known", and it must never update the Gateway's machine state. The Tech Lead is passing that rule to phase 2.

## Runs, fix round

Rebased on main first (3863 and 3869 merged).

- Core.UnitTests: 1,600 passed, 0 failed.
- Avalonia.Tests, filter `License`: 8 passed.
