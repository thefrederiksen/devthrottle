# Answers to the review of pull request 4172

The review is `review-4172.md`, beside this file. It was made at `f0603e75b`.

## Finding 1 - a silent machine whose Director comes back refused was alarmed twice, and one alarm was false: ACCEPTED, fixed

The reviewer is right. `WatchSilence` ended a silent row only on an accepted report. The rule "a refused machine is
never also called silent" was applied only before a silent row was opened. So when the silence came first and the
refusal after it, both rows stayed open, and the owner was told to start a Director that was running.

**The fix is the reviewer's second option, so the row, the page and the alarm agree.** When a silent row is open and
the machine now has an open refusal (`picture.RefusalOf(machine)`, the same rule the page uses), the minute check ends
the silent row with the new `LicenseStore.EndSilenceForRefusal`. It writes no `reporting-again` row, because no report
was accepted. The next raise then has only the refusal, so the silent alarm closes and the refused one opens in the
same minute. When a report is later accepted, the refusal ends as before. The alarm class needs no special case.

**The test** is `ASilentMachineWhoseDirectorComesBackRefused_IsNoLongerSilent_OnlyRefused`. It follows the order the
reviewer reproduced: a good report; the minute check past the dead limit; two held-twice reports a minute apart, each
followed by a minute check and a raise. It asserts that no silent alarm and no open silent row are left, that the
silent row is Ended, that there is no `reporting-again` row, and that there is one refused alarm. An accepted report
then leaves no alarm open. It sits beside 4113's test for the other order.

The API document's `machine-silent` paragraph and contract section 9.7 now say this.

## Finding 2 - the silent sentences had no length guard, and one try covered both kinds: ACCEPTED, fixed

The reviewer is right on both points.

- **The sentences.** The silent problem and action are now guarded the way the refused problem is. When the label
  would take the action past 400 characters, the action is "Start the Director on {label}." That is one action, and
  it names the label once. When the label would take the problem past 400 characters, the problem is "A machine has
  sent no report since {time}, so it is not switched; the Licenses page names it." The ingest's 128-character limit
  on a name and on a user keeps the label at 259 characters at most, so both shorter forms fit.
- **One try.** `RaiseAsync` now raises each kind in its own try (`RaiseKindAsync`). A kind whose raise fails is
  logged with its kind; the other kind is still raised; then the failures are thrown together, so the minute check
  still logs that this tenant's raise failed. A cancellation is not caught. The return value is now
  `(LicenseAlarmRaised? Refused, LicenseAlarmRaised? Silent)`, and nothing reads it but the tests.

**The tests:**
- `TwoSilentMachinesWithTheLongestNames_AreBothAlarmed_InSentencesAnAlarmHolds` uses the real path, with the
  reviewer's case. Two Directors report two machines with one 128-character name and two 128-character users, and
  both go silent. Both alarms open, every sentence fits, and the action is the short one.
- `ARefusedRaiseThatFails_StillRaisesTheSilentKind_AndThenFails`: one machine goes silent, and a refused row whose
  name is 400 characters long is written straight into the log. No real report can produce that row; it stands in
  for a refused raise that fails. The raise throws, and the silent alarm is open anyway.

## Finding 3 - no machine can be removed, so a retired or renamed machine's silent alarm never closes: the Tech Lead's decision

The Tech Lead decided that this belongs in a separate, small pull request, the next one. It will add a "Forget this
machine" action for the owner only on the Licenses page, made of a Gateway route and a Cockpit button, with the fold
deciding when the button is offered. Forgetting a machine ends its open health rows, so its alarms close, and removes
its row. A machine that reports again simply comes back. There is no automatic age-out. That pull request is not part
of 4172.

## Proof the new tests watch the fixes

I committed the fixes first, then put back the product code from `f0603e75b` (`LicenseSwitchService.cs` and
`LicenseMachineHealthAlarm.cs`). With the old code, all three new tests failed and the seven earlier ones passed. With
the fixes restored and rebuilt, all ten passed. The whole `CcDirector.Gateway.Tests.Licenses` namespace then ran
1077 tests, and all passed.
