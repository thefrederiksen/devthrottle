# Handoff - phase 4: the Director's Reports pane

Read first: `STATE.md` (rulings 1-9 bind you), issue #2936, `packages/client-core/src/devreports/CONTRACT.md`
section 4, and `PHASE-3-REPORT.md`. Phases 1-3 are on main and deployed. Open a child issue of #2936 first.

## What this phase makes true

In the Director, beside a session, the owner opens that session's reports, reads one, leaves notes and answers,
presses Send, and sees the Gateway's states and the agent's replies - exactly as in the Cockpit and on the phone.

## Design (Architect ruling)

**One implementation.** The report list, viewer, frame host and note handling already exist once, in
`packages/client-core`, and are proven against a hostile report. Do NOT reimplement them in C#. The Director pane
hosts that same web view in WebView2 - build on the existing `HtmlViewerControl`
(`src/CcDirector.Avalonia/Controls/HtmlViewerControl.axaml.cs`) or a sibling of it.

First, find out how the Director can show a Gateway-served web page as the OWNER (owner-only routes need owner
credentials, not a machine token): whether the Director already embeds any Cockpit page, how it authenticates, and
whether a bare report route (list and viewer without the Cockpit chrome) exists or must be added to the Cockpit.
Prefer, in order: (a) a bare Cockpit route for one session's reports, signed in the way the Director already signs
in to Gateway web pages; (b) if the Director has no owner sign-in for web pages, bring the Architect ONE plain
finding with a recommendation before building a sign-in - that is a design fork, not a detail.

Follow the Director's rules: responsive UI (the pane shows immediately with "Loading..."), logging on entry, exit
and errors, try/catch only at entry points, tests named Method_Scenario_Result, docs/VisualStyle.md.

## Proof required

- Unit tests for the new Director code; the frame isolation in WebView2 proven with the hostile report used in
  phase 3 (the report cannot reach the Director page or post without the token).
- A real Director on slot 5 or higher (Task Scheduler launch per the repository instructions), against a local
  Gateway you start yourself: open a session's report in the pane, note a table cell, answer, Send, read the prompt
  back from the Gateway. Screenshots. Never stop or restart any other Director; shut yours down with its named
  signal.
- `.\scripts\test-local.ps1` green plus the web tests if you touch client-core or the Cockpit.
- The Architect calls the independent inspection; do not seat a reviewer.

## Done means

A pull request from `mission/dev-reports-p4` to main, not merged, `PHASE-4-REPORT.md` (what the owner gets, what is
proven, what is not, what to try), everything pushed. Tell the Architect in one line if `message send` works; if it
fails, the pushed report is enough. Read your Workers' terminals; never wait on a message.
