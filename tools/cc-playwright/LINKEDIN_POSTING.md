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

**Replies have TWO editors on the page.** When you click "Reply to X's
comment", LinkedIn opens a second `div[aria-label="Text editor for creating
content"]` scoped to that thread, in addition to the top-level "Add a
comment" editor at the bottom. A bare selector grabs the first one (the
top-level box). Scope by the parent comment article:
`article#<commentId> div[contenteditable=true][aria-label*="Text"]`.

**Reply composer auto-inserts an @-mention of the commenter as a pill
prefix.** Do NOT begin your reply text with "FirstName LastName, ..." or
the rendered reply will read "Jane Doe Jane Doe, ...". Strip any
name salutation from the draft and let the auto-mention carry the
addressing. Start with the substance:

- bad: `"Jane Doe, fair pushback. You are right..."` -> renders as `"Jane Doe Jane Doe, fair pushback..."`
- good: `"Fair pushback. You are right..."` -> renders as `"Jane Doe Fair pushback. You are right..."`

If you need to clear an existing reply composer to retype, do NOT use
`execCommand('selectAll') + delete` -- that nukes the mention pill too.
Instead: re-click the Reply button on the comment (LinkedIn dismisses the
old composer and opens a fresh one with the mention intact), then move
the cursor to the end with `range.selectNodeContents(ed); range.collapse(false)`
before typing.

### 7. DM composer lives in the LIGHT DOM too -- but with different selectors

The popup DM composer that opens after clicking "Message" on a profile
uses **different** class names than the post comment composer:

| Thing | Selector |
|---|---|
| Body | `div.msg-form__contenteditable` (role=textbox, aria-label `Write a message...`; note LinkedIn renders the trailing dots as a single ellipsis glyph in the live DOM) |
| Subject (optional) | `input[placeholder="Subject (optional)"]` |
| Send button | `button.msg-form__send-btn` |

Two profile "Message" buttons exist on every profile page -- one in the
sticky header, one on the profile card. **Both report visible:true.
Playwright `click` times out on both because of overlay interception.**
JS-click via `evaluate` works:

```js
const btns = document.querySelectorAll('button[aria-label="Message <FirstName>"]');
btns[btns.length-1].click();   // profile-card button, not sticky-header
```

**Enter SENDS** in the DM composer. Multi-paragraph messages must be
typed as one `type --text` call per paragraph, with two `Shift+Enter`
presses between paragraphs (the composer renders `<p><br></p>` for blank
lines). Same risk profile as Upwork chat -- a literal `\n` in `--text`
will fire Enter and prematurely send a partial message.

**Never auto-Send DMs.** The connection README's DM flow stops at the
"composer filled and screenshotted" state and hands off to Soren for the
Send click. The full DM cheat sheet lives in the README at
`%LOCALAPPDATA%\cc-director\connections\linkedin\README.md`.

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
