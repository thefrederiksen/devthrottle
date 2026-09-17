# Worker record - phase 3b, the proof: the rig, the before pictures, and the end to end

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-proof`. Written by the phase 3b proof Worker.

**Half one is done: the rig stands, a valid sample report is published on it, and the before pictures are taken.
Half two - the after pictures and the end to end - waits for the Manager to say the three branches are merged.**

---

## The commit every before picture is of

Every picture in `proof/before/` was taken against a Gateway, launcher, Director and both web shells built from
**`32d5037495ff71ad71320051c2f1802c0af1aaa4`**, the head of this branch when the run was made. That is not taken on
trust: the rig's own build step printed the product version of each executable it produced, and the running Gateway
repeated it on `/healthz`.

```
built: ...\gateway\devthrottle-gateway.exe  version 2.5.0+32d5037495ff71ad71320051c2f1802c0af1aaa4  67.7 MB
built: ...\launcher\cc-launcher.exe         version 2.5.0+32d5037495ff71ad71320051c2f1802c0af1aaa4  100.4 MB
built: ...\director\cc-director.exe         version 2.5.0+32d5037495ff71ad71320051c2f1802c0af1aaa4  38.3 MB

Gateway healthy: {"status":"ok","directors":0,"sessions":0,
                  "version":"2.5.0+32d5037495ff71ad71320051c2f1802c0af1aaa4", ...}
```

The Cockpit and the phone app were not copied in from anywhere: the Gateway publish built them from this worktree
(`-p:RunCockpitBuild=true -p:RunMobileBuild=true`) and the rig fails if either `wwwroot/c/index.html` or
`wwwroot/mobile/index.html` is missing after the publish.

This commit is today's shipped behaviour plus this mission's paper only - the three Workers changing the page, the
Gateway and the apps are working in their own worktrees and none of their code is in this build. That is what makes
these pictures a "before".

---

## The rig recipe, as run

The recipe is a script in this worktree, so the after run is one command:
**`docs/missions/dev-reports/proof/rig.ps1`**.

```powershell
cd docs\missions\dev-reports\proof
.\rig.ps1 all        # build, up, session, report - or the four separately
.\rig.ps1 status
.\rig.ps1 down       # or reset, which also deletes the rig root
```

It is a copy of the phase 3 rig (`packages/client-core/browser-tests/dev-report-viewer-proof/rig.ps1`) with its own
root, its own port and its own scheduled task names, plus a `report` command. It is a copy rather than a caller on
purpose: the phase 3 rig belongs to phase 3's evidence and must stay exactly as that proof ran it, and two rigs that
shared scheduled task names could overwrite each other.

| | |
|---|---|
| Root | `%LOCALAPPDATA%\dev-report-proof-rig-p3b` - `CC_DIRECTOR_ROOT` for every rig process, so its own database, tokens, logs and signal names |
| Gateway | `<root>\gateway\devthrottle-gateway.exe --port 7951 --no-autostart`, `CC_GATEWAY_NO_TAILSCALE=1`, authentication ON, started by the scheduled task `dev-report-proof-rig-p3b-gateway` |
| Launcher | `<root>\launcher\cc-launcher.exe --no-autostart`, scheduled task `dev-report-proof-rig-p3b-launcher` |
| Director | `<root>\app\cc-director.exe`, a slot 5 build from this worktree, started by the rig launcher through the rig Gateway so its parent is outside this agent's console |
| Session | one Claude Code session on the rig Director, session number **100**, named "dev report proof fixture" |
| Sign-in | the rig Gateway's machine token stands in for a device key, put into the browser's account store and into the `cc-gateway-token` cookie - the same stand-in phase 3 used |

Nothing of the owner's was touched: not `%LOCALAPPDATA%\cc-director`, not the hosted Gateway, not port 443, not any
other Director. The rig refuses a root that is not inside `%LOCALAPPDATA%`, that is the real root or inside it, that
does not carry `dev-report-proof-rig` in its name, or that exists without the sentinel file the script writes.

**One thing did not go to plan, and it is worth knowing for the after run.** The `session` command's HTTP call came
back `502 Bad Gateway`, but the session had already been created and its pre-prompt delivered - the 502 was the
response timing out, not the work failing. A retry would have seated a second session. The rig was inspected
(`/sessions` listed exactly one) and the run continued on it. The session then carried a red "your last prompt was
not delivered" banner from that timeout, which is honest but has nothing to do with reports; one ordinary prompt was
sent and delivered, which cleared it, before any picture was taken.

---

## The sample report

`docs/missions/dev-reports/proof/sample-report.html` - "Test suites: what runs by default, what is parked, and what
it costs". It is a **valid report by the contract**, and that is not an opinion: the Gateway's shape check accepted
it at publish time, which is the only thing that can say so.

```
[p3b-proof-rig] publishing ...\proof\sample-report.html as session 23ff9d5b-...
[p3b-proof-rig] report id 853fec87-... version 1
[p3b-proof-rig] OWNER ROUTE: http://127.0.0.1:7951/dev-reports/853fec87-...
```

It carries what the brief asked for, because each one is needed to show a defect: a header with a status, a summary,
a questions section holding one question with two radio options and one `data-recommended`, three detail sections,
an evidence section, and **a table with row headers and column headers** - so a note anchored to a cell carries a
real row label and column label, and the picture of the note box covering that cell is a picture of the owner's
actual complaint.

The same file is used for the after run, so the two sets of pictures are of the same report.

**Already visible in the owner route above, before any picture:** `cc-dev-reports open` still prints
`/dev-reports/<id>`, the phase 1 route. The one link per report that routes by device (defect C in the handoff) is
not in this build, which is as expected - it is one of the three Workers' jobs.

---

## The pictures, and what each one shows

All in `docs/missions/dev-reports/proof/before/`. Measurements are the page's own numbers, read out of the document
with JavaScript at the moment the picture was taken, not estimated from the image.

### The Cockpit at 1400 by 900 - the owner's own case

**`cockpit-desktop-1400x900-two-conversations-and-two-send-buttons.png`**

Two conversations on one screen, each listing the same three headings - QUEUED - NOT SENT YET, SENT, REPLIES FROM
THE AGENT - one drawn inside the report, one in the Cockpit's panel beside it. Two Send buttons in the frame, and a
third below it belonging to the session composer.

- The Reports tab is **540 by 670** pixels. The report itself gets **200 by 537** of that; the Cockpit's conversation
  panel takes the other 340 pixels of width.
- Inside the report frame the document is **326 pixels wide in a 185 pixel viewport** and **7232 pixels tall in a 522
  pixel viewport**, so the report has a horizontal scrollbar AND a vertical one. With the tray open its own list
  scrolls as well. **Three scrollbars**, as the owner said.
- The report's own Send button is at (13, 384) in that 200 pixel frame; the Cockpit's Send is at (806, 194) on the
  page. Both are in the picture.

**`cockpit-desktop-1400x900-panel-covers-the-question-being-answered.png`**

The question scrolled into view with the tray open. The heading reads "Which parked suite should we bring back" and
stops mid-sentence where the panel begins. Neither radio option is visible.

- The question element is 145 by 722; 362 pixels of it are inside the viewport at this scroll position.
- The panel covers **145 by 414 pixels of it - 60,046 of the 75,250 visible pixels, 80 per cent** of the question the
  owner is being asked to answer.

**`cockpit-desktop-1400x900-note-box-covers-the-table-cell-it-is-noting.png`**

A note is open on a table cell. The box itself says which cell: `Table cell - row "Gateway unit tests", column
"Tests": "4259"`. That cell is not visible - the box is on top of it.

- The noted cell is 56 by 65. The note box covers **56 by 63 of it - 97 per cent of the cell the note is about**.
- It also covers 165 by 543 pixels of the table around it.

**`cockpit-desktop-1400x900-no-way-back-to-the-session.png`**

What stands today where the way back should be. The open report offers exactly one navigation control, "All
reports". Measured on the page: **zero** occurrences of "back to", the session's name "dev report proof fixture"
does not appear anywhere in the view, and neither does its number, 100.

### The phone app at 390 by 844

**`phone-390x844-the-reports-own-conversation-and-its-send-button.png`**

The report's own tray, with its QUEUED / Send / SENT / REPLIES conversation, sitting above the app's own
"Conversation - 0 queued, 0 sent, 0 replies" bar. Two conversations of the same three lists, on one screen.

**`phone-390x844-two-conversations-the-apps-sheet-over-the-reports-own.png`**

The app's conversation sheet opened on top of the report's tray: the app's QUEUED / Send / SENT / REPLIES over the
report's own. **The second Send cannot be brought into the same frame on the phone today** - the sheet is modal, so
while it is open the report's tray is behind it and cannot even be clicked. That is why there is no phone picture
with two Send buttons visible at once; it is not an omission.

**`phone-390x844-panel-covers-the-question-being-answered.png`**

With the question scrolled to the very top of the report the two radio options are readable - and the question's own
**Queue answer button is cut in half by the panel** (the button spans 334 to 367; the panel starts at 343). The
panel occupies **323 of the 666 pixel report viewport, 48 per cent**, at every scroll position.

**`phone-390x844-note-box-covers-the-table-cell-it-is-noting.png`**

The same defect as the Cockpit, on the phone. The box says `Table cell - row "Gateway unit tests", column "Tests":
"4259"`; only the table's header row is visible above it.

- The noted cell is 56 by 65 and the box covers **56 by 58 of it - 89 per cent**.
- With the note open the panel occupies **68 per cent** of the report viewport.

**`phone-390x844-no-way-back-to-the-session.png`**

The phone's report header. One navigation control, labelled "Back" and nothing else. Measured: **zero** occurrences
of "back to", the session name does not appear, the number 100 does not appear.

### The Cockpit at 390 by 844

**`cockpit-390x844-only-the-rail-fits-the-reports-tab-is-off-to-the-right.png`**

The brief asks for the Cockpit at both widths, so here it is at phone width, and the answer is that there is nothing
to see: only the left rail and the first column of the session roster fit. The Reports tab sits at x=984 in a 390
pixel viewport, reachable only by scrolling the window sideways. The Cockpit does not reflow for a phone - which is
what the phone app is for. This picture is kept as the honest answer to that half of the request, not as a defect
for this phase.

### No internal identifier is on screen

Checked on every picture: no session id, no report id, no token. The Cockpit shows the session as `100 dev report
proof fixture` and the phone shows no session identity at all. Nothing needed cropping and there is nothing to
report to the Manager on this point.

---

## What I did NOT prove in half one

- **Nothing about the fix.** These are before pictures of a build that contains none of the three Workers' changes.
  They say what is wrong today and nothing about whether it gets better.
- **No end to end.** No note was sent, no answer was delivered, no prompt reached the session, no reply came back.
  That is half two, and the brief puts it there.
- **No real phone.** The phone app ran in a desktop Chromium at 390 by 844 with mobile emulation on. The same limit
  phase 3 recorded.
- **One browser, one machine.** Chrome, through the Director's `agent-browser` profile, on SOREN_NORTH. No other
  browser and no other machine was tried.
- **The signed-out case was not exercised.** The browser was signed in with the rig's machine token before the first
  picture. The signed-out landing is half two's work.
- **The scrollbar count is measured, not counted by eye.** "Three scrollbars" above comes from the document's own
  scrollWidth against clientWidth and scrollHeight against clientHeight, plus the tray's own scroller. If the
  intended meaning was three scrollbars visible in one screenshot simultaneously, the desktop picture shows the
  report's two and the tray's third only while the tray is open.
- **The 502 on session creation is not explained.** It was worked around by inspection, not diagnosed. If it happens
  again in the after run, the rule is: list the sessions before retrying, because the spawn may have succeeded.

---

## What half two still needs

When the Manager says the three branches are merged in:

1. `git merge` the merged branch into this one, then `.\rig.ps1 build -Force`, `.\rig.ps1 down`, `.\rig.ps1 all`.
   Record the new commit the same way - every executable's version and the Gateway's `/healthz`.
2. The same shots, same report, same two widths, into `proof/after/`, plus one conversation, one Send, a note box
   beside a cell rather than over it, one scrollbar, and the back link reading `back to 100 dev report proof
   fixture`.
3. The end to end on both device shapes from the one printed address, the prompt the session actually received read
   back from the Gateway's database, the agent's reply through `cc-dev-reports reply`, and the signed-out case.

The fixture session's instructions already allow half two to drive it: a message whose first line is exactly
`FIXTURE-COMMAND` makes it run the shell command that follows and answer with its output. That is how the reply
step gets made without the fixture reacting to the note prompts themselves.
