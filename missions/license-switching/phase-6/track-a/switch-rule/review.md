# Review of A2 - per-machine switch and the real reason (pull request 4105, branch license-switching-a2-per-machine-switch, commit a24a4e908)

Reviewer: a separate review session (License Switching - Reviewer - A2 per-machine switch), 17 October 2026.

## Scope

What I read:
- The whole diff `origin/main...origin/license-switching-a2-per-machine-switch` (15 files), and the complete
  new `LicenseDecisionEngine.cs` on the branch rather than only its hunks, because the rule lives there.
- The display fold's `ApplySwitchNow`, `ApplyStatus`, `NextSwitchText`, `AccountForecast`, `MachineForecast`
  and `SwitchNowAccepted`/`SwitchNowText` on the branch, and `LicenseForecaster.cs`.
- Who consumes the engine's answers: `LicenseSwitchService.cs` (the tick, `SwitchNowAsync`), `LicenseEndpoints.cs`,
  `LicenseSnapshotSource.cs` (how spares are built: non-active holdings only), and the Cockpit's `LicensesView.tsx`
  (what the Switch now dialog sends).
- All five test files touched, the new `LicensePerMachineSwitchTests.cs` in full, the Cockpit fixture and test.
- MISSION.md section 5, contract 5.7, cockpit-api.md, the phase README, the commit message, the pull request body,
  the Developer brief, the shared phase-6 brief, and MISSION.md section 5 as it stands on origin/main.

What I ran: `git` only (fetch, diff, show, grep). A sweep of every added line for real machine names, emails,
addresses and attribution words. I did not run `dotnet test`: this worktree is the Developer's, a build here can
collide with the Developer's own build, and the Licenses tests live in the parked `Gateway.UnitTests` suite; none
of the findings below depends on a run. The pull request's own counts (848 Licenses tests, 81 Cockpit tests) are
the Developer's claim, not verified by me.

What I could not reach: the Delivery Lead mandate and the 16 October report in the internal repository's
scratchpad (not needed for this review; the brief and MISSION.md carry the ruling).

Checked and found clean:
- No real machine, account or person names in any added line. The only "Claude" hits are the product's own
  words for a Claude subscription in a pre-existing email subject. No attribution in the commit or the pull request.
- Readiness is computed in exactly one place. `HoldsUsableSpare`, `SpareSignInExpiresTooSoon` and `ChooseTarget`
  have no remaining references anywhere in `src`, `apps` or `packages`. The fold's account status (`ApplyStatus`)
  reads expiry verdicts for the card's status line, which is not a readiness ruling.
- The client decides nothing: the Cockpit shows `switchNowText` verbatim and sends `switchNowTargetAccountEmail`
  as given; the only changed Cockpit lines are a regenerated fixture and the test that pins it.
- No fallback hides a failure: the words throw on a reason without text, a forecast without a target and without a
  refusal, and a machine with neither a switch nor a reason.
- Cross-tenant: the snapshot is still built per tenant; nothing in the change reads across tenants.
- The new rule's tests cannot pass on origin/main: the 16 October test asserts three switches and no email where
  main switched nothing and emailed; the readiness tests call an API main does not have. The tests that pin kept
  behaviour (true no-room, under the threshold) are meant to pass on both and do.
- Every changed existing test is named in the README with a reason that matches what the diff shows.
- MISSION.md section 5, contract 5.7 and cockpit-api.md describe what the code does, including the tie case.
- Stale and held machines keep their rules; the no-room event and its one email are reached only when
  `TargetsWithRoom` is empty (or every free machine is held, the pre-existing keeps-failing case); a machine left
  behind adds no bar, opens no event and is picked up by the same `Decide` path on a later tick (pinned by
  `Decide_ATickLater_...`).

## Findings

1. **The page's default Switch now does less than the automatic switch in the tie case, and the schedule on the
   same page says otherwise.** `src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:244-270` (`ApplySwitchNow`)
   and `apps/cockpit/src/licenses/LicensesView.tsx:168,186`.
   The card's sentence and pre-selected target are now the answer to `PreviewManual(source, NextTarget)`, that is
   the best account NAMED. With a target named, machines not ready for it stay. The forecast on the same page uses
   `PreviewManual(source, null)`, the automatic plan, where those machines go to the second-best account. So when
   account X (best) is held by machine-a only and account U by all three: the schedule says "machine-a then moves
   to X, the account the Gateway would choose today; machine-b and machine-c then move to U, the best account with
   room they are ready for", while the card says "Moves machine-a to X. machine-b is not switched: it holds no
   sign-in for X. machine-c is not switched: it holds no sign-in for X." The owner who presses the button as
   offered leaves two ready machines on an account at 97%, which is the exact thing the ruling of 16 October
   forbids, and the two sentences contradict each other. The README names this as a known limit, so it is a
   decision rather than an oversight, but the harm is to the owner's one button at the moment it matters.
   Before this change there was no gap, because the automatic verdict had one target. Fix that keeps the design:
   when the picked target is the Gateway's own choice, send no target (the route already accepts null and makes
   the automatic moves, per cockpit-api.md), and make the card's sentence describe the automatic plan grouped by
   target, as `NextSwitchText` already does. Add a fold test for the tie case; `Decide_TheTieCase_...` covers the
   engine only. Severity: **should-fix**.

2. **An accepted Switch now with no target and split moves answers with a false sentence.**
   `src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:294` (`target = decision.TargetAccountEmail`, so the
   result's single `TargetAccountEmail` is the last one written) and `LicenseDisplayFold.cs:339-349`
   (`SwitchNowText`: "{machines} are now on {target}" for every machine). In the tie case a null-target call moves
   machine-a to X and machine-b to U, and the answer says "machine-a and machine-b are now on U." cockpit-api.md
   now tells callers that a null target makes "each machine to the best account with room it is ready for", so
   the document invites the very call that gets the wrong sentence. The Cockpit never sends null today, so this
   reaches the command line and API callers only; the README lists it as a known limit. Fix: group the answer by
   each row's own target (the rows carry it), or leave `TargetAccountEmail` null when the moves have more than one
   target and say so in the sentence. Severity: **should-fix**.

3. **"it" in the switch log row can point at the wrong account.** `LicenseDecisionEngine.cs`, `ThresholdExplanation`
   (the `bestNamed ? "it" : r.TargetAccountEmail` choice). The parts are grouped in the order machines appear, so
   when the best-account group is not the last group before a left-behind machine, the sentence reads "machine-a
   switched to X, the account with the lowest weekly use that has room; machine-b switched to U, the best account
   with room it holds a sign-in for; machine-c holds no sign-in for it and switches as soon as it does" - and "it"
   now reads as U, while machine-c's reason is about X. A log row the owner reads says a wrong reason in that
   case. Fix: use "it" only when there is exactly one move group; otherwise name the account. Severity:
   **should-fix** (one condition).

4. **Note.** `nextSwitch.targetAccountEmail` names the best account while `nextSwitch.machines` lists every machine
   that moves, including the ones going to the second-best account. The document's words ("the machines that would
   actually move") are true and the sentence beside it is right, so nothing is false; only the pairing of the two
   fields is loose. No change needed unless A3 builds on that pairing.

5. **Note.** A Switch now naming an account that was never read now answers `target-stale` with "The latest reading
   of X is too old to show it has room." For an account nobody knows, "too old" is not the real reason (there is no
   reading). The page cannot send such a target, so only the raw route reaches it, and `WhyNotTarget` used the same
   words for a never-read account before this change. Worth a two-word distinction ("has never been read") when
   someone is next in that sentence.

6. **Note.** When an account has room and no machine is ready for it, the dialog marks that account "The Gateway's
   choice" on a radio that is disabled. The sentence beside it explains why, so it is coherent; recorded only so
   nobody reads it as a defect later.

7. **Note.** `BuildSwitchKeepsFailingEmail` still says "{best} has room for its machines, but the switch keeps
   failing" when the held machines are not ready for that account. Pre-existing shape, edge case; the alarm track
   will give the better sentence.

## Verdict

**Mergeable after findings 1 to 3 are answered** (fixed, or declined with the reason in the README, as the brief
allows). Nothing is must-fix: the rule against the brief and MISSION.md section 5 is right, the 16 October case
is reproduced and pinned, the no-room email is sent only when no account has room, readiness has one definition,
the repository stays free of real names and attribution, and the client rules nothing. Findings 1 and 2 are the
two places where a sentence the owner can read disagrees with what the Gateway does; both are small folds over
answers the engine already gives per machine.
