# Issue #4748: a slow Director produced a false "Schedule failed" notice

## Root cause

When the Director took longer than 30 seconds to answer a scheduled start, `CronEngine` recorded the run as
`StartUnknown` with `ResultUtc = firedUtc`. `CronRunHistoryStore.FailedSince` returns every run with a result time
whose result is `StartUnknown` or `Problem`, so `ScheduleFailureNotifier` told the owner "Schedule failed" on its next
30-second scan, although the session started and reported ok minutes later. Nothing ever took the alarm back.

## What changed

- `CronEngine.cs`: a start whose outcome is not known is recorded with no result time. `FailedSince` does not return it,
  so it is not a failure. It becomes one only when `CronRunResultService.SettleUnknownStarts` lapses it at its window's
  close (never under 30 minutes) as `Problem` / `DidNotRun`, with a result time. If the session appears first, it is
  adopted and the run reports as usual.
- `ScheduleFailureWords.cs`: a lapsed start is told in the "it is not known whether this run started" words, not the
  generic did-not-run words.
- Recovery (`ScheduleFailureNotifier.cs`, `CronFailureNoticeStore`, `CronRunHistoryStore.FirstSuccessAfter`): a
  schedule whose owner was told it failed is told it recovered on the first successful run after its newest counted
  failure, on the same channels (Your inbox, fleet channel and webhook, phone push). Which recovery was told last is a
  row of cause `recovered` in the existing `cron_failure_notices` table - no schema change - so a restart neither
  forgets nor repeats it. A recovery is owed again only when a failure is counted after it. It is not told when the
  schedule's Notify is Off, or when the success is older than the scan's window (it came while no Gateway was watching,
  or before this change shipped); it is then recorded without being told.
- No swallowed failures. The previous seat answered an earlier review with eleven catch blocks inside helpers (`Claim`,
  push, each recovery leg, the recovery loop, three around `SaveChanges`, two in `Dispose`). All are removed. A failing
  claim, push or recovery notice now fails the scan; the scan loop's per-tick handler, the entry point, logs it and the
  next scan tries again. The recovery's inbox write goes first, since it is the only leg that can throw, so a retry sends
  nothing twice. The two per-channel catches in the failure path (fleet notice, Your inbox) were on main before this pull
  request and are unchanged.

## Tests, each seen red without the change and green with it

How red was seen: the named production file swapped for the older version, the test run, the file restored.

| Test | Red against | Red result |
|---|---|---|
| `ASlowDirectorIsNotAFailedScheduleTests` - a fire through the real `CronEngine` with a starter answering "outcome not known", then the real notifier | `CronEngine.cs` from origin/main | `Expected: 0, Actual: 1` notices |
| `AStartThatLapsesAtItsWindowsClose_IsToldInTheStartNotKnownWords` - lapsed through `CronRunHistoryStore.LapseStartUnknown` | `ScheduleFailureWords.cs` from origin/main | cause `other`, expected `start-not-known` |
| `ASuccessBeforeTheFailure_IsNotARecovery` | the previous commit's notifier and history store | failed |
| `ARecovery_IsToldOnce_AndStillToldAfterAGatewayRestart` | same | failed |
| `AScheduleThatFailsAgainAfterARecovery_IsToldOfTheNextRecoveryToo_ButNotOfTheOldSuccess` | same | failed |
| `ARecovery_IsNotTold_WhenTheOwnerSetNotifyToOffAfterTheFailure` | same | failed |
| `PushWords_ForARecovery_SayItRecovered_NotThatItFailed` | same | failed |
| `ARecoveryNoticeThatFails_FailsTheScan_AndTheNextScanTellsTheOwner` | same | `Assert.Throws() Failure: No exception was thrown` |

For the previous-commit runs the old notifier was given only the new `RecoveredReason` constant, so the test file
compiled. `AStartUnknownRun_IsNotToldImmediately...` (from the previous seat) builds its record by hand with no result
time and passes on main too; it is kept as a notifier-level check and is not counted as proof.

## The gate

`.\scripts\test-local.ps1 -Parked` at commit f87771c2b (rebased on origin/main), every suite outcome=Completed, 0 failed:
Core.UnitTests 3650, Avalonia.Tests 1272, Engine.Tests 85, HostedAgent.Tests 85, Launcher.Tests 234,
Terminal.Avalonia.Tests 111, Reclaim.Tests 307, DeliveryQualification.Tests 151, RuleHook.Tests 70,
cc-director-setup.Tests 43, cc-director-setup-engine.Tests 1023 of 1029 (6 skipped), cc-director-setup-cli.Tests 78,
Gateway.Tests 426 + 482 + 439 of 442 (3 skipped) + 489, Core.Tests 4643 of 4661 (18 skipped), Gateway.UnitTests 17497.
TEMP was pointed at drive D for the run because drive C was nearly full. No web or Python code was touched.

## The separate review, and the answers

Reviewed in a separate session on a detached worktree of afb3f7ce4.

1. HIGH, recovery push titled "Schedule failed" - fixed: `PushWords` says "Schedule recovered" for a recovery; test added.
2. HIGH, a success before the failure counted as a recovery, and an old success was told again after a re-failure -
   fixed: the recovery is the first success strictly after the newest counted failure; two tests added.
3. MEDIUM, recovery forgotten on restart - fixed: kept in `cron_failure_notices` as a `recovered` row; test covers two
   restarts.
4. MEDIUM, the lapsed-start words branch untested - fixed: test drives `LapseStartUnknown`, red on main's words.
5. LOW, the hand-built start-unknown test proves nothing - agreed; not counted as proof (above).
6. LOW, a failed recovery inbox write re-sent the other channels - fixed: inbox first; the test asserts the counts.
7. LOW, recovery ignored Notify - fixed: not told when Notify is Off; test added.
8. LOW, stale documentation - the notifier's summary now says a not-yet-known start is not a failure and describes the
   recovery; the gate line's stray tab is gone. `GatewayAccountFailures.cs` still has words for a start-unknown row that
   `FailedSince` can no longer return; left as is, it is unreachable rather than wrong.
9. NOTE, naming - `SuccessRunsSince` replaced by `FirstSuccessAfter`, which returns one run in one account.

## Not proven

- End to end with a real slow Director, a real phone push and a real Your inbox message on the hosted Gateway.
- How the Schedule page and the phone show a recovery next to the earlier failure (the push for a recovery has its own
  tag, so it does not replace the failure notification on the phone).
- A recovery whose success came while no Gateway was running for more than 15 minutes is recorded but not told.
- The fleet channel and the push are relied on not to throw, as their contracts say; if one did after the inbox write,
  the retry would write the inbox message twice.
