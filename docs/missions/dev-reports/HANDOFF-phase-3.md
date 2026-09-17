# Handoff - phase 3: the Reports view in the Cockpit and on the phone

Read first: `STATE.md` (rulings 1-9 bind you), issue #2936, `packages/client-core/src/devreports/CONTRACT.md` -
section 4 (Trust) is the law for a host - and `PHASE-2-REPORT.md` for the Gateway routes. Open a child issue of
#2936 first.

**Branch and parallel work.** You are on `mission/dev-reports-p3`, cut from `mission/dev-reports` while a phase 2
fix round (delivery cancellation, publish race, a response header) still runs there. Those fixes do not change the
routes. Merge `origin/mission/dev-reports` into your branch when it moves; do not edit Gateway delivery code.

## What this phase makes true

The owner opens a session in the Cockpit or on the phone, taps **Reports**, sees that session's reports, opens one,
leaves notes and answers questions, presses Send, and watches each item go queued -> held -> delivered as the
Gateway rules it. The agent's replies appear. When the agent republishes, the page reloads in place, keeping the
scroll position and anything half-typed.

## Design (Architect rulings for this phase)

1. **One implementation, two shells** - the rule that governs Settings applies here. The report list, the viewer
   and the host protocol live ONCE in `packages/client-core` and both apps mount them. Each shell supplies only its
   frame: a **Reports** tab beside the session's other tabs in the Cockpit; on the phone a Reports entry on the
   session screen, the report full screen, and the conversation (queued, sent, replies) as a bottom sheet.
2. **The frame follows CONTRACT.md section 4 exactly**: `sandbox="allow-scripts"` with no `allow-same-origin`, the
   host writes the head with a Content-Security-Policy that lets only its injected note script run (fresh nonce
   per load), a fresh token per load, traffic over the private message port, and any frame load the host did not
   cause ends that token. The note script is bundled from its one source file, never copied.
3. **The host holds state** (the frame cannot): queued items, half-typed text, unqueued answers and scroll
   position, per report and version, surviving a reload of the frame and of the app (local storage is fine for
   this - it is the owner's own unsent draft).
4. **The Gateway rules, the client renders** (repository rule 7). Item states, labels, refusal reasons and replies
   come from the Gateway verbatim. No client decides what a state means. Refresh while a report is open by the
   mechanism the apps already use for live session data; if there is none, poll the detail route at a modest
   interval only while the report is on screen.
5. **Ended sessions**: the report stays readable; Send shows the Gateway's refusal sentence.
6. **The tray looks like the app**: the note tray currently falls back to a serif font. Give it the app's type and
   colours (docs/VisualStyle.md), inside its shadow root, without letting report CSS reach it.
7. **Access**: the apps call only the owner routes. A report the Gateway answers 404 for simply does not appear.

## Proof required

- Unit tests for the host protocol (token, port, restore, stale-token refusal, reload keeps state) and for the
  list and viewer rendering Gateway states verbatim.
- **The frame cannot reach the app (tested)**: a real-browser test on the built Cockpit and phone app with a
  hostile report proves the report cannot read app storage, cookies or DOM, cannot post as the owner without the
  token, and cannot keep a token after a navigation.
- **End to end on a local Gateway** (a Gateway you start yourself, never the hosted one, never another session's):
  publish a report with `cc-dev-reports open`, open it in the built phone app at phone width, note a table cell,
  answer a question, Send, and read back from the Gateway the prompt it composed naming the row and column and the
  chosen option; then `cc-dev-reports reply` and watch the reply appear; republish and watch the page reload in
  place. Screenshots at phone and desktop width. If a live session is needed, use a Director on slot 5 or higher via
  the Task Scheduler launch in the repository instructions; never stop or restart any other Director.
- `.\scripts\test-local.ps1` green, and the web workspace tests for client-core, cockpit and mobile (the default gate
  runs no web tests).
- A reviewer session from a different agent family reads the change before the pull request. Confirm any
  reviewer is working by reading its terminal (`cc-devthrottle session buffer <id>`), never by its state, and never
  wait more than 20 minutes without reading it.

## Done means

A pull request from `mission/dev-reports-p3` to main, not merged (it will contain phase 2 until phase 2 merges;
say so in the body), a report at `docs/missions/dev-reports/PHASE-3-REPORT.md` - including, for the owner, exactly
what to try on the phone and in the Cockpit after the deploy - everything pushed, and ONE line to the Architect
(session 184d1571). Sending that line is required.
