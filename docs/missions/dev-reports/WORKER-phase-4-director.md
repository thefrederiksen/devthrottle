# Phase 4, Worker 2: the Director's Reports pane - what was built and what is proven

Issue #3019. Branch `mission/dev-reports-p4-director`, cut from `mission/dev-reports-p4`, worktree
`D:\ReposFred\devthrottle-dev-reports-p4-director`. Never merged, never a pull request.

Read against `STATE.md` (rulings 1-9), `FINDING-phase-4-sign-in.md` (option 1, which this builds),
`BRIEF-phase-4-embed-worker.md` and the page Worker 1 actually produced (merged in and checked, see
"Where the two halves meet"), and `packages/client-core/src/devreports/CONTRACT.md` section 4.

---

## What the owner gets

A **Reports** button beside the session, in the tab bar. Pressing it opens that session's dev reports in a
document tab, the way a file viewer opens - the pane shows "Loading..." at once, then the session's reports:
the list, the report itself, notes on it, its questions, and the conversation. It is the SAME list, viewer,
frame host and note-taking script the Cockpit and the phone use. Nothing was rewritten in C#.

---

## What was built

### 1. The Gateway hands back the pane's address

`GET /dev-reports/pane-url?sessionId=<id>` answers `{ "url": "<absolute address>" }`. It sits with the four
owner routes in `src/CcDirector.Gateway/Api/DevReportEndpoints.cs`, so it inherits their identity refusal and
tenant scoping. A session key is refused; a missing or malformed session identifier is a 400 with a plain
sentence; a request carrying no host at all is a 409.

The address is built from the base the CALLER reached the Gateway on, not from `GatewayPublicUrl`. That is
deliberate and is the point of the route: the host embedding the page authenticates it with the key it holds
for THAT Gateway, so handing back a hosted or tailnet address the caller did not reach would point the page
at a Gateway whose reports that key may not open at all.

Every decision below the identity and tenant checks is one pure function,
`DevReportPaneUrl.Answer(scheme, host, pathBase, sessionIdQuery)`, in
`src/CcDirector.Gateway/DevReports/DevReportPaneUrl.cs`.

### 2. THE GAP NEITHER BRIEF ACCOUNTED FOR: the page could not load at all

**Both phase 4 briefs assume the reports page loads. It did not, and could not.** `AuthMiddleware` gates
every path that is not on its explicit public list, and a browser navigation with no credential is redirected
to `/signin`. The Director's web view has no Gateway credential and never gets one - the whole design is that
the host hands it a key in memory AFTER the page has loaded. So the pane would have shown the Cockpit's
sign-in screen, every time.

Worker 1's brief forbids touching C#, so this could only be done here. `/embed/reports/{sessionId}` is now a
public shell surface, for exactly the reason `/signin` and the phone shell are: the page carries no data and
no secret, and every dev report route it then calls stays owner-only and account-scoped. Precisely one
segment after the prefix is public, GET and HEAD only, so a deeper path or a future write route under
`/embed` is credential-gated by default. `/embed` also joined `ShellPrefixRouteSurfaceGuardTests`, so an
endpoint mapped under it later fails a test until somebody rules on it.

**The Manager should know this changes what Worker 1's page is: it is served pre-credential.** Nothing about
the page's own behaviour changed, and no report data opened.

### 3. The Director pane

- `src/CcDirector.Avalonia/Controls/DevReportsPaneControl.axaml(.cs)` - a `WebView2Host` control, the same
  host `HtmlViewerControl` uses, opened as a document tab for the selected session and titled
  `Reports - <session>`.
- The entry point is `TabBarReportsButton` in `MainWindow.axaml`, in the tab bar beside Capture and Reset
  View, shown whenever a session is selected (the reports are about the session, not about the terminal).
  Nothing else on the session screen changed.
- `MainWindow.OpenDocumentFile` was refactored into `OpenDocumentTab(key, title, viewer, control)`, so the
  file viewers and the pane open through ONE tab path rather than two. The pane's key is
  `dev-reports:<sessionId>` - not a file, nothing opens it - so pressing Reports twice returns to the tab
  already open and two sessions get two tabs.
- **Immediate feedback:** the pane's status text is "Loading..." from the constructor, before any Gateway
  call. The call itself runs inside `Task.Run` and is never on the user interface thread.
- It asks the Gateway for the address (`DevReportPaneUrlClient`) and navigates to exactly that address. It
  composes no page address. When the Gateway answers without one, the pane says so and shows nothing - it
  does not guess a path.

### 4. The bridge, and what it refuses

The pane answers with `GatewayConfig.Load().Token`, in memory, to the page, and nowhere else.

- `WebMessageReceived` only - which fires for the TOP-LEVEL document. `FrameCreated`,
  `FrameNavigationStarting`, `AddHostObjectToScript` and `AddScriptToExecuteOnDocumentCreated` are all
  absent, so the agent-written report in its sandboxed frame has no path to the bridge at all.
- A message earns an answer only when its source address is exactly the pane address the Gateway handed back
  AND its kind is `dev-report-host-ready`. Anything else is logged and ignored.
- The answer is `{"kind":"dev-report-host-key","key":"...","sessionId":"..."}` by `PostWebMessageAsJson`,
  which reaches the top-level document only.
- `NavigationStarting` cancels any top-level navigation that is not the pane address (or `about:blank`, the
  empty document, which is named explicitly rather than matched loosely) and logs it.
- A second, independent lock on the same door: `ContentLoading` sets whether the bridge is open, from the
  address of the document that is STARTING to load. A document at any other address gets nothing, and only
  loading the pane address again opens it. It is read at content-loading time on purpose - a check made after
  the page had loaded would race the page's own first message and could silently never hand the key over.
- The key reaches no log line, no file, no error message and no address. The pane logs THAT a key was handed
  over, never the key.

Every one of those decisions is a pure function in `src/CcDirector.Avalonia/DevReports/DevReportPaneBridge.cs`.

---

## Where the two halves meet

Worker 1's page was merged in and read, not assumed. The three things that had to agree, do:

| | Worker 1's page | This pane |
|---|---|---|
| Page path | `EMBED_REPORTS_PATH_PREFIX = "/embed/reports/"` | `DevReportPaneUrl.PagePathPrefix = "/embed/reports/"` |
| Page to host | object `{kind:"dev-report-host-ready", sessionId}` posted with `window.chrome.webview.postMessage` | read from `WebMessageAsJson`; extra fields ignored, kind must be exactly that |
| Host to page | object `{kind, key, sessionId}`, and its `hostBridge.ts` says explicitly it must be sent with `PostWebMessageAsJson`, not the string form | `PostWebMessageAsJson` of exactly that object |

The page's own note says the string form "looks exactly like a host that never answered". This pane sends the
object form.

---

## What is proven, and how

### Every new test was watched failing on purpose

Each mutation below was made, the run watched going red with the symptom named, then reverted and watched
green again. Nothing here was inferred.

| What was broken | Symptom watched |
|---|---|
| Removed the public-page rule from `AuthMiddleware` | 3 unit tests red: the page unreachable without a credential |
| Same, on a real booted Gateway | `anonymous GET .../embed/reports/<id> -> 302 location=/signin?next=...` - the exact defect |
| Built the address from a hardcoded base instead of the caller's | 5 red: "Strings differ" on every Build and Answer test |
| Widened `IsPagePath` to any depth under the prefix | 3 red, including the gate test that a path under the page stays refused |
| Renamed the literal route so `/dev-reports/pane-url` fell to `/dev-reports/{reportId}` | 3 red with `Expected: OK / Actual: NotFound` - route precedence proven, not remembered |
| Opened the guard allow list for pane-url | still GREEN - the route's own `RefuseSessionIdentity` held it alone |
| Opened the guard AND removed `RefuseSessionIdentity` | red: `Expected: Forbidden / Actual: OK` - an agent reads the owner's pane address |
| Dropped the message-source check in the bridge | 3 red: a page on another host, and another session's page, both earn the key |
| Dropped the message-kind check | 8 red: any message from the pane address earns the key |
| Made `NavigationIsAllowed` return true | 10 red: `file://`, `data:`, another origin, another port all allowed |
| Subscribed to `FrameCreated` in the pane | red, naming the line: the report's frame would have a path to the bridge |
| Logged the key in the pane's "handed over" line | red, naming the line |
| Made the client compose a page address instead of reading the Gateway's | 2 red: the Gateway's answer no longer used verbatim |

**A finding that came out of this, and which the Manager and the inspector should have:** opening the
`SessionKeyGuard` allow list alone did NOT turn the hosted proof red, because every owner route carries a
second refusal of its own. Both layers exist and either one holds the line alone. The class comment on
`DevReportEndpoints` said the opposite - "there is deliberately no second check on the owner routes" - while
that second check sat twenty lines below it. The words were corrected to the code, not the code to the words.

### Test numbers

**The default gate - GREEN.** `.\scripts\test-local.ps1`: eight suites, every one `outcome=Completed`,
2,112 tests, 0 failed. It printed a COVERAGE GAP for `CcDirector.Gateway.Tests` and
`CcDirector.Gateway.UnitTests`, which is why the parked run below was done too.

**The parked gate - `.\scripts\test-local.ps1 -Parked`, 11 suites, 2 hours 20 minutes.** Nine suites green.
Two suites reported ONE failure each, and **neither is in dev reports**. Both were reproduced on clean
`origin/main`, so both are pre-existing and belong to other work:

| Failing test | Why it is not this change |
|---|---|
| `Gateway.UnitTests` `TurnsVerbUnresolvedTranscriptTests.Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)` - expected `no_transcript`, got `ok` | **Measured, not argued:** run in a throwaway worktree detached at `origin/main` (e38217bc6), it fails identically. It depends on this machine having no locatable Grok transcript. |
| `Gateway.Tests` `PostgresProviderProofTests.Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres` - the real schema has `fleet_manager_marks`, the test's declared list does not | **Read off `origin/main` directly:** the table arrives in main's own migration `20260917090109_AddFleetManagerMarkHistory`, and main's copy of that test contains the string `fleet_manager_marks` zero times. This branch changes no entity and no migration (`git diff origin/main...HEAD` over `Gateway/Data` and `Gateway.Migrations.Postgres` is empty). It is the Fleet Manager work's to fix. |

Dev report numbers, as the brief asked:

- `CcDirector.Gateway.UnitTests` (parked) - **33 new tests, all passing** (`DevReportPaneUrlTests` 26,
  `DevReportPanePageIsPublicTests` 7), on top of the existing `DevReports/` suite. Suite total 5,379 passed.
- `CcDirector.Gateway.Tests` `DevReportRoutesHostedTests` (parked) - **20 of 20 passing**, 3 of them new
  (`ThePaneUrlRoute_AnswersThePageAddressOnTheCallersOwnBase`,
  `ThePaneUrlRoute_WithNoSessionOrAMalformedOne_Is400WithAPlainSentence`,
  `ThePaneAddress_IsServedToABrowserWithNoCredential_NotRedirectedToSignIn`), plus `pane-url` added to
  `ASessionKey_IsRefusedEveryOwnerRoute`.
- `CcDirector.Avalonia.Tests` - **55 dev report tests**, all passing, inside a green 601-test suite.

A note for whoever runs the gate next: an orphaned `testhost.exe` from this worktree's own earlier
`Gateway.Tests` run held the machine-wide test lock for about 35 minutes after that run had reported
`Passed! 20` and exited. It was idle (0.03 processor seconds over 5 seconds of wall clock), and the parked
run correctly queued behind it and started by itself once it finally went. Nothing was killed. If a parked
run looks hung, read
`%LOCALAPPDATA%\cc-director\test-locks\gateway-test-suite.lock.log` - it names the holder and says plainly
that waiting is not a hang.

---

## What is NOT proven

Stated before anybody asks, because a proof that covers the wrong thing is worse than none.

- **Nothing here ran a Director.** No Director was started, stopped or restarted by this worker, per the
  brief. The pane has never been seen on screen. The tab wiring, the button, the "Loading..." appearing
  first, the web view actually loading the page, and the key actually reaching the page are all for the
  Manager's live run on its own slot.
- **The bridge was never exercised through a real WebView2.** Every bridge decision is proven as a pure
  function. What is NOT proven here is the wiring around them: that `WebMessageReceived` really is
  top-level-only in this WebView2 version, that `ContentLoading` really does fire before the page's script,
  and that `NavigationStarting` really does not fire for the report's frame. Those are claims about
  WebView2's own behaviour and only the live proof can hold them. **If the live run shows the page saying it
  was never handed a key, the ordering of `ContentLoading` against the page's first message is the first
  thing to look at.**
- **The key never reaching a file is proven only for this one source file.** The guard test reads
  `DevReportsPaneControl.axaml.cs`. It cannot prove no other Director code logs a Gateway key, and it says
  nothing about what WebView2 itself writes into its own user data folder. Claim D12 of the proof plan -
  searching the whole run for the key - is the one that covers that, and it is the Manager's.
- **The hostile reports were not run against the pane.** Claims D9 to D11 are the Manager's live run.
- **Nothing was checked against a hosted Gateway.** Every Gateway test here is a local one.
- **The page address being public was proven at the gate and on a booted local Gateway, not on the hosted
  one.** The hosted Gateway runs the same `AuthMiddleware`, but that is reasoning, not a measurement.
- **The two pre-existing failures were reproduced on `origin/main`, not fixed.** They are still red on this
  branch and will be red on whatever the Architect lands, until the work that owns them fixes them.

---

## What the Manager needs to run the live proof

1. **Opening the pane:** select a session, then press **Reports** in the tab bar, on the right beside
   "Capture" and "Reset View". It is visible whenever a session is selected. A document tab titled
   `Reports - <session name>` opens and is switched to.
2. **What to expect, in order:** "Loading..." immediately; then the Gateway call; then the page. If the
   Gateway cannot be reached or refuses, the pane replaces "Loading..." with the sentence saying so - it
   never sits blank.
3. **This Gateway deploy is required first.** The page address route AND the rule that serves the page
   without a credential are both Gateway code. A Director pointed at a Gateway without them gets a 404 on
   the address route, and the pane will say so. The rig Gateway must be built from this branch.
4. **Reading the log:** everything the pane does is under `[DevReportsPane]` in the Director log, and the
   Gateway side under `[DevReportEndpoints] GET /dev-reports/pane-url`. The line to look for is
   `handed the Gateway key to the reports page for session=<id>`. If it is absent, read the
   `web message ignored:` line just before it - it names which check refused and why.
5. **For claim D12**, the key is whatever `gateway.token` is in that rig's `config.json`. Nothing in this
   worker's code writes it anywhere; search the rig logs and the evidence directory for that literal value.

---

## Files

New:

- `src/CcDirector.Gateway/DevReports/DevReportPaneUrl.cs`
- `src/CcDirector.Gateway.UnitTests/DevReports/DevReportPaneUrlTests.cs`
- `src/CcDirector.Gateway.UnitTests/DevReports/DevReportPanePageIsPublicTests.cs`
- `src/CcDirector.Avalonia/DevReports/DevReportPaneBridge.cs`
- `src/CcDirector.Avalonia/DevReports/DevReportPaneUrlClient.cs`
- `src/CcDirector.Avalonia/Controls/DevReportsPaneControl.axaml(.cs)`
- `src/CcDirector.Avalonia.Tests/DevReports/` (four test files)

Changed:

- `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` - the route, and the corrected refusal paragraph
- `src/CcDirector.Gateway/Util/AuthMiddleware.cs` - the page is a public shell surface
- `src/CcDirector.Gateway.Tests/ShellPrefixAllowlistTests.cs` - `/embed` joins the route-surface guard
- `src/CcDirector.Gateway.Tests/DevReportRoutesHostedTests.cs` - three new tests, one extended
- `src/CcDirector.Avalonia/MainWindow.axaml(.cs)` - the Reports button and the one tab path
