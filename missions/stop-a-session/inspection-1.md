# Stop a session: inspection 1

**Do not call this branch complete.** Eight defects remain, including stops that lose their audit record, unsupported claims that a process is gone, and a Cockpit answer that disappears when the roster refreshes.

Inspected on 9 September 2026. Final branch tip: `91be52a642e65aaf7eeeb954f5361ea8483b5f70`. Freshly fetched `origin/main`: `db141e2868e6f99e42373a5053a68a378f74ed62`; three-dot merge base: `6710a86c7d94a9bd1a13f1e05eda058984871b20`. Scope: the whole 62-file branch diff, including its contracts, callers, tests and mission reports. The full build and suites began at `7626e8f3`; the two subsequent commits change documentation only. Their corrections were also read.

No implementation or existing test was changed. Inspection probes and mutations were confined to ignored `.temp` files and isolated build output. No live fleet session was stopped. The evidence below distinguishes observations from the states simulated to obtain them.

## Findings, worst first

### I1 — P1: cancelling the request can leave a completed stop with no audit record

**Where:** `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:2024`, `:2025`, `:2059`; `src/CcDirector.Gateway/GatewayHost.cs:4864`.

The shared handler passes the HTTP request's cancellation token into the tunnel, then records the stop only after a successful reply. If the Director executes the command and the caller disconnects before the reply arrives, cancellation leaves the handler before the audit append. The reason was not sent down the tunnel either: that payload is `null`. A tunnel timeout or lost acknowledgement also returns before the append. Thus the audit requirement depends on delivery of the response, even after the destructive operation has happened.

This is reachable from the desktop dialog: closing it cancels its request (`StopSessionDialog.axaml.cs:196`), and its client has a ten-second timeout (`GatewayClient.cs:109`). It does not require a database fault.

**Observed:** a loopback host running the real endpoint and real `SessionCommandExecutor.KillAsync`, with a controlled tunnel delegate, removed a test session row before its reply was released. Cancelling the HTTP request at that point produced **zero audit rows**. The otherwise identical uncancelled control produced **one row with the exact reason**. These two probes used a buffer-only test session; they establish the endpoint's sequencing, not a live process kill or real network outage. Production forwards the same cancellation token through its tunnel invocation.

**Confidence: high.** Ruling 4's required audit is not guaranteed. The separately acknowledged best-effort database append is another way to lose it; that admission does not account for this ordinary cancellation path.

### I2 — P1: an unreadable live process is treated as a process that is gone

**Where:** `src/CcDirector.ControlApi/SessionCommandExecutor.cs:546`, especially `:558`, with consumption at `:433`, `:479`, `:496`.

`DefaultProcessIsAlive` returns `false` for every exception, including inability to read `HasExited`. Before the stop, that suppresses the subsequent process-exit verification; the row is removed and the verdict becomes `alreadyStopped`. During the final check, the same failure can instead certify `ProcessEnded = true`. An access failure establishes neither fact.

**Observed:** the operating system exposed a live process identifier while `HasExited` threw `Win32Exception`. An inert test backend supplied that identifier to the unchanged executor and deliberately performed no shutdown. The executor returned success, `alreadyStopped`, and `RowRemoved = true`; an independent operating-system lookup still found the process. The process was only read, never signalled or killed. This reproduces the native query failure rather than injecting a false liveness answer.

**Coverage defect confirmed:** replacing the production liveness method with the constant `false` left **all 68 tests in `SessionCommandExecutorTests` passing**. The tests that describe live-process outcomes inject their own check, so they cannot protect this production method. This confirms the gap already admitted at `phase-a-report.md:102`; it also shows an actual consequence of that gap.

**Confidence: high.** This is the mission's forbidden inference from “could not check” to “gone.”

### I3 — P1: a running remote session can be reported already stopped after cancellation fails

**Where:** the new classification at `src/CcDirector.ControlApi/SessionCommandExecutor.cs:424`, `:433`, `:513`; interaction with `src/CcDirector.Core/Backends/GitHubActionsBackend.cs:74`, `:158`.

The remote workflow backend reports process identifier zero even while a remote run is active. The executor therefore never performs a liveness check for it. Its shutdown method catches a failed `CancelRunAsync`, writes the failure to its buffer, then returns normally and raises its exit event. The new stop removes the session row and returns `alreadyStopped`, whose folded headline asserts that no process was running. A refused cancellation can leave the remote work running while the fleet has discarded its row.

This is a supported creation path, including `MainWindow.axaml.cs:2237` and `SessionManager.cs:970`. The comment that the process identifier supplies a backend-independent answer is false for this backend. The swallowed cancellation predates the branch; the branch's new factual verdict incorrectly treats that existing behavior as verified absence.

**Observed:** using the production remote backend with an active run identifier and a client fixture that rejects cancellation, the executor made one cancellation attempt, returned `alreadyStopped`, and removed the row. No external run was created or cancelled in this probe.

**Confidence: high.** The simulated condition is an ordinary remote cancellation failure; the classification and removal are production code.

### I4 — P1: the Cockpit destroys the stop answer on the next roster update

**Where:** `apps/cockpit/src/sessions/SessionMenu.tsx:60`, `:279`; its owners at `SessionRoster.tsx:134`, `:421`, and `SessionDetail.tsx:124`.

The answer is state owned by `SessionMenu`, but both placements of that component exist only while the session exists in the current roster. A successful stop removes that row. The roster's next refresh unmounts the menu and its portal, destroying the answer without `Done`, Escape, or a backdrop click. The shared poll interval is two seconds (`packages/client-core/src/fleet/rosterStore.ts:19`); an update can also arrive before the stop response, so the answer need never become visible.

**Observed:** render the real `SessionRoster`, stop its session, observe the returned headline and `Done`, then rerender with the stopped row absent. Both the dialog and headline disappear without dismissal. The same ownership failure is explicit in the detail page's `selected && <SessionMenu ...>` condition.

**Confidence: high.** The claim at `phase-b-report.md:76` that the answer stays until dismissed is contradicted by the actual parent lifecycle. Tests mounting `SessionMenu` alone do not exercise that lifecycle.

### I5 — P2: clients turn an unknown outcome into a definite outcome

**Where:** `tools/cc-devthrottle/src/session_ops.py:766`; `src/CcDirector.Avalonia/StopSessionDialog.axaml.cs:59`, `:169`; `packages/client-core/src/api/client.ts:1938`.

The CLI prefixes every Gateway exception with `Not stopped:`. The desktop dialog prefixes every exception with `The session was not stopped:`. A lost reply or timeout can happen after the stop, and the Gateway deliberately says that it does not know whether the command was carried out. Both clients add a contrary conclusion. The desktop's own missing-headline error also conveys uncertainty, which the dialog then overrides.

The shared web client makes the opposite unsupported claim for a malformed successful response: `The session was stopped, but...`. HTTP success alone cannot distinguish `stopped` from `notOnFleet`, and an unreadable body cannot supply that missing fact.

**Observed:** invoking the real CLI with the router's timeout sentence produced `Not stopped: ... It is not known whether the command was carried out.` The desktop prefix applies to all exceptions by construction; its test at `StopSessionDialogTests.cs:202` actually requires the prefix without testing an uncertain outcome.

**Confidence: high.** These are locally composed stop conclusions, not harmless transport labels. Successful, valid headlines otherwise are rendered verbatim.

### I6 — P2: repeating a stop by a name containing a slash fails instead of returning notOnFleet

**Where:** `tools/cc-devthrottle/src/session_ops.py:699`, `:758`.

An existing name resolves to its session identifier, so the first stop works. Once that row is gone, `_stop_target` returns the typed name and the caller interpolates it into the URL without encoding the path segment. For `Mission / Worker`, the second call becomes `sessions/Mission / Worker/stop`, which is no longer the stop route. `?` and `#` also acquire URL syntax instead of remaining part of the target.

**Observed:** the real CLI passed that exact unencoded path to its Gateway helper. Independently, the real endpoint returned **404** for the raw slash-containing path, while the encoded control returned **200, notOnFleet**. Existing raw-target tests stub the request helper and never subject the path to HTTP routing.

**Confidence: high.** This restores the second-stop error that Ruling 3 was intended to remove for a valid class of session names.

### I7 — P2: Enter can send repeated stops while the web controls say Stopping

**Where:** `apps/cockpit/src/sessions/SessionMenu.tsx:279`, `:455`; `apps/mobile/src/components/SessionAppBar.tsx:91`, `:265`; `useSessionManage.ts:194`.

The buttons disable while busy, but the reason inputs remain active. Their Enter handlers call functions that check the reason but do not check whether a request is already in flight. The mobile hook also lacks the busy guard that the previous remove operation had. Two Enter presses therefore send two stop requests. Their independently completing handlers can overwrite the first response with the second stop's outcome and details, or clear busy while another request is outstanding.

**Observed:** with the first call pending and the visible `Stopping...` button disabled, a second Enter produced **two calls** in each shell. The mobile observation used its real management hook. This was not a synthetic click on a disabled control. Response-order corruption follows from the two uncoordinated state updates; the probes directly establish the duplicate requests.

**Confidence: high.** The added Enter tests cover empty reasons and initial submission, but not submission during a pending call.

### I8 — P2: the phone's stop failure is outside the modal, and dismissing the modal deletes it

**Where:** `apps/mobile/src/components/SessionAppBar.tsx:111`, `:221`, `:248`; `apps/mobile/src/styles.css:3138`.

On failure the confirmation modal stays open, but its error is rendered in the app bar's sibling banner. The full-screen overlay is still above it, and the modal declares `aria-modal="true"`. There is no failure text inside the active dialog. Pressing Cancel to get back to the underlying screen immediately clears that banner as well. The operator is left with the reason box and a retry button instead of a failure explanation in the sheet.

**Observed:** rejecting the stop through the real mobile hook created an alert outside the dialog; querying within the dialog found no error. Cancel removed the only alert. The existing sheet test supplies an error on its stub before the action and searches the whole document, so it does not prove that a failed stop communicates through the modal. Pixel rendering and a screen-reader session were not exercised here; the modal containment and clearing behavior were.

**Confidence: high.** This contradicts the shared failure experience claimed at `phase-b-report.md:80`.

## Build, suite and mutation evidence

These are inspection runs, not numbers copied from the phase reports.

| Run | Observed result |
|---|---|
| `scripts/test-local.ps1 -Parked` | Build succeeded. All 11 suites completed: **12,559 passed, 4 failed, 63 skipped; 12,626 total**. Gate exit **1**. |
| Cockpit | **317 passed**, 36 files, exit 0. |
| Mobile | **55 passed**, 9 files, exit 0. |
| Shared client | **1,041 passed**, 98 files, exit 0. |
| Workspace type checking | All four workspaces completed, exit 0. |
| CLI full suite, from its tool directory | **266 passed, 2 failed**, exit 1. |
| CLI stop test file | **25 passed**, exit 0. |
| Additional web observation probes | **4 passed**; they assert the defects described above, not corrected behavior. |
| Additional endpoint/executor observation probes | **5 passed**, including the audit control and cancellation case. |

The four local-gate failures were:

- `PythonToolsHealAndShimTests.InstallAsync_FailedVenvRebuild_LeavesNoManagedShim`: a managed shim remained after the failed rebuild fixture. Its isolated rerun passed, one test executed.
- `LifecycleSignalTests.EachRaise_RunsTheHandlerExactlyOnce`: its timing assertion failed. Its isolated rerun passed, one test executed.
- The two `PathContainmentLinkEscapeTests` file-symbolic-link cases: this host cannot create the required links. These remain unproven here.

Those test files are unchanged by the branch. The isolated passes do not turn the full gate green or prove the failures harmless. The CLI failures are the existing email/spawn help-rendering tests, failing inside the installed command-line dependencies. A first CLI run with `FORCE_COLOR=1` additionally failed six wrapping assertions, four in the new stop file; removing that inspection setting produced the results above. Those extra failures were rendering-environment sensitivity, not six additional product findings.

Mutation sampling was performed without modifying tracked sources:

| Mutation | Result | Restored control |
|---|---|---|
| Replace the real executor liveness check with constant false in isolated output | **68 passed, zero failed**: an uncovered production check. | Executor plus fold: **95 passed**. |
| Make the Gateway fold print clean for an unknown worktree | **1 failed, 26 passed**. The named unknown-worktree test failed on the changed detail sentence. | Executor plus fold: **95 passed**. |
| Replace the Cockpit's rendered headline with a constant during module loading | **8 failed, 12 passed**. Tests failed because the supplied Gateway headlines were absent. | **20 passed**. |

Thus the latter two sampled mutation claims are independently supported. This does **not** establish the reports' broader claim that every new test was individually watched failing, nor that their chosen mutations cover every required behavior. The surviving constant and the parent-lifecycle failure show why that distinction matters.

Local evidence remains in `.temp/inspection-web.test.tsx`, `.temp/inspection-web-probes.log`, `.temp/inspection-dotnet/InspectionTests.cs`, `.temp/inspection-observations-final.log`, the `.temp/inspection-mutation-*.log` files, and `.temp/inspection-dotnet-restored.log`. The full gate's logs and result files are under `%TEMP%/cc-test-local-4a86c554`. The two unsuccessful mutation-tool setup attempts did not execute tests or count as mutation evidence; the recorded results above came from completed runs after resolving the tool's dependencies and restoring its isolated outputs.

## Answers to the remaining inspection questions

- **Claims versus code:** I1–I8 identify concrete counterexamples. In particular, the normal smoke-test audit rows do not establish audit reliability, and a component that delays its own close does not control whether its parent removes it.
- **Unguarded behavior:** the native liveness check, refused remote cancellation, cancelled-request audit, roster-driven removal, busy Enter, routing of unmatched names, and failure text inside the phone modal all escape the cited tests. The desktop menu-to-host wiring remains untested end to end, as its own report admits.
- **Who writes the headline:** valid responses use the one Gateway fold and clients preserve its strings. I5 is the unsupported client interpretation on exceptional responses.
- **Gone when nothing looked:** no-fleet and stale-Director paths remain distinct. The no-row-on-an-asked-Director behavior follows the explicit handoff. I2 and I3 show other paths that still claim absence without establishing it.
- **Reason and legacy routes:** the POST rejects missing, empty and whitespace-only reasons. Bare DELETE remains refused to session keys; DELETE of `request-deletion` is narrowly allowed. The retained device/native-phone DELETE path may omit a reason by the explicit compatibility ruling. This is not an additional finding.
- **Second stop implementation:** the moved Cockpit, browser phone and desktop control go through the shared stop handler/fold. The native phone compatibility door, whole-Director shutdown and polite deletion reaper are explicit exceptions. No additional interactive bypass was established.
- **Unknown worktree versus clean:** the production fold keeps them separate, and the mutation actually fails. The documented ten-second status cache remains a limit on freshness, not an independently discovered defect.
- **What the default gate misses:** all web and Python behavior, plus the two suites behind `-Parked`, including host-bound route and tenant tests. This inspection ran those suites as well as the web and CLI suites. In the full Gateway results, all **48 executed cases** matching the stop endpoint, tenant-isolation and outcome-ledger test classes passed. Neither that count nor the broader suite proves real session-key authentication composed with a real tunnel and a live stop; the new route tests use controlled credentials/answers. No browser pixels, real desktop interaction, cross-machine stop, or independent replay of the builders' live-stack smoke test was performed.

The findings are ready for the Architect to assign. No fixes, commits, merges, or deployment were performed.
