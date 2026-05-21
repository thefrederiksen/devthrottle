# FIX: HTML "Raw terminal" tab renders mangled output

## TL;DR

The HTML session view's **Raw terminal** tab dumps the PTY byte stream into a `<div>` after only stripping ANSI control sequences. Stripping is not the same as executing -- cursor moves, line clears, and carriage-return overwrites are silently discarded, so spinner updates and status bars that should overwrite the same line instead pile up as junk lines.

The Avalonia desktop tab does not have this problem because it pipes the same bytes through `CcDirector.Terminal.Core.AnsiParser` (a real xterm-compatible VT emulator validated against `@xterm/headless`) and renders the resulting cell grid.

**Fix:** make the HTML tab use the same emulator. We already own `AnsiParser` and `AnsiToHtmlConverter`. The endpoint just needs to be rewired and the JS reduced from "append text deltas" to "swap in a styled HTML snapshot".

---

## The screenshots

| | |
|--|--|
| **Bad (HTML)** | `Screenshot 2026-05-20 204126.png` -- "Hatching..." spinner fragments stacked vertically, status bar fragments wrapped at a 250+ column width, prompt re-prints interleaved with spinner frames, "Cooked for 2s" lines doubled. |
| **Good (Avalonia)** | `Screenshot 2026-05-20 204210.png` -- spinner overwrites itself, status bar sits on one line, "Cogitated for 5s" appears once, the agent message wraps cleanly at the visible width. |

Both views read the **same** raw PTY bytes from the same `CircularTerminalBuffer`. The only difference is what they do with them.

---

## What actually goes wrong (concrete)

Claude Code's TUI relies heavily on a real terminal grid. Examples of byte sequences it sends every few hundred milliseconds while running:

```
"Hatching..."          (write to row N, col 0)
\r                     (carriage return -- move cursor to col 0)
"Hatching... 1s"       (overwrite the same row)
\r ESC[2K               (CR then "erase entire line")
"Hatching... 2s . 1 tokens"
ESC[1A                 (cursor up 1 -- to redraw the status bar above)
ESC[K                  (erase to end of line)
"esc to interrupt . xhigh /effort"
ESC[1B                 (cursor down 1 -- back to working row)
```

A proper terminal emulator (xterm, Windows Terminal, our Avalonia view) walks each of those bytes against a 2-D cell grid and ends up with:

```
> What is 2 plus 2? One short sentence. Do not use any tools.
. 2 plus 2 equals 4.
* Cogitated for 5s
> show me the cli reference
  bypass permissions on (shift+tab to cycle)
```

### What the HTML view does today

`src/CcDirector.ControlApi/ControlEndpoints.cs:158` -- `GET /sessions/{sid}/buffer`:

```csharp
text = AnsiCleaner.Clean(bytes);   // src/CcDirector.ControlApi/AnsiCleaner.cs
```

`AnsiCleaner` is exactly what it says on the tin -- a **stripper**, not an emulator. From its own docstring:

```
- CSI sequences ESC[...letter -> removed
- OSC sequences ESC]...(BEL|ESC\) -> removed
- Standalone ESC + single char two-byte sequences -> removed
- DEL (0x7F), BEL (0x07), C1 controls (0x80-0x9F) -> removed
- Lone CR -> LF
```

That last rule is the most damaging: **every bare `\r` is replaced with `\n`**. In a real terminal `\r` followed by new text **overwrites** the same line. After `AnsiCleaner.Clean` it becomes a **new line**, so:

- `Hatching... 0\rHatching... 1\rHatching... 2\r...` becomes ten consecutive lines instead of one updating line.
- Status bar redraws (`ESC[1A ... ESC[1B`) are not just stripped -- the text that *was* meant to overwrite the bar above gets emitted in-line, so the bar text appears mid-stream after the working text.
- `ESC[2K` (erase line) is dropped, so the old text the spinner intended to wipe stays put.

Then the HTML client at `src/CcDirector.ControlApi/Web/session-view.html:684`:

```js
async function refreshRaw() {
  const r = await fetch('/sessions/' + SID + '/buffer?since=' + rawCursor, ...);
  ...
  rawEl.textContent += body.text;   // pure append; never overwrites
  rawCursor = body.newCursor || rawCursor;
}
```

The client treats the stream as **append-only**. Even if the server gave it correct ANSI bytes, the client couldn't act on them -- there is no terminal emulator on this side either.

CSS makes it worse but is not the root cause: `#rawView` uses `white-space: pre-wrap; word-break: break-word;` at body-width, so the absurdly wide ConPty rows wrap mid-token, producing the mangled prompt line in the screenshot.

### Why Avalonia looks fine

`src/CcDirector.Avalonia/CcDirector.Avalonia.csproj` references `CcDirector.Terminal.Core` and `CcDirector.Terminal.Avalonia`. The desktop terminal control feeds raw PTY bytes through `CcDirector.Terminal.Core.AnsiParser` (a Paul Williams DEC ANSI state machine + xterm dispatch table) into a `TerminalCell[,]` grid, then renders the grid each frame. CR, line clears, cursor moves, scroll regions, alt-screen swaps, SGR colors, DECAWM wrap behaviour -- all of it is handled.

So the parser already exists, is shared, is tested against `@xterm/headless`, and has a companion `Rendering/AnsiToHtmlConverter.cs` that turns its grid into styled HTML. The HTML endpoint just isn't wired to it.

---

## Plan to fix

### Option chosen: server-side VT emulation, HTML snapshot delivery

Server-side because the emulator is already in `CcDirector.Terminal.Core` and is .NET. Snapshot delivery because the grid IS the state -- a "since=N byte cursor" stops being meaningful once we render through a grid. We just return the current grid as styled HTML each poll. It's a few KB; well within budget for a 1.5 s poll interval.

The Avalonia terminal stays untouched. Its parser instance is bound to the live window size. We give the HTML view its **own** parser instance with a sane fixed terminal size (e.g. 120 cols, full scrollback), so resizing the browser does not cause Claude to re-render at a new width inside ConPty (ConPty width is owned by Avalonia and must remain stable).

### Step 1 -- Add a per-session HTML parser instance

**File:** `src/CcDirector.Core/Sessions/Session.cs`

Add an `AnsiParser` field initialised on session creation, with fixed cols/rows (start with `120 x 40`, max scrollback `5000` lines). Hook the existing byte-write path (whatever currently feeds `CircularTerminalBuffer.Write`) to also call `_htmlParser.Parse(data)`.

This keeps the parser warm and incremental -- no re-parse from scratch on each poll, no allocations per byte chunk beyond what `AnsiParser` already does.

Thread safety: `AnsiParser` is not advertised as thread-safe; gate `Parse` calls with the same lock that gates the buffer write, or marshal them onto a single dedicated channel reader.

### Step 2 -- Add an HTML-snapshot endpoint

**File:** `src/CcDirector.ControlApi/ControlEndpoints.cs`

Add `GET /sessions/{sid}/buffer/html`. It calls `AnsiToHtmlConverter.ConvertToHtml(scrollback, cells, cols, rows)` and returns:

```json
{
  "sessionId": "...",
  "cols": 120,
  "rows": 40,
  "totalBytes": 12345,
  "html": "<div class=\"block\">...</div>..."
}
```

Leave the existing `/sessions/{sid}/buffer` endpoint alone -- something else (TestHarness, chat service, voice transcript) may still want stripped text. Only the HTML tab needs to change.

### Step 3 -- Switch the HTML tab to use the snapshot

**File:** `src/CcDirector.ControlApi/Web/session-view.html`

Replace `refreshRaw()`:

```js
async function refreshRaw() {
  const r = await fetch('/sessions/' + SID + '/buffer/html', { credentials: 'same-origin' });
  if (!r.ok) { rawEl.textContent = 'Lost connection (HTTP ' + r.status + ')'; return; }
  const body = await r.json();
  const nearBottom = window.scrollY + window.innerHeight > document.body.scrollHeight - 80;
  rawEl.innerHTML = body.html || '';   // full-snapshot swap
  if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
}
```

Update `#rawView` CSS:
- Drop `word-break: break-word;` (the parser already wrapped at the column boundary; don't double-wrap).
- Keep `white-space: pre;` (not `pre-wrap`) so the column grid is preserved.
- Inline `.block` / `.line` / `.status-bar` styles already targeted by `AnsiToHtmlConverter`.
- Add an outer `overflow-x: auto;` so a wide terminal scrolls horizontally rather than smearing.

Drop the `rawCursor` / `rawFirstLoad` state -- snapshot mode does not need them.

XSS note: `AnsiToHtmlConverter.AppendChar` already escapes `<`, `>`, `&`, `"`. Safe to assign to `innerHTML` because the input is the converter's output, not user-controlled text. Add a code-comment to that effect so a future contributor does not switch to a user-driven source without re-checking.

### Step 4 -- Tests

**New file:** `src/CcDirector.Core.Tests/Sessions/SessionHtmlSnapshotTests.cs`

Three regression tests built on the same fixture style as `AnsiParserXtermSnapshotTests`:

1. `SpinnerCarriageReturn_OverwritesNotStacks` -- feed `Hatching... 0\rHatching... 1\rHatching... 2`, assert resulting HTML contains exactly one row whose text ends in `Hatching... 2`.
2. `StatusBarRedraw_StaysOnSingleLine` -- feed a captured Claude Code status-bar redraw sequence, assert the bar produces one `<div class="status-bar">` block, not N.
3. `WideTerminal_DoesNotWrap` -- feed a 110-column line, assert it appears as one `<div class="line">` (no mid-token break).

Tests for the endpoint live in `src/CcDirector.ControlApi.Tests/` if/when that project exists; otherwise wire into `CcDirector.Gateway.Tests` since the gateway proxies the same call.

### Step 5 -- Verify in-browser

Per CLAUDE.md rule 0b, we cannot launch `cc-director.exe` from inside a Claude Code session. After implementation:

1. Ask the user to launch CC Director from Explorer.
2. Curl the new endpoint to confirm shape: `curl http://localhost:7879/sessions/<sid>/buffer/html`.
3. Open the session in a browser, switch to **Raw terminal**, send a prompt that triggers a long spinner (e.g. `/help`), confirm the spinner stays on one line and the status bar stays on one line.
4. Side-by-side compare against the Avalonia tab to confirm rough parity.

### Out of scope (intentionally)

- Animated frame-perfect spinner -- a 1.5 s poll cannot keep up with sub-second redraws and that is fine; the desired outcome is "looks like a terminal", not "is a remote terminal". If/when we want frame-perfect, swap the poll for a Server-Sent-Events stream sending grid deltas. Not now.
- Colour-perfect rendering -- `AnsiToHtmlConverter` already emits SGR colors via inline styles. Verify it; do not redesign.
- Touchable input on the Raw tab -- the Send box at the bottom already covers this. The Raw tab stays read-only.
- Avalonia terminal changes -- nothing changes there.

### Risk / rollback

- Risk: HTML payload size grows from "delta text" to "full snapshot" -- ~5-30 KB per poll for a 120x40 grid + a few thousand scrollback lines. Still small. If it becomes a problem, switch to grid-delta diffs later.
- Risk: a buggy parser path becomes user-visible in the HTML where before it was only visible in Avalonia. Mitigated by the existing xterm snapshot suite; any divergence is a parser bug we'd want to know about anyway.
- Rollback: revert the JS changes and switch the tab back to `/buffer`. The parser instance on the server is harmless if no client reads it.

---

## Summary (one paragraph)

The HTML Raw terminal tab today calls `AnsiCleaner.Clean` which strips ANSI control codes and replaces every bare `\r` with `\n`, so spinner overwrites become stacked lines and the status bar fragments across the page. We already ship a real xterm-compatible VT emulator (`CcDirector.Terminal.Core.AnsiParser`) and a grid-to-styled-HTML renderer (`AnsiToHtmlConverter`) -- they're what makes the Avalonia view look right. The fix is to attach a per-session `AnsiParser` instance on the server, expose a new `GET /sessions/{sid}/buffer/html` endpoint that returns the converted snapshot, and change `refreshRaw()` to swap the snapshot into `innerHTML` instead of appending stripped text. No new dependencies, no parser changes, no Avalonia changes. Three regression tests against captured byte streams lock in the fix.
