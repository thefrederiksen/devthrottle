# Brief - phase 3 proof Worker: the frame cannot reach the app, and end to end on a local Gateway

You are a Worker on the Dev Reports mission. Your supervisor is the phase 3 Manager (session 14fde710). You report
to it - one line, `cc-devthrottle message send 14fde710 "..."` - when you are ready for the viewer build, and when
done. You never contact the owner. Conduct: `cc-devthrottle workflow instructions mission --version 17`.

Issue: #3010. Your worktree: `D:\ReposFred\devthrottle-dev-reports-p3-proof`, branch `mission/dev-reports-p3-proof`,
cut from `mission/dev-reports-p3`. Commit and push to that branch. Never merge to main or to the p3 branch.

## Context

Another Worker (session ef74f29e, branch `mission/dev-reports-p3-viewer`) is building the Reports view in
`packages/client-core`, `apps/cockpit` and `apps/mobile` right now, to `BRIEF-phase-3-viewer-worker.md` beside this
file. You prove it. You do NOT change its product code; a defect you find goes to the Manager in one line with the
evidence file path. Read: `HANDOFF-phase-3.md` (the "Proof required" section is your mandate), `STATE.md`,
`packages/client-core/src/devreports/CONTRACT.md` (section 4 is what you are proving), `PHASE-2-REPORT.md`,
`src/CcDirector.Gateway/Api/DevReportEndpoints.cs`, `tools/cc-dev-reports`, and the existing browser proof in
`packages/client-core/browser-tests/dev-report-notes-proof` (reuse its Playwright loading and its hostile report).

## Part 1 - start now, before the viewer lands

Build the rig so that when the viewer branch is ready you only merge it in and run:

- **A local Gateway you start yourself** from your worktree, isolated: its own `CC_DIRECTOR_ROOT` scratch root, its
  own port, `CC_GATEWAY_NO_TAILSCALE=1`, never touching the owner's installed Gateway, the hosted one, or port 443.
  Find out how the current code actually launches, authenticates, serves `wwwroot/c` and `wwwroot/mobile`, and
  shuts down - older recipes mention routes that have since been removed, so verify against the code. Prove the
  Gateway you are hitting is the one you built (its reported version against your commit).
- **A live session on it**, so that publishing, delivery and replies are real. If that needs a Director, use slot 5
  or higher via the Task Scheduler launch in the repository `CLAUDE.md`, connected to YOUR Gateway only. Never stop,
  restart or reconfigure any other Director. Shut yours down with the named signal the `CLAUDE.md` describes.
- **Fixtures**: a well-formed report with a table (row and column headers) and a question; a republished version of
  it; and a hostile report. The Gateway shape check refuses `<script>`, so the hostile report must attack by what
  the shape check lets through or by what a navigation brings in: a plain link or meta refresh to a page whose
  scripts run (served by your rig on another origin), CSS, forms, `data:` and `javascript:` URLs, and a report
  crafted as though a script did run (use the existing `hostile-report.html` and `evil.html` for ideas). Also
  load a hostile document into the frame directly through the host (bypassing the shape check) to prove the
  Content-Security-Policy and token, not the shape check, are the boundary.
- **The harness**: Playwright as a library (deliberate: a repeatable scripted proof with assertions, the same as the
  existing notes proof), in `packages/client-core/browser-tests/dev-report-viewer-proof/` with a README in the
  style of the notes proof. It prints PASS or FAIL per claim, exits non-zero on any failure, and writes an evidence
  file and screenshots.

## Part 2 - when the Manager tells you the viewer branch is ready

Merge `origin/mission/dev-reports-p3-viewer` into your branch, build both apps, put them where your Gateway serves
them, and prove on the BUILT apps:

**The frame cannot reach the app** (Cockpit and phone both): the hostile report cannot read the app's local
storage, cookies or DOM; cannot post a `send` the host accepts without the token (a forged ready, a second ready,
a message on the window); and after a navigation away the page it lands on gets nothing and cannot send. Each
check must be one you have seen FAIL with its guard removed (edit the built bundle or the host in a scratch copy,
never commit the mutation) - record the red output.

**End to end, at phone width (390 by 844)**: `cc-dev-reports open` publishes the report; open it in the phone app;
note a table cell; answer the question; Send; read back from the Gateway (its database or log) the prompt it
composed and show it names the row, the column and the chosen option; `cc-dev-reports reply` and watch the reply
appear; republish and watch the page reload in place with the scroll position and a half-typed note kept. Then
the same report in the Cockpit at desktop width. Also: end the session and show Send displays the Gateway's
refusal sentence. Screenshots of each step at both widths.

## Rules

- Plain English, ASCII only. No attribution in commits.
- Foreground only. A long command is narrowed, not backgrounded.
- Tear down everything you started: your Director (named signal), your Gateway, any scheduled task you registered,
  any container. Confirm each is gone by process path.
- When done: evidence committed and pushed, then ONE line to the Manager naming the commit, the pass count, and
  anything that did not pass.
