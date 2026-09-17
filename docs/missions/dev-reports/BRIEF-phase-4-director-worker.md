# Phase 4, Worker 2: the Director's Reports pane

Issue #3019 (child of #2936). Branch `mission/dev-reports-p4`, worktree `D:\ReposFred\devthrottle-dev-reports-p4`.
You are a Worker. You report to the phase 4 Manager, nobody else. Read `docs/missions/dev-reports/STATE.md`
(rulings 1-9 bind you), `docs/missions/dev-reports/FINDING-phase-4-sign-in.md`,
`docs/missions/dev-reports/BRIEF-phase-4-embed-worker.md` (the page you will host, built by Worker 1 - read the
page it actually produced, not only the brief), and `packages/client-core/src/devreports/CONTRACT.md` section 4.

## What this makes true

In the Director, beside a session, the owner opens that session's reports, reads one, leaves notes and answers,
presses Send, and sees the Gateway's states and the agent's replies - exactly as in the Cockpit and on the phone.

There is ONE implementation of the list, the viewer, the frame host and the notes, and it is the web one in
`packages/client-core`. You host it. You do not rewrite any of it in C#.

## The work

### 1. The Gateway hands back the pane's address (rule 7 - the client is dumb)

The Director must never compose a Cockpit URL. Add one owner route, in
`src/CcDirector.Gateway/Api/DevReportEndpoints.cs` beside the four owner routes so it inherits their identity
refusal and tenant scoping:

`GET /dev-reports/pane-url?sessionId=<id>` answers `{ "url": "<absolute address of the reports page for that
session>" }`, built by the GATEWAY from the address this caller reached it on, plus the route Worker 1 added.
A session key is refused exactly as on the other owner routes; a missing or malformed session identifier is a
400 with a plain sentence.

Tests in `src/CcDirector.Gateway.UnitTests/DevReports/` (and a hosted route test beside the existing
`DevReportRoutesHostedTests.cs` if the shape there fits): the address is built from the caller's own base; a
session key is refused; a malformed identifier is refused.

### 2. The Director pane

- A new control in `src/CcDirector.Avalonia/Controls/` built on `WebView2Host` (the same host
  `HtmlViewerControl` uses). It opens as a document tab for the selected session, the way a file viewer does
  (`MainWindow.axaml.cs`, `CreateViewer` / the document tab code), titled for that session's reports.
- An entry point on the session, following `docs/VisualStyle.md` and whatever the session's other actions do.
  Keep it obvious and small; do not redesign the session screen.
- **Immediate feedback:** the pane appears at once showing "Loading...", before any Gateway call. Every Gateway
  call is off the user interface thread.
- It asks the Gateway for the pane address (step 1) and navigates the web view to exactly that address.

### 3. The bridge - and what it must refuse

The page asks the host for the Gateway credential; the Director answers with the key it already holds
(`GatewayConfig.Load().Token`). The key is passed in memory, to the page, and nowhere else.

- Handle `CoreWebView2.WebMessageReceived`, which fires only for the TOP-LEVEL document. Answer only when the
  message source address is the pane address the Gateway handed back, and only the message kind
  `dev-report-host-ready`. Anything else is logged and ignored.
- Answer with `{"kind":"dev-report-host-key","key":"<key>","sessionId":"<id>"}` by
  `PostWebMessageAsJson`, which reaches the top-level document only.
- Never subscribe to `CoreWebView2.FrameCreated` or add a host object to script. The report's frame must have no
  path to the bridge at all.
- **A navigation away ends it:** on `NavigationStarting`, cancel any top-level navigation whose address is not
  the pane address, and log it. If the top-level document ever changes anyway, the pane stops answering the
  bridge until it loads the pane address itself again.
- The credential never reaches a log line, a screenshot, an error message or a file. Log that a key was handed
  over, never the key.

Put every decision in the bridge into PURE functions (does this message source earn an answer, is this
navigation allowed, what is the message body) so they are unit-tested with no user interface and no web view.

## Repository rules that bind you

Responsive user interface (feedback under 100 milliseconds, nothing blocking the interface thread), logging on
entry, exit and error (`FileLog.Write($"[ClassName] Method: ...")`), try/catch ONLY at entry points, no
fallbacks, tests named `Method_Scenario_Result`, plain English with no abbreviations, no Unicode or emoji in any
output, and nothing anywhere that names an assistant or its vendor.

## Proof required

- `.\scripts\test-local.ps1` green. You touched the Gateway, so also run the parked Gateway unit suite and
  report the dev report test numbers.
- Every new test watched failing on purpose: break what it proves, see it red with the symptom, restore, see it
  green. Write down what you watched.
- Do NOT run a real Director yourself and do NOT start, stop or restart any Director. The Manager runs the live
  proof on its own slot.

## When you are done

Commit and push to `mission/dev-reports-p4` as you go (never merge, never touch main, never open a pull
request). Write `docs/missions/dev-reports/WORKER-phase-4-director.md`: what you built, what is proven and how,
what you watched fail, what is NOT proven, and anything the Manager must know to run the live proof (how to open
the pane, what to expect). Commit that too. Then tell the Manager in ONE line and stop.
