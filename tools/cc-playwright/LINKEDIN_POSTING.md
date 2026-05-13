# LinkedIn posting via cc-playwright

How a queued post (text + image) gets published end-to-end through Brave +
Playwright + the cc-playwright connection model.

## What ships in this directory

- `src/cli.py` -- cc-playwright CLI. Supports per-connection isolation
  (`--connection <name>`), auto-port allocation, profile resolution that
  shares cc-browser's connection dirs, and a `set-files` command for file
  uploads.
- `../../scripts/linkedin-post-from-queue.py` -- the canonical posting
  script. Takes a cc-comm-queue id prefix, posts text + image, captures
  before/after screenshots. Rerunnable.

## End-to-end flow that works

```
1. Close cc-browser's "linkedin" connection (Chrome locks the user-data-dir).
2. Start cc-playwright on the linkedin profile, opening LinkedIn directly:

   cc-playwright --connection linkedin start --url https://www.linkedin.com/feed/

   cc-playwright auto-allocates a free port (9223+), uses the cc-browser
   linkedin connection dir as profile, and pins www.linkedin.com so future
   commands route to the right tab.

3. Run:

   python scripts/linkedin-post-from-queue.py <queue-id-prefix>

   The script connects via CDP at the auto-allocated port, drives the
   composer, attaches the image, posts, and prints the result + screenshot
   paths.

4. Verify visually, then mark posted in the queue:

   cc-comm-queue mark-posted <queue-id> --by cc_playwright
```

## What the script does (the parts that mattered)

### 1. The composer is in an open Shadow DOM

LinkedIn mounts the post composer (a Quill editor) inside the shadow root
of `<div data-testid="interop-shadowdom">`. Playwright's CSS engine pierces
open shadow roots automatically, so `page.locator(".ql-editor")` works
without special syntax. JS queries from `evaluate` need to dig in
explicitly:

```js
const sr = document.querySelector('[data-testid="interop-shadowdom"]').shadowRoot;
const editor = sr.querySelector('.ql-editor');
```

### 2. Trusted typing into Quill

`page.keyboard.type` and `locator.press_sequentially` produce
`isTrusted=true` keyboard events because cc-playwright connects via
`--remote-debugging-port` (CDP), not Chrome's extension `chrome.debugger`
API. Quill (and LinkedIn's React state above it) accept these as real user
input -- the Post button transitions from disabled to enabled exactly as
if a human were typing.

### 3. Click intercepts and the JS-click escape hatch

LinkedIn renders an `<div id="interop-outlet">` overlay above some elements
that intercepts pointer events. Playwright's `locator.click()` retries and
eventually times out on those. The fix is to invoke `el.click()` from
inside an `evaluate()` -- a synthetic JS click that goes through the React
handler directly without touching the page coordinate system. We use this
for "Add media", "Next", and "Post".

### 4. The OS file dialog trap (do not click "Upload from computer")

When the media editor modal opens, it shows a big blue "Upload from
computer" button. Clicking it -- via JS or otherwise -- triggers the
**operating-system file picker**, which Playwright cannot dismiss and
which blocks the visible browser window even though CDP commands keep
working underneath. The user sees a stuck dialog.

The modal also mounts a hidden `<input type="file">` at
`#media-editor-file-selector__file-input` inside the same shadow root.
**Set files directly on this hidden input** with
`set_input_files(path)` and skip the "Upload from computer" button
entirely. No OS dialog, no blocked window, file uploads cleanly.

### 5. Verification gates before clicking Post

The script reads composer state via `evaluate()` and refuses to click
Post unless: editor exists, text length is at least half the source
length, image is attached (data: URL preview present), Post button is
enabled. If any check fails, the script aborts BEFORE submitting.

After Post is clicked we verify the composer is gone (`editorPresent:
false`), and only then do we screenshot and mark posted.

### 6. Comments live in the LIGHT DOM

Unlike the post composer, the comment editor on a post page is a normal
contenteditable div in the light DOM with
`aria-label="Text editor for creating content"` and placeholder
"Add a comment...". Use `cc-playwright type --selector "div[aria-label='...']"`
to type, then click the submit button at
`button.comments-comment-box__submit-button--cr`.

## Gotchas worth remembering

| Gotcha | What to do |
|---|---|
| Brave session-restore opens stale tabs that drift our automation | `--connection`'s `pinned_host` makes `_select_pinned_page` always pick the LinkedIn tab, even if Brave restores a Luma tab on top of it. |
| `loc.input_value()` errors on contenteditables | `cmd_type` now wraps it in try/except. The typing already succeeded -- the inspection is best-effort. |
| `page.screenshot()` hangs on the post-update page | Some LinkedIn pages keep a long network connection open and Playwright's pre-screenshot wait never resolves. Use raw CDP `Page.captureScreenshot` instead. |
| Two cc-playwright agents running concurrently used to overwrite each other's state | Per-connection state files (`state/<name>.json`) + auto-port allocation isolate them. The Luma agent on `default` and our LinkedIn driver on `linkedin` ran side-by-side without interference. |
| LinkedIn renames CSS classes constantly | We use shape-stable selectors: `.ql-editor`, `aria-label="..."`, `id="media-editor-file-selector__file-input"`, `button.comments-comment-box__submit-button--cr`. Hashed class names like `_217e04b9` are useless tomorrow. |

## When NOT to use this

Browser automation against LinkedIn is fragile by nature. Each release
cycle, LinkedIn can change the shadow-DOM wrapper, the editor library, or
add bot-detection signals that Playwright (even via CDP attach) trips.

For posting on a regular cadence, the right tool is the **LinkedIn
Marketing API** (UGC Posts / Posts API). It needs a one-time OAuth app
setup but is the only reliable long-term path. Browser automation here is
a "we have something that works today" tool, not infrastructure.

## What this run proved

- Per-connection isolation in cc-playwright works -- the Luma agent kept
  running on its connection (port 9224) while we drove the LinkedIn
  connection (port 9223) for the entire BPMN posting flow, no collision.
- Trusted CDP events satisfy LinkedIn's React state checks (Post button
  enabled correctly).
- The OS-file-dialog trap is fully avoidable by writing to the hidden
  file input directly.
- A queue item with text + image BLOB stored in the cc-comm-queue SQLite
  database can go from "approved" to "live on LinkedIn with a first
  comment threaded under it" in one script invocation plus a verify step.

Reference run: queue item `78965f75` (BPMN 90-days post), posted as
`urn:li:share:7460446157765373952` on 2026-05-13, with the early-access
form link as the first comment.
