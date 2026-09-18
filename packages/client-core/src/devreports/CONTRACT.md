# Dev report contract (version 1)

This is the ONE contract for a dev report. It has three parts, and all three live here:

1. **The report markup** - the markers the Gateway's shape check looks for
   (`src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs`).
2. **The question markup** - what the note-taking script turns into a Queue button.
3. **The host protocol** - the `postMessage` messages between the note-taking script
   (`dev-report-notes.js`, beside this file) and the app that shows the report, and what a host must do
   so that a message it receives really came from the owner (section 4, Trust).

If you change a marker or a message, change it here, in the script, and in the shape check in the same
pull request. The idea for in-page notes and queued answers comes from lavish-axi by Kun Chen (MIT,
https://github.com/kunchenguid/lavish-axi).

---

## 1. The report markup

Every dev report is one HTML file. The shape is carried by `data-dev-report` attributes, not by class
names or headings, so a report can look however it likes.

| Marker | Element | Rule |
|---|---|---|
| `data-dev-report="header"` | the header | Exactly one. First of all the markers. Must carry `data-dev-report-status`. |
| `data-dev-report-status` | on the header | One of `waiting-on-you`, `agent-working`, `done`. |
| `data-dev-report="summary"` | the executive summary | Exactly one. Comes right after the header. |
| `data-dev-report="questions"` | the questions section | Exactly one. Comes right after the summary. Present even when there are no questions. |
| `data-dev-report="detail"` | a detail section | At least one. Every one comes after the questions section. |
| `data-dev-report="evidence"` | evidence and what is not proven | Optional, at most one. If present, it is the last marker. |

"Right after" is about the ORDER OF THE MARKERS, not about the HTML between them: the check reads the
markers in document order and does not judge untagged content in between.

A report has no `<script>` elements and no inline event handlers (`onclick=`, `onerror=` and so on). Every
host blocks them (section 4), so the check refuses them rather than let the owner open a page that
silently does less than its author meant.

The check reads the report the way a browser does: it parses it with an HTML5 parser, after the head the
host writes (section 4, rule 2) and with scripting on, and judges the document that results. So markers a
browser would not make into elements do not count - inside comments, inside text-only elements such as
`<style>`, `<textarea>` or `<noscript>`, inside `<template>` content, or after `<plaintext>` - and a script
or handler counts only when it is in the live document, not in template content. An element left unclosed
ends where a browser ends it. "Inside" means inside in that document, which is also what the note-taking
script sees in the page.

- Sections do not contain other sections. A section left unclosed usually ends up holding the next one,
  and the check says so.
- A marker on an element a browser never renders does not count: a `<template>`, `<noscript>`, `<style>`
  or `<script>` element, anything the parser puts in the head (such as `<meta>`), or anything inside one.
- The executive summary has words in it, and neither the summary nor the questions section carries the
  `hidden` attribute, on itself or on an element around it. Text a browser does not draw - in scripts,
  styles, and SVG `<desc>`, `<title>` and `<metadata>` - is not words.

**The check judges structure, not CSS.** Styles - an inline `style` attribute or a stylesheet rule - can hide
a section, and the check does not try to detect it. So it cannot promise the owner sees every section.

**The check is guidance, not the security boundary.** It tells the agent at publish time that the report is
the wrong shape. What stops a report acting for the owner is the host's policy in section 4.

### The questions section

- Each open question is an element with `data-dev-report-question="<id>"` inside the questions section.
  The id is unique in the report and made of letters, digits, `-` and `_`.
- When there are no questions, the section must contain an element with `data-dev-report-no-questions`
  that says so in words ("No questions - nothing needed from you."). A missing section never reads as
  "no questions", and neither does an empty one.
- A section may not contain both questions and the no-questions element.
- A question may not contain another question.
- Every radio option in the questions section must be inside a question.

## 2. The question markup

```html
<div data-dev-report-question="deploy-window" data-dev-report-question-text="When should we deploy?">
  <h3>When should we deploy?</h3>
  <label><input type="radio" name="deploy-window" value="tonight" data-recommended> Tonight - quiet traffic</label>
  <label><input type="radio" name="deploy-window" value="monday"> Monday - the team is around</label>
  <textarea data-dev-report-comment placeholder="Anything to add (optional)"></textarea>
</div>
```

- **Options** are `<input type="radio">` elements inside the question element (not inside a question nested
  in it, which the check refuses anyway). At least two. **Every option in a question shares one `name`, and
  no radio outside that question uses it** - the browser lets only one radio of a name be checked, and that
  is what keeps one answer per question. The script also keeps at most one option of a question checked
  itself, so the answer it queues is always the option the owner last picked. The option's label
  is the text of its `<label>` (wrapping, or pointed at by `for=`); without a label, its `value`.
- **The recommendation** is `data-recommended` on exactly one option. The script checks it when the page
  loads, so the recommendation is preselected.
- **The question text** is `data-dev-report-question-text` if present, otherwise the text of the first
  heading (`h1`-`h6`) inside the question, otherwise the id.
- **A comment box** is optional: a `<textarea data-dev-report-comment>` inside the question.
- **The Queue button** is added by the script, one per question. An author never writes it.

The shape check enforces: at least two options, exactly one `data-recommended`, one shared `name` per
question used by no other radio, unique ids, no nesting, no option outside a question, and exactly one
no-questions element whose own text has words in it (a character reference such as `&nbsp;` alone is not
words, and text after a block element that would close a `<p>` is not the paragraph's).

Every text the script sends is at most 20000 characters. The script sets `maxlength` on the comment box,
and says so rather than queueing when a comment, the question text or an option's value is longer. The
question text is sent whole or not at all - never shortened.

### What can be noted

Anything in the page outside the notes interface can carry a note:

| Clicked | The note's anchor carries |
|---|---|
| A table cell (`td`/`th`) | `type: "table-cell"`, `selector`, `quote` (the cell text), `rowLabel` (the first cell of the row, or a `th scope="row"`), `columnLabel` (the header cell over that column) |
| A part of an SVG | `type: "svg-part"`, `selector`, `quote`, `label` (the nearest `aria-label`, `data-label`, or child `<title>` up to the `<svg>`) |
| A text selection | `type: "text"`, `selector` (the element holding the whole selection), `quote` (the selected text) |
| Any other element | `type: "element"`, `selector`, `quote` (its text, first 240 characters) |

A label that cannot be worked out for certain - a column under a spanning header, a row shifted by a
`rowspan` from an earlier row - is sent as an empty string. A wrong label reads as authoritative and is
worse than none.

`selector` is a CSS selector that `document.querySelector` resolves back to the same element in the
same page. It starts from the nearest ancestor with an `id` when there is one.

---

## 3. The host protocol

Every message, in both directions, is one object:

```json
{ "channel": "devthrottle.dev-report", "version": 1, "type": "<type>", "payload": { }, "token": "<page to host only>" }
```

- A message whose `channel` or `version` does not match is ignored.
- A message whose `type` is unknown is ignored.
- A message whose payload does not have the shape below is ignored, whole. Nothing is half-applied.
- Every message from the page carries the host's `token` (section 4).

**Where messages travel.** Exactly one message goes over the window: the page's `ready`, posted to
`window.parent` with target origin `"*"` (a sandboxed frame has an opaque origin and cannot name its
host's) and carrying ONE end of a `MessageChannel` the page created. Everything after that - `restore`,
`status`, `reply`, `send`, `state-changed` - goes over that private port, in both directions. The page does
not listen for messages on its window at all.

Why a port: a page the frame is navigated to later is a new document that never held the port, so it
cannot receive what the host pushes - not even in the moment before its load event, when the host cannot
yet tell the frame has moved - and it cannot pass for the note-taking script.

### Items

A queued or sent item is one of:

```json
{ "id": "n-3f9a0c1b7d2e4f6081a2b3c4d5e6f708", "kind": "note", "text": "This number is wrong",
  "anchor": { "type": "table-cell", "selector": "#results > tbody > tr:nth-of-type(2) > td:nth-of-type(3)",
              "quote": "42", "rowLabel": "Gateway", "columnLabel": "Failures" } }

{ "id": "a-0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", "kind": "answer", "questionId": "deploy-window", "question": "When should we deploy?",
  "optionValue": "tonight", "optionLabel": "Tonight - quiet traffic", "comment": "" }
```

`id` is **globally unique**: the script makes it from 16 random bytes (`crypto.getRandomValues`, which
works in the sandboxed frame) as `n-` or `a-` followed by 32 lowercase hex digits, and never from a counter.
One report is open in more than one browser - the phone and the Cockpit - and each keeps its own state, so a
per-page counter names a second browser's first note the same as the first browser's, and a host that holds
the first would take the second for it. When `crypto.getRandomValues` is missing the note is not queued. `anchor.rowLabel`, `anchor.columnLabel` and `anchor.label` are
present only for the anchor types that carry them. Every string is at most 20000 characters, and `id` is at
most 128 characters: the Gateway refuses the whole send (400) when any item's id is longer.

### Page to host

| type | payload | when |
|---|---|---|
| `ready` | `{ "questionIds": ["deploy-window"] }` | once, when the script has started |
| `send` | `{ "items": [ item, ... ] }` | the owner pressed the UNHOSTED page's own Send; every item still in the queue, in order. A hosted page draws no Send and never posts this: the app sends the queue it already holds from `state-changed`, and tells the page what happened with `status`. |
| `state-changed` | `{ "state": state }` | anything in the state changed - including a status or reply the host pushed |

### Host to page

| type | payload | effect |
|---|---|---|
| `restore` | `{ "state": state }` | replaces the page's state, puts back what the inputs showed, and scrolls to the saved position. Also marks the host as connected. |
| `status` | `{ "updates": [ { "id": "n3", "status": "held", "statusLabel": "Delivered when the agent finishes" } ] }` | the host's word on items, by id; an unknown id is skipped |
| `reply` | `{ "reply": { "id": "r1", "text": "Fixed - see section 2", "at": "2026-09-16T10:00:00Z" } }` | adds the agent's reply to the page (same id replaces) |

### Send, and when an item counts as sent

There is one Send on screen. Hosted it is the app's, and the app sends the queue it holds; unhosted it is
the page's own, and it posts a `send`. Either way the rules below are the same, because both end in the
host answering with a `status` for every id, and nothing counts as sent until it does.

Posting a message is not the host accepting it. So Send does not empty the queue:

1. Send posts every queued item and marks each one **pending** ("waiting for the app to confirm it has
   this"). A pending item stays in the queue and cannot be removed.
2. The host MUST answer every item of a `send` with a `status` for its id:
   - any status other than `refused` means the host has the item. It moves from the queue to Sent, with
     the host's `statusLabel` shown verbatim - the page never decides what a status means.
   - `refused` means the host will not take it. It stays in the queue, no longer pending, with the host's
     `statusLabel` shown as the reason (for example "This session has ended"), and can be sent again or
     removed.
3. Pressing Send again re-sends everything still queued, pending items included. **A host MUST treat an id
   it has already accepted, with the same content, as the same item**, not a new note. **An id it already
   holds with DIFFERENT content is a different item that collided, and the host MUST refuse it** with a
   reason, so it stays queued rather than read as the item the host holds.
4. Answering a question again replaces the latest queued answer to it - unless that answer is pending, in
   which case the new answer is added with a new id. So one question has at most two queued answers: the
   pending one and the newest revision. **A host MUST treat a later answer to the same question as
   replacing an earlier one.**

A later `status` for an item already in Sent just replaces its words.

### State

```json
{ "queued": [ item + { "pending": true, "statusLabel": "..." }, ... ],
  "sent": [ item + { "status": "held", "statusLabel": "..." }, ... ],
  "replies": [ reply, ... ],
  "draft": { "anchor": anchor, "text": "half-typed note" } | null,
  "answerDrafts": [ { "questionId": "deploy-window", "optionValue": "monday", "comment": "half-typed" } ],
  "scroll": { "x": 0, "y": 1200 } }
```

`pending` and `statusLabel` on a queued item are optional. `answerDrafts` is what the owner has picked and
typed in a question without queueing it; `optionValue` is empty when nothing is picked. Queueing an answer
clears that question's draft, so a draft that exists is always newer than the queued answer, and after a
restore the inputs show the draft if there is one, otherwise the queued answer.

The page cannot remember anything across a reload - a sandboxed frame has no storage - so the HOST keeps
the last `state-changed` state and sends it back as `restore` after the page's next `ready`.

### Hosted and unhosted - who draws the conversation

The page counts as **hosted** once it has received a valid `restore`, and not a moment before. Being inside
a frame is not enough: a framed page whose host never answers is unhosted, and so is a plain file opened in
a browser. The switch goes both ways and is driven by the restore alone.

**Hosted, the app owns the conversation and the page draws only the note-taking parts:**

| The page draws | The page does NOT draw |
|---|---|
| the "Add a note" control and the "Note on selected text" control | the Queued list, the Sent list, the Replies list, and their headings |
| the note box for what was tapped, with Queue note and Cancel | the Send button and its row |
| each question's Queue button and its state line | the payload preview box |

The parts it does not draw are taken out of the document, not merely hidden. They live ONCE, in the app's
own panel - the Cockpit's rail, the phone's sheet - which is rendered from the state the page posts. The
owner saw both at once and counted two conversations and two Send buttons, one of them permanently
disabled; that is what this rule exists to stop.

**Unhosted, the page draws the whole tray**, because there is nothing else on screen that could: the
queued, sent and replies lists, and Send - which, with no host to post to, shows the exact `send` message
that would have been posted and keeps the queue.

Hosted or not, the page keeps the WHOLE state and posts all of it in `state-changed`. Only what it DRAWS
changes. A question's state line says where the one Send is in each case, in plain words that name nothing
internal and are true of every app.

### The note box

The note box - the composer for what was just tapped - is placed against the element the note is about:

- **Below it when there is room below, otherwise above**, never on top of it, and never on top of the
  question element that contains it. The anchor and its question are avoided together, so a note on a
  paragraph inside a question does not land on the rest of that question.
- **Nudged sideways** so it stays inside the viewport.
- When it fits neither below nor above as the page stands, it goes BELOW and **the page scrolls** just
  enough to make room - never so far that what the note is about leaves the screen.
- The anchor is **measured when the box is drawn**, not when the draft was made, because the page may have
  been scrolled in between. After a reload the element is found again from the note's own `selector`; a
  selector that no longer resolves puts the box at the top left, where it covers nothing of its own.
- It is **sized to the note**, growing with the text up to a ceiling of two fifths of the viewport height.
  Past that the note's own text scrolls inside the box; the box itself is never a scrolling panel, and
  neither is the tray when hosted. The report keeps ONE scrollbar.

---

## 4. Trust - what a host MUST do

The report is written by an agent, and it runs in the same frame as the note-taking script. The sandbox
keeps the report away from the app around it, but NOT away from the note-taking script: without the rules
below, a `<script>` in the report could post a `send` that looks exactly like the owner's, or read the
notes and replies the host restores. `event.source` cannot tell the two apart - they are the same window.

So a host that shows a dev report MUST do all of the following. The test host in
`browser-tests/dev-report-notes-proof/index.html` does them, and the browser proof checks each one - each
check was also watched failing with its rule removed.

1. **Frame.** Show the report in `<iframe sandbox="allow-scripts">`, never with `allow-same-origin`.
2. **The host writes the head, first.** Build the frame's document as the host's own
   `<!doctype html><html><head>` holding the policy and the injected script, closed with `</head>`, and
   only THEN the report's bytes, untouched. Never search the report's text for a place to insert anything:
   a `<head>` inside a comment is enough to put a searched-for policy where it does nothing. (A report's
   own doctype, html and head tags after that point are ignored or merged by the parser; its styles and
   title still apply.)
3. **No report scripts.** The policy, with a fresh random nonce for every load:

   ```html
   <meta http-equiv="Content-Security-Policy"
         content="default-src 'none'; script-src 'nonce-NONCE'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'">
   ```

   Scripts, inline event handlers and `javascript:` links in the report do not run. Only the one script
   the host injects with that nonce does - and because it is in the host's head, it runs before any of the
   report exists. Styles and inline SVG work; images must be `data:` URLs.
4. **A token per load, and one ready.** Inject the note-taking script as
   `<script nonce="NONCE" data-dev-report-token="TOKEN">` with a fresh random token. On the window, accept
   only a `ready` whose `event.source` is the frame, whose `token` is the current one, and which carries
   exactly one port; then forget the token (one ready per load). Refuse everything else on the window.
5. **Everything else over the port.** Push `restore`, `status` and `reply` only over the port from that
   ready, and read `send` and `state-changed` only from it. Never post to the frame's window.
6. **A navigation closes the port.** A report can still contain a plain link or a `<meta http-equiv="refresh">`
   that takes the frame to another page, whose scripts CAN run. That page never has the port, so it gets
   nothing. And because the host sets the frame's content itself, a `load` event it did not cause means the
   frame shows something else: close the port and ignore the frame until the host loads the report again.

### The tray's theme

A host MAY give the notes interface the app's look by adding a second attribute to the same script element:
`data-dev-report-theme`, holding a JSON object (HTML-escaped inside the attribute) with exactly these keys:

```json
{ "background": "#0b1020", "surface": "#141a2e", "surface2": "#1b2238", "border": "#28304a",
  "text": "#e6e9f2", "textDim": "#99a0b8", "accent": "#3b82f6", "accentText": "#ffffff",
  "font": "-apple-system, BlinkMacSystemFont, \"Segoe UI\", Roboto, Helvetica, Arial, sans-serif",
  "monoFont": "\"Cascadia Mono\", Consolas, Menlo, monospace" }
```

- Colours are `#rgb` or `#rrggbb`. Fonts are a font list made only of letters, digits, spaces, commas,
  hyphens and double quotes, at most 200 characters.
- A theme with a missing key, an extra key or any value outside those rules is ignored whole, and the
  script uses the app's dark palette and type shown above - the same values it uses with no attribute.
- The script reads the attribute once, removes it from the element with the token, and writes the values
  into the stylesheet inside its shadow roots. The app's CSS custom properties do not cross into the frame
  and a report's CSS cannot reach the shadow roots, so the tray looks like the app whatever the report sets.

The theme is appearance only. It is not a secret and carries no authority.

**What the page does for itself.** The notes tray, the note box and each question's Queue button live in
shadow roots, so the report's CSS cannot select them, and their host elements carry inline `!important`
rules for display, visibility, opacity, position, transform, filter and clip-path. This stops a report from hiding
the interface by name. It does not stop everything a stylesheet can do to a page: a report can still lay
something over the tray, or hide the whole page. That is a report that is broken for the owner to see, not
one that acts for him.
