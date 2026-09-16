# Handoff - phase 1: the note-taking script and the report shape check

Read first: `STATE.md` beside this file (the rulings bind you), issue #2936, and
`docs/design/dev-reports/cc-dev-reports.html`. Idea source: https://github.com/kunchenguid/lavish-axi
(MIT, by Kun Chen) - read how its artifact script anchors a note and queues answers; reuse ideas and
credit him in a header comment; do not vendor its server or its poll command.

Open a child issue of #2936 for this phase before building.

## Build

**A. The script** - one plain JavaScript file, no dependencies, from one source in the web workspace
(pick the location that fits `packages/` conventions; it must also be loadable as a raw file by the
Director's WebView2 later).

- Click-to-note on a paragraph, a text selection, a table cell or a labelled SVG part. A note carries:
  a stable CSS selector, the quoted text, and for a table cell the row label and column label (the
  first cell of the row and the header of the column); for an SVG part, its label.
- Questions: markup contract defined by you and written down (for example
  `data-dev-report-question`, options, `data-recommended` preselected). One Queue button per question.
- A tray showing QUEUED separately from SENT, and a Send button that sends the whole queue at once.
- Host protocol, all by `postMessage`, versioned and documented in one file: the page announces ready;
  the host can restore state (queued items, half-typed text, scroll position) and push sent/delivered
  status and agent replies; the page emits send and state-changed events. Validate every inbound
  message's shape; ignore unknown types.
- On a plain file opened in a browser with no host: everything works except Send, which shows the
  exact payload that would be sent.
- ASCII only. No network calls from the script. It must work inside `sandbox="allow-scripts"` without
  `allow-same-origin` (so no storage APIs, no cookies).

**B. The shape check** - C# in the Gateway (a pure class, no endpoint yet), with unit tests:
header with a status (waiting on you / agent working / done); executive summary first; questions
section immediately after, present even when empty; then detail. It returns a verdict with plain
errors an agent can act on ("the questions section must come right after the summary"). Define the
markers the check looks for and write them down beside the script's markup contract - one contract.

## Proof required

- Script unit tests for anchoring (table cell row/column labels, selector round-trip), the queue, and
  message validation. Prove at least one test can fail by reverting what it guards.
- A real browser run (browser-harness or Playwright) of a sample report inside a sandboxed iframe on a
  test host page: note a table cell, answer a question, Send, and capture the payload the host got.
- Shape check tests: a good report passes; each rule has a failing report.
- `.\scripts\test-local.ps1` green; the web workspace tests you touched run and pass.
- A reviewer session from a different agent family reads the change before the pull request is opened.

## Done means

A pull request to main, ready for the Architect to merge, and a short phase report at
`docs/missions/dev-reports/PHASE-1-REPORT.md`: what was built, what is proven and how, what is not.
Commit and push to the branch as you go. Then tell the Architect in ONE line.
