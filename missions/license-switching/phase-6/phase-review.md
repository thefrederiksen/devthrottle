# License Switching phase 6 "it runs itself" - review of the phase as a whole

17 October 2026. Written by a separate review session opened by the Delivery Lead, in a worktree at origin/main
`f2f383f5a`. The reviewer read; it built nothing and fixed nothing.

## Scope

**What I read, all on origin/main.** The three track records (`track-a/report.md`, every `track-b/*.md`,
`track-c/report.md` and its part summaries), contract sections 9, 10, 11 and 12, the review of pull request 4140 and
its answers, and the code the records point at: `GatewayHost` (the once-a-minute license pass, lines 7036 to 7100,
and how the license services are built, lines 2485 to 2965), `LicenseReadinessService`, `LicenseReadinessFold`,
`LicenseAlarmService`, `LicenseApproverAlarm`, `LicenseApproverReadiness`, `LicenseApproverStore`,
`LicenseApproverRegistration` (assignments), `LicenseSignInService`, `LicenseDecisionEngine` (the per-machine plan,
`NextTarget`, `HasRoom`, `IsReadingStale`), `LicenseDisplayFold.RefusedMachine`, `LicenseSnapshotSource.ReadPicture`,
the Director's `LicenseApproverCheck`, `LicenseSwitching`, `LicenseSignInCoordinator`, `LicenseSwitchingSetting`, the
route `PUT /gateway/licenses/approvers`, the hub methods for approver assignments and reports, and the Python
`license_signin.py` (the page classifier, the approve script and the check script). I listed the 29 merged pull
requests numbered 4105 to 4181 whose title names License.

**What I ran.** Only reads of git and GitHub, and two sweeps over the diffs of the 23 phase 6 merge commits and over
`missions/license-switching/phase-6`: one for email addresses, one for private network addresses, the owner's names,
machine names and company domains. No build, no test run: the tracks' gate runs are their own testimony, and the
Delivery Lead's unfiltered parked run on main is the check for this phase.

**What I could not reach.** Nothing was run against a live Gateway or a live Director. The live proof on the owner's
machines, which each track report describes, is not replaced by this review.

## Verdict

One finding, medium, at the seam between Track A and Track C. The pieces otherwise connect end to end on main, the
dangerous actions are guarded as the brief asks, the automation is gated to tenants whose Directors report, and the
public repository carries nothing private from this phase.

## Finding 1 - a hand-set approver blocks the automatic sign-in for up to a day, and raises an alarm that says the wrong thing - MEDIUM

**Where the two tracks meet.** Track A's readiness starts no sign-in for an account whose approver "cannot approve
now", taking that word from Track C (`LicenseReadinessFold.cs` line 220, `LicenseApproverReadiness.WhyNotReady`).
Track C reads a record that was never checked, or not checked in 26 hours, as not able to approve
(`LicenseApproverStore.cs` lines 63 to 65) - "an absence is never read as healthy", which is right for the alarm. But
the only thing that ever checks a record is the approving machine's Director, once at process start and then once a
day (`LicenseApproverCheck.cs` lines 78, 138 and 177; the scheduler looks every ten minutes and finds nothing due).

**What is missing.** Nothing checks an approver when the owner sets one. The route stores the record with no check
time (`LicenseSignInEndpoints.cs` line 114, `LicenseApproverStore.cs` line 182; the last check is kept only when the
owner names the very machine, user and profile already stored, line 183) and sends nothing to any Director. There is
no command verb for a check: `LicenseContract` has switch, approve, approval-result and sign-in only. The Director's
check has no "my assignments changed" trigger; it compares nothing between runs.

**What happens, step by step.**

1. The account in use passes 80% and machine-d holds no sign-in for account-b. The 80% alarm opens and tells the owner
   the one thing to do: "Set an approver for account-b on the Licenses page."
2. The owner does exactly that, from the form Track C built.
3. Within a minute the approver alarm opens, with one email and one push: "The approver for account-b (browser
   profile ... on machine-a) has never been checked. Make sure the Director on machine-a is running with license
   switching on; it checks its approvers once a day." (`LicenseApproverAlarm.cs` lines 85 and 93,
   `LicenseApproverReadiness.cs` lines 92 to 95). The Director on machine-a IS running, with switching on. The
   instruction cannot be followed, and the only true part is "once a day".
4. The 80% alarm stays open, and its one thing to do becomes the same sentence. Readiness judges the gap
   `approver-not-ready` every minute and starts nothing. The sign-in that the whole piece exists to start does not
   start until machine-a's Director next runs its daily check, up to 24 hours later, or until someone restarts that
   Director.
5. If the account in use reaches 97% first, machine-d is left behind.

Meanwhile "Add a spare" from the Licenses page for the same machine and account works at once, through the same
approver: the approval relay looks the approver up by account and asks no question about its health
(`LicenseApprovalRelay.cs` line 273). So the record is good enough for the manual path and not for the automatic one.

**Two more shapes of the same gap.**

- **On deployment.** Every approver record the owner set before phase 6 has no check time (the fields were added in
  pull request 4109 with null defaults, `LicenseApproverStore.cs` lines 26 to 28). From the first minute after the
  Gateway deploys, each such account has an open approver alarm (one email and one push each) and the automatic sign-in
  is refused for it, until each approving machine's Director has updated to a build that carries the check and
  restarted, which is what runs the check at start.
- **An approving machine with license switching off.** The check is started only under the switching setting
  (`LicenseSwitching.cs` lines 111 and 121). I found no such requirement on the approve path (`LicenseApprovalExchange.cs`
  and `LicenseApprovalTunnel.cs` carry no reading of the setting). A machine that approves today with switching off is
  never checked: its account's alarm never closes, and its automatic sign-ins never start, while manual sign-ins through
  it keep working.

**Why it must change.** The owner's goal for the phase is "an automated step that does not need the user". The one
action the 80% alarm asks of the user produces a second alarm with an instruction that is false, and the automation then
waits on a clock nobody can see for up to a day. The earlier review of 4140 (its notes, `review-4140.md` line 156)
called the "has never been checked" alarm truthful, which it is as an alarm; nobody put it beside Track A's gate.

**What would close it (the Tech Lead's or the owner's call, not mine).** Any one of: the Gateway asks the approving
Director to check the record when the owner sets it (a new verb, or the Director re-reading its assignments on a
shorter cadence and running a check for any record it has not checked); or readiness treats an owner-set record that
was never checked as "unknown, try it" and lets the sign-in start, since the sign-in itself proves or disproves the
approver and writes the result into the Licenses log; and in either case the never-checked sentence should not tell the
owner to start a Director that is running. A test that fails today: the owner sets an approver for a gap at 80%, the
approving Director is connected and has not run its daily check, and the next readiness tick starts the sign-in.

## Observations - not findings

- **A dead machine at or above 80% gets two alarms.** Track B's `machine-silent` opens after ten minutes of silence,
  and Track A's `not-ready-at-80` opens for the same machine with "Start the Director on machine-a." Two emails and two
  pushes for one cause. Both are true to their contracts; the owner may want them folded later.
- **The 80% alarm reopens after each window reset while a gap persists.** It closes only on a fresh reading under 80%
  in both windows (`LicenseReadinessFold.AlarmConditions`); a 5-hour reset with the weekly limit under 80% closes it,
  and the climb back opens a new alarm with a new email. This is what contract section 11 says, and it reads as wanted.
- **The hand sign-in heal keeps a stale sign-in key on disk indefinitely.** The spare's folder is moved whole to
  `<store>/hand-sign-in-backups/` and "never deleted" (`license_store.py` lines 33 and 82). It is a move, never a copy,
  and the backup is outside `spares/`, so nothing lists or renews it. It is still a working refresh token in a folder no
  screen shows and no rule ages out. Low; the owner may accept it as the price of never losing a key.

## What I checked and found to hold

**The chain connects on main.** One minute pass (`GatewayHost.cs` lines 7055 to 7093) runs the switches, settles
unanswered sign-ins, judges readiness, then raises the refused and silent alarms; beside it, the renewal emails, the
alarm email retries and the approver alarm. All five alarm raisers are built on the one `LicenseAlarmService`
(`GatewayHost.cs` lines 2952 to 2963). Readiness -> `LicenseSignInService.Start` with the reasons `auto-spare` and
`auto-renewal`, which the Director only logs (`LicenseSignInCoordinator.cs` line 87; `IsKnown` and `IsRenewal` in
`LicenseMessages.cs` lines 396 and 399 accept them) -> the Director's sign-in asks the Gateway for approval -> the
relay finds the account's approver -> the Director sends a `changed` report when it finishes
(`LicenseSignInCoordinator.cs`, `RequestReport(Changed)`) -> the machine reads ready on the next judgement. The 80%
alarm -> `license_alarms` row, Licenses log row, one push, one email -> the banner on every Cockpit page and the phone
shell reads the open alarms -> it closes when the machine is ready, leaves the account, or the account reads fresh under
80%.

**Nothing switches a machine onto an account with no room.** An automatic or named target must have a fresh reading
and both windows under the threshold (`LicenseDecisionEngine.cs` lines 199 to 203 and 363 to 368), and a machine moves
only to an account it is ready for (`PlanMoves`). The tie and left-behind cases are pinned by the A2 tests named in
`switch-rule/README.md`.

**Nothing copies a sign-in key.** The active renewal writes the renewed key back into the same place it read it
(`active-renewal.md`, "exactly one copy"); the heal moves a folder; the sign-in command carries an identifier, an
email, a reason word and a time. `LicenseMessagesCarryNoKeyTests` still guards the contract.

**Nothing presses a claude.ai button it should not.** The daily check's browser script reads the page and presses
nothing (`license_signin.py` line 445 onwards; it has no click). The approve script clicks Authorize only after the
authorize URL's `login_hint` and the page's "Logged in as" both name the expected account (lines 563 to 573, 417).

**No alarm or email every minute.** A raise reconciles by identity (kind, machine, account); an alarm still true only
has its words updated (`LicenseAlarmService.cs` line 268); a failed email waits one minute, doubling to an hour (line
415); one raise and one email check per tenant at a time. The approver alarm is keyed on the account alone, and the
readiness alarm on the machine and the account in use, so a change of target account changes words, not identity.

**The 13 October ruling.** Readiness, the approver registration and the switch check all act only for a tenant whose
machines have reported (`LicenseReadinessService.cs` line 126). A Director reports only with `licenseSwitching.enabled`
exactly `true` in its config.json; nothing in the Director's screens, the command line or the Gateway sets it
(`LicenseSwitchingSetting.Set` has no caller outside tests). So automation is on exactly where the owner edited a file.

**The refused stand-in row cannot break the minute pass.** It has no active account (`LicenseDisplayFold.cs` lines 521
to 540) and the readiness fold skips such rows before it indexes the snapshot (`LicenseReadinessFold.cs` line 174).

**Public repository.** The sweeps over the 23 merge commits and the phase 6 record found no personal email, no
machine name, no company domain and no private address. The only addresses are `example.com` accounts and the
product's own `support@devthrottle.com` in the push sender.
