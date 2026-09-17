# Worker report - phase 3b, the page: one conversation, one Send, a note box that does not cover the answer

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-page`.

The owner opened a report in the Cockpit and saw two conversations, two Send buttons - one of them
permanently disabled - and a floating panel sitting on top of the question he was trying to answer. All
three of those came from the page. All three are gone.

---

## What changed

Four files, and only these:

| File | What it now does |
|---|---|
| `packages/client-core/src/devreports/dev-report-notes.js` | Hosted, the page draws only the note-taking parts. The note box is placed against what the note is about. |
| `packages/client-core/src/devreports/devReportNotes.test.ts` | Twelve new tests; four existing ones widened to cover the second shadow root. |
| `packages/client-core/src/devreports/CONTRACT.md` | Says what a hosted page draws, what an unhosted one draws, and where the note box goes. |
| `packages/client-core/browser-tests/dev-report-notes-proof/` | The test host now DRAWS the conversation, so the proof exercises the hosted shape - and two new claims. |

### A. Hosted means the app owns the conversation

The queued list, the sent list, the replies list, their three headings, the Send button and its row, and
the payload preview box now live inside one `[data-drn=conversation]` block. When the page becomes hosted
that block is **removed from the document**, not hidden - a query for it returns nothing.

What stays: the "Add a note" control, the "Note on selected text" control, the note box with Queue note
and Cancel, and each question's Queue button and state line.

The switch is driven by the **restore**, both ways (`setHosted`). The page starts unhosted with the whole
tray, and being inside a frame changes nothing: a framed page whose host never answers keeps its Send,
because that page really is the owner's only way to send.

The page still keeps the whole state and still posts all of it in `state-changed`. Only what it DRAWS
changed. Hosted, the app sends the queue it already holds (`controller.sendQueued`), so the page's `send`
message is now an unhosted-only path - written into the contract's message table.

Per-question state line, hosted: `Queued: Tonight - press Send in the app to send it.` Unhosted it still
says `press Send in the notes tray.` The Gateway does not supply this sentence, so it names nothing
internal and is true of the Cockpit and of the phone alike.

Hosted, the tray also stops being a scrolling panel (`max-height: none; overflow: visible`) - it has no
long lists left in it, and its `overflow: auto` was one of the three scrollbars the owner counted.

### B. The note box does not cover what you are noting

The composer moved out of the fixed bottom-right tray into **its own shadow root**, with the same inline
`!important` protections the tray carries (display, visibility, opacity, position, transform, filter,
clip-path - all proven in a test).

Where it goes is a **pure function**, `placeNoteBox(anchorRect, questionRect, box, viewport)`, exported on
the script's api so it can be checked with rectangles:

- below what the note is about when there is room, otherwise above, never over it;
- the anchor and the question that contains it are avoided **together**, so a note on a paragraph inside a
  question does not land on the rest of that question;
- nudged sideways to stay inside the viewport;
- when it fits neither way, it goes below and returns a `scrollBy` - never so far that what the note is
  about leaves the screen. `left` and `top` are viewport coordinates AFTER that scroll.

The anchor is **measured when the box is drawn**, not when the draft was made, because the page may have
been scrolled in between. The element is found from the note's own `selector`, so the click path and a
restore after a reload place the box the same way; a selector that no longer resolves puts the box at the
top left, where it covers nothing. Only a box that has just appeared may scroll the page - one already
open and growing as the owner types must not move the page under them.

It is sized to the note (growing to a ceiling of two fifths of the viewport height) and has no
`overflow: auto` of its own. Past the ceiling the note's own text scrolls inside the textarea; the box is
never a scrolling panel.

---

## The tests, and the revert evidence

`npm test --workspace @devthrottle/client-core`: **1285 passed, 110 files**. `npm run typecheck
--workspaces`: clean for client-core, cc-assistant, cockpit and mobile. `npx eslint` on the changed files:
clean. The notes file itself: **58 tests**, run three times in a row to check for flakiness, green each
time.

Every fix below was reverted, the failure watched, the fix put back, and the suite re-run green. The tree
was committed first (`a4d29b08f`), and after each restore `git diff --numstat` on the script was **empty** -
so what was restored is byte-for-byte what was committed, not something that merely passes.

### Unit tests

| # | Test | Line reverted | What went red |
|---|---|---|---|
| 1 | `draws only the note-taking parts - no queued, sent or replies list, no Send, no payload box` | `setHosted(true);` -> `hosted = true;` (the restore handler) | `AssertionError: conversation: expected <div data-drn="conversation">...(8)</div> to be null` |
| 2 | `is the RESTORE that takes Send away, not being framed: a framed page whose host never answers keeps it` | the same line | `AssertionError: expected <button type="button" ...(3)></button> to be null` |
| 3 | `unhosted, the page keeps the whole tray - the conversation and Send belong to a page with no app` | `body.appendChild(conversation);` deleted | `AssertionError: conversation: expected null not to be null` (and five more tests went red with it: the two anchor tests, `notes a clicked element through the tray`, `with no host, Send shows the exact payload and keeps the queue`, and `starts even when the report carries the old started attribute`) |
| 4 | `goes below what the note is about when there is room, without touching it` | `var below = avoidBottom + GAP;` -> `var below = avoidTop;` | `AssertionError: expected 200 to be greater than 240` |
| 5 | `goes above when there is no room below, without touching it` | the same line | `AssertionError: expected 860 to be less than 700` |
| 6 | `when it fits neither above nor below, scrolls the page and still goes below, never on top` | the same line | `AssertionError: expected 0 to be greater than 0` |
| 7 | `clears the whole question that contains the anchor, above and below` | the four lines that widen the avoided rectangle to the question deleted | `AssertionError: expected true to be false` (the box overlapped the question) |
| 8 | `tells the owner where the one Send is, in words true of either app and naming nothing internal` | `return hosted ? "press Send in the app to send it." : "press Send in the notes tray.";` -> the tray sentence always | `Expected: "Queued: Tonight - press Send in the app to send it." Received: "Queued: Tonight - press Send in the notes tray."` |
| 9 | `hands the app the same state hosted as the page keeps unhosted - the panel still gets everything` | `emitState` mutated to drop the queue while hosted, on the wrong theory that the app already has it | `AssertionError: expected { queued: [], sent: [], ...(4) } to deeply equal { Object (queued, sent, ...) }` |

Test 9 is the only one whose red came from a deliberate wrong implementation rather than from removing a
line I added - because the behaviour it guards (hosted changes what is DRAWN, never what is SENT) is an
absence of a change, and there is no line to take away. The mutation is exactly the mistake the test
exists to catch.

Three more new tests have no revert of their own and are stated as such: the two that widen existing
coverage to the second shadow root (`never anchors to the notes interface the script added`, `keeps the
tray and the note box out of reach of the report's CSS`), and `stays inside the viewport when the anchor
is at the right edge or off the left`, whose behaviour the horizontal clamp already carried before this
phase in the tray's `max-width`.

### The browser proof

`node run-proof.mjs` against a real Chromium: **PASS - all 16 claims**, up from 14. Two are new:

- **O** - hosted, the page draws no conversation and no Send. It counts, inside the frame's shadow roots:
  queued 0, sent 0, replies 0, send 0, payload box 0, conversation 0, headings 0 - and add-a-note 1, note
  box 1, queue-answer 1. It then counts every button on the WHOLE screen whose words start with "Send":
  `{"inApp":1,"inPage":0,"total":1}`.
- **P** - measured rectangles. The note box is 320 x 136 and real, and it overlaps neither the table cell
  it is about nor - in the second half - the question whose heading was noted. The hosted tray reports
  `trayScrolls: false` and the report does not scroll sideways.

Both were reverted and watched fail in the real browser too:

| Claim | Line reverted | What went red |
|---|---|---|
| O | `setHosted(true);` -> `hosted = true;` | `{"pageDraws":{"queued":1,"sent":1,"replies":1,"send":1,"payloadBox":1,"conversation":1,"headings":3,...},"sendButtons":{"inApp":1,"inPage":1,"total":2}}` - two Send buttons on the screen, which is the owner's complaint reproduced exactly |
| P | `var below = avoidBottom + GAP;` -> `var below = avoidTop;` | the box's rectangle started at `top: 341.7`, the cell's `top: 341.7` - drawn straight on top of the cell; and `top: 336` against the question's `top: 336` |

After both, the fix went back and the proof ran green again: **PASS - all 16 claims**.

Screenshots beside the driver, all from this run:

- `evidence-note-box-beside-cell.png` - the note box below the "42" cell, the app's panel on the right with
  the only Send, and the tray reduced to Notes / Add a note / Note on selected text.
- `evidence-note-box-beside-question.png` - a note on the question's heading, the box below the whole
  question with the options, comment box, Queue answer and its state line all still readable.
- `evidence-queued.png`, `evidence-sent-status-reply.png`, `evidence-no-host-payload.png`,
  `evidence-hostile-report.png` - the existing four, re-taken.
- `evidence-2026-09-17.json` - the machine-readable record of every claim.

The test host (`index.html`) now draws the conversation in a panel beside the frame and sends the queue it
already holds, the way `controller.sendQueued` does - so the proof exercises the hosted shape rather than a
shape no app produces. Claims F, G, H, I and J now read that panel and the host's saved state instead of
the page's lists, and `G` additionally asserts the page posted **no** `send` message at all.

---

## What I did NOT prove

- **No real app.** Everything here is the shipping script against the TEST host in
  `browser-tests/dev-report-notes-proof/index.html`. The Cockpit's rail and the phone's sheet are the
  apps Worker's half; this report says nothing about how they look or whether the report frame ends up
  with one scrollbar inside them. Claim P proves the page does not add a second scrollbar - not that the
  app does not.
- **No phone.** Every measurement was taken at 1600 x 900 in Chromium. The note box at 390 x 844, and text
  selection by touch, are unproven. Its width is `min(320px, 100vw - 16px)`, so it fits, but nobody has
  watched it.
- **No Gateway, no session, no delivery.** Unchanged from phase 1.
- **The note box on a very long note** is bounded by a ceiling of two fifths of the viewport, and the
  textarea scrolls past it. No test measures that ceiling in a browser; the rule is in the stylesheet.
- **Scrolling after the box is open.** The box is `position: fixed` and does not follow its anchor when
  the owner scrolls afterwards, exactly as the tray never did. Nothing asked for it to follow, and making
  it follow risks a scroll loop, so it was deliberately left alone.
- **`window.scrollTo` is not implemented in jsdom**, so four unit tests print a `Not implemented:
  window.scrollTo` line to stderr when the restore handler puts the page back where the owner left it.
  The call is real and is what the browser proof's claim K exercises; only the jsdom stub is missing. It
  was left unmuted rather than stubbed, so the call stays visible.

## One thing for the Manager, outside my files

`packages/client-core/browser-tests/dev-report-viewer-proof/run-proof.mjs` reads `[data-drn=queued]` and
`[data-drn=sent]` **inside a hosted frame** (its claim E9, around line 755). Those elements no longer exist
when hosted, so that claim will fail on its next run. It is a Playwright end-to-end proof that needs a live
Gateway and is not run by `npm test`, and the file is not in my list, so I have not touched it. What E9
means to prove - a note either reaches the Gateway or stays visibly queued, and never reads as delivered -
is still provable; it should read the APP's queued and sent lists (`T.queuedItem` / `T.sentItem`, which it
already uses two lines earlier) instead of the tray's.
