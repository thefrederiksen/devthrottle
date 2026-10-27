# Step 4: the approver check tells the truth

Mission "Licenses - a stranded machine is loud, and repairs itself", step 4, 26 and 27 October 2026.

## A. Why every check the Director ran failed

**The cause, observed:** the Director runs `cc-devthrottle` with its own environment, and its own process is not a
session. The license tool could open a browser profile only through the Gateway, as a session, which needs
`CC_DIRECTOR_ID` and a session key. Only sessions are given those. So every check the Director ran answered
`check-failed` within seconds with "CC_DIRECTOR_ID is not set". The same command typed in a session worked, because a
session has both.

What was observed, and how:

- `root-cause/director-log-daily-runs-19-to-26-october.txt`: each daily run on SOREN_NORTH reported 11 rows and 11
  failed, from 20 to 26 October. The Gateway answered `nothing-to-store` to all of them, so the reason was lost on both
  sides.
- `root-cause/director-log-2026-10-26-approver-run.txt`: the 26 October run. Each profile took 3 to 9 seconds, exit 1,
  and `answered=True`. The tool answered JSON, and the Director kept only its state.
- `root-cause/environment-of-the-director-and-of-a-session.txt`: the names (never the values) of the variables in the
  running Director (pid 57916, the installed `cc-director.exe`), read from the process with `read-env-names.ps1.txt`.
  It had no `CC_DIRECTOR_ID`, no `CC_GATEWAY_URL` and no `CC_GATEWAY_SESSION_KEY`. A session that Director
  started has all three. `browser-harness` and `claude` are both on the Director's PATH, so neither was the cause.
- `root-cause/terminal-run-without-the-session-variables.txt`: the same command from a terminal, with the session
  variables removed. It gave the same result as in the Director: exit 1 in 5 seconds, and the JSON `detail` "The check
  could not run: the browser profile could not be started: CC_DIRECTOR_ID is not set, ...".

The earlier lead, `browser-harness --reload exited 3221225794`, was not the cause. It never appears in a Director run,
because the check never reached the browser step.

**The same function served three more commands.** `license approve`, and `license sign-in` and so the renewal, when a
profile is set on this machine, failed the same way when the Director ran them. The fix is in the one function each
command uses, as the Tech Lead ruled.

**The fix:** the Director starts its own profile in-process, as the tunnel's browser verb does
(`LicenseBrowserOpener`). It hands the tool the attach values (`CC_LICENSE_ATTACHED_BROWSER_ID` and `_NAME`, plus
`BU_NAME` and `BU_CDP_URL`), and stops the profile again afterwards if it was stopped before. The tool's
`director_browser` uses the profile it was handed. In a session it works through the Gateway as before. No fallback:
a profile the Director was not handed, or missing attach values, is refused with a sentence saying so.

**The proof that a check run by the Director works** (`harness/`). It is not a test Director: a test Director with license
switching on would report SOREN_NORTH's licenses to the production Gateway. The Tech Lead accepted a harness instead,
on two conditions. First, it calls the Director's real code path, `CcDevthrottleLicenseCommandLine.CheckApproverAsync`,
including the in-process profile start. Second, it runs under Task Scheduler, so its environment is a Director's
(`CC_DIRECTOR_ROOT` set as the Director has it, and no session variables, as the log's first lines show).

| Run | mindzie | agent-browser |
|---|---|---|
| The installed tool (without this change): `harness/run-installed-tool.log` | `check-failed`, detail "...CC_DIRECTOR_ID is not set..." | `check-failed`, the same |
| The tool from this branch: `harness/run-fixed-tool.log` | **`can-approve`** | `signed-out`, started and stopped again |

**The check never breaks a session using a browser profile** (the Delivery Lead's ruling on review observation 1).
Sessions and night-time factories drive these profiles, and the daily check reads every one of them.
- A profile whose browser is already running is read through that running browser, and the Director never starts it
  again or closes it (`LicenseBrowserOpener`).
- The tool drives every profile under a browser-harness daemon of the run's own (`run_daemon_name`), attached by
  `BU_CDP_URL`, and ends only that daemon. Until now it ran under the profile's own daemon name. That moved a driving
  session's current tab to the approval page, and the closing `--reload` ended that session's daemon under it.
- A profile that was stopped is started, read, and stopped again, as before.

Proven three ways:
- Tests: `LicenseBrowserOpenerTests` (a running profile is neither started nor stopped; a stopped one is started and
  stopped again once) and `tests/test_license_browser_daemon.py` (the profile's own daemon is never used or reloaded,
  even when the browser step fails; the session route leaves a running profile running).
- The daemon tests fail with the run's own name taken out.
- `harness/run-own-daemon.log`, the same Task Scheduler harness on 27 October: mindzie was already running, answered
  **`can-approve`** through that browser, and was "left running". agent-browser was stopped, so it was started, read,
  and "stopped again". `harness/daemons-before-and-after.txt` records that no browser-harness daemon alive before the
  run was ended by it, and that it left none of its own behind.

**Owed to the Delivery Lead's QA after the release:** the real Director's own daily check on SOREN_NORTH, shown by a
log line with `state=can-approve` for the mindzie profile.

## B. The reason is never discarded

- On the Director: on `check-failed`, the row's detail is the tool's own sentence, or the Director's own when the tool could
  not run or the profile did not start. It is fitted to the contract's 200 characters as one readable line, and logged on
  the check line (`detail=...`). Before this change it was one fixed sentence that said nothing.
- On the Gateway: a report's failed rows are kept for each machine and profile, even when no approver record names the
  profile (from 19 to 26 October every one of them was answered "nothing to store" and lost). A later check that works
  clears the row.
- On the page: the reason is folded on the Gateway into the machine's readiness line on the Licenses page, into the
  approver record's sentence (as before), and into the approver alarm, which the shared banner shows. Every one of those
  is a finished sentence that the page shows exactly as it is. No client code changed.

## C. One owner of "an account a machine needs has no approver that can approve"

`LicenseApproverAlarm` is that one owner. It was already raised after every check report and every minute. It now also
raises an alarm for an account with no approver at all, when the readiness check judges a current machine not ready
for it for that reason. The alarm names the machine, the account, the one thing to do, and why the approver checks
failed. The judgement comes from the readiness check itself (`LicenseReadinessService.JudgeNow`). A stale machine never
needs anything.

**Precedence, written in the class:** the machine's own alarm wins. A machine stranded (step 1) or warned at 80% (step 2)
already says the approver's sentence as its one thing to do. So the approver alarm stands down for an account when
every machine that needs it has one of those two open, or about to open on the same picture. One cause gives one
line, one email and one push, whichever of the two raises runs first. A broken approver record that no machine needs
is still told, as before. Usage is never the trigger: the readiness judgement is the same at 0% and at 100%.

Tests:

- `LicenseStrandedReplayTests.ApproverAlarmRunning_AMachineStrandedForWantOfAnApprover_IsToldOnce`, on the 26 October
  (100%) and 25 October (80%) pictures, with the approver alarm raised after the minute and before it. SOREN_NORTH and
  the Mac are each told once, with no approver line beside them.
- `ApproverAlarmRunning_WithTheSignInHeld_NothingIsLoud` is the negative control: no machine needs the approver, so only
  the approver itself is named.
- In `LicenseApproverAlarmTests`: the alarm opens with no approver at all; it carries the failed check's reason; it
  closes when an approver is set; a return inside six hours tells nobody; it stands down for a machine told by its own
  alarm; with no need it raises nothing at any use; and a failed check of a profile no record names is kept, then cleared.
- **Revert proof** (`revert-proof-without-the-stand-down.txt`): with the stand-down switched off, all four told-once
  cases and the stand-down test fail. The fixture test passes. The source was restored with `git checkout`.

## The runs

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (`runs/test-local.txt`) | Red in two suites, neither from this change. `cc-director-setup-engine.Tests` has the 10 known `MacAppNameAndPlaceTests` failures (issue #4484, the same on main). `CcDirector.Core.UnitTests` had 1, the machine-dependence guard, which counted the attach address `127.0.0.1:9333` I had used as test data. That is fixed (the data names no port), and the whole suite then passed: 2819 of 2819 (`runs/core-unittests-after-the-port-fix.txt`). Every other suite passed, including Gateway.UnitTests at 16598. |
| `.\scripts\test-local.ps1 -Gateway`, the parked suite the gate named as a coverage gap (`runs/test-local-gateway.txt`) | GREEN at 68a30dae9 (after the rebase onto main), 1813 tests across 4 shards, 3 skipped |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~CcDirector.Gateway.Tests.Licenses` (`runs/gateway-licenses-suite.txt`) | 1355 passed, 0 failed, the step 3 replay included (18 cases, 5 of them new) |
| Python: `test_license_signin.py` and `test_license_approver_check.py` (`runs/python-license-tests.txt`) | 103 passed |
| Python: every `test_license_*.py` | 33 fail with `stderr not separately captured`, in `test_license_cli.py` and `test_license_usage.py`, files this change does not touch. They fail in the same way on origin/main (390 passed there, 400 here). Filed as issue #4536. |
