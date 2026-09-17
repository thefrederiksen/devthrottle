# Worker brief - phase 3b, the page: one conversation, one Send, and a note box that does not cover the answer

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-page`, cut from `mission/dev-reports-p3b`.
Your worktree is yours alone. Commit and push as you go. NEVER merge anything to main, and never touch
`mission/dev-reports-p3b` itself - your Manager merges your branch in.

Read first, in this order:
- `packages/client-core/src/devreports/CONTRACT.md` - the whole contract. You are changing it.
- `docs/missions/dev-reports/HANDOFF-phase-3b.md` - what the owner saw.
- `docs/VisualStyle.md`.

## THE WHY

The owner opened a report in the Cockpit and saw TWO conversations - the page's own floating panel
listing queued, sent and replies, and the app's panel listing the same things again - plus TWO Send
buttons, one of which is permanently disabled and does nothing, plus a floating panel sitting on top of
the very question he was trying to answer, on a screen with three scrollbars. He is not happy. Every
one of those is yours.

## YOUR FILES - and only these

- `packages/client-core/src/devreports/dev-report-notes.js`
- `packages/client-core/src/devreports/devReportNotes.test.ts`
- `packages/client-core/src/devreports/CONTRACT.md`
- `packages/client-core/browser-tests/dev-report-notes-proof/` (its host and checks)

Do NOT edit `DevReportViewer.tsx`, `devReports.css`, `frameHost.ts`, anything in `apps/`, anything in
`src/` (the C# Gateway), or the tool. Other Workers hold those and you will collide.

## THE WORK

### A. Hosted means the app owns the conversation

When the page is HOSTED - it has received a valid `restore` over the port, which is exactly today's
`hosted` flag - the page shows ONLY the note-taking parts:

- the "Add a note" control and the "Note on selected text" control,
- the note box (the composer) for what was tapped, with Queue note and Cancel,
- each question's Queue button and its per-question state line.

and NOT:

- the Queued list, the Sent list, the Replies list, their headings,
- the Send button and its row,
- the payload preview box.

Those live ONCE, in the app's own panel. UNHOSTED - a bare file opened in a browser with no app - keeps
exactly today's behaviour: the full tray including Send, the queued/sent/replies lists, and the payload
preview that shows what Send would have posted. That is the only place they belong.

The page still keeps the whole state and still posts `state-changed` with it, hosted or not - the app's
panel is rendered from that state. You are changing what the PAGE DRAWS, never what it sends.

The switch must work BOTH WAYS and at the right moment: the page starts unhosted and becomes hosted when
the first `restore` arrives, so the conversation parts must be present at the start and removed when
`restore` lands. Do not decide it once at start-up from whether the page is framed - a framed page whose
host never answers is unhosted and must keep its Send.

Per-question state lines: today they say "press Send in the notes tray". Hosted, there is no tray Send -
say where the owner actually presses Send, in plain words, without naming an internal identifier. The
Gateway does not supply this text, so keep it generic and true of both apps.

### B. The note box does not cover what you are noting

Today the composer lives in the fixed tray pinned bottom-right, which lands on top of the question or the
table cell being answered. Instead:

- The note box opens NEAR the element it is about and never OVER it, and never over the question element
  that contains it. Position it against the anchor's bounding box: below it when there is room below,
  otherwise above; nudged horizontally so it stays fully inside the viewport.
- Size it to the note, not to a fixed panel: a small box that grows with the text up to a sane ceiling.
- It must not introduce a scrollbar of its own on the page. The report keeps ONE scrollbar. The tray's
  own `overflow: auto` on a fixed panel is part of why the owner counted three - hosted, the tray has no
  long lists left to scroll, so it must not be a scrolling panel.
- It stays inside the shadow root with the same inline `!important` protections the tray has, so a report's
  CSS cannot move or hide it.
- Follow `docs/VisualStyle.md`; the tray already takes the app's theme through `data-dev-report-theme`, so
  use those theme values and add no new colour of your own.

Anchoring detail that matters: the anchor may be scrolled away between opening and drawing. Re-measure when
you open it. A note box that cannot be placed without covering its anchor goes BELOW the anchor and the page
scrolls so both are visible - never on top.

### C. The contract

`CONTRACT.md` is the one contract and it must describe what you built, in the same voice as the rest of it.
Say what a hosted page draws and what an unhosted page draws, and that the app owns the conversation when
hosted. You are the ONLY Worker who edits CONTRACT.md this phase.

## PROOF - this is the point of the phase, the owner asked for it

Unit tests in `devReportNotes.test.ts` (vitest, jsdom - follow the file's existing style):

1. Hosted (after a `restore`): the queued list, sent list, replies list, their headings, the Send button and
   the payload box are all absent from the tray; the Add a note control, the note box and every question's
   Queue button are present.
2. Unhosted: all of them are present, Send works the way it does today (shows the payload preview), and the
   payload preview still shows the exact message.
3. The page starts unhosted with Send present, and the FIRST `restore` removes it - so the switch is driven
   by the restore, not by being framed.
4. The note box's position: given an anchor rectangle, the box's rectangle does not intersect the anchor's,
   and does not intersect the question element that contains the anchor. Test the placement function
   directly with rectangles - it must be a pure function you can call - and prove both the below-it and the
   above-it case (no room below).
5. Hosted, the state the page posts is UNCHANGED: queueing a note and an answer still produce the same
   `state-changed` payload as today. A test that proves the app's panel still gets everything.

**REVERT EACH ONE AND WATCH IT GO RED.** Put your fix back, re-run, green. Record in your report, per test:
the exact line you reverted, the exact failure message you saw, and that the controls stayed green. A test
you have not watched fail is decoration and will be treated as such by an inspector from another agent family.

Run: `npm test --workspace @devthrottle/client-core` (or the workspace's own test command - check
`packages/client-core/package.json`) and `npm run typecheck`.

Also keep the browser proof in `browser-tests/dev-report-notes-proof/` honest: it is a HOST, so it now
exercises the hosted shape. Update it so it still proves the trust rules of CONTRACT.md section 4 AND shows
the hosted page without a second conversation.

## DONE MEANS

Everything committed and pushed on `mission/dev-reports-p3b-page`, and a file
`docs/missions/dev-reports/WORKER-phase-3b-page.md` in your worktree holding: what you changed, the test
list, the revert evidence per test (the line, the red message), and what you did NOT prove. Then tell your
Manager in ONE line - fleet messages truncate at the first newline, so the detail goes in the file.

Do not guess. If something is genuinely undecidable, ask your Manager - do not invent a product decision.
