# Answers to the review of pull request 4153

The review is `review-4153.md`. Answered by the Developer who built it; both are accepted and fixed.

## 1. "No deadlock" is proved by a passing run, not by a bound the test owns

**Accepted and fixed.** Every renewal, report and approval command in `LicenseRenewalThroughApproverHostedTests` now
runs under one token, `_hang`, cancelled two minutes after the test starts: `Renewer.TickAsync`, `Reporter.TickAsync`,
and the `license-approve` command handler. The coordinator links its approval wait to that token and checks it after
the wait, as the review says, so a deadlock fails the named test with a cancellation on every runner - the local gate
and the continuous integration run alike - instead of hanging it. A passing run takes a few seconds and never comes
near the bound, so it only ever fails a test, never decides a pass. The class comment now says exactly that.

Shown working: with the fake's waiting login made to hang forever (honouring the token, as the real one does),
`ADirectorThatIsItsOwnApprover_RenewsItsOwnSignIn_WithNoDeadlock` failed after 1 minute 44 seconds by name, with no
hang. With the source restored the three tests pass in a few seconds.

## 2. The "never reported for this account" line and sentence say more than the code checks

**Accepted and fixed.** The relay's line, its comment, `cockpit-api.md` and the proof file now say "has never reported
to this DevThrottle account", which is what the match by Director identifier among the tenant's machines checks.

## Gate

After rebasing onto origin/main (which added `TakeHandSwitches` to `ILicenseCommandLine`; the hosted test's fake
now answers it with an empty list, as the other fakes do), both gates were run with TEMP and TMP on `D:\cc-gate-temp`:

- `.\scripts	est-local.ps1 -Parked -Filter "FullyQualifiedName~License"`: GREEN. Core.UnitTests 199, Avalonia.Tests 32,
  Gateway.Tests 26 (the hosted renewal tests among them), Gateway.UnitTests 1044, every suite outcome=Completed.
- `.\scripts	est-local.ps1` (default): every suite green except one test in Core.UnitTests,
  `BackgroundWorkRegisterTests.EveryTimerWatcherAndWaitingLoop_HasARowInTheRegister_AndEveryRowStillPointsAtOne`:
  "tools/cc-director-setup-engine/LauncherSelfUpdate.cs has 3 site(s) in code, the register says 2". That file and
  `docs/BackgroundWork.md` are untouched by this pull request; the third site came to main with #4162, which did not
  add its row. It is red on main, not here. Gateway.UnitTests ran 14114 tests (332 affected classes), all passed.
