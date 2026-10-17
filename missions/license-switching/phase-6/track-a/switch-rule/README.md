# Phase 6, track A: the switch rule - every ready machine, at once, with the real reason

## Why

On 16 October 2026 one account was at 97% and another at 0%. Three machines held a sign-in for the
empty account; one did not. The old rule only accepted a target that EVERY machine on the tired
account held a usable sign-in for, so nothing switched at all, and the owner was emailed "No other
account has room" - which was false. The owner's ruling: "Switch every ready machine at once; the one
left behind keeps trying and switches the moment it is ready." And every message must say the real
cause.

## What changed

All of it is in the Gateway's pure decision code and the folds that put it into words. The switch
command, the Director and the store are unchanged; the Cockpit changes one line, sending no target for
the Gateway's own choice (review finding 1).

- **One definition of ready** - `LicenseDecisionEngine.Readiness(machine, account, now)`. A machine is
  ready for an account when its latest report shows a sign-in for it that was not refused and whose
  expiry, when known, comes after the switch command's not-after time (now plus 10 minutes). When not
  ready it says why, as a fact: `no-sign-in`, `sign-in-refused`, or `sign-in-expires-too-soon` with the
  expiry time. `NotReadyWhy` puts that into words ("holds no sign-in for account-b@example.com"). The
  old private checks `HoldsUsableSpare` and `SpareSignInExpiresTooSoon` are gone; nothing else works
  readiness out.
- **The account it would switch to next** - `TargetsWithRoom(snapshot, account)` (every account with
  room, best first) and `NextTarget(snapshot, account)` (the best). Judged on each account alone: not the
  account in use, a fresh reading, both windows under 97%, not barred. Best is the lowest weekly use,
  then the lowest 5-hour use, then the email in alphabetical order (the existing order). Machine
  sign-ins no longer decide whether an account has room. `ChooseTarget` is removed; the forecaster uses
  `NextTarget`.
- **Per machine at 97%** - each machine that can be sent a switch goes to the best account with room
  that it is ready for. A machine ready for none is left behind: not switched, blocking nobody, and
  switched on a later minute by the same rule the moment it is ready while the account is still at its
  limit. There is no special retry code. Stale and held machines keep their existing rules.
- **The tie case** (the best account held by few machines, the second best by all): per machine, the
  best account that machine is ready for. So nobody waits. Written into MISSION.md section 5.
- **The no-room event only when it is true** - only when `TargetsWithRoom` is empty. When an account has
  room and no machine is ready for it, there is no event and no email (the license alarm covers it);
  every machine is in the tick's skipped list with its reason.
- **The words** - the switch log row, the account card's Switch now sentence, the forecast's next-switch
  sentence and machine rows, and Switch now's answers all give the real reason. The forecast's next
  switch names the target whenever an account has room, and its machine list holds only the machines
  that would actually move.
- **Switch now** with a target moves the machines ready for it and lists each one it leaves, with its
  reason. With no target named it makes exactly the automatic moves. It is refused only when the target
  is the account in use, is stale, is full, or no machine is ready for it - and that refusal names every
  machine and why. The account's own state is now checked BEFORE any machine's sign-ins, so a target
  that is full is called full, not "not held".

## The sentences, as pinned by the tests

- Switch log, 16 October: "account-a@example.com reached 97% of its weekly limit; machine-a, machine-b
  and machine-c switched to account-b@example.com, the account with the lowest weekly use that has room;
  machine-d holds no sign-in for it and switches as soon as it does."
- Switch log, the left-behind machine a minute later: "account-a@example.com reached 97% of its weekly
  limit; machine-d switched to account-b@example.com, the account with the lowest weekly use that has
  room."
- Switch log, the tie case: "...; machine-a switched to account-b@example.com, the account with the
  lowest weekly use that has room; machine-b and machine-c switched to account-c@example.com, the best
  account with room they hold a sign-in for."
- Account card: "Moves machine-a, machine-b and machine-c to account-b@example.com. machine-d is not
  switched: it holds no sign-in for account-b@example.com."
- Account card, the tie case: "Moves machine-a to account-b@example.com; machine-b and machine-c to
  account-c@example.com, the best account with room they are ready for."
- Switch log, two accounts named and a machine left behind: "...; machine-b switched to
  account-c@example.com, the best account with room it holds a sign-in for; machine-c holds no sign-in
  for account-b@example.com and switches as soon as it does."
- Switch now with no target, moves to two accounts: "the-mac is now on account-c@example.com.
  the-windows-machine is now on account-b@example.com."
- Forecast: "account-a@example.com has reached 97% of its weekly limit; machine-a, machine-b and
  machine-c then move to account-b@example.com, the account the Gateway would choose today. machine-d
  would not move: it holds no sign-in for account-b@example.com."
- Room but nobody ready (card, Switch now refusal, forecast): "account-b@example.com has room;
  machine-a holds no sign-in for it."
- True no-room, Switch now: "No other account has room for the machines on account-a@example.com: for
  account-b@example.com, it is at 98% of its weekly limit." The owner email keeps its existing words.
- Switch now with a machine left: "Switch now: from account-a@example.com to account-c@example.com;
  WINDOWS (second) holds no sign-in for account-c@example.com, so it stays on account-a@example.com."

## The tests, in plain terms

New, in `src/CcDirector.Gateway.UnitTests/Licenses/LicensePerMachineSwitchTests.cs`:

- `Readiness_HoldsAUsableSignIn_IsReady` - a machine holding the sign-in is ready, with no reason.
- `Readiness_HoldsNoSignIn_IsNotReady_AndSaysSo` - no sign-in: not ready, reason "holds no sign-in".
- `Readiness_SignInRefused_IsNotReady_AndSaysSo` - a sign-in whose read was refused (both refusal codes)
  is not ready, and says it was refused rather than absent.
- `Readiness_SignInExpiringBeforeTheSwitchCouldFinish_IsNotReady_AndNamesTheExpiry` - expiring exactly at
  the command's not-after time is not ready and the reason names the time; one second later is ready.
- `Readiness_SignInWithNoKnownExpiry_IsReady` - an unknown expiry does not block (contract 5.7).
- `NextTarget_IsChosenOnTheAccountsMerits_EvenWhenOneMachineHoldsNoSignInForIt` - the 16 October picture
  names the empty account as next.
- `NextTarget_IsNull_OnlyWhenNoAccountHasRoom`.
- `Decide_SixteenOctober_...` - at 97% the three ready machines get switch decisions, the fourth does
  not and is skipped with its reason, no no-room event, no email, the weekly bar is recorded, and the
  log sentence is pinned.
- `Decide_ATickLater_...` - the same picture a minute later, the fourth machine now holding the sign-in
  and the other three already moved: it alone is switched, no new bar, no email.
- `Decide_UnderTheThreshold_NothingSwitches` - at 96.9% nothing is switched or skipped.
- `Decide_AnAccountHasRoomAndNoMachineIsReadyForItYet_...` - nothing switches, no event, no email, no
  bar, both machines skipped, and the re-send of a no-room email has nothing to write.
- `Decide_TheTieCase_...` - the best account goes to the one machine that holds it, the other two take
  the second best in the same tick.
- `Decide_TrulyNoRoom_...` - every other account tired or barred: one event, exactly one email with the
  existing subject and body words, and no second email on the next tick.
- `PreviewManual_SixteenOctober_...` - Switch now moves the three, lists the fourth with its reason, and
  the no-target answer makes the same moves.
- `PreviewManual_AnAccountHasRoomAndNoMachineIsReady_...` - refused with "account-b has room; machine-a
  holds no sign-in for it.", the same whether or not the target is named.
- `PreviewManual_TrulyNoRoom_...` - refused with each account's reason.

New, in `LicenseDisplayFoldTests.cs` (through the real store, as that file does):

- `SwitchNow_SixteenOctober_...` - the card's sentence and the automatic choice.
- `Forecast_SixteenOctober_...` - the next switch names the target, lists only the three machines that
  move, the sentence names machine-d and why, and machine-d's own row says why.
- `SwitchNowAndForecast_AnAccountHasRoomAndNoMachineIsReady_...` - the card and the schedule say the
  account has room and why nothing moves, never "No other account has room".

## Existing tests changed, and why

Each of these pinned the all-or-nothing rule or its words - the rule this work replaces.

- `Decide_MachineMissingTheSpare_BlocksThatTarget` became `..._GoesToTheBestAccountItIsReadyFor`: one
  machine's missing sign-in no longer moves every machine to the worse account.
- `Decide_NoTargetHeldByEveryMachine_EmailsAndSwitchesNothing` became
  `Decide_NoAccountHeldByEveryMachine_EachMachineMoves_AndNoOneIsToldThereIsNoRoom`: this was the
  16 October defect, pinned as correct.
- `Decide_SpareSignInExpiringWithinTheCommandLifetime_IsNotChosen` became `..._ForThatMachine`: the
  expiring machine goes elsewhere, the other keeps the best account.
- `DecideManual_TargetNotHeldOrIsTheSource_IsRefused` became
  `DecideManual_TargetNoMachineIsReadyForOrIsTheSource_IsRefused`: a target one machine holds is no
  longer refused, so the case now has no machine holding it.
- `DecideManual_ARefusalThatNamesAMachine_...` became `DecideManual_AMachineLeftOut_IsNamedWithItsUser_...`:
  the target is accepted for the ready machines and the one left out is named with its user.
- `DecideManual_AnExpiringSpare_...`: the refusal sentence is the new one, with the expiry time.
- `Decide_NoTarget_TheEmailSaysWhichUserOfASharedComputer_...` and `Decide_NoTarget_TheEmailNamesAMachineWithNoSpare_...`
  pinned a no-room email that is no longer sent (an account had room). They became a true no-room email
  test and `Decide_TwoUsersOfOneComputerGoingToDifferentAccounts_AreEachNamedWithTheirUser`.
- Display fold: three tests pinned "No other account has room for the machines on account-a." with no
  reason; they now pin the sentence with each account's reason. One forecast test expected no target
  while the only machine was mid-switch; an account had room, so the target is now named and the
  sentence still says why nothing would move.
- Switch now route and service: the refusal sentence is the new one; an unknown target is now
  `target-stale` before any machine is looked at (the account is judged first), so one test's code and
  the count of distinct codes changed; the shared-computer refusal test now uses a target no user holds.
- Cockpit: `fixtures.ts` and the overview example in `cockpit-api.md` were regenerated with the
  committed producer (`phase-3/overview-example/produce_overview_example.py`, then `copy_fixtures.py`).
  The one difference: account C's reason became "account-c@example.com has no room: it is at 99% of its
  5-hour window." - it had been "the-mac holds no usable spare", which hid that C was full. One Cockpit
  test pinned the old fixture sentence and now pins the new one.

## The failure cases covered

No sign-in, a refused sign-in, a sign-in expiring before the switch, an unknown expiry; a machine left
behind and switched a tick later; nobody ready while an account has room; no account has room at all;
the tie between two accounts; two users of one computer named apart; under the threshold.

## Known limits

- A target that is not the automatic choice shows no sentence of which machines it would leave before
  it is clicked (the target list has only `unavailableText`); the answer after the click lists them.
- The two limits this section first listed (the page's Switch now doing less than the automatic switch
  in the tie case, and the answer's single target) were review findings 1 and 2, and are fixed below.

## Proof

- `.\scripts\test-local.ps1` and `.\scripts\test-local.ps1 -Parked`: results in the pull request.
- The Licenses tests of `CcDirector.Gateway.UnitTests`, after the review fixes and the rebase onto main
  with the license alarm: 886 passed, 0 failed.
- The Cockpit Licenses web tests (`npx vitest run src/licenses` in `apps/cockpit`): 81 passed.

## Review findings

The review is committed beside this file as `review.md` (a separate review session, 17 October 2026).
Its verdict: mergeable once findings 1 to 3 are answered. All three are accepted and fixed, each with a
test that was run against the code before the fix and failed there (the revert proof: the four changed
code files put back to the first commit, the four new tests run - all four failed - and the files
restored).

1. **Accepted, fixed. The page's Switch now did less than the automatic switch in the tie case.** The
   card's sentence is now the AUTOMATIC answer (`PreviewManual` with no target), grouped by account:
   "Moves machine-a to account-b@example.com; machine-b and machine-c to account-c@example.com, the
   best account with room they are ready for." When the Gateway's choice is the one picked, the Cockpit
   sends NO target, so the button makes exactly those moves - the same ones the schedule on the page and
   the switch at 97% make. A target picked by hand is still sent as named. `switchNowTargetAccountEmail`
   is set only when a Switch now with no target would be accepted. cockpit-api.md sections 2 and 6 and
   MISSION.md section 5 say so. Tests: `SwitchNow_TheTieCase_TheCardDescribesTheAutomaticMoves_ToBothAccounts_AsTheScheduleDoes`
   (the card's sentence and the schedule's, pinned side by side); in the Cockpit, the confirmation test
   in `LicensesView.test.tsx` and the wire test in `LicensesViewWrites.test.tsx` now pin that the Gateway's
   choice is sent as `targetAccountEmail: null` (they pinned the named target before; that was the defect).
2. **Accepted, fixed. A Switch now with no target and split moves answered with a false sentence.** The
   answer's sentence is now grouped by each switch log row's own target ("the-mac is now on
   account-c@example.com. the-windows-machine is now on account-b@example.com."), and the result's single
   `targetAccountEmail` is the account only when every switch in the answer went to it, null otherwise.
   Test: `SwitchNow_WithNoTarget_ThatSendsMachinesToTwoAccounts_SaysWhereEachOneIs_AndNamesNoSingleTarget`
   (through the route: each machine sent to its own account, `targetAccountEmail` null, the sentence).
3. **Accepted, fixed. "it" in the switch log row could point at the wrong account.** "it" is now used only
   when exactly one account is named before it and that account is the best one; otherwise the account is
   named. Test: `Decide_TwoAccountsNamedAndAMachineLeftBehind_TheLogNamesTheAccountItsReasonIsAbout`
   ("...; machine-c holds no sign-in for account-b@example.com and switches as soon as it does.").
4. **Noted, no change.** `nextSwitch.targetAccountEmail` names the best account while `machines` lists
   every machine that moves, to either account; the sentence beside them is right. A3 is told here: do
   not pair the two fields to mean "these machines go to that account".
5. **Noted, no change in this pull request.** "too old to show it has room" for an account never read
   was the same words before this change and only the raw route can reach it; it is worth "has never been
   read" when that sentence is next touched.
6. **Noted, no change.** The radio is still marked "The Gateway's choice" and disabled when no machine is
   ready for the best account; nothing is pre-selected then, and the sentence beside it says why.
7. **Noted, no change.** The "switch keeps failing" email's "{best} has room for its machines" when the
   held machines are not ready for it is pre-existing and an edge case; the license alarm track gives the
   better sentence.
