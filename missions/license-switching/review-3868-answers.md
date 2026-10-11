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
