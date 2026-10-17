# Answers to the review of pull request 4106

The review is `review-4106.md`. It found no key-safety defect. Every point is answered here.

## Finding 1 (low): an operating-system error in the heal's rename escapes as a traceback - ACCEPTED, fixed

The harm is real. On Windows a folder cannot be renamed while another process holds a file in it open, and
the `OSError` ran past `_run_journal` (which catches only `_StepFailed`) and past the command line's
handler (which catches only `LicenseStoreError`). The tool crashed instead of answering. The next round's
recovery hit the same error while the file was still in use.

Fixed in `LicenseStore._settle`, the one place every reading or changing method passes through first. An
`OSError` from the recovery or the heal is logged (`settle-interrupted`) and raised as a store error with
`switch-in-progress`. The command line prints that as `Error:` with next steps; the Director logs it as one
"no report sent" line and tries again next round. The journal stays where it stopped, so the next run
finishes the heal. This mirrors what `switch()` already did for a disk error part way through.

Test: `test_heal_TheRenameFailsWithAnOperatingSystemError_IsAStoreErrorAndTheNextRoundHeals` (both backends).
A `PermissionError` at the rename gives a store error. The spare is untouched and no backup is made. A
second try while the file is still in use gives the same clean error. The round after that heals: one
backup, the heal reported, every sign-in in exactly one place. Revert check: with the handler removed, the
test fails on both backends.

## Finding 2 (process): the parked gate result is not recorded - ACCEPTED, recorded

The parked gate ran on the rebased commit e3f28d12a and is recorded on the pull request (comment of 17
October). Every suite ran every test. Five failed. Three failed only because the temporary folder's
spelling differed in case from the disk, one was a Wingman test outside this change that passed alone, and
one is the Reclaim test that fails whenever TEMP is on D: (on clean main too). The re-run of only those
five passed four; the Reclaim one failed again, as expected. The fix above is re-gated with the Delivery
Lead's new rule (default gate plus `-Parked -Filter License`); the results are in the hand-back.

## Not findings, noted by the reviewer

- `remove`, `sign_in_target`, `finish_sign_in` and `store_refreshed_key` call `recover()`, not
  `_settle()`. Agreed, no change: in the held-twice state each of them gives the right answer (the active
  entry is found first), and the next listing heals.
- `LicenseKeyHolderLock` decides which Director runs the tool; the Python store lock serialises the
  writes. Agreed, as built.
- `from_journal` parsing the journal's time. Agreed, no change.
