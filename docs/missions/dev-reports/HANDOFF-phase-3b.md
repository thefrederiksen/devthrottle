# Handoff - phase 3b: one conversation, one Send, and a link that lands where you are

The owner used the live Reports tab and it is a mess. Fix exactly this, nothing else. Read
`packages/client-core/src/devreports/CONTRACT.md` and `PHASE-3-REPORT.md` first. Open a child issue of #2936.

## What he saw (the defects, in his words and mine)

1. **Two conversations on one screen.** The report's own floating panel (from phase 1, for a bare file with no app)
   and the Cockpit's panel both list queued, sent and replies. Duplicated, confusing, and it looks broken.
2. **The floating panel covers the report**, including the question being answered, and the screen ends up with
   three scrollbars.
3. **Two Send buttons, one of them dead.** The floating panel's Send is disabled because nothing is queued in it, so
   it reads as a button that does nothing. Unacceptable.

## What to build

**A. Hosted means the app owns the conversation.** When the note script is hosted by one of our apps, the page shows
ONLY the note-taking parts: the "Add a note" control and the note box for what was tapped, plus each question's
Queue button. No queued list, no sent list, no replies list, and NO Send button in the page. Those live once, in the
app's panel (Cockpit rail, phone sheet, Director pane later). Unhosted (a bare file in a browser) keeps today's full
panel including Send and the payload preview - that is the only place it belongs.

**B. The note box does not cover what you are noting.** It opens near the element, sized to the note, and never over
the question or the cell being answered. The report keeps ONE scrollbar; the app frames it without nesting another.
Follow docs/VisualStyle.md; the tray already uses the app's theme.

**C. One link per report that lands where you are.** `cc-dev-reports open` prints one Gateway address, e.g.
`<gateway>/r/<report id>`. The Gateway routes it by device the way it already does elsewhere
(`src/CcDirector.Gateway/Mobile/MobileRedirect.cs`): phone to the phone report screen, anything else to the
Cockpit's Reports tab, in both cases STRAIGHT INTO that report. Not signed in: sign in, then land on the report.

**D. The way back, named for a human.** Every report view shows a link back to its session reading
`back to <three-digit session number> <session name>`, for example `back to 121 devthrottle - tool not working on
linux`. The report's title area also names that session. NO internal identifier anywhere the owner can see. The
Gateway supplies that text on the report record (client renders it verbatim, repository rule 7), so all three apps
say the same thing. If the Gateway does not already hold the session's short number and name for a report, add it.

## Proof required - the owner has asked for proof, so this is the point of the phase

- Unit tests: hosted hides the page's conversation and Send; unhosted keeps them; the note box position; the back
  link text comes from the Gateway; the link routes by device to each app and lands on the report; signed-out lands
  on the report after sign-in.
- Revert each and watch it go red.
- Real browser on the BUILT apps at phone width (390x844) and desktop width (1400x900): screenshots showing ONE
  conversation, ONE Send, the note box beside a table cell without covering the question, and the back link with the
  three-digit number. Before-and-after screenshots against today's live behaviour.
- End to end on a local Gateway with a real session: open the printed link, note a cell, answer the question, Send,
  the session receives one prompt naming the cell and the option, the agent replies, the reply shows.
- `.\scripts\test-local.ps1`, the client-core, cockpit and mobile web tests, and
  `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"`.

## Done means

A pull request from `mission/dev-reports-p3b` to main, not merged, `PHASE-3B-REPORT.md` with the screenshots named,
everything pushed. The Architect inspects, merges, deploys, and then tests it himself on the live site for the
owner. Read your Workers' terminals; fleet messages to child sessions are unreliable (issue #3009). Move fast: the
owner is waiting and is not happy.
