# Re-review - pull request 3865: the Gateway decision engine and forecast (round 2)

Reviewed at `7ca0df853` (rebased onto main), against round 1 (`3865-decision-engine-review.md`), the
answers (`3865-answers.md`), MISSION.md sections 4, 5 and 7, and contract.md sections 5 and 7.

## Verdict: approve

All nine round 1 findings are answered, and the fixes are right. The two must-fix findings are fixed
and their mutations are now caught. A walk of one-minute ticks across a reset sends one email before
the reset, none at the reset, and exactly one for a real new exhaustion after it. One new
should-fix finding (below) is in the same area as finding 1. It is narrower, and it can go in this
pull request or the wiring step, as the Tech Lead decides. Nothing new blocks the merge.

## How it was checked

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` in this
  worktree: 67 passed, 0 failed.
- Out of tree, as in round 1: the three source files and both test files were copied into a scratch
  test project outside the repository, which references the built `CcDirector.Core.dll` and
  `CcDirector.Gateway.Contracts.dll`. Baseline there: 67 passed. I applied every mutation and probe
  there. I edited nothing tracked in this worktree.

| Mutation (scratch copy) | Result |
|---|---|
| Weekly trigger `>=` changed to `>` (week only) | caught: 2 failed (`Decide_WeekAtThreshold_Switches`, boundary theory row week 97) |
| 5-hour trigger `>=` changed to `>` (5-hour only) | caught: 20 failed |
| Room check `<` changed to `<=` on the 5-hour window | caught: 1 failed (`Decide_CandidateRoomBoundary...(97, 10, False)`) |
| Room check `<` changed to `<=` on the weekly window | caught: 2 failed |
| Event no longer closed by a fresh reading under 97% | caught: 1 failed (`..._ClosesBeforeItsEnd`) |
| Event closed by a STALE reading under 97% too | caught: 1 failed (`..._StaleReadingUnderThreshold_StaysOpen`) |
| A window whose reset has passed still counted as tired | caught: 2 failed (both pre-reset tests) |
| Forecast passes every machine instead of the switchable ones | caught: 1 failed |
| Event no longer closed when no machine is on the account | caught: 1 failed |
| Ordinal email tie-break removed | caught: 1 failed |
| Event no longer closed by time | caught: 1 failed |
| `IsTired` reset check `reset > now` changed to `>=` | **survives** - new finding 2 |
| Forecast's "reset has passed" guard removed (`LicenseForecaster.cs:204`) | survives - equivalent, see new finding 3 |
| Restored | 67 passed |

Probes (scratch tests, ticks one minute apart, the 5-hour window resetting at R):

- **Across a reset.** A at 99% before R, no target. The provider read fails after R, so the last
  pre-reset reading (99%, reset R) stays fresh until R+5. From R+6, post-reset readings show 20%. At
  R+20, A reaches 97.5% of the new window, again with no target. Result: one email at the start,
  the event closed at R, **no event and no email from R to R+5**, one email at R+20, none after. Total
  2, which is correct.
- **A tick exactly at R.** The event closes and does not re-open. One email in total, which is
  correct.
- **A second window tires during the event**, and **a later reset time for the same window**: see new
  finding 1.

## Round 1 findings

| # | Round 1 | Verdict now |
|---|---|---|
| 1 | Pre-reset reading re-opens the event every minute (must fix) | **Fixed.** `IsTired` (`LicenseDecisionEngine.cs:255-259`) is the one check used by the trigger, the close, the reason a candidate is not chosen, and the forecast. A pre-reset reading cannot re-open an event or trigger a switch. It cannot record a bar that has already passed: `until` comes from a reset that is still ahead, `:103`. In the forecast it is no longer "tired now". The tick walk above shows it end to end. A real new exhaustion after the reset still sends exactly one email (`Decide_ReadingTakenAfterTheResetAgainAtThreshold_IsANewEventWithOneEmail`, and the walk). The bar test's inconsistent fixture is corrected as well. |
| 2 | Weekly window not tested at exactly 97% (must fix) | **Fixed.** Four-row boundary theory (`LicenseDecisionEngineTests.cs:81-95`); week-only `>` mutation caught. |
| 3 | Room check not tested at 97% on the 5-hour window | **Fixed.** `Decide_CandidateRoomBoundary_OnEachWindowSeparately` (`:131-145`); the mutation is caught on both windows. |
| 4 | Close on a fresh reading under 97% untested | **Fixed.** A fresh reading closes the event and a stale one does not (`:254-280`). Both mutations are caught. The comment about readings that wobble across 97% (`:366-368`) is adequate. |
| 5 | Forecast target can disagree with the engine | **Fixed.** `SwitchableMachines` (`:245-249`) uses the same `SplitMachines` as the tick and Switch now. The forecast uses it (`LicenseForecaster.cs:126`). The round 1 probe is now a test, and the mutation is caught. |
| 6 | Constants duplicated | **Fixed.** The staleness limits, `SwitchNotAfter`, `SignInExpiryWarning`, the read errors and the switch reasons all come from `LicenseMessages.cs`. A search of the license files finds no second copy. Only the window lengths and window words remain local, as agreed. `LicenseDecisionReasons` is gone. |
| 7 | Barred named target allowed | Agreed in round 1. Nothing to do. |
| 8 | Pre-reset reading distorts the forecast | **Fixed.** `Forecast_ReadingTakenBeforeAPassedReset_IsNotTiredNow`. See new finding 3 on the guard. |
| 9 | Smaller points | **All answered.** On the declined form: the explicit check is correct. `WhyNotTarget` returns null only after `HasRoom` is true, and `HasRoom` requires both windows to be present. So the pattern at `:273-275` always matches and its `throw` can only fire on a real defect. That is the honest form, and `docs/CodingStyle.md` does forbid `!`. `AccountsInUse` with `OfType<string>()` has no default at all. The summary, the skipped machines in the email, the logging note and the rate comment are all in place. |

## New findings

### 1. The event closes on its STORED end even while a fresh reading still shows a tired window - should fix (low)

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:362-363` (close), `:86-91` and `:374-375` (open)

The event closes on `now >= EndsAtUtc`. `EndsAtUtc` is fixed when the event opens, from the windows
that were tired then. The trigger reads the reset time from the current reading. When the current
reading has a tired window whose reset is still ahead and later than the stored end, the event closes
at the stored end and, on the same tick, opens a new one with a second email. The account had no room
at any point between the two. Two ways this happens, both probed:

- **A second window tires during the event.** The event opens on the 5-hour window (reset at
  +10 minutes), and the week reaches 98% at +3 minutes. At +10 the event closes and a new one opens on
  the week. Result: 2 emails. The second one does say something new: the account is now out until the
  weekly reset, not the 5-hour one. So this case may be acceptable. But the owner experiences it as
  two emails for one continuous outage, and the first email's "This is the only email for this event"
  turns out to be untrue.
- **The provider reports a later reset time for the same window than the one stored.** With 90
  seconds of drift, the result is 2 emails, one of them a duplicate. Whether Anthropic's `resets_at`
  ever moves later for the same window is not established here, and I have not observed it. If it
  does, even by seconds, a tick that lands inside the gap sends a duplicate.

Fix direction: close by time only when no fresh reading shows a tired window, so the time-based close
becomes `now >= EndsAtUtc && !(fresh && TiredWindows(reading).Count > 0)`. An event that is still
genuinely tired then stays open. To send a second email when the cause changes to the week, make that
a deliberate rule and test it. Either way, add a test for each of the two cases above, so that
whichever behaviour is chosen is pinned.

### 2. The reset boundary in `IsTired` is not pinned - note

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:258`

Changing `reset > nowUtc` to `reset >= nowUtc` passes all 67 tests. The strict form is the right one:
my probe of a tick landing exactly on the reset shows it lines up with the close's `now >= EndsAtUtc`.
With `>=`, that tick would close the event, re-open it and send a duplicate email. Add one row with
the reset equal to now (not tired, no switch, no email), so the boundary that finding 1 depends on
cannot move unnoticed.

### 3. The forecast's "reset has passed" guard is redundant - note

`src/CcDirector.Gateway/Licenses/LicenseForecaster.cs:204-205`

Removing it changes no result. At 97% or more, `IsTired` is already false for a passed reset. Under
97%, the later check `reset <= at` already gives null, because a passed reset is before `at`, which is
at least now. This is not a defect. It is a readable statement of intent, but the test named for it
does not depend on it. Keep it with that said in the comment, or drop it.

### 4. Switch now's "target full" message does not cover a pre-reset reading - note

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:163-165`

A named target whose fresh reading was taken before a passed reset, at 97% or more, is refused with
"at or over 97% on a window, or a window is unknown". Its use now is not known. The no-room email
already has the right words for this (`:299`). Reusing `WhyNotTarget`'s reason here would make the
two agree. The refusal itself is correct.

### 5. Test literals for the read errors - note

`src/CcDirector.Gateway.UnitTests/Licenses/LicenseDecisionEngineTests.cs:378-379`

`[InlineData("unauthorized")]` and `[InlineData("refresh-failed")]` spell out values that are `const`
in `LicenseReadErrors`. Using `LicenseReadErrors.Unauthorized` and `LicenseReadErrors.RefreshFailed`
would keep the tests on the one copy too. Trivial.

## Nothing else found broken

The changes since round 1 do not alter the rule elsewhere. The threshold, the target order, the
staleness limits, the skipped machines, the bars, the sign-in expiry and Switch now all behave as
round 1 found them. Those tests are still green, and their mutations are still caught.
