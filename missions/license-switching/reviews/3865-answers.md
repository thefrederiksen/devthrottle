# Answers - review of pull request 3865 (the Gateway decision engine and forecast)

The review: `3865-decision-engine-review.md` beside this file. The branch was rebased on origin/main
(1d8a4c2e2) first. Every finding is answered below. After the fixes the license tests are 67, all passing,
and each mutation the review found surviving is now caught (table at the end).

## 1. A reading taken before a reset re-opens the exhaustion event - accepted, fixed

The root was that a window whose reset time had passed still counted as tired. Now
`LicenseDecisionEngine.IsTired(window, now)` is true only when the window is at or over 97% AND its reset
time is unknown or still ahead. Every place that asks "is this window tired" goes through it: the trigger,
the exhaustion close, the reason a candidate was not chosen, and the forecast. So a pre-reset reading
neither triggers a switch (no bar that is already over), nor re-opens an event, nor sends an email. The
class summary says why.

A candidate's room check (`HasRoom`) is unchanged on purpose: a pre-reset reading under 97% is still true
after the reset, because use only falls at a reset. A pre-reset reading at 97% or more is not a target, and
the no-room email says "its latest reading was taken before a window reset, so its use now is not known yet".

The test with the inconsistent fixture is replaced by three:
- `Decide_PreResetReadingOverSeveralTicksAfterTheReset_SendsNoEmail` - the probe from the review: reset a
  minute ago, a fresh pre-reset reading at 99% carrying that reset, three ticks a minute apart: no email,
  no new event, the old event closed.
- `Decide_PreResetReadingAfterTheReset_DoesNotSwitchOrBar` - the same root in the trigger path.
- `Decide_ReadingTakenAfterTheResetAgainAtThreshold_IsANewEventWithOneEmail` - a genuinely new event
  (a post-reset reading at 97.5% of the new window, no target) emails once.

`Decide_AccountThatTriggered_IsNotChosenBeforeItsResetAndIsAfter` had the same inconsistent fixture in its
"after" half (a reading at the new time still carrying the old reset) and failed once the rule was right;
its post-reset readings now carry the new window's reset time.

## 2. The weekly window not tested at exactly 97% - accepted, fixed

`Decide_ThresholdBoundary_OnEachWindowSeparately` has the four rows asked for: 96.99 and 97.0 on the
5-hour window with the week low, and the same on the week. `Decide_WeekAtThreshold_Switches` now uses
exactly 97.0 and `Decide_UnderThreshold_DoesNothing` uses 96.99 on both.

## 3. The target room check not tested at 97% on the 5-hour window - accepted, fixed

`Decide_CandidateRoomBoundary_OnEachWindowSeparately`: a candidate at 97.0 is not chosen and one at 96.99
is, on each window separately.

## 4. Closing an exhaustion on a fresh reading under 97% untested - accepted, fixed

`Decide_ExhaustionWithAFreshReadingUnderThreshold_ClosesBeforeItsEnd` and
`Decide_ExhaustionWithAStaleReadingUnderThreshold_StaysOpen`. On the wobble note: the close carries a
comment saying why no margin is needed - the provider's use only falls at a reset, so a fall under 97% is
the end of the event.

## 5. The forecast's next-switch target could disagree with the engine - accepted, fixed

New public `LicenseDecisionEngine.SwitchableMachines(snapshot, account)` - the same split the tick and
Switch now use - and the forecast passes it to `ChooseTarget`. The `ChooseTarget` summary now says to pass
it. Test: `Forecast_NextSwitchTarget_IgnoresAStaleMachineTheWayTheEngineDoes` (the review's probe).

## 6. Constants duplicated from LicenseMessages.cs - accepted, fixed

Rebased on origin/main. The engine now uses `LicenseContract.ActiveHoldingStaleAfter`,
`SpareHoldingStaleAfter`, `MachineStaleAfter`, `SwitchNotAfter` and `SignInExpiryWarning`,
`LicenseReadErrors.Unauthorized` / `RefreshFailed`, and `LicenseSwitchReasons` (my
`LicenseDecisionReasons` is deleted). The window words and the window lengths stay here, as the review
says. One further alignment: contract 5.7 calls the null-expiry verdict "not known", so the forecast's word
is now `not-known` (was `unknown`).

## 7. Switch now allowing a barred named target - agreed, no change

Noted for phase 3: the Cockpit should show the bar next to the account so the person naming it knows.

## 8. A pre-reset reading distorts the forecast - accepted, fixed

Covered by finding 1's shared rule: a window whose reset has passed gets no expected time.
Test: `Forecast_ReadingTakenBeforeAPassedReset_IsNotTiredNow`.

## 9. Smaller points

- Exhaustion summary: accepted, fixed - it now says the event starts on the tick that finds no target, and
  names all three ways it ends.
- No-room email lists skipped machines: accepted, fixed - "Machines on it: windows, mac (not sent a switch:
  its last report is too old)." Test: `Decide_NoTarget_EmailNamesTheSkippedMachinesToo`.
- Unreachable defaults: accepted in substance, declined in form. The review suggests `!`, but
  `docs/CodingStyle.md` section 3 forbids the null-forgiving operator outside constructor fields. So
  `AccountsInUse` now filters with `OfType<string>()` (no default at all), and the ranking matches both
  windows with a pattern and throws, naming the account, if a candidate that passed the room check had
  none - which states what is true and fails loudly if it ever is not.
- Logging of the public predicates: accepted - the class summary now says `Decide` and `DecideManual` log
  entry and exit and the predicates do not, and why.
- Any decrease read as a reset in the forecast rate: accepted - commented where it is done.

## Mutations, after the fixes (each applied to the committed code, the 67 tests run, then restored)

| Mutation | Result |
|---|---|
| Tired check `>=` changed to `>` (now one shared check, both windows) | 23 failed, 44 passed, including both 97.0 rows of the boundary theory |
| Target room check on the 5-hour window `<` changed to `<=` | 1 failed: `Decide_CandidateRoomBoundary_OnEachWindowSeparately(97, 10, False)` |
| Exhaustion no longer closed by a fresh reading under 97% | 1 failed: `Decide_ExhaustionWithAFreshReadingUnderThreshold_ClosesBeforeItsEnd` |
| A window whose reset has passed still counted as tired | 2 failed: both pre-reset tests |
| Forecast passes every machine instead of the switchable ones | 1 failed: `Forecast_NextSwitchTarget_IgnoresAStaleMachineTheWayTheEngineDoes` |
| Restored | 67 passed |

# Round 2 - answers to `3865-re-review.md`

The re-review approved the pull request. The Tech Lead ruled on its first finding; every finding is answered
below. The branch is rebased on origin/main (2effe261f), which now carries the storage and ingest work, so the
license tests run here are 115: 70 for the engine and forecast, the rest for the store, ingest and read model.

## 1. The event closed on its stored end while a fresh reading was still tired - fixed, as ruled

Ruling: one email per CONTINUOUS outage. An event never closes by time while a fresh reading still shows a
tired window; its end moves to the later reset instead. The close is now judged in this order: no machine on
the account closes it; a fresh tired reading keeps it open and, when a tired window's reset is later than the
stored end, moves the end there (`LicenseDecisions.ExtendedExhaustions`, a new output the store applies);
the stored end having passed closes it; a fresh reading with no tired window closes it; a stale reading leaves
it open. The model's summary of an exhaustion event says the same.

Both cases are pinned:
- `Decide_WeekTiresDuringTheEvent_OneEmailForTheWholeOutage` - ticks at 0, 3, 10 and 11 minutes: the event
  opens on the 5-hour window, the week tires at 3, the 5-hour window resets at 10. One email, and the event's
  end is the weekly reset.
- `Decide_ResetReportedLaterThanTheEventsEnd_KeepsTheEventOpenAndMovesItsEnd` - the same window reported
  resetting 90 seconds after the stored end, ticked inside the gap: not closed, no email, end moved.

`Decide_ReadingTakenAfterTheResetAgainAtThreshold_IsANewEventWithOneEmail` relied on the old close-by-time and
now walks two ticks: a fresh reading at 20% closes the old event with no email, and a later reading at 97.5%
with no target opens a new one with one email - a new outage, as the ruling intends.

## 2. The reset boundary in `IsTired` not pinned - fixed

`Decide_ReadingWhoseResetIsExactlyNow_IsNotTired`: a reading at 99% whose reset equals now - no switch, no
bar, no email.

## 3. The forecast's "reset has passed" guard is redundant - kept, and said so

Kept for readability; its comment now says the checks below would give the same null, so it states intent
rather than changing a result.

## 4. Switch now's "target full" message - fixed

`NoRoomReason` is the one wording, used by both the no-room email and Switch now. A named target read before
a passed reset is refused with "its latest reading was taken before a window reset, so its use now is not
known yet".

## 5. Test literals for the read errors - fixed

`[InlineData(LicenseReadErrors.Unauthorized)]` and `[InlineData(LicenseReadErrors.RefreshFailed)]`.

## Mutations, round 2 (each applied to the committed code, the 115 license tests run, then restored)

| Mutation | Result |
|---|---|
| A fresh tired reading no longer keeps the event open | 3 failed: both new outage tests and `Decide_NoTargetOnTheSecondTick_SendsNoSecondEmail` |
| The event's end never moved later | 2 failed: both new outage tests |
| `IsTired` reset check `reset > now` changed to `>=` | 1 failed: `Decide_ReadingWhoseResetIsExactlyNow_IsNotTired` |
| Restored | 115 passed |
