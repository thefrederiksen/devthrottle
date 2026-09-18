# Room for the report: collapsible panes, a full-screen address, and an HTML export

Issue [#3074](https://github.com/thefrederiksen/devthrottle/issues/3074). Written before the work, kept
current as it was built. Not a mission - one change, one pull request.

## Why

On the Reports tab, the report is the narrowest region on the screen it is the whole point of. Measured
from the stylesheets rather than from the picture:

| Region | Width | Where it is set |
|---|---|---|
| Left navigation rail | 220px | `--rail-left-width`, `apps/cockpit/src/styles.css` |
| Session list | 340px | `.sessions-view` grid, same file |
| Queue / Screenshots dock | 300px | `.session-detail` grid, same file |
| The report's own conversation | 340px | `.reports-conversation`, same file |
| **Total** | **1200px** | |

On a 1920-wide display that leaves the report about 700 pixels: a third of the screen for the document and
two thirds for the furniture. The dock is frequently 300 of those pixels saying "No queued prompts."

Behind that sits a second problem the first one hides. A dev report is a document meant to be SENT - the
printed link `<gateway>/r/<report id>` exists for exactly that. That address forwarded the reader into the
session's Reports tab, so a person who does not have the session arrived inside somebody else's fleet, with
the report squeezed between a rail, a session list and a queue dock that mean nothing to them.

## What was built

### 1. The left rail collapses to its icons

`AppShell` keeps the collapsed state and puts `shell-rail-collapsed` on the frame; the stylesheet redefines
`--rail-left-width` from 220px to 56px under that class, so the grid column follows the same variable it
always did and no layout is duplicated.

The rail collapsed is the SAME navigation with the words hidden, and that claim is the interesting part:

- Every label stays in the page. It is hidden from the eye with the clip-path technique, NOT with
  `display: none`, because the label is the row's accessible name - removing it leaves twelve links
  announced as nothing. `railCollapse.test.tsx` compares the list of link names before and after the
  collapse and requires the identical array.
- The attention badge stays visible, as a small count on the icon. A rail that drops its badge when it
  narrows turns collapsing into a way to stop being told something needs you.
- Only the app name and the status pill are actually removed, and those are rendered conditionally rather
  than hidden, so what the test sees is what the reader sees.

The choice is written to `localStorage` under `cockpit.railCollapsed`, the same way the roster's ordering
already works. Storage that is unavailable reads as expanded and the feature still works, it just forgets.

### 2. The queue dock collapses to a strip

`SessionDetail` keeps the state (`cockpit.dockCollapsed`) and the `.session-detail` grid goes from
`1fr 300px` to `1fr 30px`. Collapsed, the strip draws one control - the chevron that brings the dock back -
and the queue count when anything is queued. The dock is never removed: a queue you cannot see and cannot
reach is a queue you forget.

This composes with the behaviour already there for the Wingman tab, where an empty dock gives its width back
by itself (`.session-detail-wide`). The two are separate states and the class list carries whichever
applies.

**The session list does not collapse.** Two collapsible panes is a fix; three is a screen where nothing is
where you left it. The owner ruled this out when the work was specified.

### 3. The report at its own address

`/report/<report id>` no longer forwards into the session's Reports tab. It IS the report now: the shared
viewer, the conversation beside it, and no application chrome.

The mechanism that matters is one line of the route table - the route is a SIBLING of `<AppShell />` rather
than a child of it. A full-width page mounted inside the shell is still a page with a navigation rail; only
moving it out of the shell actually removes the rail. `reportAddressRoute.test.tsx` proves this by finding
the Primary navigation on an ordinary page first (so the query is a working instrument) and then requiring
it to be absent on the report page.

It stays INSIDE the sign-in gate, exactly as before: signed out, the browser still goes to
`/signin?next=/report/<id>`, and that `next` still resolves at the end of the round trip. Nothing about the
printed link changes on the Gateway side (`DevReportLinkRoute` already sends anything that is not a phone
to `/report/<id>`).

Nothing was lost by dropping the forward. The way into the session is the viewer's own back link, whose
words are the Gateway's, for a reader who HAS that session; a reader who does not never sees a screen built
for somebody else's fleet. An open report on the Reports tab carries a "Full screen" link to that address,
opening in a new tab - a real link to the real address, so it can be copied, and so what the sender opens is
what the recipient will see.

The phone needed no change: its report page has always been full screen.

### 4. Export the report as HTML

A dev report is one self-contained HTML file by contract (`CONTRACT.md` section 1: no scripts, styles
inline, images as `data:` URLs). So the export is the bytes the Gateway serves for the version on screen,
saved unchanged - not a rendering of them, and not the application's frame around them.

It lives in the shared viewer's own header bar rather than in either shell, so it is on the Reports tab, on
the full-screen page and on the phone from one implementation.

Two details worth keeping:

- **The version exported is the one on screen** (`loadedVersion`), not the version the last record read
  said. A report is republished in place, and the controller loads a new version into the frame; exporting
  the record's version would hand the reader a different document from the one in front of him.
- **The frame's copy cannot be reused.** The report is in a sandboxed iframe with an opaque origin, so the
  page cannot read its bytes back out. The export asks the Gateway, which is also what makes the first
  point possible.

A failed export says so on screen, in the Gateway's words where there are any. A button that reports
nothing reads as broken.

HTML only. No other format was built and none is planned here.

## The files

| File | What changed |
|---|---|
| `apps/cockpit/src/AppShell.tsx` | the rail's collapsed state, its toggle, and remembering it |
| `apps/cockpit/src/components/Chevron.tsx` | new - the one collapse shape, on the nav icons' grid |
| `apps/cockpit/src/sessions/SessionDetail.tsx` | the dock's collapsed state, its toggle, and remembering it |
| `apps/cockpit/src/sessions/ReportPage.tsx` | new - the report at `/report/<id>`, outside the shell |
| `apps/cockpit/src/sessions/ReportLanding.tsx` | deleted - the forward it existed for is gone |
| `apps/cockpit/src/sessions/ReportsTab.tsx` | the "Full screen" link |
| `apps/cockpit/src/routes.tsx` | `/report/:reportId` moved out of `<AppShell />` |
| `apps/cockpit/src/styles.css` | the collapsed rail, the collapsed dock, the report page |
| `packages/client-core/src/devreports/exportReport.ts` | new - the file name, and handing the browser the file |
| `packages/client-core/src/devreports/DevReportViewer.tsx` | the Export button, and saying when it fails |
| `packages/client-core/src/devreports/devReports.css` | the Export button |

`DevReportLanding` in client-core is untouched and still used by the phone.

## How it was proved

Local, before the merge:

- `packages/client-core`: 1427 tests pass, including 10 new ones for the export (the bytes, the version,
  the file name, the object URL, and both failure paths).
- `apps/cockpit`: 456 tests pass, including 5 new ones for the rail and 6 for the dock and the full-screen
  link, plus the 5 rewritten address tests.
- `apps/mobile`: 97 tests pass - the shared viewer changed, so the phone is checked too.
- `npm run typecheck` across the workspace, and a production `vite build` of the Cockpit.

No C# changed, so no .NET suite is affected.

## What this does NOT prove

- **Layout.** jsdom has no layout engine, so every test here is about structure and behaviour. That the
  collapsed rail is 56 pixels wide and the collapsed dock 30 is a stylesheet reading, not a measurement of
  a painted screen. The look was checked in the real Cockpit after the deploy, not before it.
- **The three lint errors** reported by `npm run lint` are pre-existing and unrelated: two missing
  `react-hooks/exhaustive-deps` rule definitions in mobile pages and one missing
  `@typescript-eslint/no-implied-eval` in a Cockpit test, all in files this change does not touch.
