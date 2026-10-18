# Track C, Developer C2, pull request 3 - the monthly renewal through the standing approvers

The brief's item 10: prove that the daily renewal on a Director, for an account with no browser profile on that
machine, completes through the machine the owner set to approve it, with no person - including when the approving
machine is the machine being renewed - and fix whatever the proof finds.

## What the proof found

One defect. **A renewal that no person started left nothing in the Licenses log.** The daily renewal asks the relay
for an approval, and the relay wrote only file log lines; the Gateway saw the renewal only as the holding's new
sign-in time in the next report. Nothing showed which machine approved it, or that one refused. A "Renew now" from
the Cockpit was logged, as a sign-in row, because the Gateway started it; the renewal the Director starts on its
own, which is the one that happens every month, was not.

Before the fix, the three hosted tests below ran to the end of the renewal with every assertion about it passing,
and failed only at the assertion that the Licenses log holds a row for it (`Assert.Single() Failure: The collection
was empty`, once in each test).

Everything else held: the renewal completed, the approval went only to the machine and profile the owner set, and a
Director that approves its own renewal did so with no deadlock - main as it was, before pull request 4123's lane.

## The fix: one Licenses log row for each relayed approval

`LicenseApprovalLog` (new, in the Gateway) writes one row of a new kind, `approval`, final when written, for every
relay that is answered: the machine that asked (the row's machine), the account, `approved` or `refused` with the
contract's reason word, and a sentence the Gateway writes from those, naming the machine set to approve. The
Licenses page shows it as "Approval", "Approved" or "Refused".

- **It keeps nothing that completes a sign-in.** Not the address the sign-in was approved at, not the sealed
  approval - and not the approving Director's own message, which could quote either: the sentence is built from the
  reason word only (`LicenseDisplayFold.RenewalWhy`). The existing `LicenseSignInKeepsNothingTests`, which searches
  the bytes of the database after a relay for both, passes with these rows in it.
- **It is written after the answer is sent**, so a store that cannot take it never delays or loses the answer.
- **A request from a Director that has never reported to this DevThrottle account writes no row**, and says so in a line: it
  holds no sign-in the Gateway knows of, so there is no machine to put it against. The answer is still sent.
- A sign-in started from the Cockpit and approved through the relay now has two rows: its sign-in row (what was done)
  and its approval row (who approved). They say different things.

`missions/license-switching/cockpit-api.md` documents the kind, its two states and its sentences.

## The tests

`src/CcDirector.Gateway.Tests/Licenses/LicenseRenewalThroughApproverHostedTests.cs` - a real hosted Gateway, and
behind each fake tunnel Director the REAL Director code: `LicenseSwitching` with its `LicenseRenewer`, the
`LicenseSignInCoordinator` that asks the relay, the `LicenseApprover`, the `LicenseApprovalExchange`, the
`LicenseUsageReporter`, and the two verbs exactly as the Director's tunnel runs them
(`LicenseSignInExecutor.ApproveAsync` and `DeliverApproval`). Only the license command line (the browser and the
claude login) and the seal are fakes. Made-up names only: machine-a, machine-b, alice@example.com, profile
work-chrome.

- `ADueSpare_OnAMachineWithNoProfileOfItsOwn_IsRenewedByTheRegisteredApprover_AndTheLogRecordsIt` - machine-b holds
  alice's spare, due in three days, with no profile of its own; the owner set machine-a's work-chrome to approve it.
  machine-b's daily renewal runs: machine-a is sent the approval with that profile and machine-b's own key, the
  sign-in completes on machine-b, nothing was approved on machine-b, the next report shows the new sign-in time, and
  the Licenses log has one `approved` row on machine-b naming machine-a.
- `ADirectorThatIsItsOwnApprover_RenewsItsOwnSignIn_WithNoDeadlock` - the same with one Director: machine-a holds
  the due spare and is the machine set to approve it (registered on the Gateway, not set in its tool), so its renewal
  asks the relay and the relay asks machine-a. The sign-in waiting for its approval and the approval run on one
  Director at once; the renewal completes.
- `AnApproverThatNeedsAFreshSignIn_FailsTheRenewalWithThatReason_AndItsPlainSentence` - machine-a's approval page
  asks for a fresh claude.ai sign-in: the renewal fails `needs-fresh-sign-in`, not a generic failure; the waiting
  login is ended with nothing written; the report carries the word and the Director's sentence; the Licenses page
  says "Renewing this sign-in failed ...: the approving browser needs a fresh claude.ai sign-in on the approval
  page."; and the log has a `refused` row with that word.

The fake tunnel Director gained `OnCommandAsync`, a dispatcher that may await, and its handler is registered with a
result as the real Director's is, so the client runs commands concurrently - which is what the self-approval test
depends on, and what the real Director does (`GatewayStreamClient`, "THE LICENSE SIGN-IN RELAY DEPENDS ON Command
BEING A CLIENT RESULT").

`LicenseApprovalRelayTests` (unit): an approval writes one final row against the machine that asked; a refusal by
the approver records its word in the Gateway's own sentence, never the Director's (a Director that echoes the address
into its message leaves no trace of it); a request with no approver set records the refusal naming no machine; a
request from a Director that never reported is answered and writes no row; the sentences for an approval by another
machine, by the machine itself (any case), and a refusal for a word this Gateway does not know; and the row in words.

## Observed, not changed

A renewed SPARE's new sign-in time reaches the Gateway at the reporter's next spare read, up to ten minutes after the
renewal (`LicenseContract.SpareReadInterval`): between spare reads a report reads only the active account and keeps
the spare's last numbers. The renewal's failure is cleared at once (the "changed" report carries no failure). The
two hosted tests move the clock past a spare read before they look. It heals itself within ten minutes and nothing
acts on a spare's sign-in time sooner, so it is left as it is; the Tech Lead may want the reporter to re-read a
holding that was just renewed.

## Gate output

Only C# changed; the Python tool did not, so `pytest tools/cc-devthrottle/tests` has nothing new to say and its
4,729 passing tests from pull request 4116 stand.

At 7afb2a6bf, tree clean, on origin/main 5d4d8caca:

- `.\scripts\test-local.ps1`: RESULT GREEN, every suite Completed with every test executed.
- `.\scripts\test-local.ps1 -Parked` with TEMP and TMP on D:: RESULT GREEN, every suite Completed -
  `CcDirector.Gateway.UnitTests` 14,010, `CcDirector.Gateway.Tests` 1,779 executed of 1,782 (three reported skipped
  in shard 3, as on main), `CcDirector.Core.Tests` 4,539, `CcDirector.Core.UnitTests` 2,160,
  `CcDirector.Reclaim.Tests` 305. The run waited for another session's parked gate to release the lock first.
