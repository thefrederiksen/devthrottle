# Review of pull request 3916 - the Director's side of contract section 8

Reviewed 12 October 2026, at commit `20402d73f` (two commits on `faffafcdc`, which was origin/main when I fetched).
Written by a separate review session. Nothing in the worktree was changed; this file is the only new one.

## Verdict

**The safety points hold. One finding with proven harm, small to fix; I recommend fixing it before merge because
it makes a sentence in the contract and in the pull request body untrue as merged.** Two lesser points follow it.

| Point asked | Result |
|---|---|
| Nothing touches the real default login while `licenseSwitching.enabled` is off | Holds |
| A sign-in key is never in two places and never leaves the machine; the sealed approval is the authorize code | Holds; confirmed it is the code |
| A failed renewal is logged and reported, never silent | Reported: holds. Logged: holds, thinly (finding 3) |
| No fallback code | Holds |
| Logging on public methods | Mostly; see finding 3 |
| No email address, profile or machine name in the repository | Holds |
| The design change does not hide real failures | Holds - but the change is incomplete (finding 1) |

## Finding 1 - the Director still makes a call that fails by design, once a day, and it files an error report

**Where.** `LicenseSignInCoordinator.RunAsync` computes `canAsk` (a Gateway that offers `RequestLicenseApproval`
is connected, and a seal is attached) and calls `_tool.StartSignInAsync(email, canAsk, ...)`. With `canAsk` false,
`CcDevthrottleLicenseCommandLine.StartSignInAsync` runs `license sign-in <email> --json` with no
`--ask-when-no-browser`. For an account with no browser profile on this machine that call ends in `no-approver`,
exit 1, through `axi_cli.fail` - which files an error report.

**When `canAsk` is false.** Always, today: no Gateway offers `RequestLicenseApproval` yet (the pull request says
so), so every Director with license switching on is in this state until phase 2 ships and deploys. After that,
whenever the tunnel is down at the moment the daily renewal runs.

**The harm, reproduced.** I ran this checkout's tool against an isolated scratch root, error reports pointed at a
closed local address, one held account due within five days and no profile set:

    license renew --check --json                 -> exit 0, row "due", "browser": null, no report kept
    license sign-in <account> --json             -> exit 1, {"result":"failed","reason":"no-approver"}
                                                    one report kept in logs/error-outbox/tool/:
                                                    "surface": "cc-devthrottle license sign-in",
                                                    "exception_type": "SignInError",
                                                    "message": "no browser profile is configured to sign <argument> in. (reason: no-approver)"

The second command is exactly what the Director starts. So each machine with the setting on sends one error report
a day for every due account that has no local profile, and keeps doing so after the sign-in has died, because a
dead sign-in stays `due`. That is the outcome the developer's design change was made to stop. Contract 8.2 says
"Asking first makes no call that is expected to fail" and the pull request body says "no call is expected to
fail"; both are untrue for this case.

**Why the proof did not show it.** `director-proof/Program.cs` only ever runs with `CanRequest => true`, so the
`canAsk` false route never reaches the real tool. And the sentence "no error report was left behind" is not a
check in the proof: the program never looks at the error outbox and the committed output has no such line. It is
a statement about an absence with nothing in the run that could have contradicted it.

**A fix that needs no fallback.** The answer is already in hand: `renew --check` returns the `browser` set for each
due row, and `ReadRenewalRows` drops it. Carry it, and when a row has no profile here and nothing can be asked,
record `no-approver` for the holding without starting the tool. For a sign-in the Gateway asks for, the same
question can be put to the tool first. Then add the case to the proof, and make "no report kept" a real check that
counts files in the outbox.

## Finding 2 - the Director's own time limits are longer than the Gateway's waits in the same contract

Not a defect in this pull request alone, so no harm is proven yet; it will be one when phase 2 is built to section
8.5 as written.

- `license-approve`: the Gateway waits 3 minutes (`ApproveCommandTimeout`); the approving Director lets
  `license approve` run for 4 (`SignInTimeout`). An approval that lands in the fourth minute is sealed and returned
  to a Gateway that has stopped listening, and the code - which can be used once - is spent for nothing.
- `license-sign-in`: the Gateway waits 5 minutes (`SignInCommandTimeout`); the target waits for an approval until
  `NotAfterUtc`, which is 10 minutes, plus up to 90 seconds to finish. A sign-in approved on another machine after
  minute five succeeds on the target while the Gateway has already recorded a timeout. The stored result can be
  replayed by `SignInId`, so nothing is lost if the Gateway asks again - but that has to be decided, not assumed.

Worth one line to the phase 2 Tech Lead before the Gateway side is built.

## Finding 3 - a failure found by the check is logged only as a count

`LicenseUsageReporter.RecordRenewal` is the one place a renewal failure is recorded, and it writes no log line.
For a failure that comes from a sign-in the coordinator logs the account and the reason, so that route is fine.
For a row that `renew --check` itself answers as failed (a stored sign-in it could not read), the Director's log
holds only `rows=N, due=N, failed=1` and `run: accounts=N, renewed=0, failed=1` - never which account or which
word. It does travel in the report, so it is not silent; but nothing yet reads those report fields (the Gateway
side is not built), so until phase 2 the log is the only place a person can look, and it does not say.
One line in `RecordRenewal` naming the account and the word closes it.

Other new public methods with no log line of their own: `LicenseRenewer.Stop`,
`LicenseSignInCoordinator.AttachRelay` and `AttachSeal`, `LicenseApprover.AttachSeal`,
`LicenseApprovalExchange.Expect`, `GatewayStreamClient.RequestLicenseApprovalAsync` (its failure is logged by the
caller). None of these hides a failure.

## What I confirmed, point by point

**Off means off.** `LicenseSwitching.Start` returns before `Renewer.Start` when the setting is off, and it is the
only caller of `Renewer.Start`. `LicenseSignInCoordinator.SignInAsync` returns `not-key-holder` before it reads the
result store or the lock. `LicenseApprover.ApproveAsync` refuses before the tool or the seal is touched.
`license-approval-result` only completes a wait that an enabled sign-in created, so with the setting off there is
nothing to deliver to. Attaching the seal starts no process. `LicenseRenewerTests` asserts the renewer is not
scheduled when off.

**The sealed approval is the authorize code, not a key.** `license approve --json` answers
`{"result":"approved","code":...}`; the code is what the consent page shows (`code#state`, checked against
`CODE_SHAPE` on both machines). It is the one-time answer to one login and completes only the login holding the
matching verifier, on the target. It goes to `cc-secrets seal` on standard input, never as an argument, and comes
out of `cc-secrets open` on standard output straight into the waiting login's standard input. No new message type
has a field for a token, and `LicenseMessagesCarryNoKeyTests` names `SealedApproval` as its single exception.
The key that opens it is the machine key `cc-secrets` already holds and the Director already publishes in `Hello`
(`SecretTransferMachineKey`), so the two ends do match. The envelope binds purpose, id and the receiver's
fingerprint; the tests show another id, another purpose or another machine does not open it, and neither kind of
envelope opens as the other. The sender is not proven, and the contract says so honestly.

**Nothing is copied.** The sign-in itself is written once, by the tool, on the target, in the folder that holds
the account. The Director moves no key file.

**The call-off does not hide a real failure.** Only an end of standard input while the tool waits for a code is
treated as the caller stopping it; a blank line is still a reported failure. I ran it: the called-off case exits
1, prints the `failed` answer, and keeps no error report. Every way the Director ends a waiting login is logged
with its `SignInId` and recorded against the holding with its own word (`expired`, `approval-failed`, a refusal's
word, `no-approver`), so the reason is not lost - it is the Director's to state, and it states it. A real tool
failure (the login would not start, the store refused, the browser profile is signed out) still exits through
`axi_cli.fail` and is reported as before. One cosmetic note: the called-off case prints a full traceback ending in
`Abort` on standard error. The Director discards standard error, so nothing breaks; a person running it by hand
gets a wall of text.

**No fallback code.** Unknown words from the tool are narrowed to `login-failed` or `approval-failed` and logged;
that is rule 6.1 (never carry a tool's own text), not a hidden failure. A missing tool, an unreadable answer and a
timeout each end as a named failure.

**Nothing private in the repository.** Every address in the added lines is `account-<letter>@example.com`; the only
profile names are `Profile B`, `P` and `the proof profile`; machine names are `the-target-machine` and
`the-approving-machine`. No user folder, host name or network address.

## Scope

**Read in full:** the pull request body; `MISSION.md` section 5 (and 4, 6 to 8); `contract.md` sections 2, 6, 7
and 8; `LicenseRenewer.cs`, `LicenseSignInCoordinator.cs`, `LicenseCommandLine.SignIn.cs`,
`LicenseApprovalExchange.cs`, `LicenseSignInResultStore.cs`, `LicenseApprovalTunnel.cs`,
`LicenseSignInExecutor.cs`; the diffs of `LicenseSwitching.cs`, `LicenseCommandLine.cs`,
`LicenseUsageReporter.cs`, `LicenseMessages.cs`, `ControlApiHost.cs`, `GatewayStreamClient.cs`,
`SessionCommandExecutor.cs`; every changed file under `tools/`; `director-proof/Program.cs`, the committed proof
output and the README changes. Around the diff: how the tunnel answers a command (`HandleCommandAsync`,
`CommandAnswerTiming`), how the tool is found, where the `Hello` key comes from, and how `tool_errors.py` decides
what is a user's stop.

**Read only in part:** the six new C# test files (names and the cases relevant to the points above, not every
line); `fake_claude.py` not at all.

**Ran,** in a separate scratch checkout of the same commit, since removed:

- Director license unit tests (`Licenses` and `LicenseMessagesCarryNoKey` in `CcDirector.Core.UnitTests`): 168
  passed. The pull request says 181; I used a narrower filter and did not chase the difference.
- License tests in the window test project: 32 passed.
- `tools/cc-devthrottle/tests/test_license_signin.py`: 42 passed. `tools/cc-secrets/tests/test_seal.py`: 9 passed.
- The three tool runs in finding 1 and the call-off run, against an isolated scratch root.

**One thing I did not intend.** My call-off run found the real `claude` on this machine instead of my stand-in, so
a real `claude auth login` was started for a made-up `example.com` account in the scratch folder. It printed its
URL, was called off with no code, and exited. No browser was opened, nothing was approved, the machine's real
sign-in file was not modified (its timestamp is two hours older than the run), and no login process of mine was
left running.

**Did not run:** the proof program itself; the full default gate; the parked suites; the Gateway unit suite; the
revert check the pull request describes.

**Could not reach:** a real Gateway sending any of the three commands or taking `RequestLicenseApproval`. In
particular nothing here proves that `license-approval-result` is delivered while the same Director is still
answering a long `license-sign-in` on the same connection; the design depends on the tunnel answering commands
side by side, which I believe it does and did not test. Also not reached: a real browser approval, a Mac and its
Keychain, and `CcSecretsLicenseApprovalSeal` starting the installed `cc-secrets` (the README says the same).

**Seen on this machine, not caused by this pull request.** Two `claude auth login --claudeai` processes for a real
account have been waiting since 11 October (started 09:32 and 12:58), presumably from the first phase 4 pull
request's live runs. I left them alone. Someone who knows what started them should end them.
