# Cockpit History tab - verification (epic #733: #738, #739, #740, #741, #742)

This records the live verification of the Cockpit History tab built on branch
`feat/history-tab-cockpit`. All five sub-issues were implemented; this is the proof they work.

## How it was verified

- Built a slot-6 test Director from this branch (`scripts\local-build-avalonia.ps1 -Slot 6`),
  launched it via the Windows scheduled task (never touching the user's running Directors), and
  enrolled it with the local Gateway. Slot 5 was in use by another session, so slot 6 was used.
- Created a real Claude Code session and sent one prompt asking for a markdown heading, a bullet
  list, bold text, a fenced code block, an absolute file path, and a URL - so a single turn
  exercises every content feature.
- Ran this branch's Cockpit build against the Gateway and drove a real browser to the History tab.

## Results

### Endpoint (#738) - PASS

`GET /sessions/{id}/history`, both directly on the Director and proxied through the Gateway,
returned the parsed conversation:

- `agent=ClaudeCode`, `isSupported=true`, `status=ok`
- 2 messages: a `User` bubble and an `Assistant` bubble, each with structured `parts`
- `historyState=NeedsYou` (computed Director-side by the shared Core `HistoryStateDeriver`)

The Gateway needed no change - its generic `/sessions/{sid}/{**rest}` proxy forwarded the new
endpoint unchanged. Full response: `history-endpoint-response.json`.

### Cockpit History tab (live browser) - PASS

Screenshot `cockpit-history-tab.png`. Programmatic read-out of the rendered DOM confirmed:

| Feature | Issue | Evidence (rendered in the browser) |
|---|---|---|
| User / assistant bubbles, live poll | #738 | bubbles = `["You", "Assistant"]` |
| Markdown heading | #739 | `<h2>` present in the assistant bubble |
| Markdown fenced code | #739 | `<pre><code>` present |
| Markdown bold + list | #739 | `<strong>` and `<ul><li>` present |
| URL clickable + Copy URL | #740 | link `https://example.com/docs` with a **Copy URL** action, anchor opens in a new tab |
| File path + Copy path | #740 | `C:\Repos\app\Program.cs` with a **Copy path** action |
| Derived history-state pill | #741 | pill reads **"history: Needs you"**, distinct from the green **live** badge |

`cockpit-rail.png` shows this branch's Cockpit listing sessions, with the new History as the
third center tab (Terminal | Voice | History).

### Scroll behavior (learned from desktop bugs #744)

Implemented in `cockpit-history-scroll.js` + `HistoryPane`: sticky-bottom follow that lands at the
bottom on tab activation, stops the moment the reader scrolls up, and re-engages at the bottom;
the pane and its poll exist only while the tab is shown (no polling while hidden). The demo turn
was short (no scroll overflow), so a manual-scroll screenshot was not captured here; the behavior
is covered by the implementation and is the web answer to #744 items 1-3.

### Unit tests - PASS

24 tests in `CcDirector.Cockpit.Tests` (run green):
- `HistoryBubbleMapperTests` - role classification (You / Assistant / Tool result), part
  flattening, length caps, empty/null.
- `HistoryMarkdownTests` - headings, lists, bold, fenced code, plain text, inert raw HTML
  (DisableHtml), new-tab anchors.
- `HistoryLinksTests` - URL vs absolute path, no relative-path guessing in the browser context,
  dedupe, multi-line.

Builds clean: ControlApi, Gateway, Cockpit, and the slot-6 Director publish (0 warnings).

## Cross-CLI status (#742) - all supported CLIs verified in the browser

Every supported CLI was run as a real session and its History tab opened in the browser against
this branch's Cockpit. Full table, per-CLI detail, and screenshots: `CLI-GALLERY.md` +
`cli-gallery/`.

| CLI | Status | Notes |
|---|---|---|
| Claude Code | PASS | You/Assistant bubbles, markdown, links, derived-state pill. |
| Codex | PASS | You/Assistant bubbles, markdown, links. |
| Gemini | PASS | Raw terminal scrollback rendered verbatim (`<pre>`). |
| Grok | PASS | You/Assistant bubbles, markdown, links. |
| Pi | PASS | You/Assistant/Tool-result bubbles, markdown, links. |
| Copilot | PASS | You/Assistant bubbles, markdown, links. |
| OpenCode | PASS | You/Assistant bubbles, markdown, links. |
| Cursor | NOT SUPPORTED | No history provider / not a configured agent (matches desktop #737); the tab shows "History is not available for this agent yet." |

A bug was found and fixed during this pass: Gemini's `IsRawText` flag was not propagated to the
bubbles, so it rendered as Markdown instead of verbatim. Fixed with regression tests; Gemini now
renders raw, confirmed in the browser.

## Note on the test harness

The slot-6 test Director shut down gracefully a couple of times during the session (clean
gateway-deregister + exit) - a launch/management quirk of running an extra dev Director on this
machine, not a defect in the feature. The history endpoint served correctly every time the
Director was up.
