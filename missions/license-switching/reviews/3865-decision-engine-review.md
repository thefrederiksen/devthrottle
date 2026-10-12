# Review - pull request 3865: the Gateway decision engine and forecast

Reviewed at head `0411343cf`, diffed against its merge base `54fd5da0d` with origin/main. Read against
MISSION.md sections 4, 5 ("Gateway") and 7, contract.md sections 5 (including 5.2 and 5.7, which is only
on origin/main) and 7, and the pull request description.

## Verdict: changes needed

The rule itself is built correctly: 97% of either window triggers, a stale reading never triggers and is
never a target, stale and mid-switch machines are left out of the target choice and the switch (the
previous reviewer's first worry is unfounded for `Decide` and `DecideManual`), the order is lowest weekly,
then lowest 5-hour, then ordinal email, every switchable machine on the tired account switches, and the bar
holds until the triggering window resets and lifts after. The license tests pass (52 of 52, run here).

One defect breaks "exactly one owner email per exhaustion event", and the tests do not pin the threshold at
the boundary on the weekly window (the previous reviewer's second worry is confirmed).

## How the findings were checked

Out of tree only: the three source files and two test files were copied into a scratch test project that
references the built `CcDirector.Core.dll`. Probes and mutations ran there; nothing in this worktree was
edited except this file.

- Boundary probes, both windows: 96.99 does nothing and 97.0 switches, on the 5-hour and on the weekly
  window. The CODE is right at the boundary.
- Mutations run against the pull request's own 52 tests:

| Mutation | Result |
|---|---|
| Weekly trigger `>=` changed to `>` (`TiredWindows`) | all 52 pass - survives |
| Target room check on the 5-hour window `<` changed to `<=` (`HasRoom`) | all 52 pass - survives |
| Exhaustion no longer closed by a fresh reading under 97% | all 52 pass - survives |
| Exhaustion no longer closed when no machine is on the account | 1 fails - caught |
| Ordinal email tie-break removed | 1 fails - caught |

## Findings

### 1. A reading taken before a reset re-opens the exhaustion event and emails the owner every minute - must fix

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:340`, `:96-104`, `:327-335`, `:350-351`

The event closes the moment `now >= EndsAtUtc` (the window's reset time). But the freshest reading of the
account was, at that moment, taken BEFORE the reset: it is up to 6 minutes old and is not stale, still
shows 97% or more, and carries the reset time that has just passed. On the same tick `TiredWindows` counts
that window as tired, no target exists (nothing changed), the account is no longer in `stillOpen`, so a new
event opens and a second email is sent. The new event's `EndsAtUtc` is `ExhaustionEnd`, the maximum reset
time, which is that same past time - so the next tick closes it and opens another, with another email,
every tick until a reading taken after the reset arrives.

Probe: reset one minute ago, a fresh pre-reset reading of 99%, an open event ending at the reset, three
ticks one minute apart - 3 emails, one per tick. Expected: none (the window has reset; this is not a new
event).

Why it matters: the owner's rule is one email per exhaustion event, and the email itself says "This is the
only email for this event" (`:380`). With a 2-minute report interval and a provider read that can fail and
keep the old numbers for up to 6 minutes (contract 2.2, 5.2), this is two to six duplicate emails at every
reset of every exhausted account.

The existing test `Decide_ExhaustionAfterItsWindowResets_ClosesAndANewEventEmailsAgain`
(`LicenseDecisionEngineTests.cs:150-163`) encodes the behaviour with an inconsistent fixture: the open
event ended a minute ago, yet the reading still says the window resets in two hours. A real reading taken
before the reset carries the reset that just passed.

Fix direction: a window whose `ResetsAtUtc` is at or before now is not tired - its numbers are from before
the reset and say nothing about now (the same rule belongs in the trigger path, where a pre-reset reading
can otherwise trigger a switch and record a bar that is already over). Then add a test with a pre-reset
reading carrying the past reset time, over several ticks, asserting no email; and keep a test for a
genuinely new event (a reading taken after the reset that is again at 97% with no target emails once).

### 2. The weekly window is not tested at exactly 97% - must fix

`src/CcDirector.Gateway.UnitTests/Licenses/LicenseDecisionEngineTests.cs:66-78` (and `:34-47`)

`Decide_WeekAtThreshold_Switches` uses 98.5%, and no `Decide` test puts the weekly window at exactly 97.
Changing the weekly trigger from `>=` to `>` (`LicenseDecisionEngine.cs:332`) passes all 52 tests. The
5-hour window is tested at 97.0 and that mutation is caught. "97% on the week switches" is a named case of
MISSION.md section 7, so it must fail when the weekly boundary moves.

Also `Decide_UnderThreshold_DoesNothing` uses 96.9 on both windows; the boundary the Tech Lead asked for is
96.99 does nothing, 97.0 triggers, on each window separately. A theory with four rows (96.99/97.0 on the
5-hour, 96.99/97.0 on the week, the other window low) covers both and is what the probe here ran.

### 3. The target room check is not tested at exactly 97% on the 5-hour window - should fix

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:220-225`; tests `:114-131`, `:477-487`

A candidate at exactly 97.0 on its 5-hour window must not be chosen. Only the weekly side is tested at 97
(`DecideManual_TargetAtThreshold_IsRefused`); the 5-hour candidates are tested at 98 and 97.5, so `<`
changed to `<=` on the 5-hour room check passes everything. Add a candidate at 97.0 on the 5-hour window
(and 96.99, which must be chosen).

### 4. Closing an exhaustion on a fresh reading under 97% is untested - should fix

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:344-347`

One of the three ways an event ends (the pull request description names it) has no test: replacing the
condition with `false` passes all 52 tests. It needs a test that closes the event on a fresh reading under
97% on both windows before `EndsAtUtc`, and one that does NOT close it on a stale reading under 97%.

Related note, not a separate defect: if a provider reading ever wobbles across 97% (97.0, 96.9, 97.0), each
dip closes the event and the next rise opens a new one with a new email. Anthropic's windows do not
normally fall before their reset, so this is unlikely, but it is the same "one email per event" promise -
worth a sentence in the test or the code saying why no margin is needed.

### 5. The forecast's next-switch target can disagree with the engine - should fix

`src/CcDirector.Gateway/Licenses/LicenseForecaster.cs:127`; `LicenseDecisionEngine.cs:189-190`

The forecast calls `ChooseTarget` with `MachinesOn(snapshot, source)` - every machine on the account,
stale and mid-switch ones included - while `Decide` and `DecideManual` pass only the switchable machines.
Probe: Windows holds a spare for B, a stale Mac on the same account holds none; the engine switches Windows
to B, the forecast says the next switch has no target (which the page will read as "the owner will get the
no-room email"). The `ChooseTarget` summary says it is shared "so all three agree"; they do not. Either the
forecast uses the same switchable-machine split (make `SplitMachines` public or add a public overload that
applies it), or the page must say the forecast counts every machine - the first is simpler and matches the
summary.

### 6. The constants and read-error words duplicate a file that now exists on main - should fix

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:27-55`, `LicenseForecaster.cs:97`,
`LicenseDecisionModel.cs:11-28`

The branch is cut from `54fd5da0d`, before `LicenseMessages.cs` merged. origin/main now has it, with
`LicenseContract.ActiveHoldingStaleAfter`, `SpareHoldingStaleAfter`, `MachineStaleAfter`, `SwitchNotAfter`,
`SignInExpiryWarning`, `LicenseReadErrors.Unauthorized` / `RefreshFailed`, and `LicenseSwitchReasons`.
Contract section 7 says these live in one place "so the two sides cannot drift". The pull request defers
this to the wiring step; since the file is already on main, rebase and use it now rather than merge a
second copy. The window words (`five-hour`, `seven-day`) are not in the contract and can stay here.

### 7. Switch now allowing a barred named target - note (agree with the choice)

`src/CcDirector.Gateway/Licenses/LicenseDecisionEngine.cs:132-135`, test `:436-447`

Right as built. The bar exists to stop the automatic choice bouncing straight back to the account that just
tired; a named target still has to be fresh, under 97% on both windows, held usable by every machine and
not expiring, so a person cannot use it to move machines onto an account with no room. The Cockpit should
show the bar next to the account so the person naming it knows. Taking an account rather than a machine is
also right under decision (a).

### 8. A pre-reset reading also distorts the forecast - note

`src/CcDirector.Gateway/Licenses/LicenseForecaster.cs:203-204`

Same root as finding 1: a fresh reading at 97% or more whose `ResetsAtUtc` has passed is forecast as
"tired now". Fixing finding 1 with a shared "this window has reset" rule should cover it.

### 9. Smaller points - note

- `LicenseDecisionModel.cs:86-88` says an event "runs from the moment the account reached the threshold";
  `:92` and the code say it starts on the tick that found no target. Make the summary match.
- `LicenseDecisionEngine.cs:368`: the no-room email lists only the switchable machines; a stale or
  mid-switch machine on the same account is on it too and will also stop. List skipped machines with why.
- `LicenseDecisionEngine.cs:259-260` and `:233`: `?? double.MaxValue` and `?? ""` cannot be reached (the
  filter before them guarantees a value). Harmless, but under the no-fallback rule an unreachable default
  reads as if the case could happen; `!` or a direct value says what is true.
- Logging (CLAUDE.md rule 2): `Decide`, `DecideManual` and `Forecast` log entry and exit; the public
  predicates (`IsReadingStale`, `HasRoom`, `IsMachineStale`, `AccountsInUse`, `MachinesOn`, `ChooseTarget`,
  `RatePerHour`, `SignInVerdict`) do not. Logging pure predicates called in loops would flood the log, so
  this is a sensible exception, but it should be said in the class summary so the next reviewer does not
  flag it.
- `LicenseForecaster.cs:156`: any decrease, however small, is taken as a reset. If the provider ever
  reports rounding noise (50.0 then 49.9), the rate restarts. Fine for now; worth a comment.
- Plain English is good throughout; the email text and explanations read well.

## Checked and found right

- Threshold at 97.0 on both windows, `>=` to trigger and `<` for room (probed at 96.99 and 97.0).
- Stale reading of the account in use never triggers; a stale candidate is never a target; never-read is
  stale; 6 minutes active, 30 minutes spare, at the exact limits.
- Stale, open-switch and switch-in-progress machines are skipped, listed in `Skipped`, and take no part in
  the target choice in `Decide` and `DecideManual`; the others still switch.
- A spare whose last read was `unauthorized` or `refresh-failed` is not a target; a spare whose sign-in
  expires at or before now plus 10 minutes is not a target (contract 5.7, tested at the exact limit); a
  null expiry does not block.
- Bar recorded per triggering window until its reset (full window length when no reset time is given), not
  recorded twice, not chosen before the reset and chosen after.
- A second tick during an open event sends no second email (true while the reset has not passed - see
  finding 1 for when it has).
- Forecast: rate since the last drop, stale reading forecasts nothing, a window resetting before 97% is not
  expected to reach it, expiry verdicts at their limits.
