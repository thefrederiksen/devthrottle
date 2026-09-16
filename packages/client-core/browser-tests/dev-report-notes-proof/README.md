# Dev report note-taking proof

Drives the ONE shipping note-taking script (`src/devreports/dev-report-notes.js`, served from source) in a
real Chromium, inside `<iframe sandbox="allow-scripts">` with no `allow-same-origin`, on a test host page.
Run it from this directory:

```
node run-proof.mjs
```

It prints PASS or FAIL per claim, exits non-zero on any failure, and writes `evidence-<date>.json` plus
screenshots beside it.

Playwright is not a dependency of this repository, so tell the proof where it is. It loads Playwright from
`PLAYWRIGHT_PATH` when that is set, and otherwise by ordinary Node module resolution from this directory.
With a global `@playwright/cli` install:

```
PLAYWRIGHT_PATH="$(npm root -g)/@playwright/cli/node_modules/playwright" node run-proof.mjs
```

When neither finds Playwright the run stops with FAIL and this instruction; it does not look anywhere else.

## What is real and what is not

REAL: the shipping script, a real browser, a real sandboxed frame, real mouse clicks and typing, and the
sample report (`sample-report.html`), which the Gateway's shape check also reads and passes
(`DevReportShapeCheckTests.Check_TheSampleReportUsedByTheBrowserProof_Passes`).

TEST HOST: `index.html` stands in for the Cockpit, the phone and the Director, which gain a report view in
phases 3 and 4. It injects the script, answers `ready` with `restore`, keeps the last `state-changed`
state, and records every message. The status words and the reply it pushes are made up by the proof -
the real ones will come from the Gateway in phase 2.

NOT PROVEN HERE: any real app hosting the page, the Gateway record, delivery into a session, the phone's
touch interactions (text selection on a phone in particular), and WebView2 in the Director.

## The claims

- **A** - the report runs in a sandboxed frame that cannot read its host, and has no storage.
- **B** - the page announces `ready` with its question ids.
- **C** - the recommended option is preselected.
- **D** - a note on a table cell carries its selector (which resolves back to the cell), its text, its row
  label and its column label.
- **E** - an answer carries the question, the chosen option and the comment.
- **F** - queued items show as queued, apart from sent.
- **G** - Send posts one `send` message with the whole queue.
- **H** - the host's status words and the agent's reply are shown verbatim.
- **I** - malformed, foreign-channel, wrong-version and unknown messages are ignored.
- **J** - after a reload, the host's `restore` brings back sent items, the reply, the half-typed note and
  the scroll position.
- **K** - opened as a plain page with no host, Send shows the exact `send` message and keeps the queue.
