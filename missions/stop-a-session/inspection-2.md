# Stop a session: partial second inspection

The owner called this inspection off on 9 September 2026. **This is an unfinished inspection, not approval to merge and not a completed review of I1 through I8.** No further checks were started after the cancellation instruction. The observations below had already been collected.

Inspected snapshot: `583d59d8529935da91fbdacb3f8acab7730c7d0d`, on `mission/stop-a-session`. A fresh fetch before inspection showed the branch zero commits behind main. Source builds and compiled-code mutations used a separate snapshot under `.temp/inspection2`; web mutations changed modules in memory. No implementation or existing tracked test was changed.

## Blocking finding already established

**I1: NARROWED, P1. A completed stop can still have no audit record when the audit write fails.**

The cancellation, timeout and dropped-tunnel paths now attempt an append. However, `RecordStopInTheAuditTrail` in `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` catches an append exception, logs it, and returns. `StopSessionAsync` then returns the successful stop answer. There is no durable pending record or retry on that path. A missing audit dependency also logs and returns.

Two completed observation tests exercised the real endpoint over local HTTP, the real executor, a buffer-only session and a real temporary database. The control first established that the audit store could write and read a sentinel row. In the failure arm only, a database trigger rejected inserts into the audit table. The stop was then dispatched once, removed its session row, and returned HTTP 200 with `RowRemoved = true` and the supplied reason. The audit contained **zero** rows for that session. The otherwise identical control contained **one**. The sentinel remained readable in both arms.

The tests were `ObservationTests.Observe_completed_stop_when_audit_insert_fails(rejectWrite: False)` and its `True` arm. Both passed in the 144-test observation/control run. They assert the observed behavior, including the defect; they are not regression tests asserting the desired correction. This establishes the handler's behavior under an actual rejected database write. It does not demonstrate a real process termination or a naturally occurring database outage.

**This residual was already acknowledged in the code and in inspection 1. The new evidence reproduces it; it is not a claim that the cancellation fix failed.** It prevents an unconditional statement that completed stops are audited. Under the brief's mandatory audit condition, I would treat it as blocking. The phase report's statement that every finding is closed is too strong.

## Findings for which enough evidence was collected

- **I2: CLOSED for the reported false liveness inference.** The production-method mutations below failed on the reported symptom. The unreadable-process fixture establishes that its own live child is listed but reading its exit state throws; it passes no replacement liveness check. The existing limitation remains: the undescribed path removes the row, as the ruling permits.
- **I4: CLOSED for the roster/page ownership defect.** The provider is mounted in `AppShell` above the outlet. Moving ownership back into `SessionMenu` in memory made five lifecycle tests fail because the returned headline disappeared after the parent removed the menu. Their unmutated controls passed. This does not establish browser pixels or every navigation path.
- **I7: CLOSED for repeated Enter submissions while a stop is pending.** Removing the action guard in either web surface made two tests fail, including two presses before a rerender. The first failure reported one expected call and three actual calls. The 48-test unmutated control passed.
- **I8: CLOSED for the reported modal error and cancel behavior.** Suppressing the error inside the phone dialog made two tests fail on missing error text. Restoring clear-on-cancel made two tests fail because the explanation disappeared. The unmutated control passed. These are component observations, not a screen-reader or pixel inspection.

No final closure verdict was reached for **I3, I5 or I6** before cancellation. Their passing suites are recorded below, without substituting those numbers for the unfinished review. The web portion of I5 was mutation-tested: restoring the definite stopped claim made two tests fail; the command-line and desktop mutation work was not completed.

## The requested liveness constants

The method now returns a three-state reading, so the equivalents of the original boolean constants are an unconditional `ProcessLivenessReading.IsGone` and an unconditional `ProcessLivenessReading.IsAlive`. Each was substituted into the compiled production `DefaultProcessLiveness` method in isolated output. The test assembly contained the unchanged source of both executor test classes from the inspected snapshot.

| Mutation | Executed | Passed | Failed |
|---|---:|---:|---:|
| Always Gone, equivalent to false | 74 | 72 | 2 |
| Always Alive, equivalent to true | 74 | 71 | 3 |

With **Gone**, these two tests failed:

- `Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped`: expected `stopped`, actual `alreadyStopped`.
- `Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped`: expected a verdict other than `alreadyStopped`, actual `alreadyStopped`.

With **Alive**, these three failed, each expecting `Ok` and receiving `Error`:

- `Kill_WithNoInjectedCheck_ReadsARealLiveProcessAndReportsItStopped`.
- `Kill_WithNoInjectedCheck_WillNotCallAnUnreadableLiveProcessAlreadyStopped`.
- `Kill_WithNoInjectedCheck_ReadsAnEndedProcessAsGoneAndReportsAlreadyStopped`.

**All 68 tests in `SessionCommandExecutorTests` still passed under both constants.** In the six-test liveness class, the ended-process case additionally passed under Gone. The following three passed under both constants:

- `Kill_SessionCarryingNoProcessIdentifier_IsNotDescribedRatherThanAlreadyStopped`.
- `Kill_WhenTheCheckStopsBeingReadableAfterTheStop_ReportsNeitherEndedNorStuck`.
- `Kill_WhenTheBackendReportsItsShutdownFailed_IsAFailureAndTheRowStays`.

That division is expected from their paths: the missing-identifier and backend-failure cases do not consult the production method, and the after-stop unreadability case injects its readings. The report's description of six tests all protecting the production method is inaccurate; **three** do. Those three do protect it against both constants, which is the substantive improvement over inspection 1.

The 144-test unmutated control passed before mutation. The production library was restored from its saved copy after each constant run. An attempted subsequent audit-cancellation mutation failed its target-selection assertion before writing a modified library or executing tests. It is **not mutation evidence**. The backend mutation and the final restored suite in that sequence therefore never ran; the earlier control must not be described as a later restored run.

## Other completed runs

| Run | Observed result |
|---|---|
| Isolated solution build | Succeeded; zero warnings and zero errors |
| Cockpit suite | 326 passed, 37 files; exit 0 |
| Phone web suite | 62 passed, 11 files; exit 0 |
| Shared web client suite | 1,042 passed, 98 files; exit 0 |
| Type checking, all four workspaces | Exit 0 |
| Command-line suite | 273 passed, 2 failed; exit 1 |
| Shared Python suite | 129 passed, 1 failed; exit 1 |
| Shipped-tools contract suite | 36 passed; exit 0 |
| Isolated observation/control assembly | 144 passed, zero failed or skipped; completed |
| Selected web mutation control | 48 passed |
| Restore menu ownership in Cockpit | 5 failed, 43 passed |
| Remove Cockpit pending-stop guard | 2 failed, 46 passed |
| Remove phone pending-stop guard | 2 failed, 46 passed |
| Suppress phone error inside dialog | 2 failed, 46 passed |
| Restore phone clear-on-cancel | 2 failed, 46 passed |
| Restore shared web client's definite stopped claim | 2 failed, 46 passed |

The command-line failures were `test_owner_has_no_recipient_option` and `test_type_option_is_removed`, in help rendering. The shared Python failure was `test_attribute_access_resolves_markdown_parser`, reporting the missing `mdit_py_plugins` dependency. Their pre-existing status was reported by the builders; this inspection did not independently rerun main to establish that attribution.

The full Gateway suite had been started and had produced **no completed result** when cancellation arrived. It was allowed to end without killing a process. The other ten suites queued behind it were prevented from starting by renaming only their project files inside the disposable inspection snapshot; no project in the mission worktree was changed. No full parked/default-suite pass is claimed here.

## The documentation commit containing code

Commit `a6c4c28c` changed 33 files, including substantial code and tests. Its endpoint change removed the three statements that computed the unknown outcome and appended its audit row, plus a blank line. Commit `d79017a4` restores those statements, and the inspected snapshot contains them. This directly answers the known deletion; it does **not** complete the requested audit of every other deletion in that commit. No additional lost code was established before cancellation.

## Boundaries and retained evidence

No live fleet session was stopped. No real browser, desktop interaction, cross-machine stop, live tunnel composition, or independent replay of the live-stack demonstration was performed. Remote cancellation, real session-key authentication composed with a real stop, the final full Gateway result, and the remaining parked/default suites are not certified by this partial record.

Local evidence is under `.temp/inspection2`: `results/probes-control.trx`, `results/liveness-gone.trx`, `results/liveness-alive.trx`, the seven `web-*.json` mutation/control reports, the full web logs, the Python result files, and `build.log`. The observation fixture is `probes/ObservationTests.cs`; the mutation programs/configuration are beside it. These ignored artifacts remain local and are not included in this commit. Only this partial review is being committed.
