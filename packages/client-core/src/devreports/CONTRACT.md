# Dev report contract (version 1)

This is the ONE contract for a dev report. It has three parts, and all three live here:

1. **The report markup** - the markers the Gateway's shape check looks for
   (`src/CcDirector.Gateway/DevReports/DevReportShapeCheck.cs`).
2. **The question markup** - what the note-taking script turns into a Queue button.
3. **The host protocol** - the `postMessage` messages between the note-taking script
   (`dev-report-notes.js`, beside this file) and the app that shows the report.

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

Markers inside HTML comments, `<script>`, `<style>`, `<template>` and `<textarea>` do not count.

### The questions section

- Each open question is an element with `data-dev-report-question="<id>"` inside the questions section.
  The id is unique in the report and made of letters, digits, `-` and `_`.
- When there are no questions, the section must contain an element with `data-dev-report-no-questions`
  that says so in words ("No questions - nothing needed from you."). A missing section never reads as
  "no questions", and neither does an empty one.
- A section may not contain both questions and the no-questions element.

## 2. The question markup

```html
<div data-dev-report-question="deploy-window" data-dev-report-question-text="When should we deploy?">
  <h3>When should we deploy?</h3>
  <label><input type="radio" name="deploy-window" value="tonight" data-recommended> Tonight - quiet traffic</label>
  <label><input type="radio" name="deploy-window" value="monday"> Monday - the team is around</label>
  <textarea data-dev-report-comment placeholder="Anything to add (optional)"></textarea>
</div>
```

- **Options** are `<input type="radio">` elements inside the question. At least two. The option's label
  is the text of its `<label>` (wrapping, or pointed at by `for=`); without a label, its `value`.
- **The recommendation** is `data-recommended` on exactly one option. The script checks it when the page
  loads, so the recommendation is preselected.
- **The question text** is `data-dev-report-question-text` if present, otherwise the text of the first
  heading (`h1`-`h6`) inside the question, otherwise the id.
- **A comment box** is optional: a `<textarea data-dev-report-comment>` inside the question.
- **The Queue button** is added by the script, one per question. An author never writes it.

The shape check enforces: at least two options, exactly one `data-recommended`, unique ids.

### What can be noted

Anything in the page outside the notes tray can carry a note:

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
{ "channel": "devthrottle.dev-report", "version": 1, "type": "<type>", "payload": { } }
```

- A message whose `channel` or `version` does not match is ignored.
- A message whose `type` is unknown is ignored.
- A message whose payload does not have the shape below is ignored, whole. Nothing is half-applied.
- The page accepts messages only from `window.parent`. It posts to `window.parent` with target origin
  `"*"`, because a sandboxed frame without `allow-same-origin` has an opaque origin and cannot name its
  host's. The host must check `event.source` is its own frame.

### Items

A queued or sent item is one of:

```json
{ "id": "n3", "kind": "note", "text": "This number is wrong",
  "anchor": { "type": "table-cell", "selector": "#results > tbody > tr:nth-of-type(2) > td:nth-of-type(3)",
              "quote": "42", "rowLabel": "Gateway", "columnLabel": "Failures" } }

{ "id": "a1", "kind": "answer", "questionId": "deploy-window", "question": "When should we deploy?",
  "optionValue": "tonight", "optionLabel": "Tonight - quiet traffic", "comment": "" }
```

`id` is unique within the report's state. `anchor.rowLabel`, `anchor.columnLabel` and `anchor.label` are
present only for the anchor types that carry them. Every string is at most 20000 characters.

A sent item also carries `status` (a short machine word from the host, e.g. `sent`, `held`, `delivered`,
`refused`) and `statusLabel` (the words to show, rendered verbatim - the page never decides what a
status means).

### Page to host

| type | payload | when |
|---|---|---|
| `ready` | `{ "questionIds": ["deploy-window"] }` | once, when the script has started |
| `send` | `{ "items": [ item, ... ] }` | the owner pressed Send; the whole queue, in order |
| `state-changed` | `{ "state": state }` | the queue, the sent list, the half-typed note or the scroll position changed |

### Host to page

| type | payload | effect |
|---|---|---|
| `restore` | `{ "state": state }` | replaces the page's queued, sent, draft and replies, and scrolls to the saved position. Also marks the host as connected. |
| `status` | `{ "updates": [ { "id": "n3", "status": "held", "statusLabel": "Delivered when the agent finishes" } ] }` | updates sent items by id; an unknown id is skipped |
| `reply` | `{ "reply": { "id": "r1", "text": "Fixed - see section 2", "at": "2026-09-16T10:00:00Z" } }` | adds the agent's reply to the page (same id replaces) |

### State

```json
{ "queued": [ item, ... ], "sent": [ sentItem, ... ], "replies": [ reply, ... ],
  "draft": { "anchor": anchor, "text": "half-typed" } | null,
  "scroll": { "x": 0, "y": 1200 } }
```

The page cannot remember anything across a reload - a sandboxed frame has no storage - so the HOST keeps
the last `state-changed` state and sends it back as `restore` after the page's next `ready`.

### No host

The page counts as hosted once it has received a valid `restore`. Before that - a plain file opened in a
browser, or a host that has not answered yet - everything works except Send: pressing it shows the exact
`send` message that would have been posted, and the queue is kept.
