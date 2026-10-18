# Track C, Developer C2 - summary for the track report

17 October 2026, by the Track C Developer for the approval-page check, the Director's daily approver check and the
proof of the monthly renewal through the standing approvers. Three pull requests, all merged to main, then this
summary:

| Pull request | What it did | Proof and review |
|---|---|---|
| 4116 | `license approve` and the new `license check-approver` read an approval page with one function, and the "reauth" page has its own word, `needs-fresh-sign-in` | `c2-approval-page-check.md`, `review-4116.md`, `review-4116-answers.md` |
| 4123 | The Director checks every approver record that names its machine once a day, and its own browser profiles when the machine offers them, and reports the result to the Gateway | `c2-daily-approver-check.md`, `review-4123.md`, `review-4123-answers.md`, `review-4123-round2.md`, `review-4123-round2-answers.md` |
| 4153 | Proof, against a real hosted Gateway, that the daily renewal completes through the approver with no person, including a machine that approves itself; and the one defect it found fixed: each relayed approval is now a row in the Licenses log | `c2-renewal-through-approvers.md`, `review-4153.md`, `review-4153-answers.md` |
| this one | This summary | - |

## What was built

**An approval page is read one way, everywhere (4116).** One Python function, `classify_page`, decides what a claude.ai
approval page shows: `can-approve`, `wrong-account`, `signed-out`, `needs-fresh-sign-in` or `check-failed`. The approval
and the check both run that same function inside the browser, so they cannot disagree about a page. Authorize is
pressed only on a page it calls `can-approve`. The reauth page that broke the 16 October sign-ins was read as "not
signed in"; it is now refused as `needs-fresh-sign-in`, with the command to fix it. The new
`cc-devthrottle license check-approver` opens a real approval page from a throwaway `claude auth login` in a temporary
folder, reads it, and then cancels the login, deletes the folder and puts the profile back. It never presses anything,
never types and never signs in. The new word joined the contract's renewal words (now nine), with its own sentence on
the Director, on the Licenses page and in the sign-in log.

**Every approver is checked daily, so one that cannot approve is found within a day (4123).** A new
`LicenseApproverCheck` on the Director runs once at start and then once a day, only while license switching is on and
only on the key holder. It asks the Gateway which approver records name this machine, runs `license check-approver`
for each, and, when the new `licenseSwitching.offerApprovers` setting is on, also checks each of the machine's own
browser profiles with no account, to learn who each one is signed in as. Everything goes up in one report, even when
every check failed. A check that could not run is reported as `check-failed`, never left out. Each row's detail is one
of the Director's own fixed sentences, never text from the page or the tool. Browser work on one machine now goes
through a shared lane: a check never runs beside a sign-in or an approval, and a sign-in and the approval it waits
for never wait for each other. That last rule is what keeps a machine that approves its own renewal from deadlocking.

**The monthly renewal works with no person, and it is now visible (4153).** Hosted tests run the real Director code,
from the renewal through the Gateway relay to the approver and back. Only the browser, the login and the seal are
fakes. The proof held: the renewal completed through the machine the owner set, and a machine that is its own approver
renewed with no deadlock. It found one defect: a renewal the Director started by itself left nothing in the Licenses
log. Each relayed approval now writes one `approval` row against the machine that asked, saying who approved or why
it was refused, in the Gateway's own sentence. The row never holds the address of the approval, the sealed approval
or the approving machine's own words.

## The tests, in plain words

**The approval-page check (4116, Python, `test_license_approver_check.py`).** Its fake browser runs the real script
against recorded page shapes, with every name made up, and has no click or typing helpers, so any click where none
is allowed fails the test.

- Each of the eight recorded page shapes reads as the state it shows. A different case or a trailing full stop is
  still the same account.
- Both browser scripts begin with `classify_page` and carry no page rules of their own.
- A check of a real approval page answers its state, presses nothing, and leaves no login, no folder and no changed
  profile behind. That holds even when the browser step fails, the folder cannot be removed, the login will not end
  or the program crashes.
- A consent page still loading is waited for. A sign-in page passing on the way is not taken from one look.
- With no account given, the check first asks claude.ai who is signed in. Only claude.ai's own "session invalid"
  answer reads as signed out; any other refusal is `check-failed` with its status.
- The profile is answered by its identifier, with its display name beside it, whichever of the two was given.
- The approval still presses Authorize once on a consent page. Every page that cannot approve is refused with its own
  reason and nothing is pressed. The reauth refusal names the command to fix it.
- On the C# side: the contract's nine renewal words each have their own sentence on the Director, on the Licenses page
  and in the sign-in log, and none of them falls through to the bare word.

**The daily check (4123, `LicenseApproverCheckTests`, `LicenseBrowserLaneTests`, `LicenseApproverOfferSettingTests`).**

- Each record that names this machine is checked with its own account, and all rows go up in one report.
- With the offer setting off, the machine's own profiles are not checked. With it on, each is checked with no
  account, and the report says the machine offers.
- With license switching off, or on a Director that is not the key holder, nothing runs and the Gateway is not asked.
- A failed check is reported as `check-failed`. A report where every row failed is still sent, and with nothing to
  check an empty report is sent.
- A run happens once a day, counted from when its checks started, not from when the Gateway took the report.
- A report the Gateway did not take is sent again at the next look without checking again, unless it is a day old;
  then the checks run again.
- The detail is always the Director's own words. Text planted in the fake tool appears nowhere in the report.
- A display name of a shape the Gateway would refuse is left off; the row still goes. Reports stop at 32 rows.
- The lane: a sign-in and its own approval never wait for each other. A check waits for a running sign-in. An approval
  waits for a running check, but only as long as it could still finish in time; past that it is refused `expired` and
  nothing is pressed. A waiting sign-in or approval goes in before the next check of a run. A cancelled wait takes
  nothing.
- The offer setting is off unless it is exactly `true`, and writing it changes no other setting.
- Only the contract's state words, for the account that was asked about, are taken from the tool's answer.

**The renewal proof (4153, `LicenseRenewalThroughApproverHostedTests`, `LicenseApprovalRelayTests`).**

- A due spare on a machine with no profile of its own is renewed by the approving machine with the profile the owner
  set. Nothing is approved on the machine being renewed, the next report shows the new sign-in time, and the Licenses
  log has one `approved` row naming the approver.
- A machine that is its own approver renews its own sign-in with no deadlock. Every renewal, report and approval in
  these tests runs under a two-minute limit, so a deadlock fails this test by name instead of hanging the run.
- An approver whose page asks for a fresh sign-in fails the renewal with `needs-fresh-sign-in` and its plain sentence.
  The waiting login is ended, the Licenses page says why, and the log has a `refused` row with that word.
- The relay's unit tests: one final row against the machine that asked. A refusal is written in the Gateway's own
  sentence, so a Director that echoes the approval address into its message leaves no trace of it. A request with no
  approver set is recorded naming no machine. A request from a Director that never reported to this DevThrottle
  account is answered and writes no row. The existing test that searches the database bytes for the address and the
  sealed approval still passes with these rows in it.

## The gate

Each pull request's proof file holds its full gate output. The final runs, after the last rebase onto main, with TEMP
and TMP on `D:\cc-gate-temp`:

- **4153** at 588d704cd: `.\scripts\test-local.ps1 -Parked -Filter "FullyQualifiedName~License"` GREEN (Core.UnitTests 199,
  Avalonia.Tests 32, Gateway.Tests 26, Gateway.UnitTests 1,077).
- **4123** at 561225153, rebased onto main with 4153 in it: the same License-filtered run GREEN (Core.UnitTests 253,
  Avalonia.Tests 32, Gateway.Tests 26, Gateway.UnitTests 1,077). The Tech Lead's own License runs agreed on both.
- **4116**: the whole Python tool suite in a clean environment, 4,729 passed, 0 failed.
- The default `.\scripts\test-local.ps1` on both branches was green except one test that is red on main itself (see
  below).

## Known follow-up

- **A daily check does not yet give way to an approval that arrives while it runs.** The check takes its turn in the
  lane and keeps it until it ends, which can be several minutes. An approval that arrives meanwhile waits only as long
  as it could still be answered in time, 30 seconds, and is then refused `expired`, with nothing pressed. That is
  correct, never pressing anything for a sign-in already given up, but the sign-in has to be tried again later. The
  better behaviour is for a running check to stop and hand the browser over when an approval arrives.
- A renewed spare's new sign-in time reaches the Gateway at the reporter's next spare read, up to ten minutes after the
  renewal. It heals itself and nothing acts on it sooner, but the reporter could re-read a holding it has just renewed.

## Left for others

- `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne` is red
  on main: #4162 added a third waiting loop to `tools/cc-director-setup-engine/LauncherSelfUpdate.cs` without its row
  in `docs/BackgroundWork.md`. Reported to the Tech Lead.
- `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused` cannot pass with TEMP on drive D:, which
  makes no short (8.3) names. It passes with TEMP on C:.
