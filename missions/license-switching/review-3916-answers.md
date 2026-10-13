# Answers to the review of pull request 3916

13 October 2026, by the phase 4 Developer. Every finding in `review-3916.md` is answered here.

## Finding 1 - a call that fails by design, once a day - FIXED

The reviewer was right, and right about why the proof did not show it.

**The cause.** With no Gateway that relays an approval, the Director started `license sign-in <account> --json` for
a due account whether or not a browser profile was set for it here. With none set that call must fail, and a tool
that exits with a failure files an error report.

**The fix, with no fallback.** The Director now decides before it starts anything:

- The renewal check's row carries whether a profile is set here (`LicenseRenewalRow.HasBrowserProfileHere`, read
  from the `browser` field `renew --check` already printed; the profile's name itself stays in the tool).
- With no relay and no profile, `LicenseSignInCoordinator` records `no-approver` and starts nothing.
- A sign-in the Gateway asks for may be for an account not held yet, so there is no row. For that case the tool has
  one new read, `license get-browser <account> --json`, which answers the profile or none and exits 0. The
  Director asks it only when no Gateway relays.
- When the tool cannot answer that question the sign-in is `login-failed` and nothing is started.

**Proved by.**

- `LicenseRenewerTests.TickAsync_NoProfileHereAndNoGatewayThatRelays_IsNoApprover_AndNoSignInIsStarted`: the
  tool's sign-in is not started and no extra question is asked.
- `LicenseSignInCoordinatorTests`: the same for a sign-in that was asked for, plus the profile-here case, the
  tool-cannot-say case, and that a Director with a relay never asks the extra question.
- The proof, step 4: against the real tool, with the relay off, the renewal ends `no-approver`, the count of
  sign-ins started does not move, and a `license-sign-in` for an account not held ends the same way.
- The proof, step 8: "no report kept" is now a check. It counts the files in the tool's error outbox after the
  whole run (0), then starts on purpose the very call the reviewer reproduced and counts again (1). So the count
  is shown to see a report, in the same outbox, in the same run.

The sentences in contract 8.2 and in the pull request body are corrected to say what is now true.

## Finding 2 - the Director's time limits were longer than the Gateway's waits - FIXED on the Director's side

The Gateway's waits in section 8.5 were agreed with the phase 2 Tech Lead, so I kept them and made the Director
fit inside them. Three constants are added to `LicenseContract` and to 8.5:

| | Was | Is | Inside |
|---|---|---|---|
| The approving Director lets `license approve` run | 4 minutes | 2 minutes 30 seconds | the Gateway's 3 minute wait for `license-approve` |
| The target waits for an approval | until `NotAfterUtc` (10 minutes) | 3 minutes from the start of the sign-in, or `NotAfterUtc` if sooner | with the 90 seconds below, the Gateway's 5 minute wait for `license-sign-in` |
| The target lets the login finish | 90 seconds | 90 seconds, now a contract constant | |

The target sends its own approval deadline as `NotAfterUtc` in `LicenseApprovalRequest`, so the approving machine
refuses a request that arrives too late to be of use.
`LicenseSignInCommandLineTests.TheDirectorsTimeLimits_FitInsideTheGatewaysWaits` holds the six inequalities.

**For the phase 2 Tech Lead, one line:** the Director now answers `license-sign-in` inside 5 minutes and
`license-approve` inside 3, so the Gateway's waits in 8.5 can be built as written; `NotAfterUtc` in
`LicenseSignInRequest` still means only "do not START after this".

Not changed: a local sign-in (a profile on this machine) may take up to 4 minutes, which is inside the 5.

## Finding 3 - a failure found by the check was logged only as a count - FIXED

`LicenseUsageReporter.RecordRenewal` now writes one line for every failure it records, naming the account, the
word and the time, whichever route found it; and one line when an earlier failure is cleared. `AttachRelay` and
both `AttachSeal` methods log too. `LicenseRenewer.Stop`, `LicenseApprovalExchange.Expect` and
`GatewayStreamClient.RequestLicenseApprovalAsync` are left as they are: the reviewer found none of them hides a
failure, and the caller of each logs the event with its `SignInId`.

## The cosmetic note - the traceback on a called-off sign-in - NOT FIXED

The stop is now raised without its cause attached, which is the part I could change safely. I did not run the
called-off case by hand to see whether the traceback is gone, because that run found the real login command on
this machine when the reviewer did it, and live sign-in runs are stopped. The Director discards standard error, so
nothing depends on it.

## The reviewer's two side notes

- **The real login started by the reviewer's call-off run.** Noted. It is the reason I did not repeat that run.
- **Two login processes waiting since 11 October.** They are still there (started 09:32 and 12:58 that day). They
  are very likely from the first pull request's live runs, which were mine, but I cannot prove which run started
  them and I do not end a running process I cannot identify. Reported to the Delivery Lead to decide.

## What was run after the changes

- Director license unit tests: 181 passed (13 of them new).
- License tests in the window test project: 32 passed.
- `tools/cc-devthrottle/tests/test_license_signin.py`: 43 passed.
- The proof: every check passed; its output is `phase-4/director-proof-2026-10-13.txt`, unedited.
- Not run again: the full `cc-devthrottle` test suite, the `cc-secrets` tests (untouched), the default gate.
