# Dev Reports - phase 3 report: reading and answering reports in the Cockpit and on the phone

Issue #3010 (child of #2936). Branch `mission/dev-reports-p3`. Written 2026-09-17 by the phase 3 E9 Manager.

## What you get

When an agent publishes a report for its session, you can open it from that session - a **Reports** tab in the
Cockpit, a Reports entry on the phone - read it, tap a paragraph or a table cell and leave a note, answer its
questions, and press Send. Each note and answer then shows the Gateway's own words for where it is: waiting for the
agent to finish its turn, delivered, or refused because the session has ended. The agent's replies appear under the
report. When the agent publishes the report again, the page reloads in place, keeping where you had scrolled to and
anything you were half way through typing.

The report cannot reach the app around it: it cannot read your sign-in, cannot send anything as you, and a report
that navigates itself away loses its connection to the app.

## What to try (once this is merged and the Gateway is deployed)

1. In a session, ask the agent to write a short HTML report with a table and one question, and publish it with
   `cc-dev-reports open <file>`.
2. **On the phone:** open that session, tap **Reports**, open the report. Tap a table cell and write a note. Pick an
   answer to the question and tap Queue answer. Open the conversation sheet and tap **Send**. Each item should move
   to Sent with "Delivered to the session" (or "Delivered when the agent finishes its turn" if the agent is working).
3. Ask the agent to reply with `cc-dev-reports reply` and to republish the report. The reply appears; the page
   reloads where you were.
4. **In the Cockpit:** open the same session, click the **Reports** tab. The same report, version and statuses show.
5. Write a note in the Cockpit too and send it. It must either show as delivered and reach the agent, or stay
   queued with a reason. It must never show as delivered without reaching the agent - that was the last defect
   fixed in this phase.
6. End the session and send another note: it stays queued with "This session has ended".

## What is proven, and how

**The last defect: a note from a second browser was lost while showing as delivered.** Fixed in two places, and
each fix was broken on committed code, seen red, and restored green:

- The note script numbered notes `n1`, `n2` per browser. The phone and the Cockpit each keep their own state, so
  the Cockpit's first note reused the phone's `n1`; the Gateway took it for the phone's note, never kept its words,
  and the Cockpit listed it as "Delivered to the session". Ids are now random (`n-` or `a-` plus 32 hex digits from
  the browser's cryptographic random source). With the counter put back, five note script tests go red, including
  one that makes two fresh browsers and checks their first notes differ.
- The Gateway now refuses an id it already holds when the content is different ("Not sent: a different note already
  has this id. Remove this one and write it again"), so a collision stays queued instead of reading as delivered.
  The same id with the same content is still an ordinary resend. With the refusal removed, the two new Gateway tests
  go red; a third test checks every field of a note and an answer is part of the comparison.

**On the real built apps, against a Gateway, launcher and Director built from this branch and run in their own
isolated root:** 34 of 34 claims pass (`packages/client-core/browser-tests/dev-report-viewer-proof/evidence/rig-frame-e2e-2026-09-17.json`).
That covers frame isolation in both apps, and the whole flow above at phone width (390 by 844) and desktop width
(1400 by 900), with the prompt the agent received read back from the Gateway's database. Before the fix the same
run was 33 of 34 with this defect (kept beside it as `...-before-e9-fix.json`). Each of the five isolation guards
was removed in the served app in an earlier run on this phase's build and every claim it protects went red in both
apps; those runs were not repeated after the fix, which does not touch the frame host.

**Test suites on this branch:** the local gate (`scripts/test-local.ps1`) passed, 2,057 tests. Web tests: client-core
1,275, Cockpit 351, phone 78, all passing. The Gateway's hosted dev report tests: 17 passing. The Gateway unit suite
(parked from the default gate): the 158 dev report tests pass; the whole suite ran 5,327 passing and 9 failing, none in
code this mission touched - 8 need the throwaway PostgreSQL that only the release run (`-Parked`) starts, and 1
(`Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk(agent: Grok)`) expects no Grok transcript and
finds one on this machine.

## What is NOT proven

- **The Gateway's collision refusal was not reached in the browser run.** By the time the Cockpit sends its notes the
  session has been ended, so they are refused for that reason. The collision refusal is proven only by the Gateway
  unit tests. With random ids a real collision should not happen at all; the refusal is the guard if it ever does.
- **No real phone was used.** The phone app ran in a desktop Chromium at phone size.
- **The same id twice inside ONE send with different content** is still read as one item (the first wins). The note
  script never sends that; it is not guarded.
- **No independent inspection of phase 3 has happened yet.** The Architect calls it.

## What looks wrong and is not fixed here

In the Cockpit at 1400 by 900 (`evidence/e2e-cockpit-1-open.png`) the report is squeezed into a column about 200
pixels wide beside the conversation panel, and the note tray drawn inside the report covers most of it. That tray
also keeps its own Queued and Sent lists for this browser, so it can say "Sent: Nothing here" right beside the
conversation panel listing two delivered items. Everything works, but it reads as two disagreeing lists on one screen.
This is a layout and design question for the Architect, not something this Manager was sent to change.

## What we got wrong along the way

The note ids were designed as a per-page counter on the assumption that a report is answered from one place. The
product shows every report on two surfaces by design, so that assumption was wrong from the start; only the end to
end run with a second browser exposed it.
