# License Switching - phase 4 report (adding a spare by signing it in, and renewing sign-ins)

Phase 4 of `MISSION.md` section 6: a spare is added by signing the account in on the machine that needs it,
every sign-in is renewed before it dies, a failed renewal is reported, and the Director does its half of a
sign-in approved on another machine. Written by the phase 4 Developer on 13 October 2026. Every pull request
below is merged to main.

## What was built

| Pull request | What it is | Merged |
|---|---|---|
| 3875 | Signing in and renewing, in `tools/cc-devthrottle` (`license_signin.py`, `license_signin_ops.py`): `cc-devthrottle license sign-in <account>` adds a spare, or renews in place a sign-in already held (a spare's, or the active account's); `license approve` is the other machine's half; `license set-browser` records which browser profile approves an account on this machine; `license renew` renews every holding whose sign-in ends within five days. The login runs straight into the folder the account belongs in, so a sign-in is never moved or copied. The approval reads "Logged in as" on the consent page and clicks nothing when it names another account. `license usage` now reports `signedInAtUtc`. Contract section 8 was written and agreed with the phase 2 Tech Lead in this pull request, and the 24-hour spare check (`proof/spare-24-hours.md`) was committed with it. | cea3c3775 |
| 3916 | The Director's side of contract section 8. It renews once a day and at start (`LicenseRenewer`), reports a failed renewal in the usage report (`RenewalFailedAtUtc`, `RenewalError`, `RenewalErrorDetail`), signs an account in when asked (`license-sign-in`), asks the Gateway for an approval when no browser profile is set here (`RequestLicenseApproval`, answered by `license-approval-result`), and approves for another machine when asked (`license-approve`), sealing the answer to the asking machine's key. Tool changes that carry it: `cc-secrets seal` / `open`, and in `cc-devthrottle license` the `renew --check`, `sign-in --ask-when-no-browser` and `get-browser` commands and JSON answers for a failure. Off until `licenseSwitching.enabled` is true, like the rest. | cb17f8445 |
| 3917 | Not phase 4 work, but found by it: main's default gate was red on `GateTestsDoNotDependOnTheMachineTests`, because two tests added to `CronRunResultTests.cs` read the real clock. The schedule store now takes an optional `TimeProvider` and the two tests hand it a fake clock. The baseline was not raised and the file was not exempted. | b1831bfa1 |

## The check

- **Unit tests.** On the merged head of 3916 the Delivery Lead ran 183 Director license tests and 366 command
  line license tests: green. The Developer's runs on the same head: 181 Director license unit tests, 32 license
  tests in the window test project, 43 sign-in tool tests, 115 license tests in the Gateway unit suite, and 9
  `cc-secrets` sealing tests - all passed. The whole `cc-devthrottle` suite passed (4239 passed, 3 skipped) before
  the review answers of 3916 and was not run again after them.
- **Local gate** (`.\scripts\test-local.ps1`). 3875: green. 3916: red on two things it did not cause, both
  reported and one fixed - the clock rule above (fixed by 3917), and `CcDirector.Avalonia.Tests` stopped at the
  120 second ceiling. That suite passes on its own (1067 tests) but takes a little over four minutes on this
  machine, on the build of 3875 as well as on 3916. Every other suite in the default run was green. The gate was
  not run again after the review answers of 3916, and the parked suites were not run in full.
- **Live proof of the tool** (`phase-4/README.md`, 11 October). `live_sign_in_proof.py` on isolated folders:
  a sign-in for the wrong account is refused and keeps nothing; a sign-in adds a spare; signing in again renews
  it in place; the active account's sign-in is renewed in place. Windows as the target with the approval in a
  Director browser profile on the same machine: passed 5 of 5 complete runs. The Mac as the target, key in the
  Keychain, approved from the Windows machine with `license sign-in --remote-approval` and `license approve`:
  passed 1 of 2, the other losing one Authorize click.
- **The 24-hour spare check** (`proof/spare-24-hours.md`, 12 October): a spare left untouched for 24 hours 46
  minutes renewed and read.
- **Proof of the Director** (`phase-4/director-proof/`, output `phase-4/director-proof-2026-10-13.txt`). No live
  sign-in was made for 3916; the owner had stopped live runs. Two Directors' license parts run in one process
  against the real `cc-devthrottle license` and `cc-secrets` from the checkout, with a stand-in for the login
  command and an in-process stand-in for the Gateway. Every check passed: a sign-in approved on the other
  machine; a repeated `SignInId` replayed; no sign-in started when nothing could approve it; a refused renewal
  reported and the held sign-in untouched; the next renewal renewing in place and clearing the failure; a
  sign-in nobody answers ended at its deadline with nothing left behind; the answer in no log, stored result or
  relayed message; and the tool's error reports counted after the run (0), with a control call that makes the
  count 1.

## Reviews and their answers

Every pull request was reviewed by a separate review session before merge; every finding is answered in the
files below.

| Pull request | Review | Answers | In short |
|---|---|---|---|
| 3875 | `review-3875.md`, `review-3875-round-2.md` | `review-3875-answers.md` | Found: adopting a new sign-in from a staging folder left one sign-in in two places across a crash; the proof's cleanup deleted a folder the caller named without checking it; the renewal error words did not match the command line. Fixed by removing the move altogether - the login writes once, into the folder the account belongs in - by a proof root the run creates and marks itself, and by one vocabulary. Round 2 found: a renewal approved for another account replaced the held sign-in; contract 8.3 said to delete "its sign-in folder" on expiry, which for a renewal holds the key in use; `expired` was missing from the error words. Fixed: `license approve` refuses, before any browser opens, a URL that names another account; a renewal's folder is never touched; `expired` added. Nothing declined. |
| 3916 | `review-3916.md` | `review-3916-answers.md` | All the safety points held. Found: with no Gateway able to relay and no browser profile here, the daily renewal still started a sign-in that fails by design and files an error report every day; the Director's own time limits were longer than the Gateway's waits in the same contract; a failure found by the renewal check was logged only as a count. Fixed: nothing is started when nothing can approve it, and `no-approver` is recorded; `license approve` runs 2 minutes 30 seconds and the target waits 3 minutes for an approval, both inside the Gateway's waits, held by a test; every renewal failure is named in the log. Not fixed: a traceback printed to standard error when a sign-in is called off, which nothing reads. |
| 3917 | by a separate review session, arranged by the Delivery Lead | none needed from the Developer | Merged as reviewed. |

## Decisions the phase made

- **A sign-in is never moved.** The login runs into the folder that holds, or will hold, the account, and Claude
  Code's own single write is the only write. Why: copying a key between two places always has a moment when it
  is in both, and trap 2 forbids that moment.
- **Nothing assumes a sign-in lasts 30 days.** The sign-ins made on 11 October ended 27.1 to 29.9 days after they
  were made. A renewal is due when the end date the key itself reports is within five days.
- **The Director makes no tool call that is expected to fail.** Every cc-* tool that exits with a failure files
  an error report, so a design that tried locally and then asked another machine would have filed one a day per
  account. The Director asks what is due (`renew --check`), and whether a profile is set here, before it starts
  anything. An early run of the proof sent three scrubbed error reports to the hosted Gateway before this was
  understood; that is how it was found.
- **The answer to an approval is sealed to one machine and typed straight into the waiting login.** It is the
  one-time authorize code, not a key. The seal keeps it from the Gateway; it does not prove who sealed it, and
  the contract says so. `SealedApproval` is the one named exception to rule 6.1.
- **A sign-in the Director gives up on is the caller stopping the tool, not a tool failure.** It is ended by
  closing standard input, and that files no error report; a blank line still does.

## Open, for later phases

- **The Gateway relay (phase 2) is not built.** It needs: the hub method `RequestLicenseApproval`, returning at
  once; the owner's setting of which machine and browser profile approve which account (`LicenseApprover`, set on
  the Licenses page); the `license-approve` command to the approving machine's Director and the
  `license-approval-result` command back to the target, relayed and never stored or logged; an answer at once
  with `approver-offline` or `no-approver` when there is nobody to ask; and the one email per holding per failure
  streak when a holding has `RenewalError` set and its sign-in ends within three days. The Gateway's waits in
  contract 8.5 can be built as written: the Director now answers `license-sign-in` inside 5 minutes and
  `license-approve` inside 3. Until a Gateway offers the hub method, a Director with no browser profile for an
  account records `no-approver` once a day and starts nothing.
- **Unproven until the relay exists:** nothing in phase 4 ran against a real Gateway. In particular, that
  `license-approval-result` is delivered while the same Director is still answering a long `license-sign-in` on
  the same connection has not been tested, and the Director has not yet started the installed `cc-secrets`
  through its own class (`CcSecretsLicenseApprovalSeal`; unit-tested with a stand-in process). A Director-run
  sign-in on the Mac, with the key in the Keychain, is unproven too.
- **The Cockpit button (phase 3) is not built.** "Add a spare" and "Renew now" send `license-sign-in` (contract
  8.4) and show the result's `Outcome`, `ReasonCode` and `Message`; the Licenses page shows `RenewalError` and
  `RenewalErrorDetail` beside the expiry verdict. A result with `Replayed = true` is an old answer.
- **claude.ai asks a browser profile to log in again after about 30 automated approvals in one day.** On 11
  October, after roughly that many approvals by the proof, the authorize link led to "Log in - Continue with
  your Claude.ai account to authenticate connections" although the same profile was still signed in to claude.ai
  itself. The profile was the Edge profile on the Windows machine that the Director manages for account A, the
  owner's work account; it approved every sign-in in both live runs, for the Windows target and for the Mac. Its
  name and the account's address are in the private record, not here. The tool reported it correctly
  (`browser-not-signed-in`, nothing approved). Signing a profile in is the owner's job, so the live runs stopped,
  and the owner then stopped all live sign-in runs. Whether that profile has been signed in again since is not
  known to the Developer. No other profile was asked to log in.
- **A login wall is not passed automatically.** An unattended renewal that meets one is reported as
  `browser-not-signed-in`. Passing it without a person needs the email-link login the prototype proved, which is
  not built.
- **The lost Authorize click is not proven cured.** One click in eight was lost on the Mac run even after the
  scrolling and the re-clicks. Focus emulation was added after that and has two Windows runs and no Mac run
  behind it.
- **Two login processes started on 11 October were still running on the Windows machine on 13 October.** They
  are very likely from the live runs of 3875. The Developer could not prove which run started them and did not
  end them; reported to the Delivery Lead.
- **The window test suite runs over the default gate's 120 second ceiling on this machine** and has no owner.
- **The installed `cc-devthrottle` and `cc-secrets` have none of this yet**; it arrives with the next release,
  and phase 5 turns `licenseSwitching.enabled` on.
